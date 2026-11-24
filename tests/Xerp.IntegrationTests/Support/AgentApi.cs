using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Xerp.IntegrationTests.Support;

/// <summary>
/// Spec A01 (agent experience): every route, tool name and DTO field name the spec leaves open is chosen HERE, once,
/// so the builder can align the implementation (or this file) in one place.
///
/// CHOSEN SHAPES (camelCase):
///   GET  /guide                      -> { items: [ { topic, summary } ] }
///   GET  /guide/{topic}              -> { topic, summary, markdown }          (404 NOT_FOUND for an unknown topic)
///   GET  /setup-status               -> { ready, done, total, items: [ { key, done, detail, nextStep } ] }
///   GET  /rule-groups                -> { items: [ { group, description, ruleCount, changedCount } ] }
///   POST /rules/set-many             -> { changes: [ { key, value } ], reason? }  answers { items: [ rule ] } (the changed rules)
///   GET  /rule-profiles              -> { items: [ { key, name, description, ruleCount } ] }
///   GET  /rule-profiles/{key}        -> { key, name, description, changes: [ { key, value } ] }
///   POST /rules/preview              -> { changes: [...] }  answers { items: [ { key, name, oldValue, newValue, judges, affectedDrafts } ] }
///                                       (affectedDrafts: integer, or null where it is not cheap to compute)
///   POST /setup/rules-reviewed       -> { rulesReviewedAt }                    (also on GET /company as rulesReviewedAt)
///   GET  /search?q=&types=a,b        -> { items: [ { type, id, code|number, name|label, status? } ] }, max 10 per type
///   rule-changes items gain          -> reason (string or null)
///   Tools: guide{topic?}, setup_status, rule_group_list, rule_set_many{changes,reason?}, rule_profile_list,
///          rule_profile_get{key}, rule_preview{changes}, setup_mark_rules_reviewed, search{text,types?}.
///   Errors of set-many: 400 VALIDATION_FAILED, errors keyed "changes[i].key" / "changes[i].value".
/// </summary>
public static class AgentApi
{
    public const string Guide = "/api/v1/guide";
    public const string SetupStatus = "/api/v1/setup-status";
    public const string RuleGroups = "/api/v1/rule-groups";
    public const string SetMany = "/api/v1/rules/set-many";
    public const string Profiles = "/api/v1/rule-profiles";
    public const string Preview = "/api/v1/rules/preview";
    public const string MarkReviewed = "/api/v1/setup/rules-reviewed";
    public const string Search = "/api/v1/search";

    /// <summary>The nine new tools of A01 (tools/list: 123 + 9 = 132).</summary>
    public static readonly string[] ToolNames =
    [
        "guide", "setup_status", "rule_group_list", "rule_set_many", "rule_profile_list", "rule_profile_get",
        "rule_preview", "setup_mark_rules_reviewed", "search",
    ];

    public const int ToolCount = 132;

    /// <summary>The topics the spec names (2.2, "at least").</summary>
    public static readonly string[] Topics =
    [
        "getting-started", "company-setup", "articles-and-prices", "partners", "buying", "selling", "invoicing", "stock",
        "production", "business-rules", "reports", "month-end", "errors",
    ];

    /// <summary>The profiles the spec names (2.4, "at least").</summary>
    public static readonly string[] ProfileKeys = ["defaults", "strict", "retail", "production"];

    /// <summary>The checklist items of setup_status (2.3), in the order of the spec.</summary>
    public static readonly string[] SetupKeys =
    [
        "companyProfile", "warehouses", "taxRates", "paymentMethods", "unitsOfMeasure", "articles", "customer", "supplier",
        "rulesReviewed", "users", "backups",
    ];

    /// <summary>The item the spec lets be <c>unknown</c> until backups are monitored (R01); it is never required to be done.</summary>
    public const string BackupsKey = "backups";

    // ---- HTTP ----

    public static async Task<JsonElement> GetAsync(HttpClient http, string url)
    {
        using var response = await http.GetAsync(url);
        return await HttpAssert.JsonAsync(response, HttpStatusCode.OK);
    }

    public static async Task<JsonElement> PostAsync(HttpClient http, string url, object? body = null)
    {
        using var response = await http.PostAsJsonAsync(url, body ?? new { });
        return await HttpAssert.JsonAsync(response, HttpStatusCode.OK);
    }

    public static Task<JsonElement> GuideAsync(HttpClient http, string? topic = null) =>
        GetAsync(http, topic is null ? Guide : $"{Guide}/{Uri.EscapeDataString(topic)}");

    public static Task<JsonElement> StatusAsync(HttpClient http) => GetAsync(http, SetupStatus);

    public static Task<JsonElement> GroupsAsync(HttpClient http) => GetAsync(http, RuleGroups);

    public static Task<JsonElement> ProfileListAsync(HttpClient http) => GetAsync(http, Profiles);

    public static Task<JsonElement> ProfileAsync(HttpClient http, string key) => GetAsync(http, $"{Profiles}/{key}");

    /// <summary>The body of set-many / preview: <c>{ changes: [ { key, value } ], reason? }</c>; a value may be any JSON (to test refusals).</summary>
    public static JsonObject Changes(IEnumerable<(string Key, JsonNode? Value)> changes, string? reason = null)
    {
        var body = new JsonObject { ["changes"] = new JsonArray(changes.Select(c => (JsonNode)new JsonObject { ["key"] = c.Key, ["value"] = c.Value }).ToArray()) };
        if (reason is not null)
            body["reason"] = reason;
        return body;
    }

    public static JsonObject Changes(params (string Key, bool Value)[] changes) => Changes(changes.Select(c => (c.Key, (JsonNode?)c.Value)));

    public static Task<HttpResponseMessage> SendSetManyAsync(HttpClient http, JsonObject body) => http.PostAsJsonAsync(SetMany, body);

    public static async Task<JsonElement> SetManyAsync(HttpClient http, JsonObject body)
    {
        using var response = await SendSetManyAsync(http, body);
        return await HttpAssert.JsonAsync(response, HttpStatusCode.OK);
    }

    public static Task<JsonElement> PreviewAsync(HttpClient http, JsonObject body) => PostAsync(http, Preview, body);

    public static Task<JsonElement> MarkReviewedAsync(HttpClient http) => PostAsync(http, MarkReviewed);

    public static Task<JsonElement> SearchAsync(HttpClient http, string text, string? types = null) =>
        GetAsync(http, $"{Search}?q={Uri.EscapeDataString(text)}" + (types is null ? "" : $"&types={Uri.EscapeDataString(types)}"));

    // ---- MCP ----

    public static Task<JsonElement> GuideAsync(McpConnection mcp, string? topic = null) =>
        mcp.OkAsync("guide", topic is null ? new { } : new { topic });

    public static Task<JsonElement> SearchAsync(McpConnection mcp, string text, string[]? types = null) =>
        mcp.OkAsync("search", types is null ? new { text } : new { text, types });

    // ---- reading ----

    public static JsonElement Item(this JsonElement list, string property, string value) =>
        Assert.Single(list.Items(), i => i.Str(property) == value);

    /// <summary>The code or the number of a search hit (spec 2.5: <c>code|number</c>).</summary>
    public static string Ref(this JsonElement hit) =>
        hit.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.String ? code.GetString()!
        : hit.TryGetProperty("number", out var number) && number.ValueKind == JsonValueKind.String ? number.GetString()!
        : "";

    /// <summary>The name or the label of a search hit (spec 2.5: <c>name|label</c>).</summary>
    public static string Label(this JsonElement hit) =>
        hit.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String ? name.GetString()!
        : hit.TryGetProperty("label", out var label) && label.ValueKind == JsonValueKind.String ? label.GetString()!
        : "";

    public static string[] ChangeKeys(this JsonElement changes) => changes.Items().Select(c => c.Str("key")).Order(StringComparer.Ordinal).ToArray();

    // ---- an agent: a tenant with an API key and an MCP connection ----

    public sealed record Agent(TestTenant Tenant, McpConnection Mcp) : IAsyncDisposable
    {
        public HttpClient Http => Tenant.Client;

        public ValueTask DisposeAsync() => Mcp.DisposeAsync();
    }

    public static async Task<Agent> AgentAsync(XerpFixture app, TestTenant? tenant = null)
    {
        tenant ??= await app.NewTenantAsync();
        var key = await Keys.CreateAsync(app, tenant.Client, "claude-a01", "agent");
        return new Agent(tenant, await app.McpAsync(key.Key));
    }
}
