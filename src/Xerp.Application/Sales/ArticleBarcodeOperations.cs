using Microsoft.EntityFrameworkCore;
using Xerp.Application.Common;
using Xerp.Application.Ports;
using Xerp.Domain.Rules;
using Xerp.Domain.Sales;

namespace Xerp.Application.Sales;

/// <summary>
/// The barcodes of articles (spec P01, 4.B). One method = one HTTP endpoint = one MCP tool.
/// <see cref="IXerpDb"/> is already filtered to the current tenant.
/// <para>
/// Whether a barcode may belong to several articles is a tenant's rule and no unique index can hold it, so a
/// create runs its lookup and its write under <see cref="IXerpDb.SerializedPerTenantAsync{T}"/> (R18). That an
/// article lists a barcode once is an invariant, and a unique index is its authority.
/// </para>
/// </summary>
public sealed class ArticleBarcodeOperations(IXerpDb db, ITenantContext context, IClock clock, IRules rules)
{
    private const string ArticleField = "articleId";
    private const string NotFoundDetail = "Article barcode not found.";

    /// <summary>The lookup by barcode (R19): exact after trimming; ordered by barcode (ordinal), then article code.</summary>
    public async Task<Result<PagedResult<ArticleBarcodeDto>>> ListAsync(ListArticleBarcodesInput input, CancellationToken cancellationToken = default)
    {
        var validated = SalesValidation.BarcodeList(input);
        if (!validated.IsSuccess)
            return validated.Error;
        var query = validated.Value;

        var barcodes = db.ArticleBarcodes.AsNoTracking();
        if (query.Barcode is { } barcode)
            barcodes = barcodes.Where(b => b.Barcode == barcode);
        if (query.ArticleId is { } articleId)
            barcodes = barcodes.Where(b => b.ArticleId == articleId);

        var total = await barcodes.CountAsync(cancellationToken);
        var items = await (
            from b in barcodes
            join a in db.Articles.AsNoTracking() on b.ArticleId equals a.Id
            orderby b.Barcode, EF.Property<string>(a, DbNames.CodeLower), b.Id
            select new ArticleBarcodeDto(b.Id, b.Barcode, new ReferenceSummary(a.Id, a.Code, a.Name), b.CreatedAt, b.CreatedBy))
            .Skip(query.Offset)
            .Take(query.Limit)
            .ToListAsync(cancellationToken);
        return new PagedResult<ArticleBarcodeDto>(items, total, query.Limit, query.Offset);
    }

    public async Task<Result<ArticleBarcodeDto>> GetAsync(Guid id, CancellationToken cancellationToken = default) =>
        await ReadAsync(id, cancellationToken) is { } barcode ? barcode : NotFound();

    /// <summary>
    /// Order of checks (R16): validation -> the article -> the article already has the barcode (an invariant)
    /// -> with <c>article.barcodeUnique</c> true, another article has it.
    /// </summary>
    public async Task<Result<ArticleBarcodeDto>> CreateAsync(CreateArticleBarcodeInput input, CancellationToken cancellationToken = default)
    {
        var tenantRules = await rules.ReadAsync(cancellationToken);
        var validated = SalesValidation.Barcode(input);
        if (!validated.IsSuccess)
            return validated.Error;
        var (articleId, barcode) = validated.Value;

        try
        {
            return await db.SerializedPerTenantAsync<Result<ArticleBarcodeDto>>(async ct =>
            {
                if (await ReferenceCheck.ValidateAsync(
                        db.Articles.Where(a => a.Id == articleId).Select(a => a.IsActive), ArticleField, articleId, alreadyAssigned: false, ct) is { } articleError)
                    return articleError;

                var sameBarcode = db.ArticleBarcodes.Where(b => b.Barcode == barcode);
                if (SalesDataRules.ArticleAlreadyHasBarcode(await sameBarcode.AnyAsync(b => b.ArticleId == articleId, ct)))
                    return AppError.BarcodeTaken(barcode, rule: null);
                // With the rule false nothing is looked up: several articles may have the barcode.
                var unique = tenantRules[RuleRegistry.ArticleBarcodeUnique];
                if (unique && SalesDataRules.BarcodeTakenByAnotherArticle(unique, await sameBarcode.AnyAsync(b => b.ArticleId != articleId, ct)))
                    return AppError.BarcodeTaken(barcode, RuleRegistry.ArticleBarcodeUnique);

                var created = ArticleBarcode.Create(articleId, barcode, clock.UtcNow, ActorKeyId());
                db.ArticleBarcodes.Add(created);
                await db.SaveChangesAsync(ct);
                return await ReadAsync(created.Id, ct)
                    ?? throw new InvalidOperationException("The barcode that was just saved cannot be read.");
            }, cancellationToken);
        }
        catch (UniqueConstraintViolationException ex) when (ex.EntityType == typeof(ArticleBarcode))
        {
            return AppError.BarcodeTaken(barcode, rule: null);
        }
        catch (ForeignKeyViolationException ex) when (!ex.BlockedDelete)
        {
            // The article was deleted between the check and the write: the foreign key is the authority.
            return AppError.ReferenceNotFound(ArticleField, articleId);
        }
    }

    public async Task<Result<SalesRecordDeleted>> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var barcode = await db.ArticleBarcodes.SingleOrDefaultAsync(b => b.Id == id, cancellationToken);
        if (barcode is null)
            return NotFound();
        db.ArticleBarcodes.Remove(barcode);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return NotFound();
        }
        return new SalesRecordDeleted();
    }

    /// <summary>As the overload with a <see cref="Guid"/>; an id that is not a UUID names no record (spec 003, R15).</summary>
    public Task<Result<ArticleBarcodeDto>> GetAsync(RecordIdInput input, CancellationToken cancellationToken = default) =>
        RecordAddress.Id(input.Id, NotFoundDetail, out var id) is { } error
            ? Task.FromResult<Result<ArticleBarcodeDto>>(error)
            : GetAsync(id, cancellationToken);

    /// <summary>As the overload with a <see cref="Guid"/>; an id that is not a UUID names no record (spec 003, R15).</summary>
    public Task<Result<SalesRecordDeleted>> DeleteAsync(RecordIdInput input, CancellationToken cancellationToken = default) =>
        RecordAddress.Id(input.Id, NotFoundDetail, out var id) is { } error
            ? Task.FromResult<Result<SalesRecordDeleted>>(error)
            : DeleteAsync(id, cancellationToken);

    private Task<ArticleBarcodeDto?> ReadAsync(Guid id, CancellationToken cancellationToken) =>
        (from b in db.ArticleBarcodes.AsNoTracking().Where(b => b.Id == id)
         join a in db.Articles.AsNoTracking() on b.ArticleId equals a.Id
         select new ArticleBarcodeDto(b.Id, b.Barcode, new ReferenceSummary(a.Id, a.Code, a.Name), b.CreatedAt, b.CreatedBy))
        .SingleOrDefaultAsync(cancellationToken);

    private Guid ActorKeyId() =>
        context.ApiKeyId ?? throw new InvalidOperationException("A barcode can only be written by a tenant API key.");

    private static AppError NotFound() => AppError.NotFound(NotFoundDetail);
}
