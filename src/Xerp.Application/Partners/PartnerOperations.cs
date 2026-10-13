using Microsoft.EntityFrameworkCore;
using Xerp.Application.Common;
using Xerp.Application.Orders;
using Xerp.Application.Ports;
using Xerp.Domain.Common;
using Xerp.Domain.Partners;
using Xerp.Domain.Rules;

namespace Xerp.Application.Partners;

/// <summary>
/// The partner operations of spec 004, section 4. One method = one HTTP endpoint = one MCP tool.
/// <see cref="IXerpDb"/> is already filtered to the current tenant, so a partner of another tenant is
/// simply not found here. Writes check in the order of R11 and of spec 013, R10 and R16: validation ->
/// addressed partner -> code change -> code uniqueness -> name uniqueness -> tax id uniqueness.
/// <para>
/// A create and a replace read the tenant's rules once, at their start, and are judged by that one set (spec
/// 013, R4). What follows validation runs under <see cref="IXerpDb.SerializedPerTenantAsync{T}"/>: whether a
/// name or a tax id is unique is a tenant's rule and no unique index can hold it, so the lookup and the write
/// must not be overtaken by another write of the tenant (R13).
/// </para>
/// </summary>
public sealed class PartnerOperations(IXerpDb db, ITenantContext context, IClock clock, IRules rules)
{
    public async Task<Result<PagedResult<PartnerDto>>> ListAsync(ListPartnersInput input, CancellationToken cancellationToken = default)
    {
        var validated = PartnerValidation.List(input);
        if (!validated.IsSuccess)
            return validated.Error;
        var query = validated.Value;

        var partners = db.Partners.AsNoTracking();
        // The filters combine with AND (R18); a partner that is both customer and supplier matches either role.
        if (query.IsCustomer is { } isCustomer)
            partners = partners.Where(p => p.IsCustomer == isCustomer);
        if (query.IsSupplier is { } isSupplier)
            partners = partners.Where(p => p.IsSupplier == isSupplier);
        if (query.IsActive is { } isActive)
            partners = partners.Where(p => p.IsActive == isActive);
        if (query.Search is { } search)
        {
            // Code, name and tax id - not the address (R17).
            var pattern = LikePattern.Contains(search);
            partners = partners.Where(p =>
                EF.Functions.Like(EF.Property<string>(p, DbNames.CodeLower), DbText.Lower(pattern), LikePattern.EscapeCharacter) ||
                EF.Functions.Like(DbText.Lower(p.Name), DbText.Lower(pattern), LikePattern.EscapeCharacter) ||
                (p.TaxId != null && EF.Functions.Like(DbText.Lower(p.TaxId), DbText.Lower(pattern), LikePattern.EscapeCharacter)));
        }

        var total = await partners.CountAsync(cancellationToken);
        var page = await partners
            .OrderBy(p => EF.Property<string>(p, DbNames.CodeLower))
            .ThenBy(p => p.Id)
            .Skip(query.Offset)
            .Take(query.Limit)
            .ToListAsync(cancellationToken);
        return new PagedResult<PartnerDto>(page.Select(ToDto).ToList(), total, query.Limit, query.Offset);
    }

    public async Task<Result<PartnerDto>> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var partner = await db.Partners.AsNoTracking().SingleOrDefaultAsync(p => p.Id == id, cancellationToken);
        return partner is null ? NotFound() : ToDto(partner);
    }

    public async Task<Result<PartnerDto>> GetByCodeAsync(string? code, CancellationToken cancellationToken = default)
    {
        // A string that cannot be a code cannot name a partner.
        if (!CodeRules.TryNormalize(code, out var normalized))
            return NotFound();
        var partner = await db.Partners.AsNoTracking()
            .SingleOrDefaultAsync(p => EF.Property<string>(p, DbNames.CodeLower) == DbText.Lower(normalized), cancellationToken);
        return partner is null ? NotFound() : ToDto(partner);
    }

    public async Task<Result<PartnerDto>> CreateAsync(PartnerInput input, CancellationToken cancellationToken = default)
    {
        var tenantRules = await rules.ReadAsync(cancellationToken);
        var validated = PartnerValidation.Create(input, tenantRules);
        if (!validated.IsSuccess)
            return validated.Error;
        var values = validated.Value;

        return await WriteAsync(values.Code, async ct =>
        {
            if (await CodeIsUsedAsync(values.Code, exceptId: null, ct))
                return CodeTaken(values.Code);
            if (await CheckUniqueAsync(tenantRules, values, exceptId: null, ct) is { } taken)
                return taken;

            var partner = Partner.Create(
                values.Code, values.Name, values.IsCustomer, values.IsSupplier, values.TaxId, values.Address, values.IsActive,
                clock.UtcNow, ActorKeyId());
            db.Partners.Add(partner);
            await db.SaveChangesAsync(ct);
            return ToDto(partner);
        }, cancellationToken);
    }

    public async Task<Result<PartnerDto>> ReplaceAsync(Guid id, PartnerInput input, CancellationToken cancellationToken = default)
    {
        var tenantRules = await rules.ReadAsync(cancellationToken);
        var validated = PartnerValidation.Replace(input, tenantRules);
        if (!validated.IsSuccess)
            return validated.Error;
        var values = validated.Value;

        return await WriteAsync(values.Code, async ct =>
        {
            var partner = await db.Partners.SingleOrDefaultAsync(p => p.Id == id, ct);
            if (partner is null)
                return NotFound();
            if (MasterWriteChecks.CodeChange(tenantRules, RuleRegistry.Partner, "a partner", partner.Code, values.Code) is { } codeChange)
                return codeChange;
            if (await CodeIsUsedAsync(values.Code, exceptId: id, ct))
                return CodeTaken(values.Code);
            if (await CheckUniqueAsync(tenantRules, values, exceptId: id, ct) is { } taken)
                return taken;

            partner.Replace(
                values.Code, values.Name, values.IsCustomer, values.IsSupplier, values.TaxId, values.Address, values.IsActive,
                clock.UtcNow, ActorKeyId());
            await db.SaveChangesAsync(ct);
            return ToDto(partner);
        }, cancellationToken);
    }

    /// <summary>
    /// Spec 013, R12-R16: <c>NAME_TAKEN</c>, then <c>TAX_ID_TAKEN</c>; each answers alone. The record being
    /// replaced never collides with itself, and partners without a tax id never collide.
    /// </summary>
    private async Task<AppError?> CheckUniqueAsync(TenantRules tenantRules, PartnerValues values, Guid? exceptId, CancellationToken cancellationToken)
    {
        var others = db.Partners.Where(p => exceptId == null || p.Id != exceptId);
        if (await MasterWriteChecks.NameAsync(tenantRules, RuleRegistry.Partner, others, "partner", values.Name, cancellationToken) is { } nameTaken)
            return nameTaken;

        // With the rule false nothing is looked up: the tax id is not unique, as before spec 013 (ADR-0011, decision 6).
        var taxIdUnique = tenantRules[RuleRegistry.PartnerTaxIdUnique];
        if (!taxIdUnique || values.TaxId is not { } taxId)
            return null;
        var taken = await others.AnyAsync(p => p.TaxId != null && DbText.Lower(p.TaxId) == DbText.Lower(taxId), cancellationToken);
        return MasterDataRules.Taken(taxIdUnique, taken) ? AppError.TaxIdTaken(taxId, RuleRegistry.PartnerTaxIdUnique) : null;
    }

    public async Task<Result<PartnerDeleted>> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var partner = await db.Partners.SingleOrDefaultAsync(p => p.Id == id, cancellationToken);
        if (partner is null)
            return NotFound();
        // Spec 009, R37: an order of any status - draft, confirmed or closed - names its partner for good.
        // Spec 011a, R15: so does a stock document - draft, posted or reversed, linked to an order or not.
        if (await OrderReads.AnyForPartnerAsync(db, id, cancellationToken)
            || await db.StockDocuments.AnyAsync(d => d.PartnerId == id, cancellationToken))
            return InUse();
        db.Partners.Remove(partner);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (ForeignKeyViolationException ex) when (ex.BlockedDelete)
        {
            // An order or a stock document started to name the partner after the check above: the foreign key is the authority.
            return InUse();
        }
        catch (DbUpdateConcurrencyException)
        {
            return NotFound();
        }
        return new PartnerDeleted();
    }

    /// <summary>Runs the checks and the write of a create or a replace under the tenant's lock, translating what only the database can decide.</summary>
    private async Task<Result<PartnerDto>> WriteAsync(
        string code, Func<CancellationToken, Task<Result<PartnerDto>>> work, CancellationToken cancellationToken)
    {
        try
        {
            return await db.SerializedPerTenantAsync(work, cancellationToken);
        }
        catch (UniqueConstraintViolationException ex) when (ex.IsCodeOf<Partner>())
        {
            // Lost a race with a concurrent write of the same code: the unique index is the authority.
            return CodeTaken(code);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Deleted by someone else between the read and the write.
            return NotFound();
        }
    }

    private Task<bool> CodeIsUsedAsync(string code, Guid? exceptId, CancellationToken cancellationToken) =>
        db.Partners.AnyAsync(
            p => EF.Property<string>(p, DbNames.CodeLower) == DbText.Lower(code) && (exceptId == null || p.Id != exceptId),
            cancellationToken);

    private Guid ActorKeyId() =>
        context.ApiKeyId ?? throw new InvalidOperationException("A partner can only be written by a tenant API key.");

    /// <summary>By <c>id</c> or by <c>code</c>, exactly one (spec 003, R13) - for clients without a URL path.</summary>
    public Task<Result<PartnerDto>> FindAsync(RecordAddressInput address, CancellationToken cancellationToken = default)
    {
        if (RecordAddress.IdOrCode(address, NotFoundDetail, out var id, out var code) is { } error)
            return Task.FromResult<Result<PartnerDto>>(error);
        return id is { } byId ? GetAsync(byId, cancellationToken) : GetByCodeAsync(code, cancellationToken);
    }

    /// <summary>As the overload with a <see cref="Guid"/>; an id that is not a UUID names no record (spec 003, R15).</summary>
    public Task<Result<PartnerDto>> ReplaceAsync(string? id, PartnerInput input, CancellationToken cancellationToken = default) =>
        RecordAddress.Id(id, NotFoundDetail, out var parsed) is { } error
            ? Task.FromResult<Result<PartnerDto>>(error)
            : ReplaceAsync(parsed, input, cancellationToken);

    /// <summary>As the overload with a <see cref="Guid"/>; an id that is not a UUID names no record (spec 003, R15).</summary>
    public Task<Result<PartnerDeleted>> DeleteAsync(string? id, CancellationToken cancellationToken = default) =>
        RecordAddress.Id(id, NotFoundDetail, out var parsed) is { } error
            ? Task.FromResult<Result<PartnerDeleted>>(error)
            : DeleteAsync(parsed, cancellationToken);

    private const string NotFoundDetail = "Partner not found.";

    private static AppError NotFound() => AppError.NotFound(NotFoundDetail);

    private static AppError InUse() =>
        AppError.InUse("The partner is named by orders or stock documents and cannot be deleted. Deactivate it instead (isActive = false).");

    private static AppError CodeTaken(string code) => AppError.CodeTaken($"A partner with code '{code}' already exists.");

    private static PartnerDto ToDto(Partner p) =>
        new(p.Id, p.Code, p.Name, p.IsCustomer, p.IsSupplier, p.TaxId,
            p.Address.Line1, p.Address.Line2, p.Address.PostalCode, p.Address.City, p.Address.Region, p.Address.CountryCode,
            p.IsActive, p.CreatedAt, p.UpdatedAt, p.CreatedBy, p.UpdatedBy);
}
