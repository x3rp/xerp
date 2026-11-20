using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Xerp.IntegrationTests.Support;

/// <summary>Spec I01: invoices. A tenant with a unit, a stock article and a service with 25 % VAT and prices, and a customer.</summary>
public static class Invoices
{
    public const string Path = "/api/v1/invoices";
    public const string CustomerRule = "invoice.customerRequired";
    public const string BackdatingRule = "invoice.backdatingAllowed";
    public const string StockRule = "invoice.issueMovesStock";
    public const string OverpaymentRule = "invoice.overpaymentAllowed";

    public static readonly string[] RuleKeys = [BackdatingRule, CustomerRule, StockRule, OverpaymentRule];

    public sealed record Setup(HttpClient Http, Guid Goods, Guid Service, Guid Vat25, Guid Vat13, Guid Customer);

    public static async Task<Setup> SetupAsync(XerpFixture app)
    {
        var tenant = await app.NewTenantAsync();
        var http = tenant.Client;
        var unit = await PostAsync(http, "/api/v1/units-of-measure", new { code = "kom", name = "Komad" });
        var goods = await PostAsync(http, "/api/v1/articles", new { code = "G1", name = "Goods", type = "stock", baseUnitId = unit });
        var service = await PostAsync(http, "/api/v1/articles", new { code = "S1", name = "Service", type = "service", baseUnitId = unit });
        var vat25 = await PostAsync(http, "/api/v1/tax-rates", new { code = "PDV25", name = "PDV 25%", rate = 25 });
        var vat13 = await PostAsync(http, "/api/v1/tax-rates", new { code = "PDV13", name = "PDV 13%", rate = 13 });
        // Sales prices include tax: 12.50 gross at 25 % is 10.00 net.
        await PutAsync(http, $"/api/v1/articles/{goods}/sales-data", new { categoryId = (string?)null, taxRateIds = new[] { vat25 }, salesPrice = 12.5m });
        await PutAsync(http, $"/api/v1/articles/{service}/sales-data", new { categoryId = (string?)null, taxRateIds = new[] { vat13 }, salesPrice = 113m });
        var customer = await PostAsync(http, "/api/v1/partners", new { code = "C1", name = "Customer d.o.o.", isCustomer = true, isSupplier = false });
        return new Setup(http, goods, service, vat25, vat13, customer);
    }

    public static async Task ReceiveAsync(HttpClient http, Guid article, decimal quantity)
    {
        var doc = await PostAsync(http, "/api/v1/stock-documents", new { type = "receipt", documentDate = "2026-01-01", lines = new[] { new { articleId = article, quantity } } });
        using var post = await http.PostAsync($"/api/v1/stock-documents/{doc}/post", null);
        await HttpAssert.JsonAsync(post, HttpStatusCode.OK);
    }

    public static JsonObject Body(Setup s, string issueDate = "2026-03-01", params JsonObject[] lines) => new()
    {
        ["customerId"] = s.Customer.ToString(),
        ["issueDate"] = issueDate,
        ["lines"] = new JsonArray(lines.Length > 0 ? lines : [Line(s.Goods, 3), Line(s.Service, 1)]),
    };

    public static JsonObject Line(Guid article, decimal quantity) => new() { ["articleId"] = article.ToString(), ["quantity"] = quantity };

    public static async Task<JsonElement> CreateAsync(HttpClient http, JsonObject body)
    {
        using var response = await http.PostAsJsonAsync(Path, body);
        return await HttpAssert.JsonAsync(response, HttpStatusCode.Created);
    }

    public static Task<HttpResponseMessage> SendIssueAsync(HttpClient http, Guid id) => http.PostAsync($"{Path}/{id}/issue", null);

    public static async Task<JsonElement> IssueAsync(HttpClient http, Guid id)
    {
        using var response = await SendIssueAsync(http, id);
        return await HttpAssert.JsonAsync(response, HttpStatusCode.OK);
    }

    public static async Task<Guid> PostAsync(HttpClient http, string path, object body)
    {
        using var response = await http.PostAsJsonAsync(path, body);
        return (await HttpAssert.JsonAsync(response, HttpStatusCode.Created)).GetProperty("id").GetGuid();
    }

    private static async Task PutAsync(HttpClient http, string path, object body)
    {
        using var response = await http.PutAsJsonAsync(path, body);
        await HttpAssert.JsonAsync(response, HttpStatusCode.OK);
    }
}
