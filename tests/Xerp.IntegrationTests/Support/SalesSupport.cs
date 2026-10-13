using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Xerp.IntegrationTests.Support;

/// <summary>
/// The ten rules of spec P01 (section 6.1): <c>codeChangeable</c> and <c>nameUnique</c> of the three sales masters,
/// the three required members of an article's sales data and <c>article.barcodeUnique</c>.
/// </summary>
public static class SalesRules
{
    public const string BarcodeUnique = "article.barcodeUnique";
    public const string CategoryRequired = "article.categoryRequired";
    public const string TaxRateRequired = "article.taxRateRequired";
    public const string SalesPriceRequired = "article.salesPriceRequired";

    /// <summary>The first segment of the keys of the three masters (<c>&lt;m&gt;</c> of section 6.1).</summary>
    public static readonly string[] Groups = ["articleCategory", "taxRate", "paymentMethod"];

    /// <summary>The three required rules with the member of the Set body each judges (R10).</summary>
    public static readonly (string Rule, string Member)[] Required =
    [
        (CategoryRequired, "categoryId"),
        (TaxRateRequired, "taxRateIds"),
        (SalesPriceRequired, "salesPrice"),
    ];

    /// <summary>The ten keys of spec P01.</summary>
    public static readonly string[] Keys =
    [
        .. Groups.Select(g => $"{g}.codeChangeable"),
        .. Groups.Select(g => $"{g}.nameUnique"),
        BarcodeUnique,
        CategoryRequired,
        TaxRateRequired,
        SalesPriceRequired,
    ];

    public static IEnumerable<object[]> GroupData() => Groups.Select(g => new object[] { g });

    public static IEnumerable<object[]> KeyData() => Keys.Select(k => new object[] { k });

    public static IEnumerable<object[]> RequiredData() => Required.Select(r => new object[] { r.Rule, r.Member });

    /// <summary>Sets every one of the ten rules to the value that is not its default.</summary>
    public static async Task SetAllToOtherAsync(HttpClient client)
    {
        foreach (var key in Keys)
            await Rules.SetAsync(client, key, !Rules.Default(key));
    }
}

/// <summary>
/// One of the three masters of spec P01 (part A) — article categories, tax rates, payment methods: its routes and
/// tools, a valid create body and the replace body that keeps a record as it is. The three are one theory (section 11).
/// </summary>
public sealed class SalesMaster
{
    public static readonly SalesMaster Categories = new("articleCategory", "article-categories", "article_category", null);
    public static readonly SalesMaster TaxRates = new("taxRate", "tax-rates", "tax_rate", "rate");
    public static readonly SalesMaster PaymentMethods = new("paymentMethod", "payment-methods", "payment_method", "kind");

    private readonly string _toolPrefix;

    private SalesMaster(string group, string resource, string toolPrefix, string? extra)
    {
        Group = group;
        Path = $"/api/v1/{resource}";
        _toolPrefix = toolPrefix;
        Extra = extra;
    }

    /// <summary><c>articleCategory</c>, <c>taxRate</c> or <c>paymentMethod</c>: the group of its rules.</summary>
    public string Group { get; }

    public string Path { get; }

    /// <summary>The field the master has besides code, name and isActive: <c>rate</c>, <c>kind</c> or none.</summary>
    public string? Extra { get; }

    public string CodeChangeable => $"{Group}.codeChangeable";

    public string NameUnique => $"{Group}.nameUnique";

    public static SalesMaster Of(string group) => group switch
    {
        "articleCategory" => Categories,
        "taxRate" => TaxRates,
        "paymentMethod" => PaymentMethods,
        _ => throw new ArgumentOutOfRangeException(nameof(group), group, null),
    };

    /// <summary>Tool name for an operation: <c>list</c>, <c>get</c>, <c>create</c>, <c>update</c>, <c>delete</c>.</summary>
    public string Tool(string operation) => $"{_toolPrefix}_{operation}";

    /// <summary>A valid create body: code, name and <c>rate: 25</c> / <c>kind: "cash"</c> (AC-20).</summary>
    public JsonObject Body(string code, string name)
    {
        var body = new JsonObject { ["code"] = code, ["name"] = name };
        if (Extra == "rate")
            body["rate"] = 25;
        if (Extra == "kind")
            body["kind"] = "cash";
        return body;
    }

    /// <summary>The replace body that keeps every value of the record ("its own body").</summary>
    public JsonObject BodyOf(JsonElement record)
    {
        var body = new JsonObject { ["code"] = record.Str("code"), ["name"] = record.Str("name") };
        if (Extra is not null)
            body[Extra] = JsonNode.Parse(record.GetProperty(Extra).GetRawText());
        body["isActive"] = record.Bool("isActive");
        return body;
    }

    public Task<HttpResponseMessage> PostAsync(HttpClient client, JsonObject body) => client.PostAsJsonAsync(Path, body);

    public Task<HttpResponseMessage> PutAsync(HttpClient client, Guid id, JsonObject body) => client.PutAsJsonAsync($"{Path}/{id}", body);

    public Task<HttpResponseMessage> DeleteAsync(HttpClient client, Guid id) => client.DeleteAsync($"{Path}/{id}");

    public Task<HttpResponseMessage> SendGetAsync(HttpClient client, Guid id) => client.GetAsync($"{Path}/{id}");

    public Task<HttpResponseMessage> ByCodeAsync(HttpClient client, string code) =>
        client.GetAsync($"{Path}/by-code/{Uri.EscapeDataString(code)}");

    public async Task<JsonElement> CreateAsync(HttpClient client, JsonObject body)
    {
        using var response = await PostAsync(client, body);
        return await HttpAssert.JsonAsync(response, HttpStatusCode.Created);
    }

    public Task<JsonElement> CreateAsync(HttpClient client, string code, string name) => CreateAsync(client, Body(code, name));

    public async Task<JsonElement> ReplaceAsync(HttpClient client, Guid id, JsonObject body)
    {
        using var response = await PutAsync(client, id, body);
        return await HttpAssert.JsonAsync(response, HttpStatusCode.OK);
    }

    public async Task<JsonElement> GetAsync(HttpClient client, Guid id)
    {
        using var response = await SendGetAsync(client, id);
        return await HttpAssert.JsonAsync(response, HttpStatusCode.OK);
    }

    /// <summary><paramref name="query"/> starts with <c>?</c> or is empty.</summary>
    public Task<JsonElement> ListAsync(HttpClient client, string query = "?limit=500") => Stock.ListAsync(client, Path + query);

    public async Task RemoveAsync(HttpClient client, Guid id)
    {
        using var response = await DeleteAsync(client, id);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    public async Task SetActiveAsync(HttpClient client, JsonElement record, bool isActive) =>
        await ReplaceAsync(client, record.Id(), BodyOf(record).With("isActive", isActive));

    /// <summary>Asserts that a refused write left the record exactly as it was (<c>updatedAt</c> included).</summary>
    public async Task AssertUnchangedAsync(HttpClient client, JsonElement before) =>
        McpAssert.JsonEqual(before, await GetAsync(client, before.Id()), $"The {Group} changed");

    public override string ToString() => Group;
}

/// <summary>Sales data of an article (spec P01, 4.B): a sub-resource of the article, replaced as a whole.</summary>
public static class SalesData
{
    public const string ListPath = Art.Path + "/sales-data";

    public static string Path(Guid article) => $"{Art.Path}/{article}/sales-data";

    /// <summary>The Set body: all three members present (R8).</summary>
    public static JsonObject Body(Guid? category, Guid[] taxRates, decimal? salesPrice) => new()
    {
        ["categoryId"] = category?.ToString(),
        ["taxRateIds"] = new JsonArray(taxRates.Select(t => (JsonNode?)t.ToString()).ToArray()),
        ["salesPrice"] = salesPrice,
    };

    /// <summary>The body that empties the sales data: <c>{ null, [], null }</c>.</summary>
    public static JsonObject Empty() => Body(null, [], null);

    public static Task<HttpResponseMessage> PutAsync(HttpClient client, Guid article, JsonObject body) =>
        client.PutAsJsonAsync(Path(article), body);

    /// <summary>"Set": <c>PUT /articles/{id}/sales-data</c> answered <c>200</c>.</summary>
    public static async Task<JsonElement> SetAsync(HttpClient client, Guid article, JsonObject body)
    {
        using var response = await PutAsync(client, article, body);
        return await HttpAssert.JsonAsync(response, HttpStatusCode.OK);
    }

    public static Task<JsonElement> SetAsync(HttpClient client, Guid article, Guid? category, Guid[] taxRates, decimal? salesPrice) =>
        SetAsync(client, article, Body(category, taxRates, salesPrice));

    public static async Task<JsonElement> GetAsync(HttpClient client, Guid article)
    {
        using var response = await client.GetAsync(Path(article));
        return await HttpAssert.JsonAsync(response, HttpStatusCode.OK);
    }

    /// <summary><c>GET /articles/sales-data</c>; <paramref name="query"/> starts with <c>?</c> or is empty.</summary>
    public static Task<JsonElement> ListAsync(HttpClient client, string query = "") => Stock.ListAsync(client, ListPath + query);

    /// <summary>The article codes of a sales data list, in its order.</summary>
    public static string[] ArticleCodes(JsonElement list) =>
        list.Items().Select(i => i.GetProperty("article").Str("code")).ToArray();

    public static string[] TaxCodes(JsonElement salesData) =>
        salesData.GetProperty("taxRates").EnumerateArray().Select(t => t.Str("code")).ToArray();

    /// <summary>Sales data nobody set, or set to nothing: no category, no tax rate, no price (4.B).</summary>
    public static void AssertNothingSet(JsonElement salesData)
    {
        JsonBody.AssertNull(salesData, "category", "salesPrice");
        Assert.Equal(JsonValueKind.Array, salesData.GetProperty("taxRates").ValueKind);
        Assert.Equal(0, salesData.GetProperty("taxRates").GetArrayLength());
    }
}

/// <summary>Barcodes of articles (spec P01, 4.B).</summary>
public static class Barcodes
{
    public const string Path = "/api/v1/article-barcodes";

    public static JsonObject Body(Guid article, string barcode) => new() { ["articleId"] = article.ToString(), ["barcode"] = barcode };

    public static Task<HttpResponseMessage> PostAsync(HttpClient client, JsonObject body) => client.PostAsJsonAsync(Path, body);

    public static Task<HttpResponseMessage> PostAsync(HttpClient client, Guid article, string barcode) => PostAsync(client, Body(article, barcode));

    public static async Task<JsonElement> CreateAsync(HttpClient client, Guid article, string barcode)
    {
        using var response = await PostAsync(client, article, barcode);
        return await HttpAssert.JsonAsync(response, HttpStatusCode.Created);
    }

    public static Task<HttpResponseMessage> DeleteAsync(HttpClient client, Guid id) => client.DeleteAsync($"{Path}/{id}");

    /// <summary><c>GET /article-barcodes</c>; <paramref name="query"/> starts with <c>?</c> or is empty.</summary>
    public static Task<JsonElement> ListAsync(HttpClient client, string query = "") => Stock.ListAsync(client, Path + query);

    /// <summary>The lookup by barcode (R19).</summary>
    public static Task<JsonElement> LookupAsync(HttpClient client, string barcode) =>
        ListAsync(client, $"?barcode={Uri.EscapeDataString(barcode)}");

    public static Guid[] ArticleIds(JsonElement list) => list.Items().Select(i => i.GetProperty("article").Id()).ToArray();

    /// <summary><c>409 BARCODE_TAKEN</c>, key <c>barcode</c>, refused by (<c>article.barcodeUnique</c>, <c>true</c>) (R16).</summary>
    public static Task<JsonElement> TakenByRuleAsync(Task<HttpResponseMessage> send) =>
        MasterRules.ConflictAsync(send, "BARCODE_TAKEN", SalesRules.BarcodeUnique, true, "barcode");

    /// <summary><c>409 BARCODE_TAKEN</c>, key <c>barcode</c>, without <c>rules</c>: the article already has the barcode (invariant).</summary>
    public static async Task<JsonElement> TakenByArticleAsync(Task<HttpResponseMessage> send)
    {
        using var response = await send;
        var problem = await HttpAssert.ProblemAsync(response, HttpStatusCode.Conflict, "BARCODE_TAKEN");
        Assert.Equal(new[] { "barcode" }, McpAssert.ErrorKeys(problem));
        return Rules.AssertNamesNoRule(problem);
    }
}

/// <summary>A tenant with a unit of measure, ready for articles and their sales data.</summary>
public sealed record SalesSetup(TestTenant Tenant, Guid Unit)
{
    public HttpClient Http => Tenant.Client;

    public static async Task<SalesSetup> NewAsync(XerpFixture app)
    {
        var tenant = await app.NewTenantAsync();
        return new SalesSetup(tenant, await ArticleApi.UnitAsync(tenant.Client));
    }

    public async Task<JsonElement> ArticleAsync(string code, string? name = null, string type = "stock") =>
        await Art.CreateAsync(Http, code, name ?? $"Article {code}", Unit, type);

    public async Task<Guid> ArticleIdAsync(string code, string? name = null) => (await ArticleAsync(code, name)).Id();

    public async Task<JsonElement> CategoryAsync(string code, string? name = null) =>
        await SalesMaster.Categories.CreateAsync(Http, code, name ?? $"Category {code}");

    public async Task<JsonElement> TaxRateAsync(string code, decimal rate = 25) =>
        await SalesMaster.TaxRates.CreateAsync(Http, SalesMaster.TaxRates.Body(code, $"VAT {code}").With("rate", rate));
}
