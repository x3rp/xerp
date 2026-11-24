using System.Net;
using System.Text.RegularExpressions;
using Xerp.IntegrationTests.Support;

namespace Xerp.IntegrationTests;

/// <summary>Spec A01, 2.1 and 2.2 and the tool list: server instructions, the nine new tools, the <c>guide</c> playbooks.</summary>
[Collection(XerpCollection.Name)]
public class AgentGuideAndInstructionsTests(XerpFixture app)
{
    [Fact]
    public async Task The_server_instructions_point_the_agent_to_setup_status_guide_and_report_list_and_stay_short()
    {
        await using var agent = await AgentApi.AgentAsync(app);

        var instructions = agent.Mcp.Client.ServerInstructions ?? "";

        foreach (var tool in new[] { "setup_status", "guide", "report_list" })
            Assert.Contains(tool, instructions, StringComparison.Ordinal);
        Assert.Contains("rules", instructions, StringComparison.OrdinalIgnoreCase);
        Assert.InRange(instructions.Split('\n').Length, 5, 45); // "at most ~40 lines"
    }

    [Fact]
    public async Task The_tool_list_has_the_nine_new_tools_and_132_in_all()
    {
        await using var agent = await AgentApi.AgentAsync(app);

        var tools = (await agent.Mcp.Client.ListToolsAsync()).Select(t => t.ProtocolTool).ToDictionary(t => t.Name);

        Assert.Equal(AgentApi.ToolCount, tools.Count);
        Assert.Equal(123 + 9, tools.Count);
        foreach (var name in AgentApi.ToolNames)
        {
            Assert.True(tools.ContainsKey(name), $"Tool '{name}' is not listed.");
            Assert.False(string.IsNullOrWhiteSpace(tools[name].Description), $"{name}: no description.");
        }
        foreach (var name in new[] { "guide", "setup_status", "rule_group_list", "rule_profile_list", "rule_profile_get", "rule_preview", "search" })
            Assert.True(tools[name].Annotations?.ReadOnlyHint == true, $"{name} must be read-only.");
    }

    // ---- guide ----

    [Fact]
    public async Task The_guide_lists_its_topics_with_one_line_each_over_http_and_mcp()
    {
        await using var agent = await AgentApi.AgentAsync(app);

        var http = await AgentApi.GuideAsync(agent.Http);
        var mcp = await AgentApi.GuideAsync(agent.Mcp);

        McpAssert.JsonEqual(http, mcp, "guide differs from GET /guide");
        var topics = http.Items().Select(i => i.Str("topic")).ToArray();
        foreach (var topic in AgentApi.Topics)
            Assert.Contains(topic, topics);
        Assert.Equal(topics.Length, topics.Distinct().Count());
        Assert.All(http.Items(), i =>
        {
            Assert.False(string.IsNullOrWhiteSpace(i.Str("summary")), $"{i.Str("topic")}: no summary.");
            Assert.DoesNotContain('\n', i.Str("summary"));
        });
    }

    [Theory]
    [MemberData(nameof(TopicData))]
    public async Task Every_topic_returns_a_markdown_playbook_equal_over_http_and_mcp(string topic)
    {
        await using var agent = await AgentApi.AgentAsync(app);

        var http = await AgentApi.GuideAsync(agent.Http, topic);
        var mcp = await AgentApi.GuideAsync(agent.Mcp, topic);

        McpAssert.JsonEqual(http, mcp, $"guide {topic} differs from GET /guide/{topic}");
        Assert.Equal(topic, http.Str("topic"));
        var markdown = http.Str("markdown");
        Assert.True(markdown.Length > 200, $"{topic}: the playbook is nearly empty.");
        Assert.Matches(new Regex(@"^\s*#", RegexOptions.Multiline), markdown); // a Markdown heading
    }

    public static IEnumerable<object[]> TopicData() => AgentApi.Topics.Select(t => new object[] { t });

    [Fact]
    public async Task An_unknown_topic_is_404_NOT_FOUND_over_http_and_a_tool_error_through_mcp()
    {
        await using var agent = await AgentApi.AgentAsync(app);

        using var response = await agent.Http.GetAsync($"{AgentApi.Guide}/no-such-topic");
        Rules.AssertNamesNoRule(await HttpAssert.ProblemAsync(response, HttpStatusCode.NotFound, "NOT_FOUND"));
        await agent.Mcp.ErrorAsync("guide", new { topic = "no-such-topic" }, "NOT_FOUND");
    }

    [Fact]
    public async Task Every_tool_of_the_server_is_mentioned_by_at_least_one_playbook()
    {
        await using var agent = await AgentApi.AgentAsync(app);
        var all = string.Join("\n", await Task.WhenAll((await AgentApi.GuideAsync(agent.Http)).Items()
            .Select(async i => (await AgentApi.GuideAsync(agent.Http, i.Str("topic"))).Str("markdown"))));

        var tools = (await agent.Mcp.Client.ListToolsAsync()).Select(t => t.ProtocolTool.Name).ToArray();

        var missing = tools.Where(t => !Regex.IsMatch(all, $@"(?<![A-Za-z0-9_]){Regex.Escape(t)}(?![A-Za-z0-9_])")).ToArray();
        Assert.True(missing.Length == 0, $"No playbook mentions: {string.Join(", ", missing)}");
    }

    [Fact]
    public async Task The_guide_needs_no_data_and_two_tenants_read_the_same_playbooks()
    {
        var a = await app.NewTenantAsync();
        var b = await app.NewTenantAsync();

        McpAssert.JsonEqual(await AgentApi.GuideAsync(a.Client, "getting-started"), await AgentApi.GuideAsync(b.Client, "getting-started"), "the guide differs between tenants");
    }
}
