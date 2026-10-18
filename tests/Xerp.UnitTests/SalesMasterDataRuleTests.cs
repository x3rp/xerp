using Xerp.Application.Common;
using Xerp.Application.Ports;
using Xerp.Application.Sales;
using Xerp.Domain.Common;
using Xerp.Domain.Rules;
using Xerp.Domain.Sales;

namespace Xerp.UnitTests;

/// <summary>
/// Spec P01, AC-03: the decisions of 6.2 for rate, kind, salesPrice, taxRateIds and barcode, and the
/// barcode-uniqueness decision, as pure functions; and the three required rules of an article's sales data
/// judged by a tenant's values (R10).
/// </summary>
public class SalesMasterDataRuleTests
{
    private static TenantRules With(params RuleDefinition[] setToTrue) =>
        new(setToTrue.ToDictionary(r => r.Key, _ => true));

    [Theory]
    [InlineData("0", true)]
    [InlineData("25", true)]
    [InlineData("25.0000", true)]
    [InlineData("25.00000", true)] // the same rate: trailing zeros are not decimals
    [InlineData("12.3456", true)]
    [InlineData("100", true)]
    [InlineData("-1", false)]
    [InlineData("100.01", false)]
    [InlineData("12.34567", false)]
    public void Rate_is_a_percentage_with_at_most_four_decimals(string rate, bool valid) =>
        Assert.Equal(valid, SalesDataRules.IsValidRate(decimal.Parse(rate, System.Globalization.CultureInfo.InvariantCulture)));

    [Theory]
    [InlineData("cash", true)]
    [InlineData("card", true)]
    [InlineData("transfer", true)]
    [InlineData("other", true)]
    [InlineData("Cash", false)]
    [InlineData("cheque", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Kind_is_one_of_four_exact_names(string? name, bool valid)
    {
        Assert.Equal(valid, PaymentMethodKindNames.TryParse(name, out var kind));
        if (valid)
            Assert.Equal(name, kind.ToName());
    }

    [Theory]
    [InlineData("0", true)]
    [InlineData("1.8", true)]
    [InlineData("999999999.999999", true)]
    [InlineData("-1", false)]
    [InlineData("1000000000", false)]
    [InlineData("1.2345678", false)]
    public void Sales_price_has_the_bounds_of_a_unit_price_and_zero_is_a_price(string price, bool valid) =>
        Assert.Equal(valid, SalesDataRules.IsValidSalesPrice(decimal.Parse(price, System.Globalization.CultureInfo.InvariantCulture)));

    [Fact]
    public void Tax_rates_are_at_most_four_and_each_once()
    {
        var (a, b) = (Guid.NewGuid(), Guid.NewGuid());
        Assert.False(SalesDataRules.TooManyTaxRates(4));
        Assert.True(SalesDataRules.TooManyTaxRates(5));
        Assert.Empty(SalesDataRules.RepeatedTaxRates([a, b]));
        Assert.Equal([1], SalesDataRules.RepeatedTaxRates([a, a]));
        Assert.Equal([2, 3], SalesDataRules.RepeatedTaxRates([a, b, a, b]));
    }

    [Theory]
    [InlineData(" 123 ", "123")]
    [InlineData("00123", "00123")]
    [InlineData("aB-9/~!", "aB-9/~!")]
    public void Barcode_is_trimmed_and_otherwise_kept(string input, string stored)
    {
        Assert.True(SalesDataRules.TryNormalizeBarcode(input, out var barcode));
        Assert.Equal(stored, barcode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("12 3")]
    [InlineData("café")]
    [InlineData("12\t3")]
    public void Barcode_without_printable_ascii_form_is_refused(string? input) =>
        Assert.False(SalesDataRules.TryNormalizeBarcode(input, out _));

    [Fact]
    public void Barcode_length_is_at_most_fifty_and_comparison_is_exact()
    {
        Assert.True(SalesDataRules.TryNormalizeBarcode(new string('1', 50), out _));
        Assert.False(SalesDataRules.TryNormalizeBarcode(new string('1', 51), out _));
        Assert.True(SalesDataRules.SameBarcode("abc", "abc"));
        Assert.False(SalesDataRules.SameBarcode("abc", "ABC"));
        Assert.False(SalesDataRules.SameBarcode("00123", "123"));
    }

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, false)]
    public void Barcode_of_another_article_is_refused_only_when_the_rule_is_true(bool unique, bool anotherHasIt, bool refused)
    {
        Assert.Equal(refused, SalesDataRules.BarcodeTakenByAnotherArticle(unique, anotherHasIt));
        // The same article a second time is an invariant: no boolean of a rule enters the decision.
        Assert.True(SalesDataRules.ArticleAlreadyHasBarcode(true));
        Assert.False(SalesDataRules.ArticleAlreadyHasBarcode(false));
    }

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    public void Required_value_is_missing_only_when_the_rule_is_true(bool required, bool missing, bool refused) =>
        Assert.Equal(refused, MasterDataRules.RequiredValueMissing(required, missing));

    [Fact]
    public void Registry_has_forty_rules_and_the_ten_of_P01_at_their_defaults()
    {
        Assert.Equal(40, RuleRegistry.All.Count);
        Assert.True(RuleRegistry.ArticleBarcodeUnique.Default);
        Assert.All(RuleRegistry.SalesDataRequiredFields.Values, r => Assert.False(r.Default));
        Assert.Equal(["categoryId", "taxRateIds", "salesPrice"], RuleRegistry.SalesDataRequiredFields.Keys);
        foreach (var master in RuleRegistry.SalesMasters)
        {
            Assert.True(master.CodeChangeable.Default);
            Assert.False(master.NameUnique.Default);
            Assert.Empty(master.RequiredFields);
        }
    }

    private static SetArticleSalesDataInput Empty => new() { CategoryId = null, TaxRateIds = [], SalesPrice = null };

    [Fact]
    public void Empty_sales_data_passes_at_defaults() =>
        Assert.True(SalesValidation.SalesData(Empty, TenantRules.Defaults).IsSuccess);

    [Fact]
    public void A_missing_property_is_an_invariant_and_names_no_rule()
    {
        var error = SalesValidation.SalesData(new SetArticleSalesDataInput { CategoryId = null, SalesPrice = null },
            With(RuleRegistry.ArticleTaxRateRequired)).Error!;
        Assert.Equal(["taxRateIds"], error.Errors!.Keys);
        Assert.Null(error.Rules);
    }

    [Fact]
    public void Each_required_rule_refuses_its_member_and_names_itself()
    {
        var error = SalesValidation.SalesData(Empty,
            With(RuleRegistry.ArticleCategoryRequired, RuleRegistry.ArticleTaxRateRequired, RuleRegistry.ArticleSalesPriceRequired)).Error!;
        Assert.Equal(["categoryId", "taxRateIds", "salesPrice"], error.Errors!.Keys);
        Assert.Equal(
            ["article.categoryRequired", "article.taxRateRequired", "article.salesPriceRequired"], error.Rules!.Select(r => r.Key));
        Assert.All(error.Rules!, r => Assert.True(r.Value));
    }

    [Fact]
    public void E5_An_invariant_error_stands_beside_a_rule_refusal_and_is_in_no_rules_fields()
    {
        var error = SalesValidation.SalesData(new SetArticleSalesDataInput { CategoryId = null, TaxRateIds = [], SalesPrice = -1 },
            With(RuleRegistry.ArticleCategoryRequired)).Error!;
        Assert.Equal(["categoryId", "salesPrice"], error.Errors!.Keys);
        var refusal = Assert.Single(error.Rules!);
        Assert.Equal("article.categoryRequired", refusal.Key);
        Assert.Equal(["categoryId"], refusal.Fields);
    }

    [Fact]
    public void E2_E3_Zero_is_a_price_and_tax_rate_errors_carry_their_index()
    {
        var a = Guid.NewGuid().ToString();
        Assert.True(SalesValidation.SalesData(Empty with { SalesPrice = 0 }, With(RuleRegistry.ArticleSalesPriceRequired)).IsSuccess);
        Assert.Equal(["taxRateIds[1]"], SalesValidation.SalesData(Empty with { TaxRateIds = [a, a] }, TenantRules.Defaults).Error!.Errors!.Keys);
        Assert.Equal(["taxRateIds[0]"], SalesValidation.SalesData(Empty with { TaxRateIds = ["x"] }, TenantRules.Defaults).Error!.Errors!.Keys);
        var five = Enumerable.Range(0, 5).Select(_ => (string?)Guid.NewGuid().ToString()).ToList();
        Assert.Equal(["taxRateIds"], SalesValidation.SalesData(Empty with { TaxRateIds = five }, TenantRules.Defaults).Error!.Errors!.Keys);
        Assert.Equal(["categoryId"], SalesValidation.SalesData(Empty with { CategoryId = "x" }, TenantRules.Defaults).Error!.Errors!.Keys);
    }
}
