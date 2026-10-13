using System.Globalization;
using Xerp.Application.Common;
using Xerp.Application.Ports;
using Xerp.Domain.Rules;
using Xerp.Domain.Sales;

namespace Xerp.Application.Sales;

/// <summary>
/// Input rules of spec P01. No I/O. Everything here is an invariant (6.2) except the three required members
/// of an article's sales data, which the tenant's rules decide (R10).
/// </summary>
public static class SalesValidation
{
    public static Result<CodedMasterValues> Category(ArticleCategoryInput input, bool replace)
    {
        var errors = new ValidationErrors();
        var (code, name, isActive) = Common(errors, input.Code, input.Name, input.IsActive, replace);
        return errors.Any ? errors.ToError() : new CodedMasterValues(code, name, isActive);
    }

    public static Result<TaxRateValues> TaxRate(TaxRateInput input, bool replace)
    {
        var errors = new ValidationErrors();
        var (code, name, isActive) = Common(errors, input.Code, input.Name, input.IsActive, replace);
        if (input.Rate is not { } rate)
            errors.Add("rate", "rate is required: the percentage, as a JSON number.");
        else if (!SalesDataRules.IsValidRate(rate))
            errors.Add("rate", $"rate must be between 0 and {SalesDataRules.MaxRate.ToString(CultureInfo.InvariantCulture)}, with at most {SalesDataRules.RateDecimalPlaces} decimal places.");
        return errors.Any ? errors.ToError() : new TaxRateValues(code, name, SalesDataRules.Normalize(input.Rate!.Value), isActive);
    }

    public static Result<PaymentMethodValues> PaymentMethod(PaymentMethodInput input, bool replace)
    {
        var errors = new ValidationErrors();
        var (code, name, isActive) = Common(errors, input.Code, input.Name, input.IsActive, replace);
        var kind = Kind(errors, input.Kind, required: true);
        return errors.Any ? errors.ToError() : new PaymentMethodValues(code, name, kind!.Value, isActive);
    }

    /// <summary>The list arguments every sales master shares (spec 001, R9).</summary>
    public static (string? Search, int Limit, int Offset) List(ListCodedMastersInput input, ValidationErrors errors) =>
        (ListRules.Search(errors, input.Search), ListRules.Limit(errors, input.Limit), ListRules.Offset(errors, input.Offset));

    /// <summary>
    /// R8, R9, then R10: the shape of the three members is an invariant; whether one may be empty is the
    /// tenant's rule, judged only where the member has no other error.
    /// </summary>
    public static Result<SalesDataValues> SalesData(SetArticleSalesDataInput input, TenantRules rules)
    {
        var errors = new ValidationErrors();
        // Each member is judged by its rule right after its own form, so errors keep the order of the members.
        var required = new RequiredFields(rules, RuleRegistry.SalesDataRequiredFields);

        Guid? categoryId = null;
        if (!input.Has(nameof(input.CategoryId)))
            errors.Add("categoryId", "categoryId is required: the id of an article category, or null for none.");
        else if (input.CategoryId is not null)
        {
            if (Guid.TryParse(input.CategoryId, out var parsed)) categoryId = parsed;
            else errors.Add("categoryId", "categoryId must be the id (UUID) of an article category, or null.");
        }

        required.CheckValue(errors, "categoryId", input.CategoryId is null);

        var taxRateIds = new List<Guid?>();
        if (input.TaxRateIds is not { } given)
            errors.Add("taxRateIds", "taxRateIds is required: an array of tax rate ids, empty for none.");
        else if (SalesDataRules.TooManyTaxRates(given.Count))
            errors.Add("taxRateIds", $"taxRateIds must have at most {SalesDataRules.MaxTaxRates} elements.");
        else
        {
            taxRateIds = given.Select(id => Guid.TryParse(id, out var parsed) ? parsed : (Guid?)null).ToList();
            for (var i = 0; i < taxRateIds.Count; i++)
                if (taxRateIds[i] is null)
                    errors.Add($"taxRateIds[{i}]", "Each element must be the id (UUID) of a tax rate.");
            foreach (var i in SalesDataRules.RepeatedTaxRates(taxRateIds))
                errors.Add($"taxRateIds[{i}]", "This tax rate is already in the list: a tax applies once.");
        }

        required.CheckValue(errors, "taxRateIds", input.TaxRateIds is { Count: 0 });

        if (!input.Has(nameof(input.SalesPrice)))
            errors.Add("salesPrice", "salesPrice is required: the price with tax as a JSON number, or null for none.");
        else if (input.SalesPrice is { } price && !SalesDataRules.IsValidSalesPrice(price))
            errors.Add("salesPrice",
                $"salesPrice must be between 0 and {SalesDataRules.MaxSalesPrice.ToString(CultureInfo.InvariantCulture)}, "
                + $"with at most {SalesDataRules.SalesPriceDecimalPlaces} decimal places.");

        required.CheckValue(errors, "salesPrice", input.SalesPrice is null);

        if (errors.Any)
            return errors.ToError();
        return new SalesDataValues(
            categoryId, taxRateIds.Select(id => id!.Value).ToList(),
            input.SalesPrice is { } value ? SalesDataRules.Normalize(value) : null);
    }

    public static Result<SalesDataListQuery> SalesDataList(ListArticleSalesDataInput input)
    {
        var errors = new ValidationErrors();
        var search = ListRules.Search(errors, input.Search);
        var categoryId = IdFilter(errors, "categoryId", input.CategoryId);
        var taxRateId = IdFilter(errors, "taxRateId", input.TaxRateId);
        var limit = ListRules.Limit(errors, input.Limit);
        var offset = ListRules.Offset(errors, input.Offset);
        return errors.Any ? errors.ToError() : new SalesDataListQuery(search, categoryId, taxRateId, input.Priced, input.IsActive, limit, offset);
    }

    public static Result<BarcodeValues> Barcode(CreateArticleBarcodeInput input)
    {
        var errors = new ValidationErrors();
        if (!Guid.TryParse(input.ArticleId, out var articleId))
            errors.Add("articleId", string.IsNullOrEmpty(input.ArticleId)
                ? "articleId is required."
                : "articleId must be the id (UUID) of an article, not its code.");
        if (!SalesDataRules.TryNormalizeBarcode(input.Barcode, out var barcode))
            errors.Add("barcode", input.Barcode is null ? "barcode is required." : BarcodeForm);
        return errors.Any ? errors.ToError() : new BarcodeValues(articleId, barcode);
    }

    public static Result<BarcodeListQuery> BarcodeList(ListArticleBarcodesInput input)
    {
        var errors = new ValidationErrors();
        string? barcode = null;
        if (!string.IsNullOrEmpty(input.Barcode) && !SalesDataRules.TryNormalizeBarcode(input.Barcode, out barcode))
            errors.Add("barcode", BarcodeForm);
        var articleId = IdFilter(errors, "articleId", input.ArticleId);
        var limit = ListRules.Limit(errors, input.Limit);
        var offset = ListRules.Offset(errors, input.Offset);
        return errors.Any ? errors.ToError() : new BarcodeListQuery(barcode, articleId, limit, offset);
    }

    private static readonly string BarcodeForm =
        $"barcode must be 1-{SalesDataRules.BarcodeMaxLength} printable ASCII characters without spaces.";

    private static Guid? IdFilter(ValidationErrors errors, string field, string? input)
    {
        if (string.IsNullOrEmpty(input))
            return null;
        if (Guid.TryParse(input, out var id))
            return id;
        errors.Add(field, $"{field} must be a UUID.");
        return null;
    }

    private static (string Code, string Name, bool IsActive) Common(ValidationErrors errors, string? code, string? name, bool? isActive, bool replace)
    {
        var validCode = errors.Code(code);
        var validName = errors.Name(name);
        if (replace && isActive is null)
            errors.Add("isActive", "isActive is required.");
        return (validCode, validName, isActive ?? true);
    }

    /// <summary>R3: an exact name of a kind. As a list filter it is optional, and empty is absent.</summary>
    public static PaymentMethodKind? Kind(ValidationErrors errors, string? input, bool required)
    {
        if (PaymentMethodKindNames.TryParse(input, out var kind))
            return kind;
        if (required || !string.IsNullOrEmpty(input))
            errors.Add("kind", $"kind must be one of: {string.Join(", ", PaymentMethodKindNames.All)}.");
        return null;
    }
}
