using Microsoft.EntityFrameworkCore;
using Xerp.Application.Common;
using Xerp.Application.Ports;
using Xerp.Domain.Production;

namespace Xerp.Application.Production;

/// <summary>
/// The bill of materials of an article (spec W01, 4.1): read, set as a whole, delete. One method = one HTTP
/// endpoint = one MCP tool. Writes take the tenant's lock, which article writes take too, so an article cannot
/// change type or be deleted between the check and the save.
/// </summary>
public sealed class BomOperations(IXerpDb db, ITenantContext context, IClock clock)
{
    private const string NotFoundDetail = "Article not found.";

    public async Task<Result<BomDto>> GetAsync(Guid articleId, CancellationToken cancellationToken = default)
    {
        if (!await db.Articles.AnyAsync(a => a.Id == articleId, cancellationToken))
            return AppError.NotFound(NotFoundDetail);
        return await ToDtoAsync(articleId, cancellationToken);
    }

    /// <summary>
    /// Replaces the bill. Order of checks: form -> article exists -> the product and every component are known,
    /// newly assigned components active, all of type stock -> no component is the product itself.
    /// A component may appear on more than one line. The product may be inactive: its bill stays editable.
    /// </summary>
    public async Task<Result<BomDto>> SetAsync(Guid articleId, SetBomInput input, CancellationToken cancellationToken = default)
    {
        var validated = ProductionValidation.Bom(input.Components);
        if (!validated.IsSuccess)
            return validated.Error;
        var components = validated.Value.Components;

        return await db.SerializedPerTenantAsync<Result<BomDto>>(async ct =>
        {
            var stored = await db.BomComponents.Where(c => c.ArticleId == articleId).ToListAsync(ct);
            var articles = await ProductionReads.ArticlesAsync(db, components.Select(c => c.ArticleId).Append(articleId), ct);
            if (!articles.TryGetValue(articleId, out var product))
                return AppError.NotFound(NotFoundDetail);
            if (product.Type != Domain.Catalog.ArticleType.Stock)
                return new AppError(ErrorCodes.ArticleNotStocked,
                    "Only an article of type \"stock\" can have a bill of materials: what is produced goes into stock.",
                    new Dictionary<string, string[]> { ["articleId"] = [$"The article with id '{articleId}' is a service."] });

            var storedComponents = stored.Select(c => c.ComponentId).ToHashSet();
            var references = components
                .Select((c, i) => new ArticleReference(ProductionValidation.Key("components", i, "articleId"), c.ArticleId, storedComponents.Contains(c.ArticleId)))
                .ToList();
            if (ProductionReads.Check(references, articles) is { } referenceError)
                return referenceError;
            var self = components.Select((c, i) => (c, i)).Where(x => x.c.ArticleId == articleId).ToList();
            if (self.Count > 0)
                return AppError.Validation(self.ToDictionary(
                    x => ProductionValidation.Key("components", x.i, "articleId"),
                    _ => new[] { "An article cannot be a component of itself." }));

            db.BomComponents.RemoveRange(stored);
            // The rows are keyed by line number: the removal is written before the new rows by one save.
            await db.SaveChangesAsync(ct);
            var now = clock.UtcNow;
            db.BomComponents.AddRange(components.Select((c, i) => new BomComponent(articleId, i + 1, c.ArticleId, c.Quantity, now, ActorKeyId())));
            await db.SaveChangesAsync(ct);
            return await ToDtoAsync(articleId, ct);
        }, cancellationToken);
    }

    /// <summary>Deletes the bill; work orders already created keep their lines. Deleting a bill an article does not have is NOT_FOUND.</summary>
    public Task<Result<BomDeleted>> DeleteAsync(Guid articleId, CancellationToken cancellationToken = default) =>
        db.SerializedPerTenantAsync<Result<BomDeleted>>(async ct =>
        {
            if (!await db.Articles.AnyAsync(a => a.Id == articleId, ct))
                return AppError.NotFound(NotFoundDetail);
            var stored = await db.BomComponents.Where(c => c.ArticleId == articleId).ToListAsync(ct);
            if (stored.Count == 0)
                return AppError.NotFound("The article has no bill of materials.");
            db.BomComponents.RemoveRange(stored);
            await db.SaveChangesAsync(ct);
            return new BomDeleted();
        }, cancellationToken);

    public Task<Result<BomDto>> GetAsync(BomAddressInput input, CancellationToken cancellationToken = default) =>
        RecordAddress.Id(input.ArticleId, NotFoundDetail, out var id, "articleId") is { } error
            ? Task.FromResult<Result<BomDto>>(error)
            : GetAsync(id, cancellationToken);

    public Task<Result<BomDto>> SetAsync(SetBomToolInput input, CancellationToken cancellationToken = default) =>
        RecordAddress.Id(input.ArticleId, NotFoundDetail, out var id, "articleId") is { } error
            ? Task.FromResult<Result<BomDto>>(error)
            : SetAsync(id, new SetBomInput(input.Components), cancellationToken);

    public Task<Result<BomDeleted>> DeleteAsync(BomAddressInput input, CancellationToken cancellationToken = default) =>
        RecordAddress.Id(input.ArticleId, NotFoundDetail, out var id, "articleId") is { } error
            ? Task.FromResult<Result<BomDeleted>>(error)
            : DeleteAsync(id, cancellationToken);

    private async Task<BomDto> ToDtoAsync(Guid articleId, CancellationToken cancellationToken)
    {
        var lines = await db.BomComponents.AsNoTracking().Where(c => c.ArticleId == articleId).OrderBy(c => c.LineNo).ToListAsync(cancellationToken);
        var articles = await ProductionReads.ArticlesAsync(db, lines.Select(l => l.ComponentId).Append(articleId), cancellationToken);
        var units = await ProductionReads.UnitsAsync(db, articles.Values.Select(a => a.BaseUnitId), cancellationToken);
        var product = articles[articleId];
        var last = lines.MaxBy(l => l.UpdatedAt);
        return new BomDto(
            Summary(product), units[product.BaseUnitId],
            lines.Select(l => new BomComponentDto(l.LineNo, Summary(articles[l.ComponentId]), units[articles[l.ComponentId].BaseUnitId],
                Domain.Inventory.QuantityRules.Normalize(l.Quantity))).ToList(),
            last?.UpdatedAt, last?.UpdatedBy);
    }

    private static ReferenceSummary Summary(ProductionArticle a) => new(a.Id, a.Code, a.Name);

    private Guid ActorKeyId() =>
        context.ApiKeyId ?? throw new InvalidOperationException("A bill of materials can only be written by a tenant API key.");
}
