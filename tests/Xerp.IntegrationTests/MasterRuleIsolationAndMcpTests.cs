using System.Text.Json;
using System.Text.Json.Nodes;
using Xerp.IntegrationTests.Support;

namespace Xerp.IntegrationTests;

/// <summary>
/// Spec 013, AC-90 and AC-91 (tenant isolation, T2–T4) and AC-13, AC-100 to AC-102 (MCP, section 5): the rules of
/// the masters through the tools, with the same <c>code</c>, <c>errors</c> keys and <c>rules</c> as HTTP.
/// </summary>
[Collection(XerpCollection.Name)]
public class MasterRuleIsolationAndMcpTests(XerpFixture app)
{
    private const string PartnerNameUnique = "partner.nameUnique";

    private static readonly MasterApi P = MasterApi.Partners;

    private static readonly string[] MasterTools =
        [.. MasterRules.Groups.SelectMany(g => new[] { $"{g}_create", $"{g}_update" })];

    private sealed record Session(TestTenant Tenant, McpConnection Mcp) : IAsyncDisposable
    {
        public HttpClient Http => Tenant.Client;

        public ValueTask DisposeAsync() => Mcp.DisposeAsync();
    }

    private async Task<Session> AgentAsync()
    {
        var tenant = await app.NewTenantAsync();
        var agent = await Keys.CreateAsync(app, tenant.Client, "claude-masters", "agent");
        return new Session(tenant, await app.McpAsync(agent.Key));
    }

    /// <summary>The tool error has the same <c>code</c>, <c>errors</c> keys and <c>rules</c> as the problem document (AC-101).</summary>
    private static void AssertSameRefusal(JsonElement problem, JsonElement toolError)
    {
        Assert.Equal(problem.Str("code"), toolError.Str("code"));
        Assert.Equal(Rules.ErrorKeysInOrder(problem), Rules.ErrorKeysInOrder(toolError));
        McpAssert.JsonEqual(problem.GetProperty("rules"), toolError.GetProperty("rules"), "'rules' of the tool error differs from HTTP");
    }

    // ---- AC-90 ----

    [Fact]
    public async Task AC90_T2_The_values_of_one_tenant_change_nothing_for_another()
    {
        var x = await app.NewTenantAsync();
        var y = await app.NewTenantAsync();
        await MasterRules.SetAllToOtherAsync(x.Client);

        await MasterRules.AssertDefaultBehaviourAsync(y.Client, named: true);

        var ofY = await Rules.ListAsync(y.Client, "?limit=500");
        Assert.Equal(40, ofY.Total());
        Assert.Equal(Rules.All, ofY.RuleKeys());
        foreach (var rule in ofY.Items())
        {
            Rules.AssertAtDefault(rule, rule.Str("key"));
            JsonBody.AssertNull(rule, "updatedAt", "updatedBy");
        }
        Assert.Equal(0, (await Rules.ChangesAsync(y.Client)).Total());

        // X is judged by X's values: a code may not be changed, a partner needs a tax id, and none needs a role.
        var ofX = await Rules.ListAsync(x.Client, "?source=tenant&limit=500");
        Assert.Equal(MasterRules.Keys.Order(StringComparer.Ordinal), ofX.RuleKeys());
        await MasterRules.RequiredAsync(
            P.PostAsync(x.Client, P.Full("X1").With("taxId", null)), MasterRules.TaxIdRequired, "taxId");
        var noRole = await P.CreateAsync(x.Client, P.Full("X1").With("isCustomer", false).With("isSupplier", false));
        await MasterRules.ConflictAsync(P.PutAsync(x.Client, noRole.Id(), Orders.PartnerBody(noRole).With("code", "X2")),
            "CODE_CHANGE_NOT_ALLOWED", MasterRules.CodeChangeable("partner"), false, "code");
    }

    // ---- AC-91 ----

    [Fact]
    public async Task AC91_T3_A_name_or_tax_id_of_another_tenant_never_collides()
    {
        var x = await app.NewTenantAsync();
        var y = await app.NewTenantAsync();
        foreach (var tenant in new[] { x, y })
        {
            await Rules.SetAsync(tenant.Client, PartnerNameUnique, true);
            await Rules.SetAsync(tenant.Client, MasterRules.TaxIdUnique, true);
        }
        var ofX = await P.CreateAsync(x.Client, P.Minimal("P1", "Acme").With("taxId", "HR123"));

        var ofY = await P.CreateAsync(y.Client, P.Minimal("P1", "Acme").With("taxId", "HR123"));

        Assert.NotEqual(ofX.Id(), ofY.Id());
        Assert.Equal((1, 1), ((await P.ListAsync(x.Client)).Total(), (await P.ListAsync(y.Client)).Total()));
        // Each tenant's own duplicate is refused, and the refusal reveals nothing of the other tenant.
        var refused = await MasterRules.ConflictAsync(P.PostAsync(y.Client, P.Minimal("P2", "acme")), "NAME_TAKEN", PartnerNameUnique, true, "name");
        Assert.DoesNotContain(ofX.Id().ToString(), refused.GetRawText(), StringComparison.OrdinalIgnoreCase);
        await MasterRules.ConflictAsync(P.PostAsync(x.Client, P.Minimal("P2", "Other").With("taxId", "hr123")),
            "TAX_ID_TAKEN", MasterRules.TaxIdUnique, true, "taxId");
        // A replace in Y that keeps the name X also has: no collision either.
        await P.ReplaceAsync(y.Client, ofY.Id(), Orders.PartnerBody(ofY));
    }

    [Fact]
    public async Task AC91_T4_The_same_through_the_tools()
    {
        await using var x = await AgentAsync();
        await using var y = await AgentAsync();
        await x.Mcp.OkAsync("rule_set", new { key = PartnerNameUnique, value = true });
        await x.Mcp.OkAsync("rule_set", new { key = MasterRules.PartnerCityRequired, value = true });
        await x.Mcp.OkAsync("partner_create", new { code = "P1", name = "Acme", isSupplier = true, city = "Split" });

        // Y is judged by Y's values: a duplicate name and no city.
        await y.Mcp.OkAsync("partner_create", new { code = "P1", name = "Acme", isSupplier = true });
        await y.Mcp.OkAsync("partner_create", new { code = "P2", name = "Acme", isSupplier = true });

        Assert.Equal(0, (await y.Mcp.OkAsync("rule_list", new { source = "tenant" })).Total());
        Assert.Equal(new[] { MasterRules.PartnerCityRequired, PartnerNameUnique }, (await x.Mcp.OkAsync("rule_list", new { source = "tenant" })).RuleKeys());
        Rules.AssertNames(
            await x.Mcp.ErrorAsync("partner_create", new { code = "P2", name = "Acme", isSupplier = true, city = "Split" }, "NAME_TAKEN", "name"),
            PartnerNameUnique, true, "name");
    }

    // ---- AC-13 ----

    [Fact]
    public async Task AC13_The_tool_count_and_rule_list_equals_the_list_over_HTTP()
    {
        await using var s = await AgentAsync();

        var tools = await s.Mcp.Client.ListToolsAsync();
        var list = await s.Mcp.OkAsync("rule_list", new { limit = 500 });

        Assert.Equal(84, tools.Select(t => t.ProtocolTool.Name).Distinct().Count());
        McpAssert.JsonEqual(await Rules.ListAsync(s.Http, "?limit=500"), list, "rule_list differs from GET /rules");
        Assert.Equal(Rules.All, list.RuleKeys());
        foreach (var group in MasterRules.Groups)
            McpAssert.JsonEqual(await Rules.ListAsync(s.Http, $"?group={group}"), await s.Mcp.OkAsync("rule_list", new { group }));
    }

    [Fact]
    public async Task AC13_The_five_rule_tools_work_for_a_new_key_as_for_the_six()
    {
        await using var s = await AgentAsync();
        const string key = "warehouse.nameUnique";

        var set = await s.Mcp.OkAsync("rule_set", new { key, value = true });
        var got = await s.Mcp.OkAsync("rule_get", new { key });
        var reset = await s.Mcp.OkAsync("rule_reset", new { key });
        var history = await s.Mcp.OkAsync("rule_change_list", new { key });

        Rules.AssertBool(set, "value", true);
        Assert.Equal("tenant", set.Str("source"));
        McpAssert.JsonEqual(set, got);
        Rules.AssertAtDefault(reset, key);
        McpAssert.JsonEqual(await Rules.GetAsync(s.Http, key), reset);
        Assert.Equal(2, history.Total());
        McpAssert.JsonEqual(await Rules.ChangesAsync(s.Http, $"?key={key}"), history);
    }

    // ---- AC-100 ----

    [Fact]
    public async Task AC100_A_required_field_through_tools_only()
    {
        await using var s = await AgentAsync();

        await s.Mcp.OkAsync("rule_set", new { key = MasterRules.PartnerCityRequired, value = true });
        var toolError = await s.Mcp.ErrorAsync("partner_create", new { code = "P1", name = "Acme", isSupplier = true }, "VALIDATION_FAILED", "city");

        Rules.AssertNames(toolError, MasterRules.PartnerCityRequired, true, "city");
        var problem = await MasterRules.RequiredAsync(
            P.PostAsync(s.Http, new JsonObject { ["code"] = "P1", ["name"] = "Acme", ["isSupplier"] = true }), MasterRules.PartnerCityRequired, "city");
        AssertSameRefusal(problem, toolError);
        Assert.Equal(0, (await s.Mcp.OkAsync("partner_list", new { })).Total());

        var partner = await s.Mcp.OkAsync("partner_create", new { code = "P1", name = "Acme", isSupplier = true, city = "Split" });
        Assert.Equal("Split", partner.Str("city"));
        McpAssert.JsonEqual(await P.GetAsync(s.Http, partner.Id()), partner);

        // The update tool is judged the same way.
        var update = await s.Mcp.ErrorAsync("partner_update", Orders.PartnerBody(partner).With("city", null).WithId(partner.Id()), "VALIDATION_FAILED", "city");
        Rules.AssertNames(update, MasterRules.PartnerCityRequired, true, "city");
        await P.AssertUnchangedAsync(s.Http, partner);
    }

    [Fact]
    public async Task AC100_AC22_The_default_refusal_of_a_partner_without_a_role_names_its_rule_through_the_tool()
    {
        await using var s = await AgentAsync();
        var body = new JsonObject { ["code"] = "P1", ["name"] = "No role", ["isCustomer"] = false, ["isSupplier"] = false };

        var toolError = await s.Mcp.ErrorAsync("partner_create", body, "VALIDATION_FAILED", "isCustomer", "isSupplier");

        AssertSameRefusal(await MasterRules.RoleRequiredAsync(P.PostAsync(s.Http, body)), toolError);
        Rules.AssertNames(toolError, MasterRules.RoleRequired, true, "isCustomer", "isSupplier");
    }

    // ---- AC-101 ----

    [Theory]
    [MemberData(nameof(MasterRules.GroupData), MemberType = typeof(MasterRules))]
    public async Task AC101_CODE_CHANGE_NOT_ALLOWED_and_NAME_TAKEN_through_the_tools_of_each_master(string group)
    {
        await using var s = await AgentAsync();
        var m = await RuledMaster.OpenAsync(group, s.Http);
        await s.Mcp.OkAsync("rule_set", new { key = m.CodeChangeable, value = false });
        await s.Mcp.OkAsync("rule_set", new { key = m.NameUnique, value = true });
        var record = await s.Mcp.OkAsync(m.Tool("create"), m.Minimal("T1", "Acme"));

        var changed = m.BodyOf(record).With("code", "T2");
        var codeChange = await s.Mcp.ErrorAsync(m.Tool("update"), m.BodyOf(record).With("code", "T2").WithId(record.Id()), "CODE_CHANGE_NOT_ALLOWED", "code");
        Rules.AssertNames(codeChange, m.CodeChangeable, false, "code");
        AssertSameRefusal(
            await MasterRules.ConflictAsync(m.PutAsync(s.Http, record.Id(), changed), "CODE_CHANGE_NOT_ALLOWED", m.CodeChangeable, false, "code"), codeChange);

        var nameTaken = await s.Mcp.ErrorAsync(m.Tool("create"), m.Minimal("T2", "acme"), "NAME_TAKEN", "name");
        Rules.AssertNames(nameTaken, m.NameUnique, true, "name");
        AssertSameRefusal(
            await MasterRules.ConflictAsync(m.PostAsync(s.Http, m.Minimal("T2", "acme")), "NAME_TAKEN", m.NameUnique, true, "name"), nameTaken);

        await m.AssertUnchangedAsync(s.Http, record);
        using var notCreated = await m.ByCodeAsync(s.Http, "T2");
        await HttpAssert.NotFoundAsync(notCreated);
    }

    [Fact]
    public async Task AC101_TAX_ID_TAKEN_through_the_partner_tools()
    {
        await using var s = await AgentAsync();
        await s.Mcp.OkAsync("rule_set", new { key = MasterRules.TaxIdUnique, value = true });
        await s.Mcp.OkAsync("partner_create", new { code = "P1", name = "First", isSupplier = true, taxId = "HR123" });
        var second = await s.Mcp.OkAsync("partner_create", new { code = "P2", name = "Second", isSupplier = true, taxId = "HR124" });
        var body = new JsonObject { ["code"] = "P3", ["name"] = "Third", ["isSupplier"] = true, ["taxId"] = "hr123" };

        var onCreate = await s.Mcp.ErrorAsync("partner_create", body, "TAX_ID_TAKEN", "taxId");
        var onUpdate = await s.Mcp.ErrorAsync("partner_update", Orders.PartnerBody(second).With("taxId", "HR123").WithId(second.Id()), "TAX_ID_TAKEN", "taxId");

        Rules.AssertNames(onCreate, MasterRules.TaxIdUnique, true, "taxId");
        Rules.AssertNames(onUpdate, MasterRules.TaxIdUnique, true, "taxId");
        AssertSameRefusal(await MasterRules.ConflictAsync(P.PostAsync(s.Http, body), "TAX_ID_TAKEN", MasterRules.TaxIdUnique, true, "taxId"), onCreate);
        await P.AssertUnchangedAsync(s.Http, second);
        Assert.Equal(2, (await s.Mcp.OkAsync("partner_list", new { })).Total());
    }

    // ---- AC-102 ----

    [Fact]
    public async Task AC102_The_input_schemas_of_the_eight_tools_do_not_depend_on_a_rule()
    {
        await using var s = await AgentAsync();
        var before = (await s.Mcp.Client.ListToolsAsync()).ToDictionary(t => t.ProtocolTool.Name, t => t.ProtocolTool.InputSchema.Clone());
        Assert.All(MasterTools, name => Assert.True(before.ContainsKey(name), $"tools/list has no '{name}'."));

        await MasterRules.SetAllToOtherAsync(s.Http);

        // A new connection: nothing a client could have cached.
        var agent = await Keys.CreateAsync(app, s.Http, "claude-after", "agent");
        await using var fresh = await app.McpAsync(agent.Key);
        var after = (await fresh.Client.ListToolsAsync()).ToDictionary(t => t.ProtocolTool.Name, t => t.ProtocolTool.InputSchema.Clone());
        Assert.Equal(84, after.Count);
        foreach (var name in MasterTools)
            McpAssert.JsonEqual(before[name], after[name], $"The input schema of {name} changed with the rules");
        // Every field a rule can require stays nullable and out of "required" of the create tools (section 2).
        var required = after["partner_create"].GetProperty("required").EnumerateArray().Select(r => r.GetString()).ToArray();
        Assert.DoesNotContain("city", required);
        Assert.DoesNotContain("taxId", required);
    }

    // ---- section 5: descriptions ----

    [Fact]
    public async Task S5_The_descriptions_of_the_eight_tools_point_to_the_rules()
    {
        await using var s = await AgentAsync();

        var tools = (await s.Mcp.Client.ListToolsAsync()).ToDictionary(t => t.ProtocolTool.Name, t => t.ProtocolTool.Description ?? "");

        foreach (var name in MasterTools)
        {
            var description = tools[name];
            Assert.Contains("rule_list", description, StringComparison.Ordinal);
            // "Where a description lists the tool's error codes it gains the new ones that tool can answer."
            if (!description.Contains("CODE_TAKEN", StringComparison.Ordinal))
                continue;
            Assert.Contains("NAME_TAKEN", description, StringComparison.Ordinal);
            if (name.EndsWith("_update", StringComparison.Ordinal))
                Assert.Contains("CODE_CHANGE_NOT_ALLOWED", description, StringComparison.Ordinal);
            if (name.StartsWith("partner_", StringComparison.Ordinal))
                Assert.Contains("TAX_ID_TAKEN", description, StringComparison.Ordinal);
        }
    }
}
