using Xerp.Domain.Common;
using Xerp.Domain.Inventory;
using Xerp.Domain.Orders;

namespace Xerp.Domain.Invoicing;

public enum InvoiceStatus
{
    /// <summary>Freely editable and deletable; no number, no effect on anything.</summary>
    Draft,

    /// <summary>Numbered and final: it can only be paid or cancelled by a cancelling invoice.</summary>
    Issued,

    /// <summary>Issued, then cancelled: a cancelling invoice with the negated lines was issued for it. Final.</summary>
    Cancelled,
}

public static class InvoiceStatusNames
{
    public const string Draft = "draft";
    public const string Issued = "issued";
    public const string Cancelled = "cancelled";

    public static readonly IReadOnlyList<string> All = [Draft, Issued, Cancelled];

    public static string ToName(this InvoiceStatus status) => status switch
    {
        InvoiceStatus.Draft => Draft,
        InvoiceStatus.Issued => Issued,
        InvoiceStatus.Cancelled => Cancelled,
        _ => throw new ArgumentOutOfRangeException(nameof(status)),
    };

    public static InvoiceStatus Parse(string name) =>
        TryParse(name, out var status) ? status : throw new ArgumentException($"Unknown invoice status '{name}'.", nameof(name));

    public static bool TryParse(string? name, out InvoiceStatus status)
    {
        switch (name)
        {
            case Draft: status = InvoiceStatus.Draft; return true;
            case Issued: status = InvoiceStatus.Issued; return true;
            case Cancelled: status = InvoiceStatus.Cancelled; return true;
            default: status = default; return false;
        }
    }
}

/// <summary>How much of an issued invoice is paid. A cancelling invoice and a cancelled one are not paid against.</summary>
public static class PaymentStatusNames
{
    public const string Unpaid = "unpaid";
    public const string Partial = "partial";
    public const string Paid = "paid";
    public const string None = "none";

    public static readonly IReadOnlyList<string> Filterable = [Unpaid, Partial, Paid];
}

/// <summary>A line as entered and resolved: the tax rate's percentage is copied, so a later change of the rate never changes an invoice.</summary>
public readonly record struct InvoiceLineEntry(
    Guid ArticleId, string Description, decimal Quantity, decimal UnitPrice, decimal DiscountPercent, Guid TaxRateId, decimal TaxPercent);

/// <summary>One tax rate's part of an invoice: the net amount taxed at it and the tax.</summary>
public readonly record struct TaxPart(decimal TaxPercent, decimal NetAmount, decimal TaxAmount);

/// <summary>Invariants of invoices (spec I01). The configurable rules are in the rule registry.</summary>
public static class InvoiceRules
{
    public const int MaxLines = StockDocument.MaxLines;
    public const int DescriptionMaxLength = 200;
    public const int AmountDecimals = OrderAmounts.DecimalPlaces;
    public const int DiscountDecimals = 2;
    public const decimal MaxAmount = OrderAmounts.MaxLineAmount;

    public static bool IsValidDiscount(decimal discount) =>
        discount >= 0 && discount <= 100 && decimal.Round(discount, DiscountDecimals) == discount;

    /// <summary>Quantity × unit price, less the discount, rounded to cents (half away from zero).</summary>
    public static decimal LineNet(decimal quantity, decimal unitPrice, decimal discountPercent) =>
        decimal.Round(quantity * unitPrice * (100m - discountPercent) / 100m, AmountDecimals, MidpointRounding.AwayFromZero);

    /// <summary>The tax of each rate on the sum of its lines (the Croatian recapitulation), by rate, highest first.</summary>
    public static IReadOnlyList<TaxPart> Taxes(IEnumerable<(decimal TaxPercent, decimal Net)> lines) =>
        lines.GroupBy(l => l.TaxPercent)
            .Select(g =>
            {
                var net = g.Sum(l => l.Net);
                return new TaxPart(g.Key, net, decimal.Round(net * g.Key / 100m, AmountDecimals, MidpointRounding.AwayFromZero));
            })
            .OrderByDescending(p => p.TaxPercent)
            .ToList();

    /// <summary>A price that includes tax, without it: what the unit price of a line is when it comes from the article's sales price.</summary>
    public static decimal NetOfGross(decimal gross, decimal taxPercent) =>
        decimal.Round(gross * 100m / (100m + taxPercent), PriceRules.DecimalPlaces, MidpointRounding.AwayFromZero);

    public static string PaymentStatus(InvoiceStatus status, bool isCancellation, decimal total, decimal paid) =>
        status != InvoiceStatus.Issued || isCancellation || total <= 0 ? PaymentStatusNames.None
        : paid >= total ? PaymentStatusNames.Paid
        : paid > 0 ? PaymentStatusNames.Partial
        : PaymentStatusNames.Unpaid;
}

/// <summary>
/// An outgoing invoice (spec I01): a draft is prepared and then issued, which gives it the next number of the
/// tenant's gapless invoice series; an issued invoice never changes again. It is paid by payments and undone
/// only by a cancelling invoice (the same lines with negated quantities), issued in the same series. Amounts are
/// net per line; tax is computed per rate on the invoice. With the rule <c>invoice.issueMovesStock</c> the issue
/// also posts a stock issue of its stock articles.
/// </summary>
public sealed class Invoice : ITenantOwned
{
    public const int NumberMaxLength = StockDocument.NumberMaxLength;
    public const int ReferenceMaxLength = StockDocument.ReferenceMaxLength;
    public const int NoteMaxLength = StockDocument.NoteMaxLength;

    private readonly List<InvoiceLine> _lines = [];
    private readonly List<InvoicePayment> _payments = [];

    private Invoice() { }

    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public InvoiceStatus Status { get; private set; }
    public string? Number { get; private set; }
    public Guid? CustomerId { get; private set; }
    public DateOnly IssueDate { get; private set; }
    public DateOnly? DeliveryDate { get; private set; }
    public DateOnly? DueDate { get; private set; }
    public Guid? PaymentMethodId { get; private set; }

    /// <summary>Where the goods are issued from when the issue moves stock; null = the default warehouse.</summary>
    public Guid? WarehouseId { get; private set; }

    public string? Reference { get; private set; }
    public string? Note { get; private set; }
    public decimal NetAmount { get; private set; }
    public decimal TaxAmount { get; private set; }
    public decimal TotalAmount { get; private set; }
    public decimal PaidAmount { get; private set; }

    /// <summary>The stock issue its issue posted (rule <c>invoice.issueMovesStock</c>); null otherwise.</summary>
    public Guid? StockDocumentId { get; private set; }

    /// <summary>On a cancelling invoice: the invoice it cancels.</summary>
    public Guid? CancellationOfId { get; private set; }

    /// <summary>On a cancelled invoice: the invoice that cancelled it.</summary>
    public Guid? CancelledById { get; private set; }

    public DateTime CreatedAt { get; private set; }
    public DateTime UpdatedAt { get; private set; }
    public Guid CreatedBy { get; private set; }
    public Guid UpdatedBy { get; private set; }
    public DateTime? IssuedAt { get; private set; }
    public Guid? IssuedBy { get; private set; }

    public IReadOnlyList<InvoiceLine> Lines
    {
        get
        {
            _lines.Sort((a, b) => a.LineNo.CompareTo(b.LineNo));
            return _lines;
        }
    }

    public IReadOnlyList<InvoicePayment> Payments => _payments;

    public bool IsCancellation => CancellationOfId is not null;

    public static Invoice Create(
        Guid? customerId, DateOnly issueDate, DateOnly? deliveryDate, DateOnly? dueDate, Guid? paymentMethodId, Guid? warehouseId,
        string? reference, string? note, IReadOnlyList<InvoiceLineEntry> lines, DateTime now, Guid actorKeyId)
    {
        var invoice = new Invoice { Id = Guid.CreateVersion7(), Status = InvoiceStatus.Draft, CreatedAt = now, CreatedBy = actorKeyId };
        invoice.Replace(customerId, issueDate, deliveryDate, dueDate, paymentMethodId, warehouseId, reference, note, lines, now, actorKeyId);
        return invoice;
    }

    /// <summary>Replaces header and lines of a draft; returns the lines no longer on it, for the caller to delete.</summary>
    public IReadOnlyList<InvoiceLine> Replace(
        Guid? customerId, DateOnly issueDate, DateOnly? deliveryDate, DateOnly? dueDate, Guid? paymentMethodId, Guid? warehouseId,
        string? reference, string? note, IReadOnlyList<InvoiceLineEntry> lines, DateTime now, Guid actorKeyId)
    {
        EnsureStatus(InvoiceStatus.Draft, "changed");
        if (lines.Count is < 1 or > InvoiceRules.MaxLines)
            throw new ArgumentException($"An invoice has 1 to {InvoiceRules.MaxLines} lines.", nameof(lines));
        if (lines.Any(l => !QuantityRules.IsValid(l.Quantity) || !PriceRules.IsValid(l.UnitPrice) || !InvoiceRules.IsValidDiscount(l.DiscountPercent)))
            throw new ArgumentException("A line breaks the quantity, price or discount rule.", nameof(lines));
        if (dueDate is { } due && due < issueDate)
            throw new ArgumentException("The due date is before the issue date.", nameof(dueDate));

        CustomerId = customerId;
        IssueDate = issueDate;
        DeliveryDate = deliveryDate;
        DueDate = dueDate;
        PaymentMethodId = paymentMethodId;
        WarehouseId = warehouseId;
        Reference = OptionalTextRules.Normalize(reference, ReferenceMaxLength, nameof(reference));
        Note = NoteRules.TryNormalize(note, NoteMaxLength, out var n) ? n : throw new ArgumentException("Invalid note.", nameof(note));
        UpdatedAt = now;
        UpdatedBy = actorKeyId;
        return SetLines(lines);
    }

    /// <summary>Gives the draft its number; from now on it never changes.</summary>
    public void Issue(string number, Guid? stockDocumentId, DateTime now, Guid actorKeyId)
    {
        EnsureStatus(InvoiceStatus.Draft, "issued");
        if (string.IsNullOrWhiteSpace(number))
            throw new ArgumentException("An issued invoice needs a number.", nameof(number));
        Status = InvoiceStatus.Issued;
        Number = number;
        StockDocumentId = stockDocumentId;
        IssuedAt = now;
        IssuedBy = actorKeyId;
        UpdatedAt = now;
        UpdatedBy = actorKeyId;
    }

    /// <summary>The cancelling invoice of an issued one: the same header and lines with negated quantities, issued at once.</summary>
    public static Invoice CancellationOf(
        Invoice original, string number, DateOnly issueDate, string? note, Guid? stockDocumentId, DateTime now, Guid actorKeyId)
    {
        original.EnsureStatus(InvoiceStatus.Issued, "cancelled");
        if (original.IsCancellation)
            throw new InvalidOperationException("A cancelling invoice cannot be cancelled.");
        var cancellation = new Invoice
        {
            Id = Guid.CreateVersion7(),
            Status = InvoiceStatus.Issued,
            Number = number,
            CustomerId = original.CustomerId,
            IssueDate = issueDate,
            DeliveryDate = original.DeliveryDate,
            PaymentMethodId = original.PaymentMethodId,
            WarehouseId = original.WarehouseId,
            Reference = original.Number,
            Note = NoteRules.TryNormalize(note, NoteMaxLength, out var n) ? n : throw new ArgumentException("Invalid note.", nameof(note)),
            CancellationOfId = original.Id,
            StockDocumentId = stockDocumentId,
            CreatedAt = now,
            CreatedBy = actorKeyId,
            UpdatedAt = now,
            UpdatedBy = actorKeyId,
            IssuedAt = now,
            IssuedBy = actorKeyId,
        };
        cancellation.SetLines(original.Lines.Select(l => l.Entry with { Quantity = -l.Quantity }).ToList());
        original.Status = InvoiceStatus.Cancelled;
        original.CancelledById = cancellation.Id;
        original.UpdatedAt = now;
        original.UpdatedBy = actorKeyId;
        return cancellation;
    }

    public InvoicePayment AddPayment(DateOnly date, decimal amount, Guid? paymentMethodId, string? note, DateTime now, Guid actorKeyId)
    {
        EnsureStatus(InvoiceStatus.Issued, "paid");
        if (IsCancellation)
            throw new InvalidOperationException("A cancelling invoice is not paid against.");
        if (amount <= 0 || decimal.Round(amount, InvoiceRules.AmountDecimals) != amount || amount > InvoiceRules.MaxAmount)
            throw new ArgumentException("A payment is a positive amount in cents.", nameof(amount));
        var payment = new InvoicePayment(Id, date, amount, paymentMethodId,
            NoteRules.TryNormalize(note, NoteMaxLength, out var n) ? n : throw new ArgumentException("Invalid note.", nameof(note)), now, actorKeyId);
        _payments.Add(payment);
        PaidAmount += amount;
        UpdatedAt = now;
        UpdatedBy = actorKeyId;
        return payment;
    }

    public void RemovePayment(InvoicePayment payment, DateTime now, Guid actorKeyId)
    {
        if (!_payments.Remove(payment))
            throw new ArgumentException("Not a payment of this invoice.", nameof(payment));
        PaidAmount -= payment.Amount;
        UpdatedAt = now;
        UpdatedBy = actorKeyId;
    }

    private List<InvoiceLine> SetLines(IReadOnlyList<InvoiceLineEntry> lines)
    {
        var current = Lines;
        for (var i = 0; i < lines.Count; i++)
        {
            if (i < current.Count)
                current[i].Set(lines[i]);
            else
                _lines.Add(new InvoiceLine(Id, i + 1, lines[i]));
        }
        var removed = _lines.Skip(lines.Count).ToList();
        _lines.RemoveRange(lines.Count, removed.Count);

        var taxes = InvoiceRules.Taxes(_lines.Select(l => (l.TaxPercent, l.NetAmount)));
        NetAmount = taxes.Sum(t => t.NetAmount);
        TaxAmount = taxes.Sum(t => t.TaxAmount);
        TotalAmount = NetAmount + TaxAmount;
        return removed;
    }

    private void EnsureStatus(InvoiceStatus required, string attempted)
    {
        if (Status != required)
            throw new InvalidOperationException($"A {Status.ToName()} invoice cannot be {attempted}.");
    }
}

public sealed class InvoiceLine : ITenantOwned
{
    private InvoiceLine() { }

    internal InvoiceLine(Guid invoiceId, int lineNo, InvoiceLineEntry entry)
    {
        Id = Guid.CreateVersion7();
        InvoiceId = invoiceId;
        LineNo = lineNo;
        Set(entry);
    }

    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid InvoiceId { get; private set; }
    public int LineNo { get; private set; }
    public Guid ArticleId { get; private set; }
    public string Description { get; private set; } = "";

    /// <summary>In the article's base unit; negative only on a cancelling invoice.</summary>
    public decimal Quantity { get; private set; }

    /// <summary>Without tax.</summary>
    public decimal UnitPrice { get; private set; }

    public decimal DiscountPercent { get; private set; }
    public Guid TaxRateId { get; private set; }
    public decimal TaxPercent { get; private set; }
    public decimal NetAmount { get; private set; }

    public InvoiceLineEntry Entry => new(ArticleId, Description, Quantity, UnitPrice, DiscountPercent, TaxRateId, TaxPercent);

    internal void Set(InvoiceLineEntry entry)
    {
        ArticleId = entry.ArticleId;
        Description = OptionalTextRules.TryNormalize(entry.Description, InvoiceRules.DescriptionMaxLength, out var description) && description is not null
            ? description
            : throw new ArgumentException("Invalid line description.", nameof(entry));
        Quantity = entry.Quantity;
        UnitPrice = entry.UnitPrice;
        DiscountPercent = entry.DiscountPercent;
        TaxRateId = entry.TaxRateId;
        TaxPercent = entry.TaxPercent;
        NetAmount = InvoiceRules.LineNet(entry.Quantity, entry.UnitPrice, entry.DiscountPercent);
    }
}

public sealed class InvoicePayment : ITenantOwned
{
    private InvoicePayment() { }

    internal InvoicePayment(Guid invoiceId, DateOnly date, decimal amount, Guid? paymentMethodId, string? note, DateTime now, Guid actorKeyId)
    {
        Id = Guid.CreateVersion7();
        InvoiceId = invoiceId;
        PaymentDate = date;
        Amount = amount;
        PaymentMethodId = paymentMethodId;
        Note = note;
        CreatedAt = now;
        CreatedBy = actorKeyId;
    }

    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid InvoiceId { get; private set; }
    public DateOnly PaymentDate { get; private set; }
    public decimal Amount { get; private set; }
    public Guid? PaymentMethodId { get; private set; }
    public string? Note { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public Guid CreatedBy { get; private set; }
}
