using Xerp.Application.Common;
using Xerp.Application.Sales;
using Xerp.Application.Stock;

namespace Xerp.Application.Invoicing;

// ---- inputs (spec I01, section 4)

/// <summary>
/// A line of a request. <c>UnitPrice</c> is without tax; omitted, it is the article's sales price (which includes tax)
/// without the line's tax. <c>TaxRateId</c> omitted: the article's tax rate when it has exactly one.
/// <c>Description</c> omitted: the article's name.
/// </summary>
public sealed record InvoiceLineInput(
    string? ArticleId = null, decimal? Quantity = null, decimal? UnitPrice = null, decimal? DiscountPercent = null,
    string? TaxRateId = null, string? Description = null);

/// <summary>The body of a create and of a replace of a draft.</summary>
public sealed record InvoiceInput(
    string? CustomerId = null, string? IssueDate = null, string? DeliveryDate = null, string? DueDate = null,
    string? PaymentMethodId = null, string? WarehouseId = null, string? Reference = null, string? Note = null,
    IReadOnlyList<InvoiceLineInput?>? Lines = null);

/// <summary>The arguments of <c>invoice_update</c>.</summary>
public sealed record ReplaceInvoiceToolInput(
    string? Id = null, string? CustomerId = null, string? IssueDate = null, string? DeliveryDate = null, string? DueDate = null,
    string? PaymentMethodId = null, string? WarehouseId = null, string? Reference = null, string? Note = null,
    IReadOnlyList<InvoiceLineInput?>? Lines = null)
{
    public InvoiceInput ToInput() => new(CustomerId, IssueDate, DeliveryDate, DueDate, PaymentMethodId, WarehouseId, Reference, Note, Lines);
}

/// <summary>The body of a cancellation: the date of the cancelling invoice (default today) and its note.</summary>
public sealed record CancelInvoiceInput(string? IssueDate = null, string? Note = null);

public sealed record CancelInvoiceToolInput(string? Id = null, string? IssueDate = null, string? Note = null)
{
    public CancelInvoiceInput ToInput() => new(IssueDate, Note);
}

public sealed record InvoicePaymentInput(string? PaymentDate = null, decimal? Amount = null, string? PaymentMethodId = null, string? Note = null);

public sealed record AddInvoicePaymentToolInput(string? Id = null, string? PaymentDate = null, decimal? Amount = null, string? PaymentMethodId = null, string? Note = null)
{
    public InvoicePaymentInput ToInput() => new(PaymentDate, Amount, PaymentMethodId, Note);
}

public sealed record InvoicePaymentAddressInput(string? Id = null, string? PaymentId = null);

public sealed record ListInvoicesInput(
    string? Status = null, string? PaymentStatus = null, string? CustomerId = null, string? Search = null, int? Limit = null, int? Offset = null);

public sealed record InvoiceAddressInput(string? Id = null, string? Number = null);

public sealed record CompanyProfileInput : AddressInput
{
    public string? LegalName { get; init; }
    public string? TaxId { get; init; }
    public string? Iban { get; init; }
    public string? Email { get; init; }
    public string? Phone { get; init; }
    public string? Website { get; init; }
    public string? InvoiceFooter { get; init; }
}

// ---- representations

public sealed record InvoiceLineDto(
    int LineNo, ReferenceSummary Article, ReferenceSummary Unit, string Description, decimal Quantity, decimal UnitPrice,
    decimal DiscountPercent, TaxRateSummary TaxRate, decimal TaxPercent, decimal NetAmount);

public sealed record InvoiceTaxDto(decimal TaxPercent, decimal NetAmount, decimal TaxAmount);

public sealed record InvoicePaymentDto(Guid Id, DateOnly PaymentDate, decimal Amount, ReferenceSummary? PaymentMethod, string? Note, DateTime CreatedAt, Guid CreatedBy);

public sealed record InvoiceLinkDto(Guid Id, string Number);

/// <summary>
/// An invoice. Amounts are in the tenant's currency: <c>NetAmount</c> without tax, <c>TaxAmount</c> the sum of
/// <c>Taxes</c> (one per rate), <c>TotalAmount</c> what is owed; <c>OpenAmount</c> what is still unpaid.
/// <c>CancellationOf</c> is set on a cancelling invoice, <c>CancelledBy</c> on a cancelled one.
/// </summary>
public sealed record InvoiceDto(
    Guid Id, string Status, string? Number, ReferenceSummary? Customer, DateOnly IssueDate, DateOnly? DeliveryDate, DateOnly? DueDate,
    ReferenceSummary? PaymentMethod, ReferenceSummary? Warehouse, string? Reference, string? Note,
    decimal NetAmount, decimal TaxAmount, decimal TotalAmount, decimal PaidAmount, decimal OpenAmount, string PaymentStatus,
    IReadOnlyList<InvoiceTaxDto> Taxes, IReadOnlyList<InvoiceLineDto> Lines, IReadOnlyList<InvoicePaymentDto> Payments,
    StockDocumentLinkDto? StockDocument, InvoiceLinkDto? CancellationOf, InvoiceLinkDto? CancelledBy,
    DateTime CreatedAt, DateTime UpdatedAt, Guid CreatedBy, Guid UpdatedBy, DateTime? IssuedAt, Guid? IssuedBy);

public sealed record InvoiceSummaryDto(
    Guid Id, string Status, string? Number, ReferenceSummary? Customer, DateOnly IssueDate, DateOnly? DueDate,
    decimal NetAmount, decimal TaxAmount, decimal TotalAmount, decimal PaidAmount, decimal OpenAmount, string PaymentStatus,
    bool IsCancellation, int LineCount, DateTime CreatedAt, DateTime UpdatedAt);

public sealed record InvoiceDeleted(bool Deleted = true);

/// <summary>The company's own data; every field is null (and <c>LegalName</c> the tenant's name) until it is first saved.</summary>
public sealed record CompanyProfileDto(
    string LegalName, string? TaxId, string? AddressLine1, string? AddressLine2, string? PostalCode, string? City, string? Region,
    string? CountryCode, string? Iban, string? Email, string? Phone, string? Website, string? InvoiceFooter, string TenantCode,
    DateTime? UpdatedAt, Guid? UpdatedBy, DateTime? RulesReviewedAt = null);

/// <summary>Spec A01: when the business rules were marked as reviewed.</summary>
public sealed record RulesReviewedDto(DateTime RulesReviewedAt);
