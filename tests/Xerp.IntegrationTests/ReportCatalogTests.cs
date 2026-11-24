using System.Net;
using System.Text.Json;
using Xerp.IntegrationTests.Support;

namespace Xerp.IntegrationTests;

/// <summary>Spec RP01: the catalogue (report_list), every report with its defaults in an empty and in a seeded tenant, the tools.</summary>
[Collection(XerpCollection.Name)]
public class ReportCatalogTests(XerpFixture app)
{
    public static IEnumerable<object[]> AllKeys() => ReportApi.KeyData();

    [Fact]
    public async Task AC1_report_list_lists_the_13_reports_with_parameters_and_columns()
    {
        var tenant = await app.NewTenantAsync();

        var list = await ReportApi.ListAsync(tenant.Client);

        var items = list.Items();
        Assert.Equal(13, items.Length);
        Assert.Equal(ReportApi.Keys.Order(StringComparer.Ordinal), items.Select(i => i.Str("key")).Order(StringComparer.Ordinal));
        foreach (var item in items)
        {
            var key = item.Str("key");
            Assert.False(string.IsNullOrWhiteSpace(item.Str("name")), $"{key}: no name");
            Assert.False(string.IsNullOrWhiteSpace(item.Str("description")), $"{key}: no description");
            Assert.Equal(ReportApi.Columns[key], item.GetProperty("columns").EnumerateArray().Select(c => c.Str("key")).ToArray());
            foreach (var column in item.GetProperty("columns").EnumerateArray())
            {
                Assert.False(string.IsNullOrWhiteSpace(column.Str("label")), $"{key}.{column.Str("key")}: no label");
                Assert.Contains(column.Str("type"), ReportApi.ColumnTypes);
            }
            Assert.Equal(JsonValueKind.Array, item.GetProperty("parameters").ValueKind);
            foreach (var parameter in item.GetProperty("parameters").EnumerateArray())
            {
                Assert.False(string.IsNullOrWhiteSpace(parameter.Str("name")));
                Assert.False(string.IsNullOrWhiteSpace(parameter.Str("type")));
                Assert.True(parameter.TryGetProperty("required", out var required) && required.ValueKind is JsonValueKind.True or JsonValueKind.False,
                    $"{key}.{parameter.Str("name")}: 'required' must be a boolean");
                Assert.True(parameter.TryGetProperty("default", out _), $"{key}.{parameter.Str("name")}: 'default' is missing (null when there is none)");
                Assert.True(parameter.TryGetProperty("allowedValues", out _), $"{key}.{parameter.Str("name")}: 'allowedValues' is missing (null when open)");
            }
        }
    }

    [Fact]
    public async Task AC1_the_parameters_say_which_report_takes_what()
    {
        var tenant = await app.NewTenantAsync();
        var items = (await ReportApi.ListAsync(tenant.Client)).Items().ToDictionary(i => i.Str("key"));

        string[] Names(string key) => items[key].GetProperty("parameters").EnumerateArray().Select(p => p.Str("name")).ToArray();

        foreach (var key in ReportApi.PeriodKeys)
        {
            Assert.Contains("from", Names(key));
            Assert.Contains("to", Names(key));
        }
        foreach (var key in ReportApi.AsOfNowKeys)
        {
            Assert.DoesNotContain("from", Names(key));
            Assert.DoesNotContain("to", Names(key));
        }
        foreach (var key in ReportApi.GroupedKeys)
        {
            var groupBy = items[key].GetProperty("parameters").EnumerateArray().Single(p => p.Str("name") == "groupBy");
            Assert.Equal(ReportApi.GroupBys.Order(StringComparer.Ordinal), groupBy.GetProperty("allowedValues").EnumerateArray().Select(v => v.GetString()!).Order(StringComparer.Ordinal));
        }
        foreach (var key in ReportApi.Keys.Except(ReportApi.GroupedKeys))
            Assert.DoesNotContain("groupBy", Names(key));

        Assert.Contains("partnerId", Names("sales-by-period"));
        Assert.Contains("categoryId", Names("sales-by-article"));
        Assert.Contains("articleId", Names("sales-by-article"));
        Assert.Contains("warehouseId", Names("stock-on-hand"));
        Assert.Contains("warehouseId", Names("stock-movements"));
        var days = items["slow-movers"].GetProperty("parameters").EnumerateArray().Single(p => p.Str("name") == "days");
        Assert.Equal(90, days.GetProperty("default").GetInt32());
    }

    [Fact]
    public async Task AC1_report_list_needs_a_key()
    {
        using var response = await app.Anonymous().GetAsync(ReportApi.Path);
        await HttpAssert.UnauthenticatedAsync(response);
        using var run = await app.Anonymous().GetAsync(ReportApi.Url("sales-by-period"));
        await HttpAssert.UnauthenticatedAsync(run);
    }

    [Theory]
    [MemberData(nameof(AllKeys))]
    public async Task AC_Section1_every_report_runs_with_its_defaults_in_an_empty_tenant(string key)
    {
        var tenant = await app.NewTenantAsync();
        var definition = (await ReportApi.ListAsync(tenant.Client)).Items().Single(i => i.Str("key") == key);

        var body = await ReportApi.RunAsync(tenant.Client, key);

        ReportApi.AssertShape(body, key, definition);
        Assert.Empty(body.Rows());
        Assert.Equal(0, body.GetProperty("rowCount").GetInt32());
        if (body.HasTotals())
            Assert.All(body.GetProperty("totals").EnumerateObject(), t => Assert.Equal(0m, t.Value.GetDecimal()));
    }

    [Theory]
    [MemberData(nameof(AllKeys))]
    public async Task AC_Section1_every_report_runs_with_its_defaults_in_a_seeded_tenant(string key)
    {
        var seed = await ReportSeed.GetAsync(app);
        var definition = (await ReportApi.ListAsync(seed.Http)).Items().Single(i => i.Str("key") == key);

        var body = await ReportApi.RunAsync(seed.Http, key);

        ReportApi.AssertShape(body, key, definition);
    }

    [Theory]
    [MemberData(nameof(AllKeys))]
    public async Task AC2_every_report_over_the_seed_has_totals_equal_to_the_sum_of_its_rows(string key)
    {
        var seed = await ReportSeed.GetAsync(app);
        var definition = (await ReportApi.ListAsync(seed.Http)).Items().Single(i => i.Str("key") == key);

        var body = await ReportApi.RunAsync(seed.Http, key, ReportQueries.Seeded(key));

        ReportApi.AssertShape(body, key, definition);
        Assert.NotEmpty(body.Rows());
        if (key != "slow-movers")
            Assert.True(body.HasTotals(), $"{key} has rows, so it must have totals");
        Assert.Equal(key, body.Str("report"));
    }

    [Fact]
    public async Task The_default_period_is_the_current_month()
    {
        var tenant = await app.NewTenantAsync();
        var before = DateTime.UtcNow;

        var body = await ReportApi.RunAsync(tenant.Client, "sales-by-period");

        var parameters = body.GetProperty("parameters");
        var allowed = new[] { before, DateTime.UtcNow }.Select(d => $"{d:yyyy-MM}-01").Distinct().ToArray();
        Assert.Contains(parameters.Str("from"), allowed);
        Assert.True(string.CompareOrdinal(parameters.Str("to"), parameters.Str("from")) >= 0);
        Assert.StartsWith(parameters.Str("from")[..7], parameters.Str("to"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_parameters_given_are_echoed_back()
    {
        var seed = await ReportSeed.GetAsync(app);

        var body = await ReportApi.RunAsync(seed.Http, "sales-by-period", ReportApi.Period(ReportSeed.From, ReportSeed.To, "quarter"));

        var parameters = body.GetProperty("parameters");
        Assert.Equal(ReportSeed.From, parameters.Str("from"));
        Assert.Equal(ReportSeed.To, parameters.Str("to"));
        Assert.Equal("quarter", parameters.Str("groupBy"));
    }

    [Fact]
    public async Task AC1_tools_list_grows_by_two_and_the_tools_are_read_only()
    {
        await using var agent = await AgentApi.AgentAsync(app);

        var tools = (await agent.Mcp.Client.ListToolsAsync()).Select(t => t.ProtocolTool).ToDictionary(t => t.Name);

        Assert.Equal(ReportApi.ToolCount, tools.Count);
        Assert.Equal(132 + 2, tools.Count);
        foreach (var name in ReportApi.ToolNames)
        {
            Assert.True(tools.ContainsKey(name), $"Tool '{name}' is not listed.");
            Assert.False(string.IsNullOrWhiteSpace(tools[name].Description), $"{name}: no description.");
            Assert.True(tools[name].Annotations?.ReadOnlyHint == true, $"{name} must be read-only.");
        }
        var schema = tools["report_run"].InputSchema;
        Assert.Contains("key", schema.GetProperty("required").EnumerateArray().Select(r => r.GetString()));
        var properties = schema.GetProperty("properties").EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(new[] { "format", "key", "parameters" }, properties);
        Assert.Empty(tools["report_list"].InputSchema.GetProperty("properties").EnumerateObject());
    }
}

/// <summary>The query that gives a report its seeded period (the reports that take none are asked without one).</summary>
public static class ReportQueries
{
    public static (string, string?)[] Seeded(string key) =>
        !ReportApi.PeriodKeys.Contains(key) ? []
        : ReportApi.GroupedKeys.Contains(key) ? ReportApi.Period(ReportSeed.From, ReportSeed.To, "month")
        : ReportApi.Period(ReportSeed.From, ReportSeed.To);
}
