using Microsoft.EntityFrameworkCore;
using Xerp.Application.Common;
using Xerp.Application.Ports;
using Xerp.Domain.Catalog;

namespace Xerp.Application.Production;

/// <summary>An article as production needs it, read through the tenant-filtered context.</summary>
internal sealed record ProductionArticle(Guid Id, string Code, string Name, bool IsActive, ArticleType Type, Guid BaseUnitId);

/// <summary>A reference a request makes to an article: the field that names it, the id, and whether the record already had it.</summary>
internal readonly record struct ArticleReference(string Field, Guid Id, bool AlreadyAssigned);

internal static class ProductionReads
{
    public static Task<Dictionary<Guid, ProductionArticle>> ArticlesAsync(IXerpDb db, IEnumerable<Guid> ids, CancellationToken cancellationToken)
    {
        var list = ids.Distinct().ToList();
        return db.Articles.AsNoTracking()
            .Where(a => list.Contains(a.Id))
            .Select(a => new ProductionArticle(a.Id, a.Code, a.Name, a.IsActive, a.Type, a.BaseUnitId))
            .ToDictionaryAsync(a => a.Id, cancellationToken);
    }

    public static Task<Dictionary<Guid, ReferenceSummary>> UnitsAsync(IXerpDb db, IEnumerable<Guid> ids, CancellationToken cancellationToken)
    {
        var list = ids.Distinct().ToList();
        return db.UnitsOfMeasure.AsNoTracking()
            .Where(u => list.Contains(u.Id))
            .Select(u => new ReferenceSummary(u.Id, u.Code, u.Name))
            .ToDictionaryAsync(u => u.Id, cancellationToken);
    }

    /// <summary>
    /// The reference rule (ADR-0008) for a set of article references, each kind of fault answering alone and in
    /// this order: unknown (REFERENCE_NOT_FOUND), inactive and newly assigned (REFERENCE_INACTIVE), a service
    /// (ARTICLE_NOT_STOCKED: only stock articles are produced and consumed). Every reference with the fault is
    /// named. Null when all are acceptable.
    /// </summary>
    /// <param name="inactiveAlwaysRefused">At release and completion an inactive article is refused even where it is already assigned.</param>
    public static AppError? Check(
        IReadOnlyList<ArticleReference> references, IReadOnlyDictionary<Guid, ProductionArticle> articles, bool inactiveAlwaysRefused = false)
    {
        var missing = references.Where(r => !articles.ContainsKey(r.Id)).ToList();
        if (missing.Count > 0)
            return new AppError(ErrorCodes.ReferenceNotFound, "A referenced article does not exist.",
                Errors(missing, r => $"No record with id '{r.Id}' exists."));
        var inactive = references.Where(r => !articles[r.Id].IsActive && (inactiveAlwaysRefused || !r.AlreadyAssigned)).ToList();
        if (inactive.Count > 0)
            return new AppError(ErrorCodes.ReferenceInactive,
                inactiveAlwaysRefused
                    ? "A referenced article is inactive: reactivate it, or change the draft, before production is booked."
                    : "A referenced article is inactive and cannot be newly assigned.",
                Errors(inactive, r => $"The record with id '{r.Id}' is inactive."));
        var services = references.Where(r => articles[r.Id].Type != ArticleType.Stock).ToList();
        if (services.Count > 0)
            return new AppError(ErrorCodes.ArticleNotStocked,
                "Only articles of type \"stock\" can be produced or consumed; a service has no stock.",
                Errors(services, r => $"The article with id '{r.Id}' is a service."));
        return null;
    }

    private static Dictionary<string, string[]> Errors(IEnumerable<ArticleReference> references, Func<ArticleReference, string> message)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        foreach (var r in references)
            errors.TryAdd(r.Field, [message(r)]);
        return errors;
    }
}
