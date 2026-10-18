using Microsoft.EntityFrameworkCore;
using Xerp.Application.Common;
using Xerp.Application.Ports;
using Xerp.Domain.Sales;

namespace Xerp.Application.Sales;

/// <summary>
/// The sales data of an article (spec P01, 4.B): a sub-resource of the article, as its conversions are. One
/// method = one HTTP endpoint = one MCP tool. <see cref="IXerpDb"/> is already filtered to the current tenant.
/// <para>
/// Set replaces a small aggregate - the row and its tax-rate links - inside
/// <see cref="IXerpDb.SerializedPerTenantAsync{T}"/>, the lock the delete of an article, of a category and of
/// a tax rate hold too, so none of them can pass between the checks and the write (S2).
/// </para>
/// </summary>
public sealed class ArticleSalesDataOperations(IXerpDb db, ITenantContext context, IClock clock, IRules rules)
{
    private const string CategoryField = "categoryId";
    private const string ArticleArgument = "articleId";
    private const string ArticleNotFoundDetail = "Article not found.";

    /// <summary>Also for an article never given any: everything empty (R7).</summary>
    public async Task<Result<ArticleSalesDataDto>> GetAsync(Guid articleId, CancellationToken cancellationToken = default)
    {
        var article = await db.Articles.AsNoTracking()
            .Where(a => a.Id == articleId)
            .Select(a => new ReferenceSummary(a.Id, a.Code, a.Name))
            .SingleOrDefaultAsync(cancellationToken);
        return article is null ? ArticleNotFound() : (await ReadAsync([article], cancellationToken))[0];
    }

    /// <summary>
    /// Replaces all three members (R8). Order of checks (R11): validation, where the tenant's three required
    /// rules judge (R10) -> the article exists -> the category -> each tax rate in order; the first failing
    /// reference answers alone. An inactive category or tax rate the article already has is kept (ADR-0008).
    /// </summary>
    public async Task<Result<ArticleSalesDataDto>> SetAsync(Guid articleId, SetArticleSalesDataInput input, CancellationToken cancellationToken = default)
    {
        var tenantRules = await rules.ReadAsync(cancellationToken);
        var validated = SalesValidation.SalesData(input, tenantRules);
        if (!validated.IsSuccess)
            return validated.Error;
        var values = validated.Value;

        try
        {
            return await db.SerializedPerTenantAsync<Result<ArticleSalesDataDto>>(async ct =>
            {
                // R7: active or inactive - it is the article's own data, not a new reference to it.
                if (!await db.Articles.AnyAsync(a => a.Id == articleId, ct))
                    return ArticleNotFound();
                var data = await db.ArticleSalesData.SingleOrDefaultAsync(s => s.ArticleId == articleId, ct);
                var links = await db.ArticleTaxRates.Where(l => l.ArticleId == articleId).ToListAsync(ct);

                if (values.CategoryId is { } categoryId
                    && await ReferenceCheck.ValidateAsync(
                        db.ArticleCategories.Where(c => c.Id == categoryId).Select(c => c.IsActive),
                        CategoryField, categoryId, alreadyAssigned: data?.CategoryId == categoryId, ct) is { } categoryError)
                    return categoryError;
                for (var i = 0; i < values.TaxRateIds.Count; i++)
                {
                    var taxRateId = values.TaxRateIds[i];
                    if (await ReferenceCheck.ValidateAsync(
                            db.TaxRates.Where(t => t.Id == taxRateId).Select(t => t.IsActive),
                            TaxRateField(i), taxRateId, alreadyAssigned: links.Any(l => l.TaxRateId == taxRateId), ct) is { } taxRateError)
                        return taxRateError;
                }

                // R12: attributed on the sales data; the article's own updatedAt does not change.
                if (data is null)
                    db.ArticleSalesData.Add(ArticleSalesData.Create(articleId, values.CategoryId, values.SalesPrice, clock.UtcNow, ActorKeyId()));
                else
                    data.Set(values.CategoryId, values.SalesPrice, clock.UtcNow, ActorKeyId());
                db.ArticleTaxRates.RemoveRange(links.Where(l => !values.TaxRateIds.Contains(l.TaxRateId)));
                db.ArticleTaxRates.AddRange(values.TaxRateIds
                    .Where(id => links.All(l => l.TaxRateId != id))
                    .Select(id => ArticleTaxRate.Create(articleId, id)));
                await db.SaveChangesAsync(ct);

                var article = await db.Articles.AsNoTracking()
                    .Where(a => a.Id == articleId)
                    .Select(a => new ReferenceSummary(a.Id, a.Code, a.Name))
                    .SingleAsync(ct);
                return (await ReadAsync([article], ct))[0];
            }, cancellationToken);
        }
        catch (ForeignKeyViolationException ex) when (!ex.BlockedDelete)
        {
            // Something named here was deleted between the check and the write: the foreign key is the authority (S2).
            if (!await db.Articles.AnyAsync(a => a.Id == articleId, cancellationToken))
                return ArticleNotFound();
            if (values.CategoryId is { } categoryId && !await db.ArticleCategories.AnyAsync(c => c.Id == categoryId, cancellationToken))
                return AppError.ReferenceNotFound(CategoryField, categoryId);
            for (var i = 0; i < values.TaxRateIds.Count; i++)
            {
                var taxRateId = values.TaxRateIds[i];
                if (!await db.TaxRates.AnyAsync(t => t.Id == taxRateId, cancellationToken))
                    return AppError.ReferenceNotFound(TaxRateField(i), taxRateId);
            }
            throw;
        }
    }

    /// <summary>One item per article of the tenant, also articles never set (R13), ordered by article code. Filtered and paged in the database.</summary>
    public async Task<Result<PagedResult<ArticleSalesDataDto>>> ListAsync(ListArticleSalesDataInput input, CancellationToken cancellationToken = default)
    {
        var validated = SalesValidation.SalesDataList(input);
        if (!validated.IsSuccess)
            return validated.Error;
        var query = validated.Value;

        var articles = db.Articles.AsNoTracking();
        if (query.IsActive is { } isActive)
            articles = articles.Where(a => a.IsActive == isActive);
        if (query.Search is { } search)
        {
            var pattern = LikePattern.Contains(search);
            articles = articles.Where(a =>
                EF.Functions.Like(EF.Property<string>(a, DbNames.CodeLower), DbText.Lower(pattern), LikePattern.EscapeCharacter) ||
                EF.Functions.Like(DbText.Lower(a.Name), DbText.Lower(pattern), LikePattern.EscapeCharacter));
        }
        // An id that names nothing in the tenant matches no article: an empty list, not an error.
        if (query.CategoryId is { } categoryId)
            articles = articles.Where(a => db.ArticleSalesData.Any(s => s.ArticleId == a.Id && s.CategoryId == categoryId));
        if (query.TaxRateId is { } taxRateId)
            articles = articles.Where(a => db.ArticleTaxRates.Any(l => l.ArticleId == a.Id && l.TaxRateId == taxRateId));
        if (query.Priced is { } priced)
            articles = articles.Where(a => db.ArticleSalesData.Any(s => s.ArticleId == a.Id && s.SalesPrice != null) == priced);

        var total = await articles.CountAsync(cancellationToken);
        var page = await articles
            .OrderBy(a => EF.Property<string>(a, DbNames.CodeLower))
            .ThenBy(a => a.Id)
            .Skip(query.Offset)
            .Take(query.Limit)
            .Select(a => new ReferenceSummary(a.Id, a.Code, a.Name))
            .ToListAsync(cancellationToken);
        return new PagedResult<ArticleSalesDataDto>(await ReadAsync(page, cancellationToken), total, query.Limit, query.Offset);
    }

    /// <summary>As the overload with a <see cref="Guid"/>; an <c>articleId</c> that is not a UUID names no record (spec 003, R15).</summary>
    public Task<Result<ArticleSalesDataDto>> GetAsync(ArticleSalesDataAddressInput address, CancellationToken cancellationToken = default) =>
        RecordAddress.Id(address.ArticleId, ArticleNotFoundDetail, out var articleId, ArticleArgument) is { } error
            ? Task.FromResult<Result<ArticleSalesDataDto>>(error)
            : GetAsync(articleId, cancellationToken);

    /// <summary>As the overload with a <see cref="Guid"/>; an <c>articleId</c> that is not a UUID names no record (spec 003, R15).</summary>
    public Task<Result<ArticleSalesDataDto>> SetAsync(SetArticleSalesDataArguments arguments, CancellationToken cancellationToken = default) =>
        RecordAddress.Id(arguments.ArticleId, ArticleNotFoundDetail, out var articleId, ArticleArgument) is { } error
            ? Task.FromResult<Result<ArticleSalesDataDto>>(error)
            : SetAsync(articleId, arguments, cancellationToken);

    /// <summary>
    /// The sales data of the given articles, in their order, with the current codes and names of what they
    /// reference (R15): three queries whatever the number of articles.
    /// </summary>
    private async Task<List<ArticleSalesDataDto>> ReadAsync(IReadOnlyList<ReferenceSummary> articles, CancellationToken cancellationToken)
    {
        var ids = articles.Select(a => a.Id).ToList();
        var rows = await (
            from s in db.ArticleSalesData.AsNoTracking().Where(s => ids.Contains(s.ArticleId))
            join c in db.ArticleCategories.AsNoTracking() on s.CategoryId equals (Guid?)c.Id into categories
            from c in categories.DefaultIfEmpty()
            select new
            {
                Data = s,
                Category = c == null ? null : new ReferenceSummary(c.Id, c.Code, c.Name),
            }).ToDictionaryAsync(r => r.Data.ArticleId, cancellationToken);
        var taxRates = (await (
            from l in db.ArticleTaxRates.AsNoTracking().Where(l => ids.Contains(l.ArticleId))
            join t in db.TaxRates.AsNoTracking() on l.TaxRateId equals t.Id
            orderby EF.Property<string>(t, DbNames.CodeLower), t.Id
            select new { l.ArticleId, t.Id, t.Code, t.Name, t.Rate }).ToListAsync(cancellationToken))
            .ToLookup(t => t.ArticleId, t => new TaxRateSummary(t.Id, t.Code, t.Name, SalesDataRules.Normalize(t.Rate)));

        return articles.Select(article =>
        {
            var row = rows.GetValueOrDefault(article.Id);
            return new ArticleSalesDataDto(
                article, row?.Category, taxRates[article.Id].ToList(),
                row?.Data.SalesPrice is { } price ? SalesDataRules.Normalize(price) : null,
                row?.Data.UpdatedAt, row?.Data.UpdatedBy);
        }).ToList();
    }

    private static string TaxRateField(int index) => $"taxRateIds[{index}]";

    private Guid ActorKeyId() =>
        context.ApiKeyId ?? throw new InvalidOperationException("Sales data can only be written by a tenant API key.");

    private static AppError ArticleNotFound() => AppError.NotFound(ArticleNotFoundDetail);
}
