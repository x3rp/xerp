using Microsoft.EntityFrameworkCore;
using Xerp.Application.Common;
using Xerp.Application.Orders;
using Xerp.Application.Ports;
using Xerp.Domain.Common;
using Xerp.Domain.Inventory;
using Xerp.Domain.Rules;

namespace Xerp.Application.UnitsOfMeasure;

/// <summary>
/// The unit-of-measure operations of spec 001, 4.3. One method = one HTTP endpoint = one MCP tool.
/// <see cref="IXerpDb"/> is already filtered to the current tenant, so a unit of another tenant is
/// simply not found here.
/// <para>
/// A create and a replace read the tenant's rules once, at their start, and are judged by that one set (spec
/// 013, R4). What follows validation runs under <see cref="IXerpDb.SerializedPerTenantAsync{T}"/>: whether a
/// name is unique is a tenant's rule and no unique index can hold it, so the lookup and the write must not be
/// overtaken by another write of the tenant (R13).
/// </para>
/// </summary>
public sealed class UnitOfMeasureOperations(IXerpDb db, ITenantContext context, IClock clock, IRules rules)
{
    public async Task<Result<PagedResult<UnitOfMeasureDto>>> ListAsync(
        ListUnitsOfMeasureInput input, CancellationToken cancellationToken = default)
    {
        var validated = UnitOfMeasureValidation.List(input);
        if (!validated.IsSuccess)
            return validated.Error;
        var query = validated.Value;

        var units = db.UnitsOfMeasure.AsNoTracking();
        if (query.IsActive is { } isActive)
            units = units.Where(u => u.IsActive == isActive);
        if (query.Search is { } search)
        {
            var pattern = LikePattern.Contains(search);
            units = units.Where(u =>
                EF.Functions.Like(EF.Property<string>(u, DbNames.CodeLower), DbText.Lower(pattern), LikePattern.EscapeCharacter) ||
                EF.Functions.Like(DbText.Lower(u.Name), DbText.Lower(pattern), LikePattern.EscapeCharacter));
        }

        var total = await units.CountAsync(cancellationToken);
        var items = await units
            .OrderBy(u => EF.Property<string>(u, DbNames.CodeLower))
            .ThenBy(u => u.Id)
            .Skip(query.Offset)
            .Take(query.Limit)
            .Select(u => new UnitOfMeasureDto(u.Id, u.Code, u.Name, u.IsActive, u.CreatedAt, u.UpdatedAt, u.CreatedBy, u.UpdatedBy))
            .ToListAsync(cancellationToken);
        return new PagedResult<UnitOfMeasureDto>(items, total, query.Limit, query.Offset);
    }

    public async Task<Result<UnitOfMeasureDto>> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var unit = await db.UnitsOfMeasure.AsNoTracking().SingleOrDefaultAsync(u => u.Id == id, cancellationToken);
        return unit is null ? NotFound() : ToDto(unit);
    }

    public async Task<Result<UnitOfMeasureDto>> GetByCodeAsync(string? code, CancellationToken cancellationToken = default)
    {
        // A string that cannot be a code cannot name a unit (E6).
        if (!CodeRules.TryNormalize(code, out var normalized))
            return NotFound();
        var unit = await db.UnitsOfMeasure.AsNoTracking()
            .SingleOrDefaultAsync(u => EF.Property<string>(u, DbNames.CodeLower) == DbText.Lower(normalized), cancellationToken);
        return unit is null ? NotFound() : ToDto(unit);
    }

    public async Task<Result<UnitOfMeasureDto>> CreateAsync(
        CreateUnitOfMeasureInput input, CancellationToken cancellationToken = default)
    {
        var tenantRules = await rules.ReadAsync(cancellationToken);
        var validated = UnitOfMeasureValidation.Create(input);
        if (!validated.IsSuccess)
            return validated.Error;
        var values = validated.Value;

        return await WriteAsync(values.Code, async ct =>
        {
            if (await CodeIsUsedAsync(values.Code, exceptId: null, ct))
                return CodeTaken(values.Code);
            if (await MasterWriteChecks.NameAsync(tenantRules, RuleRegistry.Uom, db.UnitsOfMeasure, UnitWord, values.Name, ct) is { } nameTaken)
                return nameTaken;

            var unit = UnitOfMeasure.Create(values.Code, values.Name, values.IsActive, clock.UtcNow, ActorKeyId());
            db.UnitsOfMeasure.Add(unit);
            await db.SaveChangesAsync(ct);
            return ToDto(unit);
        }, cancellationToken);
    }

    /// <summary>Order of checks (spec 013, R10, R16): validation -> exists -> code change -> CODE_TAKEN -> NAME_TAKEN.</summary>
    public async Task<Result<UnitOfMeasureDto>> ReplaceAsync(
        Guid id, ReplaceUnitOfMeasureInput input, CancellationToken cancellationToken = default)
    {
        var tenantRules = await rules.ReadAsync(cancellationToken);
        var validated = UnitOfMeasureValidation.Replace(input);
        if (!validated.IsSuccess)
            return validated.Error;
        var values = validated.Value;

        return await WriteAsync(values.Code, async ct =>
        {
            var unit = await db.UnitsOfMeasure.SingleOrDefaultAsync(u => u.Id == id, ct);
            if (unit is null)
                return NotFound();
            if (MasterWriteChecks.CodeChange(tenantRules, RuleRegistry.Uom, "a unit of measure", unit.Code, values.Code) is { } codeChange)
                return codeChange;
            if (await CodeIsUsedAsync(values.Code, exceptId: id, ct))
                return CodeTaken(values.Code);
            var others = db.UnitsOfMeasure.Where(u => u.Id != id);
            if (await MasterWriteChecks.NameAsync(tenantRules, RuleRegistry.Uom, others, UnitWord, values.Name, ct) is { } nameTaken)
                return nameTaken;

            unit.Replace(values.Code, values.Name, values.IsActive, clock.UtcNow, ActorKeyId());
            await db.SaveChangesAsync(ct);
            return ToDto(unit);
        }, cancellationToken);
    }

    private const string UnitWord = "unit of measure";

    /// <summary>Runs the checks and the write of a create or a replace under the tenant's lock, translating what only the database can decide.</summary>
    private async Task<Result<UnitOfMeasureDto>> WriteAsync(
        string code, Func<CancellationToken, Task<Result<UnitOfMeasureDto>>> work, CancellationToken cancellationToken)
    {
        try
        {
            return await db.SerializedPerTenantAsync(work, cancellationToken);
        }
        catch (UniqueConstraintViolationException ex) when (ex.IsCodeOf<UnitOfMeasure>())
        {
            // Lost a race with a concurrent write of the same code: the unique index is the authority (E8).
            return CodeTaken(code);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Deleted by someone else between the read and the write.
            return NotFound();
        }
    }

    public async Task<Result<UnitOfMeasureDeleted>> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var unit = await db.UnitsOfMeasure.SingleOrDefaultAsync(u => u.Id == id, cancellationToken);
        if (unit is null)
            return NotFound();
        // Active and inactive referrers both count (spec 002, R16). The detail says how many, never which.
        var articles = await db.Articles.CountAsync(a => a.BaseUnitId == id, cancellationToken);
        if (articles > 0)
            return InUse($"The unit of measure is the base unit of {articles} article(s).");
        // Spec 007, R10: also while a conversion or a stock document line, draft or posted, names it.
        var conversions = await db.ArticleUnits.CountAsync(c => c.UnitId == id, cancellationToken);
        if (conversions > 0)
            return InUse($"The unit of measure is an alternative unit of {conversions} article(s): they have a conversion for it.");
        if (await db.StockDocumentLines.AnyAsync(l => l.UnitId == id, cancellationToken))
            return InUse("Lines of stock documents are entered in this unit of measure.");
        // Spec 009, R37: a line of an order of any status; a confirmed line keeps its unit although the conversion is gone.
        if (await OrderReads.AnyLineInUnitAsync(db, id, cancellationToken))
            return InUse("Lines of orders are entered in this unit of measure.");

        db.UnitsOfMeasure.Remove(unit);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (ForeignKeyViolationException ex) when (ex.BlockedDelete)
        {
            // Something started to reference the unit after the check above: the foreign key is the authority.
            return InUse("The unit of measure is referenced by other records.");
        }
        catch (DbUpdateConcurrencyException)
        {
            return NotFound();
        }
        return new UnitOfMeasureDeleted();
    }

    private Task<bool> CodeIsUsedAsync(string code, Guid? exceptId, CancellationToken cancellationToken) =>
        db.UnitsOfMeasure.AnyAsync(
            u => EF.Property<string>(u, DbNames.CodeLower) == DbText.Lower(code) && (exceptId == null || u.Id != exceptId),
            cancellationToken);

    private Guid ActorKeyId() =>
        context.ApiKeyId ?? throw new InvalidOperationException("A unit of measure can only be written by a tenant API key.");

    private static AppError InUse(string detail) =>
        AppError.InUse(detail + " Deactivate it instead (isActive = false), or remove the references first.");

    /// <summary>By <c>id</c> or by <c>code</c>, exactly one (spec 003, R13) - for clients without a URL path.</summary>
    public Task<Result<UnitOfMeasureDto>> FindAsync(RecordAddressInput address, CancellationToken cancellationToken = default)
    {
        if (RecordAddress.IdOrCode(address, NotFoundDetail, out var id, out var code) is { } error)
            return Task.FromResult<Result<UnitOfMeasureDto>>(error);
        return id is { } byId ? GetAsync(byId, cancellationToken) : GetByCodeAsync(code, cancellationToken);
    }

    /// <summary>As the overload with a <see cref="Guid"/>; an id that is not a UUID names no record (spec 003, R15).</summary>
    public Task<Result<UnitOfMeasureDto>> ReplaceAsync(string? id, ReplaceUnitOfMeasureInput input, CancellationToken cancellationToken = default) =>
        RecordAddress.Id(id, NotFoundDetail, out var parsed) is { } error
            ? Task.FromResult<Result<UnitOfMeasureDto>>(error)
            : ReplaceAsync(parsed, input, cancellationToken);

    /// <summary>As the overload with a <see cref="Guid"/>; an id that is not a UUID names no record (spec 003, R15).</summary>
    public Task<Result<UnitOfMeasureDeleted>> DeleteAsync(string? id, CancellationToken cancellationToken = default) =>
        RecordAddress.Id(id, NotFoundDetail, out var parsed) is { } error
            ? Task.FromResult<Result<UnitOfMeasureDeleted>>(error)
            : DeleteAsync(parsed, cancellationToken);

    private const string NotFoundDetail = "Unit of measure not found.";

    private static AppError NotFound() => AppError.NotFound(NotFoundDetail);

    private static AppError CodeTaken(string code) => AppError.CodeTaken($"A unit of measure with code '{code}' already exists.");

    private static UnitOfMeasureDto ToDto(UnitOfMeasure u) =>
        new(u.Id, u.Code, u.Name, u.IsActive, u.CreatedAt, u.UpdatedAt, u.CreatedBy, u.UpdatedBy);
}
