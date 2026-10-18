using Microsoft.EntityFrameworkCore;
using Xerp.Application.Common;
using Xerp.Application.Ports;
using Xerp.Domain.Common;
using Xerp.Domain.Rules;
using Xerp.Domain.Sales;

namespace Xerp.Application.Sales;

/// <summary>
/// The six operations of a sales master of spec P01, 4.A, written once for article categories, tax rates and
/// payment methods: each is a unit of measure (spec 001, 4.3) with its own entity, validation and referrers.
/// One method = one HTTP endpoint = one MCP tool. <see cref="IXerpDb"/> is already filtered to the current tenant.
/// <para>
/// A create and a replace read the tenant's rules once and are judged by that one set; the code change and
/// the name check are <see cref="MasterWriteChecks"/> of spec 013, run with the write under the tenant's lock
/// (P01/R6, 013/R13).
/// </para>
/// </summary>
public abstract class CodedMasterOperations<TEntity, TDto, TInput, TValues>(IXerpDb db, ITenantContext context, IClock clock, IRules rules)
    where TEntity : CodedMaster
    where TDto : notnull
    where TValues : CodedMasterValues
{
    protected IXerpDb Db => db;

    protected abstract DbSet<TEntity> Records { get; }
    protected abstract MasterRuleSet RuleSet { get; }

    /// <summary>The resource in the singular: <c>tax rate</c>.</summary>
    protected abstract string Word { get; }

    /// <summary>The record with its article: <c>a tax rate</c>.</summary>
    protected abstract string AWord { get; }

    protected abstract Result<TValues> Validate(TInput input, bool replace);

    /// <summary>As <see cref="Validate(TInput, bool)"/>, for a master whose input a tenant's rule also judges.</summary>
    protected virtual Result<TValues> Validate(TInput input, bool replace, TenantRules rules) => Validate(input, replace);

    /// <summary>
    /// The references of the values (ADR-0008), after "exists" and before the code checks (013, R10); null when
    /// they are acceptable. <paramref name="stored"/> is null on a create.
    /// </summary>
    protected virtual Task<AppError?> ReferencesAsync(TValues values, TEntity? stored, CancellationToken cancellationToken) =>
        Task.FromResult<AppError?>(null);

    /// <summary>What a replace does besides the record, in its transaction, before the save.</summary>
    /// <param name="wasActive">Whether the record was active before the replace.</param>
    protected virtual Task ReplacingAsync(TEntity record, bool wasActive, DateTime now, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    /// <summary>Reads what the representations of <paramref name="records"/> show besides the records, before <see cref="ToDto"/>.</summary>
    protected virtual Task PrepareAsync(IReadOnlyList<TEntity> records, CancellationToken cancellationToken) => Task.CompletedTask;
    protected abstract TEntity Create(TValues values, DateTime now, Guid actorKeyId);
    protected abstract void Replace(TEntity record, TValues values, DateTime now, Guid actorKeyId);
    protected abstract TDto ToDto(TEntity record);

    /// <summary>Why the record cannot be deleted (R5), or null. Active and inactive referrers both count.</summary>
    protected abstract Task<string?> UsedByAsync(Guid id, CancellationToken cancellationToken);

    public Task<Result<PagedResult<TDto>>> ListAsync(ListCodedMastersInput input, CancellationToken cancellationToken = default) =>
        ListAsync(input, new ValidationErrors(), null, cancellationToken);

    /// <param name="errors">The errors of the arguments only this resource has, reported with those of the shared ones.</param>
    protected async Task<Result<PagedResult<TDto>>> ListAsync(
        ListCodedMastersInput input, ValidationErrors errors, Func<IQueryable<TEntity>, IQueryable<TEntity>>? filter, CancellationToken cancellationToken)
    {
        var (search, limit, offset) = SalesValidation.List(input, errors);
        if (errors.Any)
            return errors.ToError();

        var records = Records.AsNoTracking();
        if (filter is not null)
            records = filter(records);
        if (input.IsActive is { } isActive)
            records = records.Where(r => r.IsActive == isActive);
        if (search is not null)
        {
            var pattern = LikePattern.Contains(search);
            records = records.Where(r =>
                EF.Functions.Like(EF.Property<string>(r, DbNames.CodeLower), DbText.Lower(pattern), LikePattern.EscapeCharacter) ||
                EF.Functions.Like(DbText.Lower(r.Name), DbText.Lower(pattern), LikePattern.EscapeCharacter));
        }

        var total = await records.CountAsync(cancellationToken);
        var items = await records
            .OrderBy(r => EF.Property<string>(r, DbNames.CodeLower))
            .ThenBy(r => r.Id)
            .Skip(offset)
            .Take(limit)
            .ToListAsync(cancellationToken);
        await PrepareAsync(items, cancellationToken);
        return new PagedResult<TDto>(items.Select(ToDto).ToList(), total, limit, offset);
    }

    public async Task<Result<TDto>> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var record = await Records.AsNoTracking().SingleOrDefaultAsync(r => r.Id == id, cancellationToken);
        return record is null ? NotFound() : await ToDtoAsync(record, cancellationToken);
    }

    public async Task<Result<TDto>> GetByCodeAsync(string? code, CancellationToken cancellationToken = default)
    {
        // A string that cannot be a code cannot name a record.
        if (!CodeRules.TryNormalize(code, out var normalized))
            return NotFound();
        var record = await Records.AsNoTracking()
            .SingleOrDefaultAsync(r => EF.Property<string>(r, DbNames.CodeLower) == DbText.Lower(normalized), cancellationToken);
        return record is null ? NotFound() : await ToDtoAsync(record, cancellationToken);
    }

    public async Task<Result<TDto>> CreateAsync(TInput input, CancellationToken cancellationToken = default)
    {
        var tenantRules = await rules.ReadAsync(cancellationToken);
        var validated = Validate(input, replace: false, tenantRules);
        if (!validated.IsSuccess)
            return validated.Error;
        var values = validated.Value;

        return await WriteAsync(values.Code, async ct =>
        {
            if (await ReferencesAsync(values, null, ct) is { } referenceError)
                return referenceError;
            if (await CodeIsUsedAsync(values.Code, exceptId: null, ct))
                return CodeTaken(values.Code);
            if (await MasterWriteChecks.NameAsync(tenantRules, RuleSet, Records, Word, values.Name, ct) is { } nameTaken)
                return nameTaken;

            var record = Create(values, clock.UtcNow, ActorKeyId());
            Records.Add(record);
            await db.SaveChangesAsync(ct);
            return await ToDtoAsync(record, ct);
        }, cancellationToken);
    }

    /// <summary>Order of checks (spec 013, R10, R16): validation -> exists -> code change -> CODE_TAKEN -> NAME_TAKEN.</summary>
    public async Task<Result<TDto>> ReplaceAsync(Guid id, TInput input, CancellationToken cancellationToken = default)
    {
        var tenantRules = await rules.ReadAsync(cancellationToken);
        var validated = Validate(input, replace: true, tenantRules);
        if (!validated.IsSuccess)
            return validated.Error;
        var values = validated.Value;

        return await WriteAsync(values.Code, async ct =>
        {
            var record = await Records.SingleOrDefaultAsync(r => r.Id == id, ct);
            if (record is null)
                return NotFound();
            if (await ReferencesAsync(values, record, ct) is { } referenceError)
                return referenceError;
            if (MasterWriteChecks.CodeChange(tenantRules, RuleSet, AWord, record.Code, values.Code) is { } codeChange)
                return codeChange;
            if (await CodeIsUsedAsync(values.Code, exceptId: id, ct))
                return CodeTaken(values.Code);
            if (await MasterWriteChecks.NameAsync(tenantRules, RuleSet, Records.Where(r => r.Id != id), Word, values.Name, ct) is { } nameTaken)
                return nameTaken;

            var wasActive = record.IsActive;
            var now = clock.UtcNow;
            Replace(record, values, now, ActorKeyId());
            await ReplacingAsync(record, wasActive, now, ct);
            await db.SaveChangesAsync(ct);
            return await ToDtoAsync(record, ct);
        }, cancellationToken);
    }

    /// <summary>
    /// Under the tenant's lock, which a Set of sales data holds too: a record is not deleted while sales data
    /// that names it is being saved (S2); the foreign key remains the authority.
    /// </summary>
    public async Task<Result<SalesRecordDeleted>> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        try
        {
            return await db.SerializedPerTenantAsync<Result<SalesRecordDeleted>>(async ct =>
            {
                var record = await Records.SingleOrDefaultAsync(r => r.Id == id, ct);
                if (record is null)
                    return NotFound();
                if (await UsedByAsync(id, ct) is { } usedBy)
                    return InUse(usedBy);
                Records.Remove(record);
                await db.SaveChangesAsync(ct);
                return new SalesRecordDeleted();
            }, cancellationToken);
        }
        catch (ForeignKeyViolationException ex) when (ex.BlockedDelete)
        {
            return InUse($"The {Word} is referenced by other records.");
        }
        catch (DbUpdateConcurrencyException)
        {
            return NotFound();
        }
    }

    /// <summary>By <c>id</c> or by <c>code</c>, exactly one (spec 003, R13) - for clients without a URL path.</summary>
    public Task<Result<TDto>> FindAsync(RecordAddressInput address, CancellationToken cancellationToken = default)
    {
        if (RecordAddress.IdOrCode(address, NotFoundDetail, out var id, out var code) is { } error)
            return Task.FromResult<Result<TDto>>(error);
        return id is { } byId ? GetAsync(byId, cancellationToken) : GetByCodeAsync(code, cancellationToken);
    }

    /// <summary>As the overload with a <see cref="Guid"/>; an id that is not a UUID names no record (spec 003, R15).</summary>
    public Task<Result<TDto>> ReplaceAsync(string? id, TInput input, CancellationToken cancellationToken = default) =>
        RecordAddress.Id(id, NotFoundDetail, out var parsed) is { } error
            ? Task.FromResult<Result<TDto>>(error)
            : ReplaceAsync(parsed, input, cancellationToken);

    /// <summary>As the overload with a <see cref="Guid"/>; an id that is not a UUID names no record (spec 003, R15).</summary>
    public Task<Result<SalesRecordDeleted>> DeleteAsync(string? id, CancellationToken cancellationToken = default) =>
        RecordAddress.Id(id, NotFoundDetail, out var parsed) is { } error
            ? Task.FromResult<Result<SalesRecordDeleted>>(error)
            : DeleteAsync(parsed, cancellationToken);

    /// <summary>Runs the checks and the write of a create or a replace under the tenant's lock, translating what only the database can decide.</summary>
    private async Task<Result<TDto>> WriteAsync(string code, Func<CancellationToken, Task<Result<TDto>>> work, CancellationToken cancellationToken)
    {
        try
        {
            return await db.SerializedPerTenantAsync(work, cancellationToken);
        }
        catch (UniqueConstraintViolationException ex) when (ex.IsCodeOf<TEntity>())
        {
            return CodeTaken(code);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Deleted by someone else between the read and the write.
            return NotFound();
        }
    }

    private async Task<TDto> ToDtoAsync(TEntity record, CancellationToken cancellationToken)
    {
        await PrepareAsync([record], cancellationToken);
        return ToDto(record);
    }

    private Task<bool> CodeIsUsedAsync(string code, Guid? exceptId, CancellationToken cancellationToken) =>
        Records.AnyAsync(
            r => EF.Property<string>(r, DbNames.CodeLower) == DbText.Lower(code) && (exceptId == null || r.Id != exceptId),
            cancellationToken);

    protected Guid ActorKeyId() =>
        context.ApiKeyId ?? throw new InvalidOperationException($"{AWord} can only be written by a tenant API key.");

    protected string NotFoundDetail => $"{char.ToUpperInvariant(Word[0])}{Word[1..]} not found.";

    protected AppError NotFound() => AppError.NotFound(NotFoundDetail);

    private AppError CodeTaken(string code) => AppError.CodeTaken($"{char.ToUpperInvariant(AWord[0])}{AWord[1..]} with code '{code}' already exists.");

    private static AppError InUse(string detail) =>
        AppError.InUse(detail + " Deactivate it instead (isActive = false), or remove the references first.");
}

public sealed class ArticleCategoryOperations(IXerpDb db, ITenantContext context, IClock clock, IRules rules)
    : CodedMasterOperations<ArticleCategory, ArticleCategoryDto, ArticleCategoryInput, CodedMasterValues>(db, context, clock, rules)
{
    protected override DbSet<ArticleCategory> Records => Db.ArticleCategories;
    protected override MasterRuleSet RuleSet => RuleRegistry.ArticleCategory;
    protected override string Word => "article category";
    protected override string AWord => "an article category";

    protected override Result<CodedMasterValues> Validate(ArticleCategoryInput input, bool replace) => SalesValidation.Category(input, replace);

    protected override ArticleCategory Create(CodedMasterValues v, DateTime now, Guid actorKeyId) =>
        ArticleCategory.Create(v.Code, v.Name, v.IsActive, now, actorKeyId);

    protected override void Replace(ArticleCategory record, CodedMasterValues v, DateTime now, Guid actorKeyId) =>
        record.Replace(v.Code, v.Name, v.IsActive, now, actorKeyId);

    protected override ArticleCategoryDto ToDto(ArticleCategory c) =>
        new(c.Id, c.Code, c.Name, c.IsActive, c.CreatedAt, c.UpdatedAt, c.CreatedBy, c.UpdatedBy);

    protected override async Task<string?> UsedByAsync(Guid id, CancellationToken cancellationToken)
    {
        var articles = await Db.ArticleSalesData.CountAsync(s => s.CategoryId == id, cancellationToken);
        return articles > 0 ? $"The article category is the category of {articles} article(s) (see article_sales_data_list with categoryId)." : null;
    }
}

public sealed class TaxRateOperations(IXerpDb db, ITenantContext context, IClock clock, IRules rules)
    : CodedMasterOperations<TaxRate, TaxRateDto, TaxRateInput, TaxRateValues>(db, context, clock, rules)
{
    protected override DbSet<TaxRate> Records => Db.TaxRates;
    protected override MasterRuleSet RuleSet => RuleRegistry.TaxRate;
    protected override string Word => "tax rate";
    protected override string AWord => "a tax rate";

    protected override Result<TaxRateValues> Validate(TaxRateInput input, bool replace) => SalesValidation.TaxRate(input, replace);

    protected override TaxRate Create(TaxRateValues v, DateTime now, Guid actorKeyId) =>
        TaxRate.Create(v.Code, v.Name, v.Rate, v.IsActive, now, actorKeyId);

    protected override void Replace(TaxRate record, TaxRateValues v, DateTime now, Guid actorKeyId) =>
        record.Replace(v.Code, v.Name, v.Rate, v.IsActive, now, actorKeyId);

    protected override TaxRateDto ToDto(TaxRate t) =>
        new(t.Id, t.Code, t.Name, SalesDataRules.Normalize(t.Rate), t.IsActive, t.CreatedAt, t.UpdatedAt, t.CreatedBy, t.UpdatedBy);

    protected override async Task<string?> UsedByAsync(Guid id, CancellationToken cancellationToken)
    {
        var articles = await Db.ArticleTaxRates.CountAsync(l => l.TaxRateId == id, cancellationToken);
        return articles > 0 ? $"The tax rate is a tax rate of {articles} article(s) (see article_sales_data_list with taxRateId)." : null;
    }
}

public sealed class PaymentMethodOperations(IXerpDb db, ITenantContext context, IClock clock, IRules rules)
    : CodedMasterOperations<PaymentMethod, PaymentMethodDto, PaymentMethodInput, PaymentMethodValues>(db, context, clock, rules)
{
    protected override DbSet<PaymentMethod> Records => Db.PaymentMethods;
    protected override MasterRuleSet RuleSet => RuleRegistry.PaymentMethod;
    protected override string Word => "payment method";
    protected override string AWord => "a payment method";

    /// <summary>As the list of every master, with the filter by kind (R3).</summary>
    public Task<Result<PagedResult<PaymentMethodDto>>> ListAsync(ListPaymentMethodsInput input, CancellationToken cancellationToken = default)
    {
        var errors = new ValidationErrors();
        var kind = SalesValidation.Kind(errors, input.Kind, required: false);
        return ListAsync(
            new ListCodedMastersInput(input.Search, input.IsActive, input.Limit, input.Offset), errors,
            kind is { } k ? methods => methods.Where(m => m.Kind == k) : null, cancellationToken);
    }

    protected override Result<PaymentMethodValues> Validate(PaymentMethodInput input, bool replace) => SalesValidation.PaymentMethod(input, replace);

    protected override PaymentMethod Create(PaymentMethodValues v, DateTime now, Guid actorKeyId) =>
        PaymentMethod.Create(v.Code, v.Name, v.Kind, v.IsActive, now, actorKeyId);

    protected override void Replace(PaymentMethod record, PaymentMethodValues v, DateTime now, Guid actorKeyId) =>
        record.Replace(v.Code, v.Name, v.Kind, v.IsActive, now, actorKeyId);

    protected override PaymentMethodDto ToDto(PaymentMethod m) =>
        new(m.Id, m.Code, m.Name, m.Kind.ToName(), m.IsActive, m.CreatedAt, m.UpdatedAt, m.CreatedBy, m.UpdatedBy);

    /// <summary>R5: nothing references a payment method yet, so it is always deletable.</summary>
    protected override Task<string?> UsedByAsync(Guid id, CancellationToken cancellationToken) => Task.FromResult<string?>(null);
}
