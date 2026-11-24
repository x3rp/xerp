using Microsoft.EntityFrameworkCore;
using Xerp.Application.Common;
using Xerp.Application.Ports;
using Xerp.Domain.Common;
using Xerp.Domain.Inventory;
using Xerp.Domain.Invoicing;
using Xerp.Domain.Orders;

namespace Xerp.Application.Agent;

/// <summary>
/// Spec A01, 2.5: one call that finds a record by what a person remembers - a name, a code, a barcode, a number, a tax id,
/// a reference. At most <see cref="PerType"/> hits per type, tenant-scoped like every query, case-insensitive.
/// </summary>
public sealed class SearchOperations(IXerpDb db)
{
    public const int PerType = 10;

    /// <summary>What names a draft that has neither a number nor a reference: the first 8 characters of its id.</summary>
    private static string Draft(Guid id) => "draft-" + id.ToString("N")[..8];

    public static readonly string[] Types = ["article", "partner", "warehouse", "document", "order", "invoice"];

    /// <param name="textField">The name of the text argument in the caller's own terms ("q" over HTTP, "text" in a tool), for the error.</param>
    public async Task<Result<ItemsResult<SearchHitDto>>> SearchAsync(SearchInput input, string textField = "text", CancellationToken ct = default)
    {
        var errors = new ValidationErrors();
        var text = input.Text?.Trim();
        if (string.IsNullOrEmpty(text))
            errors.Add(textField, $"{textField} is required: part of a name, a code, a number, a barcode or a tax id.");
        else if (text.Length > ListRules.MaxSearchLength)
            errors.Add(textField, $"{textField} must be at most {ListRules.MaxSearchLength} characters.");
        else if (TextRules.HasControlCharacters(text))
            errors.Add(textField, $"{textField} must not contain control characters.");
        var wanted = new HashSet<string>(StringComparer.Ordinal);
        foreach (var type in input.Types ?? [])
        {
            var name = type?.Trim().ToLowerInvariant() ?? "";
            if (Array.IndexOf(Types, name) < 0)
                errors.Add("types", $"'{type}' is not a type; use {string.Join(", ", Types)}.");
            else
                wanted.Add(name);
        }
        if (errors.Any)
            return errors.ToError();
        if (wanted.Count == 0)
            wanted.UnionWith(Types);

        var pattern = LikePattern.Contains(text!);
        var hits = new List<SearchHitDto>();

        if (wanted.Contains("article"))
        {
            var byBarcode = db.ArticleBarcodes.Where(b => EF.Functions.Like(DbText.Lower(b.Barcode), DbText.Lower(pattern), LikePattern.EscapeCharacter)).Select(b => b.ArticleId);
            var articles = await db.Articles.AsNoTracking()
                .Where(a => EF.Functions.Like(EF.Property<string>(a, DbNames.CodeLower), DbText.Lower(pattern), LikePattern.EscapeCharacter)
                    || EF.Functions.Like(DbText.Lower(a.Name), DbText.Lower(pattern), LikePattern.EscapeCharacter)
                    || byBarcode.Contains(a.Id))
                .OrderBy(a => a.Code).Take(PerType).Select(a => new { a.Id, a.Code, a.Name, a.IsActive }).ToListAsync(ct);
            hits.AddRange(articles.Select(a => new SearchHitDto("article", a.Id, a.Code, null, a.Name, null, a.IsActive ? "active" : "inactive")));
        }
        if (wanted.Contains("partner"))
        {
            var partners = await db.Partners.AsNoTracking()
                .Where(p => EF.Functions.Like(EF.Property<string>(p, DbNames.CodeLower), DbText.Lower(pattern), LikePattern.EscapeCharacter)
                    || EF.Functions.Like(DbText.Lower(p.Name), DbText.Lower(pattern), LikePattern.EscapeCharacter)
                    || (p.TaxId != null && EF.Functions.Like(DbText.Lower(p.TaxId), DbText.Lower(pattern), LikePattern.EscapeCharacter)))
                .OrderBy(p => p.Code).Take(PerType).Select(p => new { p.Id, p.Code, p.Name, p.IsActive }).ToListAsync(ct);
            hits.AddRange(partners.Select(p => new SearchHitDto("partner", p.Id, p.Code, null, p.Name, null, p.IsActive ? "active" : "inactive")));
        }
        if (wanted.Contains("warehouse"))
        {
            var warehouses = await db.Warehouses.AsNoTracking()
                .Where(w => EF.Functions.Like(EF.Property<string>(w, DbNames.CodeLower), DbText.Lower(pattern), LikePattern.EscapeCharacter)
                    || EF.Functions.Like(DbText.Lower(w.Name), DbText.Lower(pattern), LikePattern.EscapeCharacter))
                .OrderBy(w => w.Code).Take(PerType).Select(w => new { w.Id, w.Code, w.Name, w.IsActive }).ToListAsync(ct);
            hits.AddRange(warehouses.Select(w => new SearchHitDto("warehouse", w.Id, w.Code, null, w.Name, null, w.IsActive ? "active" : "inactive")));
        }
        if (wanted.Contains("document"))
        {
            var documents = await db.StockDocuments.AsNoTracking()
                .Where(d => (d.Number != null && EF.Functions.Like(DbText.Lower(d.Number), DbText.Lower(pattern), LikePattern.EscapeCharacter))
                    || (d.Reference != null && EF.Functions.Like(DbText.Lower(d.Reference), DbText.Lower(pattern), LikePattern.EscapeCharacter)))
                .OrderByDescending(d => d.CreatedAt).Take(PerType).Select(d => new { d.Id, d.Number, d.Reference, d.Type, d.Status, d.DocumentDate }).ToListAsync(ct);
            hits.AddRange(documents.Select(d => new SearchHitDto("document", d.Id, d.Number is null ? d.Reference ?? Draft(d.Id) : null, d.Number, null,
                $"{d.Type.ToName()} {(d.Reference ?? d.DocumentDate.ToString("yyyy-MM-dd"))}", d.Status.ToName())));
        }
        if (wanted.Contains("order"))
        {
            var purchases = await db.PurchaseOrders.AsNoTracking()
                .Where(o => (o.Number != null && EF.Functions.Like(DbText.Lower(o.Number), DbText.Lower(pattern), LikePattern.EscapeCharacter))
                    || (o.Reference != null && EF.Functions.Like(DbText.Lower(o.Reference), DbText.Lower(pattern), LikePattern.EscapeCharacter)))
                .OrderByDescending(o => o.CreatedAt).Take(PerType).Select(o => new { o.Id, o.Number, o.Reference, o.Status, o.CreatedAt, o.OrderDate }).ToListAsync(ct);
            var sales = await db.SalesOrders.AsNoTracking()
                .Where(o => (o.Number != null && EF.Functions.Like(DbText.Lower(o.Number), DbText.Lower(pattern), LikePattern.EscapeCharacter))
                    || (o.Reference != null && EF.Functions.Like(DbText.Lower(o.Reference), DbText.Lower(pattern), LikePattern.EscapeCharacter)))
                .OrderByDescending(o => o.CreatedAt).Take(PerType).Select(o => new { o.Id, o.Number, o.Reference, o.Status, o.CreatedAt, o.OrderDate }).ToListAsync(ct);
            hits.AddRange(purchases.Select(o => (o.CreatedAt, Hit: new SearchHitDto("order", o.Id, o.Number is null ? o.Reference ?? Draft(o.Id) : null, o.Number, null, $"purchase order {o.Reference ?? o.OrderDate.ToString("yyyy-MM-dd")}", o.Status.ToName())))
                .Concat(sales.Select(o => (o.CreatedAt, Hit: new SearchHitDto("order", o.Id, o.Number is null ? o.Reference ?? Draft(o.Id) : null, o.Number, null, $"sales order {o.Reference ?? o.OrderDate.ToString("yyyy-MM-dd")}", o.Status.ToName()))))
                .OrderByDescending(x => x.CreatedAt).Take(PerType).Select(x => x.Hit));
        }
        if (wanted.Contains("invoice"))
        {
            var invoices = await db.Invoices.AsNoTracking()
                .Where(v => (v.Number != null && EF.Functions.Like(DbText.Lower(v.Number), DbText.Lower(pattern), LikePattern.EscapeCharacter))
                    || (v.Reference != null && EF.Functions.Like(DbText.Lower(v.Reference), DbText.Lower(pattern), LikePattern.EscapeCharacter)))
                .OrderByDescending(v => v.CreatedAt).Take(PerType).Select(v => new { v.Id, v.Number, v.Reference, v.Status, v.IssueDate }).ToListAsync(ct);
            hits.AddRange(invoices.Select(v => new SearchHitDto("invoice", v.Id, v.Number is null ? v.Reference ?? Draft(v.Id) : null, v.Number, null,
                $"invoice {v.Reference ?? v.IssueDate.ToString("yyyy-MM-dd")}", v.Status.ToName())));
        }
        return new ItemsResult<SearchHitDto>(hits);
    }
}
