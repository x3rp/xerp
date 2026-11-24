using Xerp.IntegrationTests.Support;

namespace Xerp.IntegrationTests;

/// <summary>Spec 014, AC-10 to AC-13: the 13 new rules in the registry (70 rules in all, 123 tools).</summary>
[Collection(XerpCollection.Name)]
public class Rule014RegistryTests(XerpFixture app)
{
    [Fact]
    public async Task AC10_The_thirteen_rules_are_listed_at_their_place_in_ordinal_order_with_their_defaults()
    {
        var tenant = await app.NewTenantAsync();

        var list = await Rules.ListAsync(tenant.Client, "?limit=500");

        Assert.Equal(70, list.Total());
        Assert.Equal(Rules.All, list.RuleKeys());
        Assert.Equal(Rules.All.Order(StringComparer.Ordinal), list.RuleKeys());
        foreach (var (key, expected) in DocRules.Inventory)
        {
            var rule = Assert.Single(list.Items(), r => r.Str("key") == key);
            Assert.Equal(Rules.Representation, rule.PropertyNames());
            Assert.Equal(key.Split('.')[0], rule.Str("group"));
            Rules.AssertBool(rule, "default", expected);
            Rules.AssertBool(rule, "value", expected);
            Assert.Equal("default", rule.Str("source"));
            Assert.False(string.IsNullOrWhiteSpace(rule.Str("name")), $"{key}: name is empty.");
            Assert.False(string.IsNullOrWhiteSpace(rule.Str("description")), $"{key}: description is empty.");
        }
        Assert.Equal(13, DocRules.Keys.Length);
        Assert.Equal(13, DocRules.Keys.Distinct().Count());
    }

    [Fact]
    public async Task AC11_The_groups_document_posting_references_and_stock_have_five_one_one_and_seven_keys()
    {
        var tenant = await app.NewTenantAsync();
        var http = tenant.Client;

        Assert.Equal(
            new[] { DocRules.FutureDate, DocRules.PastDate, DocRules.ReferenceRequired, DocRules.RepeatedArticle, DocRules.WarehouseRequired },
            (await Rules.ListAsync(http, "?group=document")).RuleKeys());
        Assert.Equal(new[] { DocRules.ActiveMasters }, (await Rules.ListAsync(http, "?group=posting")).RuleKeys());
        Assert.Equal(new[] { DocRules.InactiveAssignable }, (await Rules.ListAsync(http, "?group=references")).RuleKeys());
        var stock = await Rules.ListAsync(http, "?group=stock");
        Assert.Equal(7, stock.Total());
        Assert.Equal(
            new[] { DocRules.IssuePartner, Rules.NegativeStock, DocRules.OutdatedCount, DocRules.PartnerRole, DocRules.ReceiptPartner,
                DocRules.ReversalMasters, DocRules.ReversalBefore },
            stock.RuleKeys());
    }

    [Theory]
    [MemberData(nameof(DocRules.KeyData), MemberType = typeof(DocRules))]
    public async Task AC12_Every_rule_accepts_both_values_and_resets(string key)
    {
        var tenant = await app.NewTenantAsync();
        var http = tenant.Client;
        var @default = DocRules.IsDefaultTrue(key);

        Rules.AssertTenantValue(await Rules.SetAsync(http, key, !@default), key, !@default, tenant.ApiKeyId);
        Rules.AssertTenantValue(await Rules.SetAsync(http, key, @default), key, @default, tenant.ApiKeyId);
        Rules.AssertTenantValue(await Rules.SetAsync(http, key, !@default), key, !@default, tenant.ApiKeyId);
        Rules.AssertAtDefault(await Rules.ResetAsync(http, key), key);
        Rules.AssertAtDefault(await Rules.GetAsync(http, key), key);
    }

    [Fact]
    public async Task AC13_The_tool_list_has_exactly_132_tools_and_no_tool_of_spec_014_is_added()
    {
        var tenant = await app.NewTenantAsync();
        var key = await Keys.CreateAsync(app, tenant.Client, "claude-014", "agent");
        await using var mcp = await app.McpAsync(key.Key);

        var tools = await mcp.Client.ListToolsAsync();

        Assert.Equal(AgentApi.ToolCount, tools.Count); // 123 + the 9 of spec A01
        Assert.Equal(tools.Count, tools.Select(t => t.Name).Distinct().Count());
    }
}
