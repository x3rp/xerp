using Xerp.Domain.Common;

namespace Xerp.Domain.Sales;

/// <summary>
/// The sales data of one article (spec P01, 4.B): its category and its sales price, tax included. At most one
/// row per article; an article without a row has empty sales data. Its tax rates are <see cref="ArticleTaxRate"/>.
/// </summary>
public sealed class ArticleSalesData : ITenantOwned
{
    private ArticleSalesData() { }

    /// <summary>Stamped by the DbContext from the current tenant when the row is inserted.</summary>
    public Guid TenantId { get; private set; }

    public Guid ArticleId { get; private set; }
    public Guid? CategoryId { get; private set; }

    /// <summary>What one base unit of the article sells for, tax included (P01, 6.2); null when not priced.</summary>
    public decimal? SalesPrice { get; private set; }

    public DateTime UpdatedAt { get; private set; }
    public Guid UpdatedBy { get; private set; }

    public static ArticleSalesData Create(Guid articleId, Guid? categoryId, decimal? salesPrice, DateTime now, Guid actorKeyId)
    {
        var data = new ArticleSalesData { ArticleId = articleId };
        data.Set(categoryId, salesPrice, now, actorKeyId);
        return data;
    }

    /// <summary>Replaces both members (P01/R8) and attributes the change (R12).</summary>
    /// <exception cref="ArgumentException">The price breaks its rule.</exception>
    public void Set(Guid? categoryId, decimal? salesPrice, DateTime now, Guid actorKeyId)
    {
        if (salesPrice is { } price && !SalesDataRules.IsValidSalesPrice(price))
            throw new ArgumentException("A sales price is between 0 and 999999999.999999 with at most 6 decimal places.", nameof(salesPrice));
        CategoryId = categoryId;
        SalesPrice = salesPrice;
        UpdatedAt = now;
        UpdatedBy = actorKeyId;
    }
}

/// <summary>One tax rate of an article's sales data (spec P01): part of the sales data, with no audit of its own.</summary>
public sealed class ArticleTaxRate : ITenantOwned
{
    private ArticleTaxRate() { }

    /// <summary>Stamped by the DbContext from the current tenant when the row is inserted.</summary>
    public Guid TenantId { get; private set; }

    public Guid ArticleId { get; private set; }
    public Guid TaxRateId { get; private set; }

    public static ArticleTaxRate Create(Guid articleId, Guid taxRateId) => new() { ArticleId = articleId, TaxRateId = taxRateId };
}

/// <summary>
/// A barcode of an article (spec P01, 4.B): it names the article, in its base unit. Never replaced: deleted
/// and created again.
/// </summary>
public sealed class ArticleBarcode : ITenantOwned
{
    private ArticleBarcode() { }

    public Guid Id { get; private set; }

    /// <summary>Stamped by the DbContext from the current tenant when the row is inserted.</summary>
    public Guid TenantId { get; private set; }

    public Guid ArticleId { get; private set; }
    public string Barcode { get; private set; } = "";
    public DateTime CreatedAt { get; private set; }
    public Guid CreatedBy { get; private set; }

    /// <exception cref="ArgumentException">The barcode breaks its rule.</exception>
    public static ArticleBarcode Create(Guid articleId, string barcode, DateTime now, Guid actorKeyId) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            ArticleId = articleId,
            Barcode = SalesDataRules.TryNormalizeBarcode(barcode, out var normalized)
                ? normalized
                : throw new ArgumentException("A barcode is 1-50 printable ASCII characters without spaces.", nameof(barcode)),
            CreatedAt = now,
            CreatedBy = actorKeyId,
        };
}

/// <summary>
/// The decisions of spec P01, 6.2 that are invariants, as pure functions: the form of a tax rate, of a sales
/// price, of the tax rates of an article and of a barcode. The configurable decisions are
/// <see cref="MasterDataRules"/>, which receive the rule's boolean.
/// </summary>
public static class SalesDataRules
{
    public const decimal MaxRate = 100m;
    public const int RateDecimalPlaces = 4;
    public const decimal MaxSalesPrice = 999999999.999999m;
    public const int SalesPriceDecimalPlaces = 6;
    public const int MaxTaxRates = 4;
    public const int BarcodeMaxLength = 50;

    /// <summary>A percentage: 0 to 100, at most 4 decimal places; <c>25</c> and <c>25.0000</c> are the same rate (R2).</summary>
    public static bool IsValidRate(decimal rate) => rate >= 0 && rate <= MaxRate && Normalize(rate).Scale <= RateDecimalPlaces;

    /// <summary>The bounds of a unit price; zero is a price (E2).</summary>
    public static bool IsValidSalesPrice(decimal price) =>
        price >= 0 && price <= MaxSalesPrice && Normalize(price).Scale <= SalesPriceDecimalPlaces;

    /// <summary>The same value without trailing zeros, so it is written one way everywhere.</summary>
    public static decimal Normalize(decimal value) => value / 1.0000000000000000000000000000m;

    public static bool TooManyTaxRates(int count) => count > MaxTaxRates;

    /// <summary>R9: the zero-based indexes of the ids that repeat an earlier one - a tax applies once.</summary>
    public static IReadOnlyList<int> RepeatedTaxRates(IReadOnlyList<Guid?> ids)
    {
        var seen = new HashSet<Guid>();
        var repeated = new List<int>();
        for (var i = 0; i < ids.Count; i++)
            if (ids[i] is { } id && !seen.Add(id))
                repeated.Add(i);
        return repeated;
    }

    /// <summary>
    /// 6.2: after trimming, 1-50 characters, each a printable ASCII character other than space (U+0021-U+007E).
    /// Stored as sent otherwise (R17): no case folding, no stripping of leading zeros.
    /// </summary>
    public static bool TryNormalizeBarcode(string? input, out string barcode)
    {
        barcode = input?.Trim() ?? "";
        return barcode.Length is >= 1 and <= BarcodeMaxLength && barcode.All(c => c is >= '!' and <= '~');
    }

    /// <summary>Barcodes are compared exactly and case-sensitively (6.2): <c>a</c> and <c>A</c> differ.</summary>
    public static bool SameBarcode(string a, string b) => string.Equals(a, b, StringComparison.Ordinal);

    /// <summary>An article lists a barcode once (6.2, an invariant): no rule lifts it.</summary>
    public static bool ArticleAlreadyHasBarcode(bool articleHasIt) => articleHasIt;

    /// <summary>R16: with <c>article.barcodeUnique</c> true, a barcode another article has is refused.</summary>
    public static bool BarcodeTakenByAnotherArticle(bool barcodeUnique, bool anotherArticleHasIt) =>
        MasterDataRules.Taken(barcodeUnique, anotherArticleHasIt);
}
