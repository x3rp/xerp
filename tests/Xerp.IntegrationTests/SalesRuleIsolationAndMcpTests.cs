using System.Text.Json;
using Xerp.IntegrationTests.Support;

namespace Xerp.IntegrationTests;

/// <summary>
/// Spec P01, AC-10 to AC-12 (the registry has 40 rules, the tool list 84 names), AC-80 and AC-81 (tenant
/// isolation, T1–T3) and AC-82 (T4, section 5): sales master data through the tools, with the same data, <c>code</c>,
/// <c>errors</c> keys and <c>rules</c> as HTTP.
/// </summary>
[Collection(XerpCollection.Name)]
public class SalesRuleIsolationAndMcpTests(XerpFixture app)
{
    private const string Ean = "3850123456789";

    private static readonly SalesMaster Categories = SalesMaster.Categories;
    private static readonly SalesMaster Tax = SalesMaster.TaxRates;
    private static readonly SalesMaster Pay = SalesMaster.PaymentMethods;

    private sealed record Session(SalesSetup S, McpConnection Mcp) : IAsyncDisposable
    {
        public HttpClient Http => S.Http;

        public ValueTask DisposeAsync() => Mcp.DisposeAsync();
    }

    private async Task<Session> AgentAsync()
    {
        var setup = await SalesSetup.NewAsync(app);
        var agent = await Keys.CreateAsync(app, setup.Http, "claude-sales", "agent");
        return new Session(setup, await app.McpAsync(agent.Key));
    }

    /// <summary>The tool error has the same <c>code</c>, <c>errors</c> keys and <c>rules</c> as the problem document (AC-82).</summary>
    private static void AssertSameRefusal(JsonElement problem, JsonElement toolError)
    {
        Assert.Equal(problem.Str("code"), toolError.Str("code"));
        Assert.Equal(Rules.ErrorKeysInOrder(problem), Rules.ErrorKeysInOrder(toolError));
        McpAssert.JsonEqual(problem.GetProperty("rules"), toolError.GetProperty("rules"), "'rules' of the tool error differs from HTTP");
    }

    // ---- AC-10 ----

    [Fact]
    public async Task AC10_A_new_tenant_lists_forty_rules_with_the_ten_of_sales_master_data_at_their_defaults()
    {
        var tenant = await app.NewTenantAsync();

        var list = await Rules.ListAsync(tenant.Client, "?limit=500");

        Assert.Equal(40, list.Total());
        Assert.Equal(Rules.All, list.RuleKeys());
        var expected = new (string Key, bool Default)[]
        {
            ("article.barcodeUnique", true),
            ("article.categoryRequired", false),
            ("article.salesPriceRequired", false),
            ("article.taxRateRequired", false),
            ("articleCategory.codeChangeable", true),
            ("articleCategory.nameUnique", false),
            ("paymentMethod.codeChangeable", true),
            ("paymentMethod.nameUnique", false),
            ("taxRate.codeChangeable", true),
            ("taxRate.nameUnique", false),
        };
        Assert.Equal(expected.Select(e => e.Key), SalesRules.Keys.Order(StringComparer.Ordinal));
        var rules = list.Items().ToDictionary(r => r.Str("key"));
        foreach (var (key, value) in expected)
        {
            Assert.True(rules.TryGetValue(key, out var rule), $"The registry has no rule '{key}'.");
            Rules.AssertAtDefault(rule, key);
            Rules.AssertBool(rule, "default", value);
            Assert.Equal(key.Split('.')[0], rule.Str("group"));
            JsonBody.AssertNull(rule, "updatedAt", "updatedBy");
        }
        Assert.Equal(7, (await Rules.ListAsync(tenant.Client, "?group=article&limit=500")).Total());
        foreach (var group in SalesRules.Groups)
            Assert.Equal(new[] { $"{group}.codeChangeable", $"{group}.nameUnique" }, (await Rules.ListAsync(tenant.Client, $"?group={group}")).RuleKeys());
    }

    // ---- AC-11 ----

    [Theory]
    [MemberData(nameof(SalesRules.KeyData), MemberType = typeof(SalesRules))]
    public async Task AC11_Each_of_the_ten_accepts_both_values_and_resets(string key)
    {
        var tenant = await app.NewTenantAsync();
        var other = !Rules.Default(key);

        Rules.AssertTenantValue(await Rules.SetAsync(tenant.Client, key, other), key, other, tenant.ApiKeyId);
        Rules.AssertTenantValue(await Rules.GetAsync(tenant.Client, key), key, other, tenant.ApiKeyId);
        Rules.AssertTenantValue(await Rules.SetAsync(tenant.Client, key, !other), key, !other, tenant.ApiKeyId);
        Rules.AssertAtDefault(await Rules.ResetAsync(tenant.Client, key), key);
        Assert.Equal(0, (await Rules.ListAsync(tenant.Client, "?source=tenant")).Total());
    }

    // ---- AC-12 ----

    [Fact]
    public async Task AC12_Eighty_four_tools_and_one_tool_of_each_resource_equals_HTTP()
    {
        await using var s = await AgentAsync();
        var article = await s.S.ArticleIdAsync("ESP");
        var category = await s.S.CategoryAsync("DRINKS");
        var vat = await s.S.TaxRateAsync("VAT25");
        await Pay.CreateAsync(s.Http, Pay.Body("CARD", "Card").With("kind", "card"));
        await Pay.CreateAsync(s.Http, "CASH", "Cash");
        await SalesData.SetAsync(s.Http, article, category.Id(), [vat.Id()], 1.8m);
        await Barcodes.CreateAsync(s.Http, article, Ean);

        var names = (await s.Mcp.Client.ListToolsAsync()).Select(t => t.ProtocolTool.Name).ToArray();

        // The literal list of the 84 is in McpToolListTests.
        Assert.Equal(84, names.Distinct().Count());
        McpAssert.JsonEqual(await Categories.ListAsync(s.Http, ""), await s.Mcp.OkAsync("article_category_list", new { }));
        McpAssert.JsonEqual(await Tax.GetAsync(s.Http, vat.Id()), await s.Mcp.OkAsync("tax_rate_get", new { code = "vat25" }));
        McpAssert.JsonEqual(await Pay.ListAsync(s.Http, "?kind=card"), await s.Mcp.OkAsync("payment_method_list", new { kind = "card" }));
        McpAssert.JsonEqual(await SalesData.GetAsync(s.Http, article), await s.Mcp.OkAsync("article_sales_data_get", new { articleId = article }));
        McpAssert.JsonEqual(await SalesData.ListAsync(s.Http, "?priced=true"), await s.Mcp.OkAsync("article_sales_data_list", new { priced = true }));
        McpAssert.JsonEqual(await Barcodes.ListAsync(s.Http, $"?articleId={article}"), await s.Mcp.OkAsync("article_barcode_list", new { articleId = article }));
    }

    // ---- AC-80 ----

    [Fact]
    public async Task AC80_T1_T2_A_tenant_cannot_read_change_or_reference_the_sales_master_data_of_another()
    {
        var x = await SalesSetup.NewAsync(app);
        var y = await SalesSetup.NewAsync(app);
        var records = new Dictionary<SalesMaster, JsonElement>();
        foreach (var tenant in new[] { x, y })
            foreach (var group in SalesRules.Groups)
                await Rules.SetAsync(tenant.Http, $"{group}.nameUnique", true);
        foreach (var m in SalesRules.Groups.Select(SalesMaster.Of))
            records[m] = await m.CreateAsync(x.Http, "SAME", "Same name");
        var (category, vat) = (records[Categories], records[Tax]);
        var article = await x.ArticleIdAsync("ESP");
        var data = await SalesData.SetAsync(x.Http, article, category.Id(), [vat.Id()], 1.8m);
        var barcode = await Barcodes.CreateAsync(x.Http, article, Ean);
        var own = await y.ArticleIdAsync("ESP");

        // In a path: 404.
        foreach (var (m, record) in records)
        {
            using (var read = await m.SendGetAsync(y.Http, record.Id()))
                await HttpAssert.NotFoundAsync(read);
            using (var replace = await m.PutAsync(y.Http, record.Id(), m.BodyOf(record).With("name", "Taken over")))
                await HttpAssert.NotFoundAsync(replace);
            using (var delete = await m.DeleteAsync(y.Http, record.Id()))
                await HttpAssert.NotFoundAsync(delete);
            using (var byCode = await m.ByCodeAsync(y.Http, "SAME"))
                await HttpAssert.NotFoundAsync(byCode);
            Assert.Equal(0, (await m.ListAsync(y.Http)).Total());
        }
        using (var read = await y.Http.GetAsync(SalesData.Path(article)))
            await HttpAssert.NotFoundAsync(read);
        using (var set = await SalesData.PutAsync(y.Http, article, SalesData.Empty()))
            await HttpAssert.NotFoundAsync(set);
        using (var read = await y.Http.GetAsync($"{Barcodes.Path}/{barcode.Id()}"))
            await HttpAssert.NotFoundAsync(read);
        using (var delete = await Barcodes.DeleteAsync(y.Http, barcode.Id()))
            await HttpAssert.NotFoundAsync(delete);

        // In a body: REFERENCE_NOT_FOUND.
        using (var response = await SalesData.PutAsync(y.Http, own, SalesData.Body(category.Id(), [], null)))
            await HttpAssert.ReferenceNotFoundAsync(response, "categoryId");
        using (var response = await SalesData.PutAsync(y.Http, own, SalesData.Body(null, [vat.Id()], null)))
            await HttpAssert.ReferenceNotFoundAsync(response, "taxRateIds[0]");
        using (var response = await Barcodes.PostAsync(y.Http, article, "999"))
            await HttpAssert.ReferenceNotFoundAsync(response, "articleId");

        // In a filter: nothing.
        Assert.Equal(0, (await SalesData.ListAsync(y.Http, $"?categoryId={category.Id()}")).Total());
        Assert.Equal(0, (await SalesData.ListAsync(y.Http, $"?taxRateId={vat.Id()}")).Total());
        Assert.Equal(0, (await Barcodes.ListAsync(y.Http, $"?articleId={article}")).Total());
        Assert.Equal(0, (await Barcodes.LookupAsync(y.Http, Ean)).Total());
        Assert.Equal(new[] { "ESP" }, SalesData.ArticleCodes(await SalesData.ListAsync(y.Http)));

        // T2: the same codes, names and barcodes in Y, with nameUnique and barcodeUnique true in both.
        foreach (var m in records.Keys)
            Assert.NotEqual(records[m].Id(), (await m.CreateAsync(y.Http, "SAME", "Same name")).Id());
        var ofY = await Barcodes.CreateAsync(y.Http, own, Ean);
        McpAssert.JsonEqual(ofY, Assert.Single((await Barcodes.LookupAsync(y.Http, Ean)).Items()));
        McpAssert.JsonEqual(barcode, Assert.Single((await Barcodes.LookupAsync(x.Http, Ean)).Items()));

        // X's data is as it was.
        foreach (var (m, record) in records)
            await m.AssertUnchangedAsync(x.Http, record);
        McpAssert.JsonEqual(data, await SalesData.GetAsync(x.Http, article));
    }

    // ---- AC-81 ----

    [Fact]
    public async Task AC81_T3_The_ten_values_of_one_tenant_change_nothing_for_another()
    {
        var x = await SalesSetup.NewAsync(app);
        var y = await SalesSetup.NewAsync(app);
        await SalesRules.SetAllToOtherAsync(x.Http);

        // AC-26 in Y: a code may be changed, two records share a name.
        foreach (var m in SalesRules.Groups.Select(SalesMaster.Of))
        {
            var first = await m.CreateAsync(y.Http, "C1", "Same name");
            Assert.Equal("C2", (await m.ReplaceAsync(y.Http, first.Id(), m.BodyOf(first).With("code", "C2"))).Str("code"));
            await m.CreateAsync(y.Http, "C3", "Same name");
        }
        // AC-45 in Y: sales data with nothing in it.
        var one = await y.ArticleIdAsync("A1");
        SalesData.AssertNothingSet(await SalesData.SetAsync(y.Http, one, SalesData.Empty()));
        // AC-63 in Y: a barcode belongs to one article.
        await Barcodes.CreateAsync(y.Http, one, Ean);
        await Barcodes.TakenByRuleAsync(Barcodes.PostAsync(y.Http, await y.ArticleIdAsync("A2"), Ean));
        Assert.Equal(0, (await Rules.ListAsync(y.Http, "?source=tenant&limit=500")).Total());
        Assert.Equal(0, (await Rules.ChangesAsync(y.Http)).Total());

        // X is judged by X's values.
        Assert.Equal(SalesRules.Keys.Order(StringComparer.Ordinal), (await Rules.ListAsync(x.Http, "?source=tenant&limit=500")).RuleKeys());
        var ofX = await x.ArticleIdAsync("A1");
        var vatOfX = await x.TaxRateAsync("VAT25");
        await MasterRules.RequiredAsync(SalesData.PutAsync(x.Http, ofX, SalesData.Body(null, [vatOfX.Id()], 1m)), SalesRules.CategoryRequired, "categoryId");
        await Barcodes.CreateAsync(x.Http, ofX, Ean);
        await Barcodes.CreateAsync(x.Http, await x.ArticleIdAsync("A2"), Ean);
        var category = await Categories.CreateAsync(x.Http, "C1", "Same name");
        await MasterRules.ConflictAsync(Categories.PostAsync(x.Http, Categories.Body("C3", "same name")),
            "NAME_TAKEN", Categories.NameUnique, true, "name");
        await MasterRules.ConflictAsync(Categories.PutAsync(x.Http, category.Id(), Categories.BodyOf(category).With("code", "C2")),
            "CODE_CHANGE_NOT_ALLOWED", Categories.CodeChangeable, false, "code");
    }

    // ---- AC-82 ----

    [Fact]
    public async Task AC82_T4_Sales_master_data_through_tools_only_equals_HTTP()
    {
        await using var s = await AgentAsync();
        var unit = await s.Mcp.OkAsync("uom_create", new { code = "cup", name = "Cup" });
        var article = await s.Mcp.OkAsync("article_create", new { code = "ESP", name = "Espresso", type = "stock", baseUnitId = unit.Id() });

        var category = await s.Mcp.OkAsync("article_category_create", new { code = "DRINKS", name = "Drinks" });
        var vat = await s.Mcp.OkAsync("tax_rate_create", new { code = "VAT25", name = "VAT 25 %", rate = 25 });
        var method = await s.Mcp.OkAsync("payment_method_create", new { code = "CASH", name = "Cash", kind = "cash" });
        var set = await s.Mcp.OkAsync("article_sales_data_set",
            new { articleId = article.Id(), categoryId = category.Id(), taxRateIds = new[] { vat.Id() }, salesPrice = 1.8m });
        var barcode = await s.Mcp.OkAsync("article_barcode_create", new { articleId = article.Id(), barcode = Ean });
        var read = await s.Mcp.OkAsync("article_sales_data_get", new { articleId = article.Id() });
        var found = await s.Mcp.OkAsync("article_barcode_list", new { barcode = Ean });

        McpAssert.JsonEqual(await Categories.GetAsync(s.Http, category.Id()), category);
        McpAssert.JsonEqual(await Tax.GetAsync(s.Http, vat.Id()), vat);
        McpAssert.JsonEqual(await Pay.GetAsync(s.Http, method.Id()), method);
        McpAssert.JsonEqual(set, read);
        McpAssert.JsonEqual(await SalesData.GetAsync(s.Http, article.Id()), read, "article_sales_data_get differs from HTTP");
        Assert.Equal(("DRINKS", 1.8m), (read.GetProperty("category").Str("code"), read.GetProperty("salesPrice").GetDecimal()));
        Assert.Equal(new[] { "VAT25" }, SalesData.TaxCodes(read));
        McpAssert.JsonEqual(await Barcodes.LookupAsync(s.Http, Ean), found, "article_barcode_list differs from HTTP");
        McpAssert.JsonEqual(barcode, Assert.Single(found.Items()));

        // The update and delete tools of a master, and the delete tool of a barcode.
        var renamed = await s.Mcp.OkAsync("payment_method_update", Pay.BodyOf(method).With("kind", "card").WithId(method.Id()));
        Assert.Equal("card", renamed.Str("kind"));
        Assert.True((await s.Mcp.OkAsync("payment_method_delete", new { id = method.Id() })).Bool("deleted"));
        await s.Mcp.ErrorAsync("article_category_delete", new { id = category.Id() }, "IN_USE");
        Assert.True((await s.Mcp.OkAsync("article_barcode_delete", new { id = barcode.Id() })).Bool("deleted"));
        await s.Mcp.ErrorAsync("article_barcode_get", new { id = barcode.Id() }, "NOT_FOUND");
    }

    [Fact]
    public async Task AC82_Tool_errors_carry_the_same_code_errors_keys_and_rules_as_HTTP()
    {
        await using var s = await AgentAsync();
        var one = await s.S.ArticleIdAsync("A1");
        var two = await s.S.ArticleIdAsync("A2");
        await s.S.CategoryAsync("DRINKS", "Drinks");
        await Barcodes.CreateAsync(s.Http, one, Ean);
        await s.Mcp.OkAsync("rule_set", new { key = Categories.NameUnique, value = true });
        await s.Mcp.OkAsync("rule_set", new { key = SalesRules.SalesPriceRequired, value = true });

        // NAME_TAKEN
        var nameTaken = await s.Mcp.ErrorAsync("article_category_create", new { code = "D2", name = "drinks" }, "NAME_TAKEN", "name");
        Rules.AssertNames(nameTaken, Categories.NameUnique, true, "name");
        AssertSameRefusal(
            await MasterRules.ConflictAsync(Categories.PostAsync(s.Http, Categories.Body("D2", "drinks")), "NAME_TAKEN", Categories.NameUnique, true, "name"),
            nameTaken);

        // BARCODE_TAKEN, by the rule and by the invariant
        var barcodeTaken = await s.Mcp.ErrorAsync("article_barcode_create", new { articleId = two, barcode = Ean }, "BARCODE_TAKEN", "barcode");
        Rules.AssertNames(barcodeTaken, SalesRules.BarcodeUnique, true, "barcode");
        AssertSameRefusal(await Barcodes.TakenByRuleAsync(Barcodes.PostAsync(s.Http, two, Ean)), barcodeTaken);
        Rules.AssertNamesNoRule(await s.Mcp.ErrorAsync("article_barcode_create", new { articleId = one, barcode = Ean }, "BARCODE_TAKEN", "barcode"));

        // A required member
        var required = await s.Mcp.ErrorAsync("article_sales_data_set",
            new { articleId = one, categoryId = (Guid?)null, taxRateIds = Array.Empty<Guid>(), salesPrice = (decimal?)null }, "VALIDATION_FAILED", "salesPrice");
        Rules.AssertNames(required, SalesRules.SalesPriceRequired, true, "salesPrice");
        AssertSameRefusal(
            await MasterRules.RequiredAsync(SalesData.PutAsync(s.Http, one, SalesData.Empty()), SalesRules.SalesPriceRequired, "salesPrice"), required);

        Assert.Equal(1, (await s.Mcp.OkAsync("article_category_list", new { })).Total());
        Assert.Equal(1, (await s.Mcp.OkAsync("article_barcode_list", new { })).Total());
        SalesData.AssertNothingSet(await s.Mcp.OkAsync("article_sales_data_get", new { articleId = one }));
    }
}
