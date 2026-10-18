using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xerp.IntegrationTests.Support;

namespace Xerp.IntegrationTests;

/// <summary>
/// Spec P02, part B, AC-50 to AC-53: the API key <c>scope</c> (R30), a <c>pos</c> key refused outside the allow-list
/// (R31, R32, E9, E10), the same through MCP (R33) and attribution (R34).
/// </summary>
[Collection(XerpCollection.Name)]
public class PosKeyScopeTests(XerpFixture app)
{
    /// <summary>R33: the tools that map to the operations of R32, <c>whoami</c> included (docs/questions/P02-q.md, Q1).</summary>
    private static readonly string[] PosTools =
    [
        "pos_role_list", "pos_role_get", "pos_operator_list", "pos_operator_get",
        "pos_session_start", "pos_session_get", "pos_session_list", "pos_session_end", "pos_authorize",
        "article_list", "article_get", "article_sales_data_get", "article_sales_data_list",
        "article_barcode_list", "article_barcode_get", "article_category_list", "article_category_get",
        "tax_rate_list", "tax_rate_get", "payment_method_list", "payment_method_get",
        "uom_list", "uom_get", "warehouse_list", "warehouse_get", "partner_list", "partner_get",
        "rule_list", "rule_get", "whoami",
    ];

    private sealed record Till(PosSetup S, TestKey Key, Guid Unit, Guid Article, Guid Category, Guid TaxRate, Guid PaymentMethod,
        Guid Barcode, Guid Partner, Guid Warehouse);

    private async Task<Till> TillAsync()
    {
        var s = await PosSetup.NewAsync(app);
        var key = await Pos.KeyAsync(app, s.Http, "pos", "tablet-1");
        var unit = await Uom.CreateAsync(s.Http, "PCS", "Piece");
        var article = await Art.CreateAsync(s.Http, "COLA", "Cola", unit.Id());
        var category = await SalesMaster.Categories.CreateAsync(s.Http, "DRINKS", "Drinks");
        var tax = await SalesMaster.TaxRates.CreateAsync(s.Http, "VAT25", "VAT 25");
        var pay = await SalesMaster.PaymentMethods.CreateAsync(s.Http, "CASH", "Cash");
        await SalesData.SetAsync(s.Http, article.Id(), category.Id(), [tax.Id()], 2.5m);
        var barcode = await Barcodes.CreateAsync(s.Http, article.Id(), "3850123456789");
        var partner = await MasterApi.Partners.CreateAsync(s.Http, MasterApi.Partners.Minimal("GUEST", "Guest").With("isCustomer", true));
        var warehouse = (await Stock.ListAsync(s.Http, "/api/v1/warehouses")).Items()[0];
        return new Till(s, key, unit.Id(), article.Id(), category.Id(), tax.Id(), pay.Id(), barcode.Id(), partner.Id(), warehouse.Id());
    }

    // ---- AC-50 ----

    [Fact]
    public async Task AC50_R30_Scope_pos_and_full_on_create_the_filter_and_the_initial_key()
    {
        var tenant = await app.NewTenantAsync();

        var pos = await Pos.KeyAsync(app, tenant.Client, "pos", "till", "agent");
        var omitted = await Pos.KeyAsync(app, tenant.Client, null, "office");
        var full = await Pos.KeyAsync(app, tenant.Client, "full", "office-2");

        Assert.Equal(("pos", "agent"), (pos.Body.Str("scope"), pos.Body.Str("actorType")));
        Assert.Equal(("full", "full"), (omitted.Body.Str("scope"), full.Body.Str("scope")));
        Assert.Equal("pos", (await Keys.GetAsync(tenant.Client, pos.Id)).Str("scope"));
        Assert.Equal("full", (await Keys.GetAsync(tenant.Client, tenant.ApiKeyId)).Str("scope"));
        Assert.Equal(new[] { pos.Id }, (await Keys.ListAsync(tenant.Client, "?scope=pos")).Ids());
        Assert.Equal(3, (await Keys.ListAsync(tenant.Client, "?scope=full")).Total());
    }

    [Theory]
    [InlineData("\"admin\"")]
    [InlineData("\"POS\"")]
    [InlineData("null")]
    [InlineData("1")]
    public async Task AC50_R30_Any_other_scope_is_400_with_the_key_scope(string scope)
    {
        var tenant = await app.NewTenantAsync();

        await MasterRules.InvariantAsync(
            tenant.Client.PostAsync(Keys.Path, HttpAssert.Raw($$"""{ "name": "till", "actorType": "human", "scope": {{scope}} }""")), "scope");

        Assert.Equal(1, (await Keys.ListAsync(tenant.Client)).Total());
    }

    // ---- AC-51 ----

    [Fact]
    public async Task AC51_E9_A_pos_key_is_FORBIDDEN_outside_the_allow_list()
    {
        var t = await TillAsync();
        var http = t.Key.Client;
        var role = t.S.Waiter.Id();
        var op = t.S.Ana.Id();

        var forbidden = new (HttpMethod Method, string Path, string? Body)[]
        {
            (HttpMethod.Put, Rules.PathOf(Rules.NegativeStock), """{ "value": true }"""),
            (HttpMethod.Post, Rules.ResetPath(Rules.NegativeStock), null),
            (HttpMethod.Get, Rules.Changes, null),
            (HttpMethod.Post, Keys.Path, """{ "name": "x", "actorType": "human", "scope": "full" }"""),
            (HttpMethod.Get, Keys.Path, null),
            (HttpMethod.Get, $"{Keys.Path}/{t.Key.Id}", null),
            (HttpMethod.Post, $"{Keys.Path}/{t.Key.Id}/revoke", null),
            (HttpMethod.Put, SalesData.Path(t.Article), SalesData.Empty().ToJsonString()),
            (HttpMethod.Post, Pos.Operators, Pos.OperatorBody("NEW", "New", role).ToJsonString()),
            (HttpMethod.Put, $"{Pos.Operators}/{op}", Pos.OperatorBodyOf(t.S.Ana).ToJsonString()),
            (HttpMethod.Delete, $"{Pos.Operators}/{op}", null),
            (HttpMethod.Put, $"{Pos.Operators}/{op}/pin", """{ "pin": "1111" }"""),
            (HttpMethod.Post, Pos.Roles, Pos.RoleBody("NEW", "New").ToJsonString()),
            (HttpMethod.Put, $"{Pos.Roles}/{role}", Pos.RoleBodyOf(t.S.Waiter).ToJsonString()),
            (HttpMethod.Delete, $"{Pos.Roles}/{role}", null),
            (HttpMethod.Post, Uom.Path, """{ "code": "KG", "name": "Kilogram" }"""),
            (HttpMethod.Delete, $"{Art.Path}/{t.Article}", null),
            (HttpMethod.Post, Barcodes.Path, Barcodes.Body(t.Article, "3850000000001").ToJsonString()),
            (HttpMethod.Get, Stock.Documents, null),
            (HttpMethod.Get, Stock.OnHand, null),
        };
        foreach (var (method, path, body) in forbidden)
        {
            using var request = new HttpRequestMessage(method, path) { Content = body is null ? null : HttpAssert.Raw(body) };
            using var response = await http.SendAsync(request);
            Assert.True(response.StatusCode == HttpStatusCode.Forbidden, $"{method} {path}: expected 403, got {(int)response.StatusCode}");
            await HttpAssert.ForbiddenAsync(response);
        }

        // Nothing changed.
        Assert.False((await Rules.GetAsync(t.S.Http, Rules.NegativeStock)).Bool("value"));
        Assert.Equal(2, (await Keys.ListAsync(t.S.Http)).Total());
        Assert.Equal(2, (await Stock.ListAsync(t.S.Http, Pos.Operators)).Total());
        McpAssert.JsonEqual(t.S.Ana, await Pos.GetOperatorAsync(t.S.Http, op));
        using var unknownPath = await http.GetAsync("/api/v1/no-such-path");
        await HttpAssert.NotFoundAsync(unknownPath);
    }

    [Fact]
    public async Task AC51_E10_A_forbidden_route_with_an_invalid_body_is_403_not_400()
    {
        var t = await TillAsync();

        using (var op = await t.Key.Client.PostAsync(Pos.Operators, HttpAssert.Raw("""{ "code": "a b", "pin": 12 }""")))
            await HttpAssert.ForbiddenAsync(op);
        using (var rule = await t.Key.Client.PutAsync(Rules.PathOf(Rules.NegativeStock), HttpAssert.Raw("""{ "value": "yes" }""")))
            await HttpAssert.ForbiddenAsync(rule);
        using (var key = await t.Key.Client.PostAsync(Keys.Path, HttpAssert.Raw("{}")))
            await HttpAssert.ForbiddenAsync(key);
    }

    [Fact]
    public async Task AC51_R32_Every_allowed_route_answers_as_for_a_full_key_and_sessions_and_authorize_work()
    {
        var t = await TillAsync();

        var allowed = new[]
        {
            Art.Path, $"{Art.Path}/{t.Article}", $"{Art.Path}/by-code/COLA", SalesData.Path(t.Article), SalesData.ListPath,
            Barcodes.Path, $"{Barcodes.Path}/{t.Barcode}",
            SalesMaster.Categories.Path, $"{SalesMaster.Categories.Path}/{t.Category}", $"{SalesMaster.Categories.Path}/by-code/DRINKS",
            SalesMaster.TaxRates.Path, $"{SalesMaster.TaxRates.Path}/{t.TaxRate}", $"{SalesMaster.TaxRates.Path}/by-code/VAT25",
            SalesMaster.PaymentMethods.Path, $"{SalesMaster.PaymentMethods.Path}/{t.PaymentMethod}", $"{SalesMaster.PaymentMethods.Path}/by-code/CASH",
            Uom.Path, $"{Uom.Path}/{t.Unit}", $"{Uom.Path}/by-code/PCS",
            "/api/v1/warehouses", $"/api/v1/warehouses/{t.Warehouse}",
            "/api/v1/partners", $"/api/v1/partners/{t.Partner}", "/api/v1/partners/by-code/GUEST",
            Rules.Path, Rules.PathOf(Rules.NegativeStock),
            Pos.Roles, $"{Pos.Roles}/{t.S.Waiter.Id()}", $"{Pos.Roles}/by-code/WAITER",
            Pos.Operators, $"{Pos.Operators}/{t.S.Ana.Id()}", $"{Pos.Operators}/by-code/ANA",
        };
        foreach (var path in allowed)
        {
            using var asFull = await t.S.Http.GetAsync(path);
            using var asPos = await t.Key.Client.GetAsync(path);
            var expected = await HttpAssert.JsonAsync(asFull, HttpStatusCode.OK);
            var text = await asPos.Content.ReadAsStringAsync();
            Assert.True(asPos.StatusCode == HttpStatusCode.OK, $"GET {path} with a pos key: {(int)asPos.StatusCode} {text}");
            using var actual = JsonDocument.Parse(text);
            McpAssert.JsonEqual(expected, actual.RootElement, $"GET {path} differs between a full and a pos key");
        }

        using (var whoami = await t.Key.Client.GetAsync("/api/v1/whoami"))
        {
            var who = await HttpAssert.JsonAsync(whoami, HttpStatusCode.OK);
            Assert.Equal(t.Key.Id, who.GetProperty("actor").GetProperty("apiKeyId").GetGuid());
        }

        var session = await Pos.StartAsync(t.Key.Client, "ANA", PosSetup.AnaPin);
        Assert.Equal(t.Key.Id, session.GetProperty("apiKeyId").GetGuid());
        await Pos.GetSessionAsync(t.Key.Client, session.Id());
        Assert.Equal(1, (await Pos.SessionsAsync(t.Key.Client)).Total());
        await Pos.AuthorizeAsync(t.Key.Client, session.Id(), "refund", "MARKO", PosSetup.MarkoPin);
        Assert.Equal("ended", (await Pos.EndAsync(t.Key.Client, session.Id())).Str("status"));
    }

    // ---- AC-52 ----

    [Fact]
    public async Task AC52_R33_Through_MCP_a_pos_key_lists_exactly_the_allowed_tools_and_other_tools_are_FORBIDDEN()
    {
        var t = await TillAsync();
        await using var mcp = await app.McpAsync(t.Key.Key);

        var names = (await mcp.Client.ListToolsAsync()).Select(tool => tool.ProtocolTool.Name).ToArray();

        Assert.Equal(PosTools.Order(StringComparer.Ordinal), names.Order(StringComparer.Ordinal));
        var who = await mcp.OkAsync("whoami", new { });
        Assert.Equal(t.Key.Id, who.GetProperty("actor").GetProperty("apiKeyId").GetGuid());
        await mcp.ErrorAsync("rule_set", new { key = Rules.NegativeStock, value = true }, "FORBIDDEN");
        await mcp.ErrorAsync("pos_operator_create", new { code = "NEW", name = "New", roleId = t.S.Waiter.Id(), pin = "1111" }, "FORBIDDEN");
        await mcp.ErrorAsync("api_key_list", new { }, "FORBIDDEN");
        Assert.False((await Rules.GetAsync(t.S.Http, Rules.NegativeStock)).Bool("value"));

        var session = await mcp.OkAsync("pos_session_start", new { operatorCode = "ANA", pin = PosSetup.AnaPin });
        Assert.Equal(t.Key.Id, session.GetProperty("apiKeyId").GetGuid());
        McpAssert.JsonEqual(await Pos.GetSessionAsync(t.Key.Client, session.Id()), session);
    }

    // ---- AC-53 ----

    [Fact]
    public async Task AC53_R34_The_session_of_a_pos_key_is_attributed_to_it()
    {
        var t = await TillAsync();

        var session = await Pos.StartAsync(t.Key.Client, "ANA", PosSetup.AnaPin);

        Assert.Equal(t.Key.Id, session.GetProperty("apiKeyId").GetGuid());
        var createdBy = await app.ScalarAsync<string>("""SELECT "CreatedBy"::text FROM "PosSessions" WHERE "Id" = @id""", ("id", session.Id()));
        Assert.Equal(t.Key.Id.ToString(), createdBy);
    }
}
