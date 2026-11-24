using Microsoft.EntityFrameworkCore;
using Xerp.Application.Ports;
using Xerp.Domain.Inventory;
using Xerp.Domain.Orders;
using Xerp.Domain.Production;

namespace Xerp.Application.Reports;

internal static class StockQueries
{
    /// <summary>The article ids of a category (through the sales data), or null when no category is asked for.</summary>
    public static IQueryable<Guid>? OfCategory(IXerpDb db, Guid? categoryId) =>
        categoryId is { } id ? db.ArticleSalesData.Where(s => s.CategoryId == id).Select(s => s.ArticleId) : null;
}

public sealed class StockOnHandReport : ReportBase
{
    public override string Key => "stock-on-hand";
    public override string Name => "Stock on hand";
    public override string Description =>
        "How much of each stock article is in each warehouse right now (quantity in the article's base unit; empty positions are left out). Answers 'what do we have, and where'. "
        + "Reserved quantities of sales orders are not subtracted: see stock_on_hand_list for the available quantity.";
    public override IReadOnlyList<ReportParameter> Parameters =>
        [ReportParameter.WarehouseId(), ReportParameter.ArticleId(), ReportParameter.CategoryId()];
    public override IReadOnlyList<ReportColumn> Columns =>
    [
        new("articleCode", "Article code", "text"), new("articleName", "Article", "text"), new("warehouseCode", "Warehouse code", "text"),
        new("warehouseName", "Warehouse", "text"), new("quantity", "Quantity", "quantity", true),
    ];

    public override async Task<IReadOnlyList<Dictionary<string, object?>>> RowsAsync(IXerpDb db, ReportArgs args, CancellationToken ct)
    {
        var balances = from b in db.StockBalances.AsNoTracking()
                       join a in db.Articles.AsNoTracking() on b.ArticleId equals a.Id
                       join w in db.Warehouses.AsNoTracking() on b.WarehouseId equals w.Id
                       where b.Quantity != 0 && (args.WarehouseId == null || b.WarehouseId == args.WarehouseId)
                           && (args.ArticleId == null || b.ArticleId == args.ArticleId)
                       select new { ArticleCode = a.Code, ArticleName = a.Name, b.ArticleId, WarehouseCode = w.Code, WarehouseName = w.Name, b.Quantity };
        if (StockQueries.OfCategory(db, args.CategoryId) is { } inCategory)
            balances = balances.Where(b => inCategory.Contains(b.ArticleId));
        var rows = await balances.ToListAsync(ct);
        return rows.OrderBy(r => r.ArticleCode, StringComparer.Ordinal).ThenBy(r => r.WarehouseCode, StringComparer.Ordinal)
            .Select(r => Row(("articleCode", r.ArticleCode), ("articleName", r.ArticleName), ("warehouseCode", r.WarehouseCode),
                ("warehouseName", r.WarehouseName), ("quantity", r.Quantity)))
            .ToList();
    }
}

public sealed class StockMovementsReport : ReportBase
{
    public override string Key => "stock-movements";
    public override string Name => "Stock movements";
    public override string Description =>
        "Per article: the quantity at the start of the period (opening), what came in, what went out and what is left at the end (closing), from the stock ledger by document date. "
        + "Quantities are in the article's base unit. A transfer between warehouses counts as out and in unless you filter by warehouse. Closing for a period that ends today equals stock-on-hand.";
    public override IReadOnlyList<ReportParameter> Parameters =>
        [ReportParameter.From, ReportParameter.To, ReportParameter.WarehouseId(), ReportParameter.ArticleId(), ReportParameter.CategoryId()];
    public override IReadOnlyList<ReportColumn> Columns =>
    [
        new("articleCode", "Article code", "text"), new("articleName", "Article", "text"), new("openingQuantity", "Opening", "quantity", true),
        new("inQuantity", "In", "quantity", true), new("outQuantity", "Out", "quantity", true), new("closingQuantity", "Closing", "quantity", true),
    ];

    public override async Task<IReadOnlyList<Dictionary<string, object?>>> RowsAsync(IXerpDb db, ReportArgs args, CancellationToken ct)
    {
        var entries = db.StockLedgerEntries.AsNoTracking()
            .Where(e => e.DocumentDate <= args.To)
            .Where(e => args.WarehouseId == null || e.WarehouseId == args.WarehouseId)
            .Where(e => args.ArticleId == null || e.ArticleId == args.ArticleId);
        if (StockQueries.OfCategory(db, args.CategoryId) is { } inCategory)
            entries = entries.Where(e => inCategory.Contains(e.ArticleId));
        var from = args.From;
        var groups = await entries.GroupBy(e => e.ArticleId)
            .Select(g => new
            {
                ArticleId = g.Key,
                Opening = g.Sum(e => e.DocumentDate < from ? e.Quantity : 0m),
                In = g.Sum(e => e.DocumentDate >= from && e.Quantity > 0 ? e.Quantity : 0m),
                Out = g.Sum(e => e.DocumentDate >= from && e.Quantity < 0 ? -e.Quantity : 0m),
            })
            .ToListAsync(ct);
        var ids = groups.Select(g => g.ArticleId).ToList();
        var articles = await db.Articles.AsNoTracking().Where(a => ids.Contains(a.Id)).Select(a => new { a.Id, a.Code, a.Name }).ToDictionaryAsync(a => a.Id, ct);
        return groups.Where(g => g.Opening != 0 || g.In != 0 || g.Out != 0)
            .OrderBy(g => articles[g.ArticleId].Code, StringComparer.Ordinal)
            .Select(g => Row(("articleCode", articles[g.ArticleId].Code), ("articleName", articles[g.ArticleId].Name), ("openingQuantity", g.Opening),
                ("inQuantity", g.In), ("outQuantity", g.Out), ("closingQuantity", g.Opening + g.In - g.Out)))
            .ToList();
    }
}

public sealed class SlowMoversReport : ReportBase
{
    public override string Key => "slow-movers";
    public override string Name => "Slow movers";
    public override string Description =>
        "Articles that are in stock but have not gone out for a while: nothing issued (sold, delivered, used in production) in the last `days` days (default 90), or never. "
        + "Shows the quantity on hand in all warehouses, the date of the last issue and the days since. Use it to find dead stock. No totals.";
    public override IReadOnlyList<ReportParameter> Parameters =>
        [new("days", "integer", false, 90, null, "Report articles with no issue in the last N days (1 to 100000). Default 90."), ReportParameter.CategoryId()];
    public override IReadOnlyList<ReportColumn> Columns =>
    [
        new("articleCode", "Article code", "text"), new("articleName", "Article", "text"), new("quantity", "On hand", "quantity"),
        new("lastOutDate", "Last issue", "date"), new("daysSinceLastOut", "Days since last issue", "number"),
    ];
    public override bool HasTotals => false;

    public override async Task<IReadOnlyList<Dictionary<string, object?>>> RowsAsync(IXerpDb db, ReportArgs args, CancellationToken ct)
    {
        var onHand = db.StockBalances.AsNoTracking();
        if (StockQueries.OfCategory(db, args.CategoryId) is { } inCategory)
            onHand = onHand.Where(b => inCategory.Contains(b.ArticleId));
        var stock = await onHand.GroupBy(b => b.ArticleId)
            .Select(g => new { ArticleId = g.Key, Quantity = g.Sum(b => b.Quantity) })
            .Where(g => g.Quantity > 0)
            .ToListAsync(ct);
        var ids = stock.Select(s => s.ArticleId).ToList();
        var lastOut = await (from e in db.StockLedgerEntries.AsNoTracking()
                             join d in db.StockDocuments.AsNoTracking() on e.DocumentId equals d.Id
                             where e.Quantity < 0 && d.Type == StockDocumentType.Issue && ids.Contains(e.ArticleId)
                             group e by e.ArticleId into g
                             select new { ArticleId = g.Key, Date = g.Max(e => e.DocumentDate) })
            .ToDictionaryAsync(x => x.ArticleId, x => x.Date, ct);
        var articles = await db.Articles.AsNoTracking().Where(a => ids.Contains(a.Id)).Select(a => new { a.Id, a.Code, a.Name }).ToDictionaryAsync(a => a.Id, ct);
        var limit = args.Today.AddDays(-args.Days);
        return stock.Where(s => !lastOut.TryGetValue(s.ArticleId, out var last) || last < limit)
            .OrderBy(s => articles[s.ArticleId].Code, StringComparer.Ordinal)
            .Select(s =>
            {
                DateOnly? last = lastOut.TryGetValue(s.ArticleId, out var date) ? date : null;
                return Row(("articleCode", articles[s.ArticleId].Code), ("articleName", articles[s.ArticleId].Name), ("quantity", s.Quantity),
                    ("lastOutDate", Date(last)), ("daysSinceLastOut", last is { } l ? args.Today.DayNumber - l.DayNumber : (int?)null));
            })
            .ToList();
    }
}

public sealed class OpenSalesOrdersReport : ReportBase
{
    public override string Key => "open-sales-orders";
    public override string Name => "Open sales orders";
    public override string Description =>
        "What has been promised to customers and not yet delivered: one row per confirmed sales order line with a quantity still outstanding (ordered, delivered, outstanding; base units). "
        + "Drafts and closed orders are not listed. Use it to plan deliveries and purchases.";
    public override IReadOnlyList<ReportParameter> Parameters => [ReportParameter.PartnerId("Only this customer"), ReportParameter.ArticleId(), ReportParameter.WarehouseId()];
    public override IReadOnlyList<ReportColumn> Columns =>
    [
        new("number", "Order", "text"), new("orderDate", "Order date", "date"), new("customerCode", "Customer code", "text"), new("customerName", "Customer", "text"),
        new("articleCode", "Article code", "text"), new("articleName", "Article", "text"), new("orderedQuantity", "Ordered", "quantity", true),
        new("deliveredQuantity", "Delivered", "quantity", true), new("outstandingQuantity", "Outstanding", "quantity", true),
    ];

    public override async Task<IReadOnlyList<Dictionary<string, object?>>> RowsAsync(IXerpDb db, ReportArgs args, CancellationToken ct)
    {
        var rows = await (from l in db.SalesOrderLines.AsNoTracking()
                          join o in db.SalesOrders.AsNoTracking() on l.OrderId equals o.Id
                          join p in db.Partners.AsNoTracking() on o.PartnerId equals p.Id into partner
                          from p in partner.DefaultIfEmpty()
                          join a in db.Articles.AsNoTracking() on l.ArticleId equals a.Id
                          where o.Status == OrderStatus.Confirmed && (l.BaseQuantity ?? l.Quantity) > l.FulfilledBaseQuantity
                              && (args.PartnerId == null || o.PartnerId == args.PartnerId) && (args.ArticleId == null || l.ArticleId == args.ArticleId)
                              && (args.WarehouseId == null || o.WarehouseId == args.WarehouseId)
                          select new
                          {
                              o.Number, o.OrderDate, l.LineNo, PartnerCode = p == null ? "" : p.Code, PartnerName = p == null ? "(no customer)" : p.Name,
                              ArticleCode = a.Code, ArticleName = a.Name, Ordered = l.BaseQuantity ?? l.Quantity, Done = l.FulfilledBaseQuantity,
                          }).ToListAsync(ct);
        return rows.OrderBy(r => r.OrderDate).ThenBy(r => r.Number, StringComparer.Ordinal).ThenBy(r => r.LineNo)
            .Select(r => Row(("number", r.Number), ("orderDate", Date(r.OrderDate)), ("customerCode", r.PartnerCode), ("customerName", r.PartnerName),
                ("articleCode", r.ArticleCode), ("articleName", r.ArticleName), ("orderedQuantity", r.Ordered), ("deliveredQuantity", r.Done),
                ("outstandingQuantity", r.Ordered - r.Done)))
            .ToList();
    }
}

public sealed class OpenPurchaseOrdersReport : ReportBase
{
    public override string Key => "open-purchase-orders";
    public override string Name => "Open purchase orders";
    public override string Description =>
        "What has been ordered from suppliers and not yet received: one row per confirmed purchase order line with a quantity still outstanding (ordered, received, outstanding; base units). "
        + "Drafts and closed orders are not listed. Use it to chase suppliers.";
    public override IReadOnlyList<ReportParameter> Parameters => [ReportParameter.PartnerId("Only this supplier"), ReportParameter.ArticleId(), ReportParameter.WarehouseId()];
    public override IReadOnlyList<ReportColumn> Columns =>
    [
        new("number", "Order", "text"), new("orderDate", "Order date", "date"), new("supplierCode", "Supplier code", "text"), new("supplierName", "Supplier", "text"),
        new("articleCode", "Article code", "text"), new("articleName", "Article", "text"), new("orderedQuantity", "Ordered", "quantity", true),
        new("receivedQuantity", "Received", "quantity", true), new("outstandingQuantity", "Outstanding", "quantity", true),
    ];

    public override async Task<IReadOnlyList<Dictionary<string, object?>>> RowsAsync(IXerpDb db, ReportArgs args, CancellationToken ct)
    {
        var rows = await (from l in db.PurchaseOrderLines.AsNoTracking()
                          join o in db.PurchaseOrders.AsNoTracking() on l.OrderId equals o.Id
                          join p in db.Partners.AsNoTracking() on o.PartnerId equals p.Id into partner
                          from p in partner.DefaultIfEmpty()
                          join a in db.Articles.AsNoTracking() on l.ArticleId equals a.Id
                          where o.Status == OrderStatus.Confirmed && (l.BaseQuantity ?? l.Quantity) > l.FulfilledBaseQuantity
                              && (args.PartnerId == null || o.PartnerId == args.PartnerId) && (args.ArticleId == null || l.ArticleId == args.ArticleId)
                              && (args.WarehouseId == null || o.WarehouseId == args.WarehouseId)
                          select new
                          {
                              o.Number, o.OrderDate, l.LineNo, PartnerCode = p == null ? "" : p.Code, PartnerName = p == null ? "(no supplier)" : p.Name,
                              ArticleCode = a.Code, ArticleName = a.Name, Ordered = l.BaseQuantity ?? l.Quantity, Done = l.FulfilledBaseQuantity,
                          }).ToListAsync(ct);
        return rows.OrderBy(r => r.OrderDate).ThenBy(r => r.Number, StringComparer.Ordinal).ThenBy(r => r.LineNo)
            .Select(r => Row(("number", r.Number), ("orderDate", Date(r.OrderDate)), ("supplierCode", r.PartnerCode), ("supplierName", r.PartnerName),
                ("articleCode", r.ArticleCode), ("articleName", r.ArticleName), ("orderedQuantity", r.Ordered), ("receivedQuantity", r.Done),
                ("outstandingQuantity", r.Ordered - r.Done)))
            .ToList();
    }
}

public sealed class PurchasesBySupplierReport : ReportBase
{
    public override string Key => "purchases-by-supplier";
    public override string Name => "Purchases by supplier";
    public override string Description =>
        "Goods received per supplier in a period: number of posted receipts and quantity (base units). Receipts without a partner (for example the product of a work order) and reversed receipts are not counted. "
        + "Values are not shown until costs exist (spec 015).";
    public override IReadOnlyList<ReportParameter> Parameters => [ReportParameter.From, ReportParameter.To, ReportParameter.PartnerId("Only this supplier"), ReportParameter.ArticleId()];
    public override IReadOnlyList<ReportColumn> Columns =>
    [
        new("supplierCode", "Supplier code", "text"), new("supplierName", "Supplier", "text"), new("receiptCount", "Receipts", "number", true),
        new("quantity", "Quantity", "quantity", true),
    ];

    public override async Task<IReadOnlyList<Dictionary<string, object?>>> RowsAsync(IXerpDb db, ReportArgs args, CancellationToken ct)
    {
        var groups = await (from l in db.StockDocumentLines.AsNoTracking()
                            join d in db.StockDocuments.AsNoTracking() on l.DocumentId equals d.Id
                            where d.Type == StockDocumentType.Receipt && d.Status == StockDocumentStatus.Posted && d.ReversalOfId == null && d.PartnerId != null
                                && d.DocumentDate >= args.From && d.DocumentDate <= args.To
                                && (args.PartnerId == null || d.PartnerId == args.PartnerId) && (args.ArticleId == null || l.ArticleId == args.ArticleId)
                            group new { d.Id, Quantity = l.BaseQuantity ?? l.Quantity } by d.PartnerId into g
                            select new { Partner = g.Key!.Value, Receipts = g.Select(x => x.Id).Distinct().Count(), Quantity = g.Sum(x => x.Quantity) })
            .ToListAsync(ct);
        var ids = groups.Select(g => g.Partner).ToList();
        var partners = await db.Partners.AsNoTracking().Where(p => ids.Contains(p.Id)).Select(p => new { p.Id, p.Code, p.Name }).ToDictionaryAsync(p => p.Id, ct);
        return groups.OrderByDescending(g => g.Quantity).ThenBy(g => partners[g.Partner].Code, StringComparer.Ordinal)
            .Select(g => Row(("supplierCode", partners[g.Partner].Code), ("supplierName", partners[g.Partner].Name), ("receiptCount", g.Receipts), ("quantity", g.Quantity)))
            .ToList();
    }
}

public sealed class ProductionOutputReport : ReportBase
{
    public override string Key => "production-output";
    public override string Name => "Production output";
    public override string Description =>
        "What was produced: completed work orders per product and period - number of work orders and the quantity produced (base units). Drafts, released, cancelled and reversed work orders are not counted. "
        + "Periods are by the completion date.";
    public override IReadOnlyList<ReportParameter> Parameters => [ReportParameter.From, ReportParameter.To, ReportParameter.GroupBy, ReportParameter.ArticleId("Only this product")];
    public override IReadOnlyList<ReportColumn> Columns =>
    [
        new("period", "Period", "date"), new("articleCode", "Article code", "text"), new("articleName", "Product", "text"),
        new("workOrderCount", "Work orders", "number", true), new("quantity", "Quantity", "quantity", true),
    ];

    public override async Task<IReadOnlyList<Dictionary<string, object?>>> RowsAsync(IXerpDb db, ReportArgs args, CancellationToken ct)
    {
        var days = await db.WorkOrders.AsNoTracking()
            .Where(w => w.Status == WorkOrderStatus.Completed && w.CompletedDate != null && w.CompletedDate >= args.From && w.CompletedDate <= args.To)
            .Where(w => args.ArticleId == null || w.ArticleId == args.ArticleId)
            .GroupBy(w => new { Day = w.CompletedDate!.Value, w.ArticleId })
            .Select(g => new { g.Key.Day, g.Key.ArticleId, Count = g.Count(), Quantity = g.Sum(w => w.ProducedQuantity ?? w.Quantity) })
            .ToListAsync(ct);
        var ids = days.Select(d => d.ArticleId).Distinct().ToList();
        var articles = await db.Articles.AsNoTracking().Where(a => ids.Contains(a.Id)).Select(a => new { a.Id, a.Code, a.Name }).ToDictionaryAsync(a => a.Id, ct);
        return days.GroupBy(d => (Period: ReportPeriods.Start(d.Day, args.GroupBy), d.ArticleId))
            .Select(g => (g.Key.Period, g.Key.ArticleId, Count: g.Sum(d => d.Count), Quantity: g.Sum(d => d.Quantity)))
            .OrderBy(r => r.Period).ThenBy(r => articles[r.ArticleId].Code, StringComparer.Ordinal)
            .Select(r => Row(("period", Date(r.Period)), ("articleCode", articles[r.ArticleId].Code), ("articleName", articles[r.ArticleId].Name),
                ("workOrderCount", r.Count), ("quantity", r.Quantity)))
            .ToList();
    }
}
