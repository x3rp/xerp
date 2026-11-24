using Microsoft.EntityFrameworkCore;
using Xerp.Application.Ports;
using Xerp.Domain.Invoicing;

namespace Xerp.Application.Reports;

/// <summary>The invoices that count as sales: issued ones, a cancelled one and its cancelling invoice alike - which net to zero (spec RP01).</summary>
internal static class SalesQueries
{
    public static IQueryable<Invoice> Issued(IXerpDb db, ReportArgs args) =>
        db.Invoices.AsNoTracking()
            .Where(i => i.Status != InvoiceStatus.Draft && i.IssueDate >= args.From && i.IssueDate <= args.To)
            .Where(i => args.PartnerId == null || i.CustomerId == args.PartnerId);
}

public sealed class SalesByPeriodReport : ReportBase
{
    public override string Key => "sales-by-period";
    public override string Name => "Sales by period";
    public override string Description =>
        "How much was invoiced per period: number of invoices, net amount, tax and total. Answers 'how are sales going' and 'how much did we invoice in March'. "
        + "Issued invoices only (drafts are not sales); a cancelled invoice and its cancelling invoice net to zero, a cancelling invoice counts as -1 invoice. Amounts in the company's currency.";
    public override IReadOnlyList<ReportParameter> Parameters => [ReportParameter.From, ReportParameter.To, ReportParameter.GroupBy, ReportParameter.PartnerId("Only this customer")];
    public override IReadOnlyList<ReportColumn> Columns =>
    [
        new("period", "Period", "date"), new("invoiceCount", "Invoices", "number", true), new("netAmount", "Net amount", "money", true),
        new("taxAmount", "Tax", "money", true), new("totalAmount", "Total", "money", true),
    ];

    public override async Task<IReadOnlyList<Dictionary<string, object?>>> RowsAsync(IXerpDb db, ReportArgs args, CancellationToken ct)
    {
        var days = await SalesQueries.Issued(db, args)
            .GroupBy(i => i.IssueDate)
            .Select(g => new
            {
                Day = g.Key,
                Count = g.Sum(i => i.CancellationOfId == null ? 1 : -1),
                Net = g.Sum(i => i.NetAmount),
                Tax = g.Sum(i => i.TaxAmount),
                Total = g.Sum(i => i.TotalAmount),
            })
            .ToListAsync(ct);
        return days.GroupBy(d => ReportPeriods.Start(d.Day, args.GroupBy))
            .Select(g => (Period: g.Key, Count: g.Sum(d => d.Count), Net: g.Sum(d => d.Net), Tax: g.Sum(d => d.Tax), Total: g.Sum(d => d.Total)))
            .Where(p => p.Count != 0 || p.Net != 0 || p.Tax != 0 || p.Total != 0)
            .OrderBy(p => p.Period)
            .Select(p => Row(("period", Date(p.Period)), ("invoiceCount", p.Count), ("netAmount", p.Net), ("taxAmount", p.Tax), ("totalAmount", p.Total)))
            .ToList();
    }
}

public sealed class SalesByCustomerReport : ReportBase
{
    public override string Key => "sales-by-customer";
    public override string Name => "Sales by customer";
    public override string Description =>
        "Who the best customers are in a period: invoices, net, tax and total per customer, biggest first. Issued invoices, net of cancellations. "
        + "Invoices without a customer are shown under the name '(no customer)'.";
    public override IReadOnlyList<ReportParameter> Parameters => [ReportParameter.From, ReportParameter.To, ReportParameter.PartnerId("Only this customer")];
    public override IReadOnlyList<ReportColumn> Columns =>
    [
        new("customerCode", "Customer code", "text"), new("customerName", "Customer", "text"), new("invoiceCount", "Invoices", "number", true),
        new("netAmount", "Net amount", "money", true), new("taxAmount", "Tax", "money", true), new("totalAmount", "Total", "money", true),
    ];

    public override async Task<IReadOnlyList<Dictionary<string, object?>>> RowsAsync(IXerpDb db, ReportArgs args, CancellationToken ct)
    {
        var groups = await SalesQueries.Issued(db, args)
            .GroupBy(i => i.CustomerId)
            .Select(g => new
            {
                Customer = g.Key,
                Count = g.Sum(i => i.CancellationOfId == null ? 1 : -1),
                Net = g.Sum(i => i.NetAmount),
                Tax = g.Sum(i => i.TaxAmount),
                Total = g.Sum(i => i.TotalAmount),
            })
            .ToListAsync(ct);
        var ids = groups.Where(g => g.Customer != null).Select(g => g.Customer!.Value).ToList();
        var partners = await db.Partners.AsNoTracking().Where(p => ids.Contains(p.Id)).Select(p => new { p.Id, p.Code, p.Name }).ToDictionaryAsync(p => p.Id, ct);
        return groups.Where(g => g.Count != 0 || g.Net != 0 || g.Total != 0)
            .Select(g => (g, Code: g.Customer is { } id ? partners[id].Code : "", Name: g.Customer is { } id2 ? partners[id2].Name : "(no customer)"))
            .OrderByDescending(x => x.g.Net).ThenBy(x => x.Code, StringComparer.Ordinal)
            .Select(x => Row(("customerCode", x.Code), ("customerName", x.Name), ("invoiceCount", x.g.Count), ("netAmount", x.g.Net),
                ("taxAmount", x.g.Tax), ("totalAmount", x.g.Total)))
            .ToList();
    }
}

public sealed class SalesByArticleReport : ReportBase
{
    public override string Key => "sales-by-article";
    public override string Name => "Sales by article";
    public override string Description =>
        "What sells: quantity (in the article's base unit) and net revenue per article, with its category, biggest revenue first. Issued invoices, net of cancellations; revenue is after line discounts and before tax.";
    public override IReadOnlyList<ReportParameter> Parameters =>
        [ReportParameter.From, ReportParameter.To, ReportParameter.ArticleId(), ReportParameter.CategoryId(), ReportParameter.PartnerId("Only this customer")];
    public override IReadOnlyList<ReportColumn> Columns =>
    [
        new("articleCode", "Article code", "text"), new("articleName", "Article", "text"), new("categoryCode", "Category code", "text"),
        new("categoryName", "Category", "text"), new("quantity", "Quantity", "quantity", true), new("netAmount", "Net amount", "money", true),
    ];

    public override async Task<IReadOnlyList<Dictionary<string, object?>>> RowsAsync(IXerpDb db, ReportArgs args, CancellationToken ct)
    {
        var lines = from l in db.InvoiceLines.AsNoTracking()
                    join i in SalesQueries.Issued(db, args) on l.InvoiceId equals i.Id
                    where args.ArticleId == null || l.ArticleId == args.ArticleId
                    select l;
        if (args.CategoryId is { } category)
            lines = lines.Where(l => db.ArticleSalesData.Any(s => s.ArticleId == l.ArticleId && s.CategoryId == category));
        var groups = await lines.GroupBy(l => l.ArticleId)
            .Select(g => new { ArticleId = g.Key, Quantity = g.Sum(l => l.Quantity), Net = g.Sum(l => l.NetAmount) })
            .ToListAsync(ct);
        var ids = groups.Select(g => g.ArticleId).ToList();
        var articles = await db.Articles.AsNoTracking().Where(a => ids.Contains(a.Id)).Select(a => new { a.Id, a.Code, a.Name }).ToDictionaryAsync(a => a.Id, ct);
        var categories = await (from s in db.ArticleSalesData.AsNoTracking()
                                join c in db.ArticleCategories.AsNoTracking() on s.CategoryId equals c.Id
                                where ids.Contains(s.ArticleId)
                                select new { s.ArticleId, c.Code, c.Name }).ToDictionaryAsync(c => c.ArticleId, ct);
        return groups.Where(g => g.Quantity != 0 || g.Net != 0)
            .OrderByDescending(g => g.Net).ThenBy(g => articles[g.ArticleId].Code, StringComparer.Ordinal)
            .Select(g => Row(("articleCode", articles[g.ArticleId].Code), ("articleName", articles[g.ArticleId].Name),
                ("categoryCode", categories.GetValueOrDefault(g.ArticleId)?.Code), ("categoryName", categories.GetValueOrDefault(g.ArticleId)?.Name),
                ("quantity", g.Quantity), ("netAmount", g.Net)))
            .ToList();
    }
}

public sealed class VatOutputReport : ReportBase
{
    public override string Key => "vat-output";
    public override string Name => "Output VAT";
    public override string Description =>
        "Output VAT per tax rate and period: the tax base (net amount) and the tax - the outgoing half of the VAT return. Tax is computed per rate on each invoice, as printed on it; "
        + "a cancelled invoice and its cancelling invoice net to zero.";
    public override IReadOnlyList<ReportParameter> Parameters => [ReportParameter.From, ReportParameter.To, ReportParameter.GroupBy];
    public override IReadOnlyList<ReportColumn> Columns =>
    [
        new("period", "Period", "date"), new("taxPercent", "Tax rate %", "percent"), new("netAmount", "Tax base", "money", true), new("taxAmount", "Tax", "money", true),
    ];

    public override async Task<IReadOnlyList<Dictionary<string, object?>>> RowsAsync(IXerpDb db, ReportArgs args, CancellationToken ct)
    {
        // One row per invoice and rate; the tax of a rate is rounded per invoice, as on the invoice, and then added up per period.
        var perInvoice = await (from l in db.InvoiceLines.AsNoTracking()
                                join i in SalesQueries.Issued(db, args) on l.InvoiceId equals i.Id
                                group l by new { i.Id, i.IssueDate, l.TaxPercent } into g
                                select new { g.Key.IssueDate, g.Key.TaxPercent, Net = g.Sum(l => l.NetAmount) })
            .ToListAsync(ct);
        return perInvoice
            .Select(r => (Period: ReportPeriods.Start(r.IssueDate, args.GroupBy), r.TaxPercent, r.Net, Tax: decimal.Round(r.Net * r.TaxPercent / 100m, 2, MidpointRounding.AwayFromZero)))
            .GroupBy(r => (r.Period, r.TaxPercent))
            .Select(g => (g.Key.Period, g.Key.TaxPercent, Net: g.Sum(r => r.Net), Tax: g.Sum(r => r.Tax)))
            .Where(r => r.Net != 0 || r.Tax != 0)
            .OrderBy(r => r.Period).ThenByDescending(r => r.TaxPercent)
            .Select(r => Row(("period", Date(r.Period)), ("taxPercent", r.TaxPercent), ("netAmount", r.Net), ("taxAmount", r.Tax)))
            .ToList();
    }
}

public sealed class PaymentsReceivedReport : ReportBase
{
    public override string Key => "payments-received";
    public override string Name => "Payments received";
    public override string Description =>
        "Money received on invoices per period and payment method: number of payments and amount. Answers 'how much did we collect, and how'. Payments are counted on the day they were recorded as received.";
    public override IReadOnlyList<ReportParameter> Parameters => [ReportParameter.From, ReportParameter.To, ReportParameter.GroupBy, ReportParameter.PartnerId("Only payments on invoices of this customer")];
    public override IReadOnlyList<ReportColumn> Columns =>
    [
        new("period", "Period", "date"), new("paymentMethodCode", "Method code", "text"), new("paymentMethodName", "Payment method", "text"),
        new("paymentCount", "Payments", "number", true), new("amount", "Amount", "money", true),
    ];

    public override async Task<IReadOnlyList<Dictionary<string, object?>>> RowsAsync(IXerpDb db, ReportArgs args, CancellationToken ct)
    {
        var payments = from p in db.InvoicePayments.AsNoTracking()
                       join i in db.Invoices.AsNoTracking() on p.InvoiceId equals i.Id
                       where p.PaymentDate >= args.From && p.PaymentDate <= args.To && (args.PartnerId == null || i.CustomerId == args.PartnerId)
                       select p;
        var days = await payments.GroupBy(p => new { p.PaymentDate, p.PaymentMethodId })
            .Select(g => new { g.Key.PaymentDate, g.Key.PaymentMethodId, Count = g.Count(), Amount = g.Sum(p => p.Amount) })
            .ToListAsync(ct);
        var ids = days.Where(d => d.PaymentMethodId != null).Select(d => d.PaymentMethodId!.Value).Distinct().ToList();
        var methods = await db.PaymentMethods.AsNoTracking().Where(m => ids.Contains(m.Id)).Select(m => new { m.Id, m.Code, m.Name }).ToDictionaryAsync(m => m.Id, ct);
        return days
            .GroupBy(d => (Period: ReportPeriods.Start(d.PaymentDate, args.GroupBy), d.PaymentMethodId))
            .Select(g => (g.Key.Period, Code: g.Key.PaymentMethodId is { } id ? methods[id].Code : "", Name: g.Key.PaymentMethodId is { } id2 ? methods[id2].Name : "(no method)",
                Count: g.Sum(d => d.Count), Amount: g.Sum(d => d.Amount)))
            .OrderBy(r => r.Period).ThenBy(r => r.Code, StringComparer.Ordinal)
            .Select(r => Row(("period", Date(r.Period)), ("paymentMethodCode", r.Code), ("paymentMethodName", r.Name), ("paymentCount", r.Count), ("amount", r.Amount)))
            .ToList();
    }
}

public sealed class UnpaidInvoicesReport : ReportBase
{
    public override string Key => "unpaid-invoices";
    public override string Name => "Unpaid invoices";
    public override string Description =>
        "Issued invoices that are not fully paid, as of now: total, paid, still open and days overdue (0 when not yet due; empty when the invoice has no due date). "
        + "Cancelled invoices and cancelling invoices are not owed. Oldest due date first. Use it to chase receivables.";
    public override IReadOnlyList<ReportParameter> Parameters => [ReportParameter.PartnerId("Only this customer")];
    public override IReadOnlyList<ReportColumn> Columns =>
    [
        new("number", "Invoice", "text"), new("issueDate", "Issued", "date"), new("dueDate", "Due", "date"), new("customerCode", "Customer code", "text"),
        new("customerName", "Customer", "text"), new("totalAmount", "Total", "money", true), new("paidAmount", "Paid", "money", true),
        new("openAmount", "Open", "money", true), new("daysOverdue", "Days overdue", "number"),
    ];

    public override async Task<IReadOnlyList<Dictionary<string, object?>>> RowsAsync(IXerpDb db, ReportArgs args, CancellationToken ct)
    {
        var rows = await (from i in db.Invoices.AsNoTracking()
                          join p in db.Partners.AsNoTracking() on i.CustomerId equals p.Id into partner
                          from p in partner.DefaultIfEmpty()
                          where i.Status == InvoiceStatus.Issued && i.CancellationOfId == null && i.TotalAmount > i.PaidAmount
                              && (args.PartnerId == null || i.CustomerId == args.PartnerId)
                          select new { i.Number, i.IssueDate, i.DueDate, Code = p == null ? "" : p.Code, Name = p == null ? "(no customer)" : p.Name, i.TotalAmount, i.PaidAmount })
            .ToListAsync(ct);
        return rows.OrderBy(r => r.DueDate ?? DateOnly.MaxValue).ThenBy(r => r.Number, StringComparer.Ordinal)
            .Select(r => Row(("number", r.Number), ("issueDate", Date(r.IssueDate)), ("dueDate", Date(r.DueDate)), ("customerCode", r.Code), ("customerName", r.Name),
                ("totalAmount", r.TotalAmount), ("paidAmount", r.PaidAmount), ("openAmount", r.TotalAmount - r.PaidAmount),
                ("daysOverdue", r.DueDate is { } due ? Math.Max(0, args.Today.DayNumber - due.DayNumber) : (int?)null)))
            .ToList();
    }
}
