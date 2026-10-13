using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xerp.IntegrationTests.Support;

namespace Xerp.IntegrationTests;

/// <summary>
/// Spec P01, part B, AC-40 to AC-50: the sales data of an article — category, tax rates, sales price — is a
/// sub-resource that Set replaces as a whole (R7–R15); three rules can require its members (R10).
/// </summary>
[Collection(XerpCollection.Name)]
public class ArticleSalesDataTests(XerpFixture app)
{
    private static readonly SalesMaster Categories = SalesMaster.Categories;
    private static readonly SalesMaster Tax = SalesMaster.TaxRates;

    private static Guid Unknown() => Guid.CreateVersion7();

    // ---- AC-40 ----

    [Fact]
    public async Task AC40_A_new_article_has_empty_sales_data_and_the_article_gains_no_member()
    {
        var s = await SalesSetup.NewAsync(app);
        var article = await s.ArticleAsync("ESP", "Espresso");
        var before = await Art.GetAsync(s.Http, article.Id());

        var data = await SalesData.GetAsync(s.Http, article.Id());

        Assert.Equal(new[] { "article", "category", "salesPrice", "taxRates", "updatedAt", "updatedBy" }, data.PropertyNames());
        SalesData.AssertNothingSet(data);
        JsonBody.AssertNull(data, "updatedAt", "updatedBy");
        var summary = data.GetProperty("article");
        Assert.Equal(new[] { "code", "id", "name" }, summary.PropertyNames());
        Assert.Equal((article.Id(), "ESP", "Espresso"), (summary.Id(), summary.Str("code"), summary.Str("name")));
        using (var unknown = await s.Http.GetAsync(SalesData.Path(Unknown())))
            await HttpAssert.NotFoundAsync(unknown);
        using (var unknown = await SalesData.PutAsync(s.Http, Unknown(), SalesData.Empty()))
            await HttpAssert.NotFoundAsync(unknown);

        // "The article itself does not change" (section 2, R12): not a member, not updatedAt.
        var category = await s.CategoryAsync("DRINKS");
        await SalesData.SetAsync(s.Http, article.Id(), category.Id(), [], 1.8m);
        McpAssert.JsonEqual(before, await Art.GetAsync(s.Http, article.Id()), "The article changed with its sales data");
    }

    // ---- AC-41 ----

    [Fact]
    public async Task AC41_Set_stores_category_tax_rates_and_price_and_a_second_Set_empties_them()
    {
        var s = await SalesSetup.NewAsync(app);
        var article = await s.ArticleIdAsync("ESP");
        var category = await s.CategoryAsync("DRINKS", "Drinks");
        var vat = await s.TaxRateAsync("VAT25", 25);
        var local = await s.TaxRateAsync("LOCAL3", 3);

        var set = await SalesData.SetAsync(s.Http, article, category.Id(), [vat.Id(), local.Id()], 1.8m);
        var read = await SalesData.GetAsync(s.Http, article);

        McpAssert.JsonEqual(set, read, "GET differs from the result of Set");
        var summary = read.GetProperty("category");
        Assert.Equal(new[] { "code", "id", "name" }, summary.PropertyNames());
        Assert.Equal((category.Id(), "DRINKS", "Drinks"), (summary.Id(), summary.Str("code"), summary.Str("name")));
        // Ordered by code, each with its rate.
        Assert.Equal(new[] { "LOCAL3", "VAT25" }, SalesData.TaxCodes(read));
        var rates = read.GetProperty("taxRates").EnumerateArray().ToArray();
        Assert.All(rates, r => Assert.Equal(new[] { "code", "id", "name", "rate" }, r.PropertyNames()));
        Assert.Equal(new[] { 3m, 25m }, rates.Select(r => r.GetProperty("rate").GetDecimal()));
        Assert.Equal(new[] { local.Id(), vat.Id() }, rates.Select(r => r.Id()));
        Assert.Equal(1.8m, read.GetProperty("salesPrice").GetDecimal());
        Assert.Equal(s.Tenant.ApiKeyId, read.GetProperty("updatedBy").GetGuid());
        var firstTime = read.Time("updatedAt");

        await Task.Delay(20);
        var emptied = await SalesData.SetAsync(s.Http, article, SalesData.Empty());

        SalesData.AssertNothingSet(emptied);
        McpAssert.JsonEqual(emptied, await SalesData.GetAsync(s.Http, article));
        Assert.True(emptied.Time("updatedAt") > firstTime, "updatedAt is not kept current by the second Set.");
        Assert.Equal(s.Tenant.ApiKeyId, emptied.GetProperty("updatedBy").GetGuid());
    }

    // ---- AC-42 ----

    [Fact]
    public async Task AC42_R7_Sales_data_of_a_service_article_and_of_an_inactive_article()
    {
        var s = await SalesSetup.NewAsync(app);
        var service = await s.ArticleAsync("SRV", "Delivery", "service");
        var inactive = await s.ArticleIdAsync("OLD");
        await Stock.SetArticleActiveAsync(s.Http, inactive, false);
        var category = await s.CategoryAsync("DRINKS");
        var vat = await s.TaxRateAsync("VAT25");

        foreach (var article in new[] { service.Id(), inactive })
        {
            SalesData.AssertNothingSet(await SalesData.GetAsync(s.Http, article));
            var set = await SalesData.SetAsync(s.Http, article, category.Id(), [vat.Id()], 5m);
            Assert.Equal(category.Id(), set.GetProperty("category").Id());
            Assert.Equal(new[] { "VAT25" }, SalesData.TaxCodes(set));
            Assert.Equal(5m, (await SalesData.GetAsync(s.Http, article)).GetProperty("salesPrice").GetDecimal());
        }
    }

    // ---- AC-43 ----

    public static TheoryData<string, string, string> BadShapes() => new()
    {
        { "no taxRateIds", """{ "categoryId": null, "salesPrice": null }""", "taxRateIds" },
        { "no categoryId", """{ "taxRateIds": [], "salesPrice": null }""", "categoryId" },
        { "no salesPrice", """{ "categoryId": null, "taxRateIds": [] }""", "salesPrice" },
        { "negative price", """{ "categoryId": null, "taxRateIds": [], "salesPrice": -1 }""", "salesPrice" },
        { "quoted price", """{ "categoryId": null, "taxRateIds": [], "salesPrice": "1.8" }""", "salesPrice" },
        { "seven decimals", """{ "categoryId": null, "taxRateIds": [], "salesPrice": 1.2345678 }""", "salesPrice" },
        { "a rate twice", """{ "categoryId": null, "taxRateIds": ["$a", "$a"], "salesPrice": null }""", "taxRateIds[1]" },
        { "five rates", """{ "categoryId": null, "taxRateIds": ["$a", "$b", "$c", "$d", "$e"], "salesPrice": null }""", "taxRateIds" },
        { "a rate that is no id", """{ "categoryId": null, "taxRateIds": ["$a", "x"], "salesPrice": null }""", "taxRateIds[1]" },
        { "category no id", """{ "categoryId": "x", "taxRateIds": [], "salesPrice": null }""", "categoryId" },
        { "unknown property", """{ "categoryId": null, "taxRateIds": [], "salesPrice": null, "foo": 1 }""", "foo" },
    };

    [Theory]
    [MemberData(nameof(BadShapes))]
    public async Task AC43_R8_R9_E3_A_malformed_body_is_refused_with_its_key_and_names_no_rule(string what, string json, string key)
    {
        var s = await SalesSetup.NewAsync(app);
        var article = await s.ArticleIdAsync("ESP");
        var category = await s.CategoryAsync("DRINKS");
        var stored = await SalesData.SetAsync(s.Http, article, category.Id(), [], 2m);
        foreach (var name in new[] { "a", "b", "c", "d", "e" })
            json = json.Replace("$" + name, (await s.TaxRateAsync($"T-{name}")).Id().ToString(), StringComparison.Ordinal);

        var problem = await MasterRules.InvariantAsync(s.Http.PutAsync(SalesData.Path(article), HttpAssert.Raw(json)), key);

        Assert.True(problem.ValueKind == JsonValueKind.Object, what);
        McpAssert.JsonEqual(stored, await SalesData.GetAsync(s.Http, article), $"{what}: the sales data changed");
    }

    [Fact]
    public async Task AC43_E2_A_price_of_zero_is_a_price()
    {
        var s = await SalesSetup.NewAsync(app);
        var article = await s.ArticleIdAsync("FREE");
        await s.ArticleIdAsync("NONE");
        await Rules.SetAsync(s.Http, SalesRules.SalesPriceRequired, true);

        var set = await SalesData.SetAsync(s.Http, article, null, [], 0m);

        Assert.Equal(JsonValueKind.Number, set.GetProperty("salesPrice").ValueKind);
        Assert.Equal(0m, set.GetProperty("salesPrice").GetDecimal());
        Assert.Equal(new[] { "FREE" }, SalesData.ArticleCodes(await SalesData.ListAsync(s.Http, "?priced=true")));
        // The bound of 6.2 and four tax rates are accepted.
        var rates = new List<Guid>();
        foreach (var code in new[] { "T1", "T2", "T3", "T4" })
            rates.Add((await s.TaxRateAsync(code)).Id());
        var bound = await SalesData.SetAsync(s.Http, article, null, [.. rates], 999999999.999999m);
        Assert.Equal(999999999.999999m, bound.GetProperty("salesPrice").GetDecimal());
        Assert.Equal(4, bound.GetProperty("taxRates").GetArrayLength());
    }

    // ---- AC-44 ----

    [Fact]
    public async Task AC44_R11_An_unknown_or_inactive_reference_is_refused_and_the_first_one_answers_alone()
    {
        var s = await SalesSetup.NewAsync(app);
        var article = await s.ArticleIdAsync("ESP");
        var vat = await s.TaxRateAsync("VAT25");
        var closed = await s.CategoryAsync("CLOSED");
        await Categories.SetActiveAsync(s.Http, closed, false);

        using (var response = await SalesData.PutAsync(s.Http, article, SalesData.Body(Unknown(), [vat.Id()], 1m)))
            Assert.Equal(new[] { "categoryId" }, McpAssert.ErrorKeys(await HttpAssert.ReferenceNotFoundAsync(response, "categoryId")));
        using (var response = await SalesData.PutAsync(s.Http, article, SalesData.Body(closed.Id(), [vat.Id()], 1m)))
            Rules.AssertNamesNoRule(await HttpAssert.ReferenceInactiveAsync(response, "categoryId"));
        using (var response = await SalesData.PutAsync(s.Http, article, SalesData.Body(null, [vat.Id(), Unknown()], 1m)))
            Assert.Equal(new[] { "taxRateIds[1]" }, McpAssert.ErrorKeys(await HttpAssert.ReferenceNotFoundAsync(response, "taxRateIds[1]")));
        using (var response = await SalesData.PutAsync(s.Http, article, SalesData.Body(Unknown(), [Unknown()], 1m)))
            Assert.Equal(new[] { "categoryId" }, McpAssert.ErrorKeys(await HttpAssert.ReferenceNotFoundAsync(response, "categoryId")));
        // Validation comes before the article and the references.
        await MasterRules.InvariantAsync(SalesData.PutAsync(s.Http, Unknown(), SalesData.Body(Unknown(), [], -1m)), "salesPrice");

        SalesData.AssertNothingSet(await SalesData.GetAsync(s.Http, article));
        JsonBody.AssertNull(await SalesData.GetAsync(s.Http, article), "updatedAt", "updatedBy");
    }

    [Fact]
    public async Task AC44_E4_An_inactive_tax_rate_the_article_already_has_is_kept_but_not_newly_assigned()
    {
        var s = await SalesSetup.NewAsync(app);
        var has = await s.ArticleIdAsync("HAS");
        var other = await s.ArticleIdAsync("OTHER");
        var vat = await s.TaxRateAsync("VAT25");
        var category = await s.CategoryAsync("DRINKS");
        await SalesData.SetAsync(s.Http, has, category.Id(), [vat.Id()], 1m);
        await Tax.SetActiveAsync(s.Http, vat, false);
        await Categories.SetActiveAsync(s.Http, category, false);

        var kept = await SalesData.SetAsync(s.Http, has, category.Id(), [vat.Id()], 2m);

        Assert.Equal(new[] { "VAT25" }, SalesData.TaxCodes(kept));
        Assert.Equal(category.Id(), kept.GetProperty("category").Id());
        using (var response = await SalesData.PutAsync(s.Http, other, SalesData.Body(null, [vat.Id()], 1m)))
            await HttpAssert.ReferenceInactiveAsync(response, "taxRateIds[0]");
        using (var response = await SalesData.PutAsync(s.Http, other, SalesData.Body(category.Id(), [], 1m)))
            await HttpAssert.ReferenceInactiveAsync(response, "categoryId");
        SalesData.AssertNothingSet(await SalesData.GetAsync(s.Http, other));
    }

    // ---- AC-45 ----

    [Fact]
    public async Task AC45_By_default_sales_data_may_be_saved_with_nothing_in_it()
    {
        var s = await SalesSetup.NewAsync(app);
        var article = await s.ArticleIdAsync("ESP");

        var set = await SalesData.SetAsync(s.Http, article, SalesData.Empty());

        SalesData.AssertNothingSet(set);
        Assert.Equal(s.Tenant.ApiKeyId, set.GetProperty("updatedBy").GetGuid());
        Assert.Equal(JsonValueKind.String, set.GetProperty("updatedAt").ValueKind);
    }

    // ---- AC-46 ----

    [Theory]
    [MemberData(nameof(SalesRules.RequiredData), MemberType = typeof(SalesRules))]
    public async Task AC46_R10_A_required_rule_refuses_a_Set_without_its_member_and_never_judges_the_article(string rule, string member)
    {
        var s = await SalesSetup.NewAsync(app);
        var article = await s.ArticleAsync("ESP");
        var category = await s.CategoryAsync("DRINKS");
        var vat = await s.TaxRateAsync("VAT25");
        var complete = SalesData.Body(category.Id(), [vat.Id()], 1.8m);
        var before = await SalesData.GetAsync(s.Http, article.Id());
        await Rules.SetAsync(s.Http, rule, true);

        var without = SalesData.Body(category.Id(), [vat.Id()], 1.8m);
        without[member] = member == "taxRateIds" ? new JsonArray() : null;
        await MasterRules.RequiredAsync(SalesData.PutAsync(s.Http, article.Id(), without), rule, member);
        McpAssert.JsonEqual(before, await SalesData.GetAsync(s.Http, article.Id()), "A refused Set stored something");

        var set = await SalesData.SetAsync(s.Http, article.Id(), complete);
        Assert.Equal(1.8m, set.GetProperty("salesPrice").GetDecimal());

        // An article is created and replaced with empty sales data whatever the rule says.
        var created = await s.ArticleAsync("NEW");
        await Stock.ReplaceArticleAsync(s.Http, created.Id(), Stock.ArticleBody(created).With("name", "Renamed"));
        await Stock.ReplaceArticleAsync(s.Http, article.Id(), Stock.ArticleBody(article).With("name", "Renamed too"));
        SalesData.AssertNothingSet(await SalesData.GetAsync(s.Http, created.Id()));
    }

    [Fact]
    public async Task AC46_E5_A_required_member_is_reported_with_every_other_invalid_field()
    {
        var s = await SalesSetup.NewAsync(app);
        var article = await s.ArticleIdAsync("ESP");
        await Rules.SetAsync(s.Http, SalesRules.CategoryRequired, true);

        using var response = await s.Http.PutAsync(SalesData.Path(article),
            HttpAssert.Raw("""{ "categoryId": null, "taxRateIds": [], "salesPrice": -1 }"""));
        var problem = await HttpAssert.ValidationAsync(response, "categoryId", "salesPrice");

        Assert.Equal(new[] { "categoryId", "salesPrice" }, McpAssert.ErrorKeys(problem));
        Assert.Equal(new[] { "categoryId" }, Rules.Fields(Rules.AssertRule(problem, SalesRules.CategoryRequired, true)));
    }

    // ---- AC-47 ----

    [Fact]
    public async Task AC47_Switching_a_required_rule_changes_no_stored_sales_data()
    {
        var s = await SalesSetup.NewAsync(app);
        var never = await s.ArticleIdAsync("NEVER");
        var emptied = await s.ArticleIdAsync("EMPTIED");
        await SalesData.SetAsync(s.Http, emptied, SalesData.Empty());
        var (ofNever, ofEmptied) = (await SalesData.GetAsync(s.Http, never), await SalesData.GetAsync(s.Http, emptied));
        var list = await SalesData.ListAsync(s.Http);

        foreach (var (rule, _) in SalesRules.Required)
            await Rules.SetAsync(s.Http, rule, true);

        McpAssert.JsonEqual(ofNever, await SalesData.GetAsync(s.Http, never));
        McpAssert.JsonEqual(ofEmptied, await SalesData.GetAsync(s.Http, emptied));
        McpAssert.JsonEqual(list, await SalesData.ListAsync(s.Http), "The list changed with the rules");
    }

    // ---- AC-48 ----

    [Fact]
    public async Task AC48_R5_E6_A_category_or_tax_rate_named_by_sales_data_is_IN_USE_until_it_is_not()
    {
        var s = await SalesSetup.NewAsync(app);
        var article = await s.ArticleIdAsync("ESP");
        var category = await s.CategoryAsync("DRINKS");
        var vat = await s.TaxRateAsync("VAT25");
        await SalesData.SetAsync(s.Http, article, category.Id(), [vat.Id()], 1m);
        // "Also of an inactive article."
        await Stock.SetArticleActiveAsync(s.Http, article, false);

        using (var response = await Categories.DeleteAsync(s.Http, category.Id()))
            await HttpAssert.InUseAsync(response);
        using (var response = await Tax.DeleteAsync(s.Http, vat.Id()))
            await HttpAssert.InUseAsync(response);
        Assert.Equal(category.Id(), (await SalesData.GetAsync(s.Http, article)).GetProperty("category").Id());

        await SalesData.SetAsync(s.Http, article, SalesData.Empty());
        await Categories.RemoveAsync(s.Http, category.Id());
        await Tax.RemoveAsync(s.Http, vat.Id());
    }

    [Fact]
    public async Task AC48_R14_Deleting_an_article_deletes_its_sales_data_and_its_barcodes()
    {
        var s = await SalesSetup.NewAsync(app);
        var article = await s.ArticleIdAsync("ESP");
        var category = await s.CategoryAsync("DRINKS");
        var vat = await s.TaxRateAsync("VAT25");
        await SalesData.SetAsync(s.Http, article, category.Id(), [vat.Id()], 1m);
        await Barcodes.CreateAsync(s.Http, article, "3850123456789");

        using (var deleted = await s.Http.DeleteAsync($"{Art.Path}/{article}"))
            Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);

        await Categories.RemoveAsync(s.Http, category.Id());
        await Tax.RemoveAsync(s.Http, vat.Id());
        Assert.Equal(0, (await Barcodes.ListAsync(s.Http)).Total());
        Assert.Equal(0, (await SalesData.ListAsync(s.Http)).Total());
        using var gone = await s.Http.GetAsync(SalesData.Path(article));
        await HttpAssert.NotFoundAsync(gone);
        // The barcode is free again for another article.
        await Barcodes.CreateAsync(s.Http, await s.ArticleIdAsync("NEW"), "3850123456789");
    }

    // ---- AC-49 ----

    [Fact]
    public async Task AC49_E9_The_list_has_one_item_per_article_also_when_nothing_was_set()
    {
        var s = await SalesSetup.NewAsync(app);
        foreach (var code in new[] { "B2", "A1", "C3" })
            await s.ArticleIdAsync(code);

        var list = await SalesData.ListAsync(s.Http);

        Assert.Equal(new[] { "items", "limit", "offset", "total" }, list.PropertyNames());
        Assert.Equal(3, list.Total());
        Assert.Equal(new[] { "A1", "B2", "C3" }, SalesData.ArticleCodes(list));
        foreach (var item in list.Items())
        {
            SalesData.AssertNothingSet(item);
            JsonBody.AssertNull(item, "updatedAt", "updatedBy");
            McpAssert.JsonEqual(await SalesData.GetAsync(s.Http, item.GetProperty("article").Id()), item, "A list item differs from GET");
        }
    }

    [Fact]
    public async Task AC49_R13_The_list_filters_by_category_tax_rate_price_search_and_activity_and_pages()
    {
        var s = await SalesSetup.NewAsync(app);
        var espresso = await s.ArticleIdAsync("ESP", "Espresso");
        var tea = await s.ArticleIdAsync("TEA", "Green tea");
        var cake = await s.ArticleIdAsync("CAKE", "Cake");
        await s.ArticleIdAsync("BARE", "Nothing set");
        var drinks = await s.CategoryAsync("DRINKS");
        var food = await s.CategoryAsync("FOOD");
        var vat25 = await s.TaxRateAsync("VAT25", 25);
        var vat13 = await s.TaxRateAsync("VAT13", 13);
        await SalesData.SetAsync(s.Http, espresso, drinks.Id(), [vat25.Id()], 1.8m);
        await SalesData.SetAsync(s.Http, tea, drinks.Id(), [vat13.Id(), vat25.Id()], null);
        await SalesData.SetAsync(s.Http, cake, food.Id(), [vat13.Id()], 3m);
        await Stock.SetArticleActiveAsync(s.Http, cake, false);

        async Task<string[]> CodesAsync(string query) => SalesData.ArticleCodes(await SalesData.ListAsync(s.Http, query));

        Assert.Equal(new[] { "BARE", "CAKE", "ESP", "TEA" }, await CodesAsync(""));
        Assert.Equal(new[] { "ESP", "TEA" }, await CodesAsync($"?categoryId={drinks.Id()}"));
        Assert.Equal(new[] { "CAKE", "TEA" }, await CodesAsync($"?taxRateId={vat13.Id()}"));
        Assert.Equal(new[] { "TEA" }, await CodesAsync($"?categoryId={drinks.Id()}&taxRateId={vat13.Id()}"));
        Assert.Equal(new[] { "CAKE", "ESP" }, await CodesAsync("?priced=true"));
        Assert.Equal(new[] { "BARE", "TEA" }, await CodesAsync("?priced=false"));
        Assert.Equal(new[] { "ESP" }, await CodesAsync("?search=press"));
        Assert.Equal(new[] { "TEA" }, await CodesAsync("?search=tea"));
        Assert.Equal(new[] { "CAKE" }, await CodesAsync("?isActive=false"));
        Assert.Equal(new[] { "BARE", "ESP", "TEA" }, await CodesAsync("?isActive=true"));

        // A well-formed id that names nothing: empty; a malformed one: 400.
        foreach (var filter in new[] { "categoryId", "taxRateId" })
        {
            var none = await SalesData.ListAsync(s.Http, $"?{filter}={Unknown()}");
            Assert.Equal((0, 0), (none.Total(), none.Items().Length));
            using var malformed = await s.Http.GetAsync($"{SalesData.ListPath}?{filter}=x");
            await HttpAssert.ValidationAsync(malformed, filter);
        }

        var page = await SalesData.ListAsync(s.Http, "?limit=2&offset=1");
        Assert.Equal(new[] { "CAKE", "ESP" }, SalesData.ArticleCodes(page));
        Assert.Equal((4, 2, 1), (page.Total(), page.GetProperty("limit").GetInt32(), page.GetProperty("offset").GetInt32()));
        using var badLimit = await s.Http.GetAsync($"{SalesData.ListPath}?limit=0");
        await HttpAssert.ValidationAsync(badLimit, "limit");
    }

    // ---- AC-50 ----

    [Fact]
    public async Task AC50_R15_A_renamed_category_or_tax_rate_shows_in_the_sales_data_at_the_next_read()
    {
        var s = await SalesSetup.NewAsync(app);
        var article = await s.ArticleIdAsync("ESP");
        var category = await s.CategoryAsync("DRINKS", "Drinks");
        var vat = await s.TaxRateAsync("VAT25", 25);
        var set = await SalesData.SetAsync(s.Http, article, category.Id(), [vat.Id()], 1m);

        await Categories.ReplaceAsync(s.Http, category.Id(), Categories.BodyOf(category).With("code", "BEV").With("name", "Beverages"));
        await Tax.ReplaceAsync(s.Http, vat.Id(), Tax.BodyOf(vat).With("name", "PDV").With("rate", 13));

        var read = await SalesData.GetAsync(s.Http, article);
        Assert.Equal(("BEV", "Beverages"), (read.GetProperty("category").Str("code"), read.GetProperty("category").Str("name")));
        var rate = Assert.Single(read.GetProperty("taxRates").EnumerateArray().ToArray());
        Assert.Equal(("PDV", 13m), (rate.Str("name"), rate.GetProperty("rate").GetDecimal()));
        // The masters changed, the sales data was not written.
        Assert.Equal(set.Time("updatedAt"), read.Time("updatedAt"));
    }
}
