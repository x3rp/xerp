using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Xerp.IntegrationTests.Support;

/// <summary>Shortcuts for spec W01: bills of materials and work orders, through HTTP.</summary>
public static class Production
{
    public const string WorkOrders = "/api/v1/work-orders";
    public const string ComponentsRule = "production.componentsMayDifferFromBom";
    public const string ConsumptionRule = "production.consumptionMayDifferFromPlan";
    public const string ProducedRule = "production.producedQuantityMayDifferFromPlan";

    public static readonly string[] RuleKeys = [ComponentsRule, ConsumptionRule, ProducedRule];

    public static string BomPath(Guid article) => $"/api/v1/articles/{article}/bom";

    public static JsonObject Components(params (Guid Article, decimal Quantity)[] components) => new()
    {
        ["components"] = new JsonArray(components.Select(c => (JsonNode)new JsonObject
        {
            ["articleId"] = c.Article.ToString(), ["quantity"] = c.Quantity,
        }).ToArray()),
    };

    public static Task<HttpResponseMessage> SendSetBomAsync(HttpClient client, Guid article, params (Guid Article, decimal Quantity)[] components) =>
        client.PutAsJsonAsync(BomPath(article), Components(components));

    public static async Task<JsonElement> SetBomAsync(HttpClient client, Guid article, params (Guid Article, decimal Quantity)[] components) =>
        await HttpAssert.JsonAsync(await SendSetBomAsync(client, article, components), HttpStatusCode.OK);

    public static JsonObject Order(Guid article, decimal quantity, Guid? warehouse = null, params (Guid Article, decimal Planned)[] lines)
    {
        var body = new JsonObject { ["articleId"] = article.ToString(), ["quantity"] = quantity, ["plannedDate"] = Stock.Date };
        if (warehouse is { } w)
            body["warehouseId"] = w.ToString();
        if (lines.Length > 0)
            body["lines"] = new JsonArray(lines.Select(l => (JsonNode)new JsonObject
            {
                ["articleId"] = l.Article.ToString(), ["plannedQuantity"] = l.Planned,
            }).ToArray());
        return body;
    }

    public static Task<HttpResponseMessage> SendCreateAsync(HttpClient client, JsonObject body) => client.PostAsJsonAsync(WorkOrders, body);

    public static async Task<JsonElement> CreateAsync(HttpClient client, JsonObject body) =>
        await HttpAssert.JsonAsync(await SendCreateAsync(client, body), HttpStatusCode.Created);

    public static Task<HttpResponseMessage> SendActionAsync(HttpClient client, Guid id, string action, JsonObject? body = null) =>
        body is null ? client.PostAsync($"{WorkOrders}/{id}/{action}", null) : client.PostAsJsonAsync($"{WorkOrders}/{id}/{action}", body);

    public static async Task<JsonElement> ActionAsync(HttpClient client, Guid id, string action, JsonObject? body = null) =>
        await HttpAssert.JsonAsync(await SendActionAsync(client, id, action, body), HttpStatusCode.OK);

    public static async Task<JsonElement> GetAsync(HttpClient client, Guid id) =>
        await HttpAssert.JsonAsync(await client.GetAsync($"{WorkOrders}/{id}"), HttpStatusCode.OK);

    public static JsonElement[] WorkLines(this JsonElement order) => order.GetProperty("lines").EnumerateArray().ToArray();
}
