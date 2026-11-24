using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xerp.IntegrationTests.Support;

namespace Xerp.IntegrationTests;

/// <summary>Spec A01, 2.4: <c>rule_group_list</c>, <c>rule_set_many</c>, profiles, <c>rule_preview</c>.</summary>
[Collection(XerpCollection.Name)]
public class AgentRuleToolsTests(XerpFixture app)
{
    // ---- rule_group_list ----

    [Fact]
    public async Task rule_group_list_describes_every_group_and_counts_the_rules_changed_from_the_default()
    {
        await using var agent = await AgentApi.AgentAsync(app);
        var before = await AgentApi.GroupsAsync(agent.Http);
        McpAssert.JsonEqual(before, await agent.Mcp.OkAsync("rule_group_list"), "rule_group_list differs from GET /rule-groups");

        var expectedGroups = Rules.All.Select(k => k.Split('.')[0]).Distinct().Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(expectedGroups, before.Items().Select(g => g.Str("group")).Order(StringComparer.Ordinal).ToArray());
        Assert.All(before.Items(), g =>
        {
            Assert.Equal(new[] { "changedCount", "description", "group", "ruleCount" }, g.PropertyNames());
            Assert.False(string.IsNullOrWhiteSpace(g.Str("description")));
            Assert.Equal(Rules.All.Count(k => k.StartsWith(g.Str("group") + ".", StringComparison.Ordinal)), g.GetProperty("ruleCount").GetInt32());
            Assert.Equal(0, g.GetProperty("changedCount").GetInt32());
        });

        await Rules.SetAsync(agent.Http, Rules.NegativeStock, true);
        await Rules.SetAsync(agent.Http, DocRules.PastDate, false);
        await Rules.SetAsync(agent.Http, DocRules.FutureDate, true); // the default value: a tenant row, but not a change from the default

        var after = await AgentApi.GroupsAsync(agent.Http);
        Assert.Equal(1, after.Item("group", "stock").GetProperty("changedCount").GetInt32());
        Assert.Equal(1, after.Item("group", "document").GetProperty("changedCount").GetInt32());
        Assert.Equal(0, after.Item("group", "sales").GetProperty("changedCount").GetInt32());
    }

    // ---- rule_set_many ----

    [Fact]
    public async Task rule_set_many_applies_several_rules_and_writes_one_history_entry_per_rule_with_the_reason()
    {
        await using var agent = await AgentApi.AgentAsync(app);
        var body = AgentApi.Changes([(Rules.NegativeStock, true), (DocRules.ReferenceRequired, true), (DocRules.PastDate, false)], "We are a strict wholesaler");

        var result = await agent.Mcp.OkAsync("rule_set_many", body);

        Assert.Equal(new[] { DocRules.PastDate, DocRules.ReferenceRequired, Rules.NegativeStock }.Order(StringComparer.Ordinal), result.ChangeKeys());
        var tenantRules = await Rules.ListAsync(agent.Http, "?source=tenant");
        Assert.Equal(result.ChangeKeys(), tenantRules.RuleKeys());
        Rules.AssertBool(tenantRules.Item("key", Rules.NegativeStock), "value", true);
        Rules.AssertBool(tenantRules.Item("key", DocRules.PastDate), "value", false);
        var history = await Rules.ChangesAsync(agent.Http);
        Assert.Equal(3, history.Total());
        Assert.All(history.Items(), c =>
        {
            Assert.Equal(Rules.ChangeRepresentation, c.PropertyNames());
            Assert.Equal("We are a strict wholesaler", c.Str("reason"));
            Assert.Equal("set", c.Str("action"));
        });
        // The rules really apply: reference is now required.
        using var refused = await Stock.PostAsync(agent.Http, Stock.Draft("receipt", (await Balance.DefaultAsync(agent.Http)).Id(), (Guid.CreateVersion7(), 1)));
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
    }

    [Fact]
    public async Task rule_set_many_over_http_and_through_mcp_answer_the_same_and_the_reason_is_optional()
    {
        var http = await app.NewTenantAsync();
        await using var tool = await AgentApi.AgentAsync(app);

        var overHttp = await AgentApi.SetManyAsync(http.Client, AgentApi.Changes((Rules.NegativeStock, true)));
        var overTool = await tool.Mcp.OkAsync("rule_set_many", AgentApi.Changes((Rules.NegativeStock, true)));

        Assert.Equal(overHttp.ChangeKeys(), overTool.ChangeKeys());
        Assert.Equal(overHttp.PropertyNames(), overTool.PropertyNames());
        JsonBody.AssertNull(Assert.Single((await Rules.ChangesAsync(http.Client)).Items()), "reason");
        JsonBody.AssertNull(Assert.Single((await Rules.ChangesAsync(tool.Http)).Items()), "reason");
    }

    [Fact]
    public async Task One_unknown_key_refuses_the_whole_call_with_errors_per_item_and_changes_nothing()
    {
        await using var agent = await AgentApi.AgentAsync(app);
        var body = AgentApi.Changes([(Rules.NegativeStock, true), ("no.such.rule", true), (DocRules.PastDate, false)], "typo");

        using (var response = await AgentApi.SendSetManyAsync(agent.Http, body))
        {
            var problem = await HttpAssert.ValidationAsync(response, "changes[1].key");
            Assert.Equal(new[] { "changes[1].key" }, McpAssert.ErrorKeys(problem));
        }
        var error = await agent.Mcp.ErrorAsync("rule_set_many", body, "VALIDATION_FAILED", "changes[1].key");
        Assert.Equal(new[] { "changes[1].key" }, McpAssert.ErrorKeys(error));

        Assert.Equal(0, (await Rules.ListAsync(agent.Http, "?source=tenant")).Total());
        Assert.Equal(0, (await Rules.ChangesAsync(agent.Http)).Total());
        JsonBody.AssertNull(await agent.Mcp.OkAsync("company_get"), "rulesReviewedAt");
    }

    [Fact]
    public async Task A_value_that_is_not_a_boolean_and_an_empty_list_are_refused_and_every_bad_item_is_reported()
    {
        await using var agent = await AgentApi.AgentAsync(app);

        var mixed = AgentApi.Changes([(Rules.NegativeStock, (JsonNode?)JsonValue.Create("yes")), (DocRules.PastDate, JsonValue.Create(1)), (DocRules.FutureDate, JsonValue.Create(false))]);
        using (var response = await AgentApi.SendSetManyAsync(agent.Http, mixed))
            await HttpAssert.ValidationAsync(response, "changes[0].value", "changes[1].value");
        await agent.Mcp.ErrorAsync("rule_set_many", mixed, "VALIDATION_FAILED", "changes[0].value", "changes[1].value");
        using (var empty = await AgentApi.SendSetManyAsync(agent.Http, AgentApi.Changes()))
            await HttpAssert.ValidationAsync(empty, "changes");
        using (var duplicate = await AgentApi.SendSetManyAsync(agent.Http, AgentApi.Changes((Rules.NegativeStock, true), (Rules.NegativeStock, false))))
            await HttpAssert.ValidationAsync(duplicate, "changes[1].key");

        Assert.Equal(0, (await Rules.ListAsync(agent.Http, "?source=tenant")).Total());
        Assert.Equal(0, (await Rules.ChangesAsync(agent.Http)).Total());
    }

    [Fact]
    public async Task rule_set_many_in_one_tenant_changes_nothing_in_another()
    {
        await using var x = await AgentApi.AgentAsync(app);
        var y = await app.NewTenantAsync();

        await x.Mcp.OkAsync("rule_set_many", AgentApi.Changes((Rules.NegativeStock, true)));

        Assert.Equal(0, (await Rules.ListAsync(y.Client, "?source=tenant")).Total());
        Assert.Equal(0, (await Rules.ChangesAsync(y.Client)).Total());
    }

    // ---- profiles ----

    [Fact]
    public async Task The_profile_list_has_defaults_strict_retail_and_production_and_equals_over_http_and_mcp()
    {
        await using var agent = await AgentApi.AgentAsync(app);

        var http = await AgentApi.ProfileListAsync(agent.Http);

        McpAssert.JsonEqual(http, await agent.Mcp.OkAsync("rule_profile_list"), "rule_profile_list differs from GET /rule-profiles");
        foreach (var key in AgentApi.ProfileKeys)
            Assert.Contains(key, http.Items().Select(p => p.Str("key")));
        Assert.All(http.Items(), p =>
        {
            Assert.Equal(new[] { "description", "key", "name", "ruleCount" }, p.PropertyNames());
            Assert.False(string.IsNullOrWhiteSpace(p.Str("description")));
        });
    }

    [Theory]
    [MemberData(nameof(ProfileData))]
    public async Task Every_key_of_every_profile_exists_with_a_boolean_value(string profile)
    {
        await using var agent = await AgentApi.AgentAsync(app);

        var http = await AgentApi.ProfileAsync(agent.Http, profile);

        McpAssert.JsonEqual(http, await agent.Mcp.OkAsync("rule_profile_get", new { key = profile }), $"rule_profile_get {profile} differs from HTTP");
        Assert.Equal(new[] { "changes", "description", "key", "name" }, http.PropertyNames());
        var changes = http.GetProperty("changes").EnumerateArray().ToArray();
        Assert.Equal(changes.Length, (await AgentApi.ProfileListAsync(agent.Http)).Item("key", profile).GetProperty("ruleCount").GetInt32());
        Assert.Equal(changes.Length, changes.Select(c => c.Str("key")).Distinct().Count());
        foreach (var change in changes)
        {
            Assert.Equal(new[] { "key", "value" }, change.PropertyNames());
            Assert.Contains(change.Str("key"), Rules.All);
            Assert.True(change.GetProperty("value").ValueKind is JsonValueKind.True or JsonValueKind.False, $"{change}");
            await Rules.GetAsync(agent.Http, change.Str("key"));
        }
    }

    public static IEnumerable<object[]> ProfileData() => AgentApi.ProfileKeys.Select(k => new object[] { k });

    [Fact]
    public async Task An_unknown_profile_is_404()
    {
        await using var agent = await AgentApi.AgentAsync(app);

        using var response = await agent.Http.GetAsync($"{AgentApi.Profiles}/nope");

        await HttpAssert.NotFoundAsync(response);
        await agent.Mcp.ErrorAsync("rule_profile_get", new { key = "nope" }, "NOT_FOUND");
    }

    [Fact]
    public async Task The_defaults_profile_holds_only_shipped_values_and_applying_it_leaves_a_default_tenant_unchanged()
    {
        await using var agent = await AgentApi.AgentAsync(app);
        var defaults = await AgentApi.ProfileAsync(agent.Http, "defaults");
        Assert.All(defaults.GetProperty("changes").EnumerateArray(), c => Assert.Equal(Rules.Default(c.Str("key")), c.Bool("value")));

        await agent.Mcp.OkAsync("rule_set_many", new { changes = defaults.GetProperty("changes"), reason = "defaults" });

        Assert.Equal(0, (await Rules.ListAsync(agent.Http, "?source=tenant")).Total());
    }

    [Fact]
    public async Task The_strict_profile_applied_through_rule_set_many_shows_exactly_its_non_default_values_as_tenant_rules()
    {
        await using var agent = await AgentApi.AgentAsync(app);
        var strict = await AgentApi.ProfileAsync(agent.Http, "strict");
        var changes = strict.GetProperty("changes").EnumerateArray().ToArray();
        var nonDefault = changes.Where(c => c.Bool("value") != Rules.Default(c.Str("key"))).ToArray();
        Assert.NotEmpty(nonDefault);

        await agent.Mcp.OkAsync("rule_set_many", new { changes = strict.GetProperty("changes"), reason = "strict profile" });

        var tenant = await Rules.ListAsync(agent.Http, "?source=tenant&limit=500");
        Assert.Equal(nonDefault.Select(c => c.Str("key")).Order(StringComparer.Ordinal), tenant.RuleKeys());
        foreach (var change in nonDefault)
            Rules.AssertBool(tenant.Item("key", change.Str("key")), "value", change.Bool("value"));
        Assert.Equal(nonDefault.Length, (await Rules.ChangesAsync(agent.Http, "?limit=500")).Total());
    }

    // ---- rule_preview ----

    [Fact]
    public async Task rule_preview_describes_each_change_without_applying_anything()
    {
        await using var agent = await AgentApi.AgentAsync(app);
        var body = AgentApi.Changes((Rules.NegativeStock, true), (DocRules.ReferenceRequired, true));

        var http = await AgentApi.PreviewAsync(agent.Http, body);
        var mcp = await agent.Mcp.OkAsync("rule_preview", body);

        McpAssert.JsonEqual(http, mcp, "rule_preview differs from POST /rules/preview");
        Assert.Equal(new[] { "items" }, http.PropertyNames());
        Assert.Equal(new[] { DocRules.ReferenceRequired, Rules.NegativeStock }.Order(StringComparer.Ordinal), http.ChangeKeys());
        foreach (var item in http.Items())
        {
            Assert.Equal(new[] { "affectedDrafts", "judges", "key", "name", "newValue", "oldValue" }, item.PropertyNames());
            Assert.False(string.IsNullOrWhiteSpace(item.Str("name")));
            Assert.False(string.IsNullOrWhiteSpace(item.Str("judges")));
            Rules.AssertBool(item, "oldValue", Rules.Default(item.Str("key")));
            Rules.AssertBool(item, "newValue", true);
        }
        Assert.Equal(JsonValueKind.Null, http.Item("key", Rules.NegativeStock).GetProperty("affectedDrafts").ValueKind);
        Assert.Equal(0, (await Rules.ListAsync(agent.Http, "?source=tenant")).Total());
        Assert.Equal(0, (await Rules.ChangesAsync(agent.Http)).Total());
    }

    [Fact]
    public async Task rule_preview_counts_the_drafts_that_would_be_refused_where_that_is_cheap()
    {
        await using var agent = await AgentApi.AgentAsync(app);
        var s = await Orders.SetupAsync(app, agent.Tenant);
        await Stock.CreateAsync(s.Http, Stock.Draft("receipt", s.W1, (s.A, 1)).Dated(DocRules.Today));
        await Stock.CreateAsync(s.Http, Stock.Draft("issue", s.W1, (s.A, 1)).Dated(DocRules.Today));
        await Stock.CreateAsync(s.Http, Stock.Draft("receipt", s.W1, (s.A, 1)).With("reference", "DN-1").Dated(DocRules.Past));
        await Stock.CreateAsync(s.Http, Stock.Draft("receipt", s.W1, (s.A, 1)).With("reference", "DN-2").Dated(DocRules.Today));

        var preview = await agent.Mcp.OkAsync("rule_preview", AgentApi.Changes((DocRules.ReferenceRequired, true), (DocRules.PastDate, false), (DocRules.ReceiptPartner, true)));

        Assert.Equal(2, preview.Item("key", DocRules.ReferenceRequired).GetProperty("affectedDrafts").GetInt32());
        Assert.Equal(1, preview.Item("key", DocRules.PastDate).GetProperty("affectedDrafts").GetInt32());
        Assert.Equal(3, preview.Item("key", DocRules.ReceiptPartner).GetProperty("affectedDrafts").GetInt32());
        Assert.Equal(0, (await Rules.ListAsync(s.Http, "?source=tenant")).Total());
    }

    [Fact]
    public async Task rule_preview_refuses_an_unknown_key_and_counts_only_the_callers_drafts()
    {
        await using var agent = await AgentApi.AgentAsync(app);
        var other = await Orders.SetupAsync(app);
        await Stock.CreateAsync(other.Http, Stock.Draft("receipt", other.W1, (other.A, 1)));

        using (var response = await agent.Http.PostAsJsonAsync(AgentApi.Preview, AgentApi.Changes(("no.such.rule", true))))
            await HttpAssert.ValidationAsync(response, "changes[0].key");
        await agent.Mcp.ErrorAsync("rule_preview", AgentApi.Changes(("no.such.rule", true)), "VALIDATION_FAILED", "changes[0].key");

        var preview = await AgentApi.PreviewAsync(agent.Http, AgentApi.Changes((DocRules.ReferenceRequired, true)));
        var count = preview.Item("key", DocRules.ReferenceRequired).GetProperty("affectedDrafts");
        Assert.True(count.ValueKind == JsonValueKind.Null || count.GetInt32() == 0, $"Another tenant's draft was counted: {count}");
    }
}
