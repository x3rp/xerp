using Xerp.Application.Common;

namespace Xerp.Application.Sales;

public sealed record ArticleCategoryDto(
    Guid Id, string Code, string Name, bool IsActive,
    DateTime CreatedAt, DateTime UpdatedAt, Guid CreatedBy, Guid UpdatedBy);

public sealed record TaxRateDto(
    Guid Id, string Code, string Name, decimal Rate, bool IsActive,
    DateTime CreatedAt, DateTime UpdatedAt, Guid CreatedBy, Guid UpdatedBy);

public sealed record PaymentMethodDto(
    Guid Id, string Code, string Name, string Kind, bool IsActive,
    DateTime CreatedAt, DateTime UpdatedAt, Guid CreatedBy, Guid UpdatedBy);

/// <summary>The body of a create and of a replace. On a create <c>IsActive</c> is optional (absent means active); a replace requires it.</summary>
public sealed record ArticleCategoryInput(string? Code = null, string? Name = null, bool? IsActive = null);

/// <summary>As <see cref="ArticleCategoryInput"/>, with the percentage, required on both (spec P01, R2).</summary>
public sealed record TaxRateInput(string? Code = null, string? Name = null, decimal? Rate = null, bool? IsActive = null);

/// <summary>As <see cref="ArticleCategoryInput"/>, with the kind, required on both (spec P01, R3).</summary>
public sealed record PaymentMethodInput(string? Code = null, string? Name = null, string? Kind = null, bool? IsActive = null);

public sealed record ListCodedMastersInput(string? Search = null, bool? IsActive = null, int? Limit = null, int? Offset = null);

public sealed record ListPaymentMethodsInput(
    string? Search = null, string? Kind = null, bool? IsActive = null, int? Limit = null, int? Offset = null);

/// <summary>Validated field values every sales master has.</summary>
public record CodedMasterValues(string Code, string Name, bool IsActive);

public sealed record TaxRateValues(string Code, string Name, decimal Rate, bool IsActive) : CodedMasterValues(Code, Name, IsActive);

public sealed record PaymentMethodValues(string Code, string Name, Xerp.Domain.Sales.PaymentMethodKind Kind, bool IsActive)
    : CodedMasterValues(Code, Name, IsActive);

/// <summary>The result of a delete (MCP: <c>{ "deleted": true }</c>; HTTP: 204).</summary>
public sealed record SalesRecordDeleted(bool Deleted = true);

/// <summary>A tax rate as an article's sales data shows it: the summary of ADR-0008 and the current percentage.</summary>
public sealed record TaxRateSummary(Guid Id, string Code, string Name, decimal Rate);

/// <summary>
/// The sales data of an article (spec P01, 4.B). <c>Category</c> and <c>SalesPrice</c> are null when not set;
/// <c>UpdatedAt</c> and <c>UpdatedBy</c> are null for an article whose sales data was never set.
/// </summary>
public sealed record ArticleSalesDataDto(
    ReferenceSummary Article,
    ReferenceSummary? Category,
    IReadOnlyList<TaxRateSummary> TaxRates,
    decimal? SalesPrice,
    DateTime? UpdatedAt,
    Guid? UpdatedBy);

/// <summary>
/// The body of a Set (spec P01, R8): all three properties must be present, and two of them may be null, so the
/// input remembers which were given.
/// </summary>
public record SetArticleSalesDataInput : TrackedInput
{
    private readonly string? _categoryId;
    private readonly IReadOnlyList<string?>? _taxRateIds;
    private readonly decimal? _salesPrice;

    public string? CategoryId { get => _categoryId; init => _categoryId = Given(value); }
    public IReadOnlyList<string?>? TaxRateIds { get => _taxRateIds; init => _taxRateIds = Given(value); }
    public decimal? SalesPrice { get => _salesPrice; init => _salesPrice = Given(value); }
}

/// <summary>The arguments of a Set without a URL path: the article and the body in one object.</summary>
public sealed record SetArticleSalesDataArguments : SetArticleSalesDataInput
{
    public string? ArticleId { get; init; }
}

/// <summary>How a client without a URL path names an article's sales data.</summary>
public sealed record ArticleSalesDataAddressInput(string? ArticleId = null);

public sealed record ListArticleSalesDataInput(
    string? Search = null, string? CategoryId = null, string? TaxRateId = null, bool? Priced = null, bool? IsActive = null,
    int? Limit = null, int? Offset = null);

/// <summary>Validated values of a Set: well-formed, not yet known to exist.</summary>
public sealed record SalesDataValues(Guid? CategoryId, IReadOnlyList<Guid> TaxRateIds, decimal? SalesPrice);

public sealed record SalesDataListQuery(
    string? Search, Guid? CategoryId, Guid? TaxRateId, bool? Priced, bool? IsActive, int Limit, int Offset);

public sealed record ArticleBarcodeDto(Guid Id, string Barcode, ReferenceSummary Article, DateTime CreatedAt, Guid CreatedBy);

public sealed record CreateArticleBarcodeInput(string? ArticleId = null, string? Barcode = null);

public sealed record ListArticleBarcodesInput(string? Barcode = null, string? ArticleId = null, int? Limit = null, int? Offset = null);

public sealed record BarcodeValues(Guid ArticleId, string Barcode);

public sealed record BarcodeListQuery(string? Barcode, Guid? ArticleId, int Limit, int Offset);
