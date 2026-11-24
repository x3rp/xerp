using System.Net;
using System.Text.Json;
using Xerp.IntegrationTests.Support;

namespace Xerp.IntegrationTests;

/// <summary>Spec RP01: parameter validation, tenant isolation, MCP parity.</summary>
[Collection(XerpCollection.Name)]
public class ReportParameterTests(XerpFixture app)
{
    public static IEnumerable<object[]> PeriodKeys() => ReportApi.PeriodKeys.Select(k => new object[] { k });

    public static IEnumerable<object[]> GroupedKeys() => ReportApi.GroupedKeys.Select(k => new object[] { k });

    public static IEnumerable<object[]> AllKeys() => ReportApi.KeyData();

    private static async Task<JsonElement> InvalidAsync(HttpResponseMessage response, params string[] anyOf)
    {
        using var _ = response;
        var problem = await HttpAssert.ProblemAsync(response, HttpStatusCode.BadRequest, "VALIDATION_FAILED");
        Assert.True(problem.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Object, "errors is missing");
        Assert.True(anyOf.Any(k => errors.TryGetProperty(k, out var messages) && messages.GetArrayLength() > 0),
            $"errors must be keyed by one of [{string.Join(", ", anyOf)}]: {errors}");
        return problem;
    }

    // ---- validation: 400 keyed by the parameter

    [Theory]
    [MemberData(nameof(PeriodKeys))]
    public async Task AC5_from_after_to_is_refused_with_the_period_as_key(string key)
    {
        var seed = await ReportSeed.GetAsync(app);

        // The spec does not say whether "from" or "to" carries the message: either is keyed.
        await InvalidAsync(await ReportApi.SendAsync(seed.Http, key, ("from", "2026-11-30"), ("to", "2026-10-01")), "from", "to");
    }

    [Fact]
    public async Task AC5_one_day_is_a_valid_period()
    {
        var seed = await ReportSeed.GetAsync(app);

        var body = await ReportApi.RunAsync(seed.Http, "sales-by-period", ("from", "2026-10-12"), ("to", "2026-10-12"));

        Assert.Equal(1m, body.Total("invoiceCount"));
    }

    [Theory]
    [MemberData(nameof(GroupedKeys))]
    public async Task AC5_an_unknown_groupBy_is_refused_with_groupBy_as_key(string key)
    {
        var seed = await ReportSeed.GetAsync(app);

        var problem = await InvalidAsync(await ReportApi.SendAsync(seed.Http, key, ("groupBy", "fortnight")), "groupBy");

        Assert.Equal(new[] { "groupBy" }, McpAssert.ErrorKeys(problem));
    }

    [Theory]
    [InlineData("from", "2026-02-30")]
    [InlineData("from", "yesterday")]
    [InlineData("from", "01.10.2026")]
    [InlineData("to", "2026-13-01")]
    [InlineData("to", "x")]
    public async Task AC5_a_date_that_is_not_a_date_is_refused_with_its_parameter_as_key(string parameter, string value)
    {
        var seed = await ReportSeed.GetAsync(app);

        await InvalidAsync(await ReportApi.SendAsync(seed.Http, "sales-by-period", (parameter, value)), parameter);
    }

    [Theory]
    [InlineData("sales-by-article", "articleId")]
    [InlineData("sales-by-article", "categoryId")]
    [InlineData("sales-by-customer", "partnerId")]
    [InlineData("stock-on-hand", "warehouseId")]
    [InlineData("stock-movements", "articleId")]
    public async Task AC5_an_id_that_is_not_a_uuid_is_refused_with_its_parameter_as_key(string key, string parameter)
    {
        var seed = await ReportSeed.GetAsync(app);

        await InvalidAsync(await ReportApi.SendAsync(seed.Http, key, (parameter, "not-a-uuid")), parameter);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("-1")]
    public async Task AC5_days_must_be_a_positive_number(string days)
    {
        var seed = await ReportSeed.GetAsync(app);

        await InvalidAsync(await ReportApi.SendAsync(seed.Http, "slow-movers", ("days", days)), "days");
    }

    [Theory]
    [MemberData(nameof(AllKeys))]
    public async Task AC_section1_no_parameter_combination_answers_500(string key)
    {
        var seed = await ReportSeed.GetAsync(app);
        var junk = new (string, string?)[][]
        {
            [("from", ""), ("to", "")],
            [("from", "0001-01-01"), ("to", "9999-12-31")],
            [("from", "9999-12-31"), ("to", "0001-01-01")],
            [("groupBy", ""), ("from", "2026-10-01")],
            [("groupBy", "DAY")],
            [("groupBy", "month"), ("groupBy", "year")],
            [("articleId", ""), ("partnerId", "00000000-0000-0000-0000-000000000000")],
            [("warehouseId", Guid.NewGuid().ToString()), ("categoryId", Guid.NewGuid().ToString())],
            [("days", "0"), ("days", "99999999999999999999")],
            [("limit", "-5"), ("unknown", "x")],
        };

        foreach (var query in junk)
        {
            using var response = await ReportApi.SendAsync(seed.Http, key, query);
            Assert.True((int)response.StatusCode < 500, $"{key} {string.Join("&", query.Select(q => $"{q.Item1}={q.Item2}"))} answered {(int)response.StatusCode}");
            if (response.StatusCode == HttpStatusCode.BadRequest)
                await HttpAssert.ProblemAsync(response, HttpStatusCode.BadRequest, "VALIDATION_FAILED");
        }
    }

    [Fact]
    public async Task AC5_an_unknown_report_key_is_404_not_found()
    {
        var seed = await ReportSeed.GetAsync(app);

        using var response = await ReportApi.SendAsync(seed.Http, "no-such-report");
        await HttpAssert.NotFoundAsync(response);
        using var csv = await seed.Http.GetAsync(ReportApi.Url("no-such-report", ("format", "csv")));
        await HttpAssert.NotFoundAsync(csv);
    }

    // ---- MCP: parity and errors

    private static Dictionary<string, string> ToParameters((string Name, string? Value)[] query) =>
        query.Where(q => q.Value is not null).ToDictionary(q => q.Name, q => q.Value!);

    [Fact]
    public async Task AC7_report_list_over_mcp_is_the_same_body_as_over_http()
    {
        await using var agent = await AgentApi.AgentAsync(app);

        var http = await ReportApi.ListAsync(agent.Http);
        var mcp = await ReportApi.ListAsync(agent.Mcp);

        McpAssert.JsonEqual(http, mcp, "report_list differs from GET /reports");
        Assert.Equal(13, mcp.Items().Length);
    }

    [Theory]
    [MemberData(nameof(AllKeys))]
    public async Task AC7_report_run_returns_the_same_body_as_the_http_json(string key)
    {
        var seed = await ReportSeed.GetAsync(app);
        var keyOfSeed = await Keys.CreateAsync(app, seed.Http, "claude-rp01", "agent");
        await using var mcp = await app.McpAsync(keyOfSeed.Key);
        var query = ReportQueries.Seeded(key);

        var http = await ReportApi.RunAsync(seed.Http, key, query);
        var viaMcp = await ReportApi.RunAsync(mcp, key, ToParameters(query));

        Assert.Equal(http.WithoutTimestamp(), viaMcp.WithoutTimestamp());
        Assert.NotEmpty(viaMcp.Rows());
    }

    [Fact]
    public async Task AC7_report_run_without_parameters_uses_the_defaults()
    {
        await using var agent = await AgentApi.AgentAsync(app);

        var viaMcp = await ReportApi.RunAsync(agent.Mcp, "stock-on-hand");
        var http = await ReportApi.RunAsync(agent.Http, "stock-on-hand");

        Assert.Equal(http.WithoutTimestamp(), viaMcp.WithoutTimestamp());
    }

    [Fact]
    public async Task AC5_report_run_over_mcp_refuses_bad_parameters_with_the_parameter_as_key()
    {
        await using var agent = await AgentApi.AgentAsync(app);

        await agent.Mcp.ErrorAsync("report_run", new { key = "sales-by-period", parameters = new { groupBy = "fortnight" } }, "VALIDATION_FAILED", "groupBy");
        var period = McpAssert.Error(await agent.Mcp.CallAsync("report_run",
            new { key = "sales-by-period", parameters = new { from = "2026-11-30", to = "2026-10-01" } }), "VALIDATION_FAILED");
        var keys = McpAssert.ErrorKeys(period);
        Assert.True(keys.Contains("from") || keys.Contains("to"), $"errors must be keyed by from or to: [{string.Join(",", keys)}]");
        await agent.Mcp.ErrorAsync("report_run", new { key = "slow-movers", parameters = new { days = "abc" } }, "VALIDATION_FAILED", "days");
        await agent.Mcp.ErrorAsync("report_run", new { key = "no-such-report" }, "NOT_FOUND");
        // A report_run without its key is an invalid call, not a crash.
        var missing = await agent.Mcp.CallAsync("report_run", new { });
        Assert.True(missing.IsError == true);
    }

    // ---- tenant isolation

    [Fact]
    public async Task AC6_a_report_never_includes_another_tenants_data()
    {
        var seed = await ReportSeed.GetAsync(app);
        var (other, otherCustomer) = await ReportSeed.OtherTenantAsync(app);
        var query = ReportApi.Period(ReportSeed.From, ReportSeed.To, "month");

        var mine = await ReportApi.RunAsync(seed.Http, "sales-by-period", query);
        var theirs = await ReportApi.RunAsync(other.Client, "sales-by-period", query);

        // The seeded tenant's October is still 345.00 net (not 1345.00); the other tenant has only its own 1000.00.
        mine.Row(("period", "2026-10-01")).AssertNums(("invoiceCount", 2), ("netAmount", 345m), ("totalAmount", 419.25m));
        Assert.Equal(435m, mine.Total("netAmount"));
        var october = Assert.Single(theirs.Rows());
        october.AssertNums(("invoiceCount", 1), ("netAmount", 1000m), ("taxAmount", 250m), ("totalAmount", 1250m));
        Assert.Equal(1000m, theirs.Total("netAmount"));

        foreach (var key in new[] { "sales-by-customer", "sales-by-article", "unpaid-invoices", "stock-on-hand", "stock-movements" })
        {
            var q = ReportQueries.Seeded(key);
            var a = (await ReportApi.RunAsync(seed.Http, key, q)).ToString();
            var b = (await ReportApi.RunAsync(other.Client, key, q)).ToString();
            Assert.DoesNotContain(otherCustomer, a);
            Assert.DoesNotContain("Tuđi artikl", a);
            foreach (var name in new[] { "Čakovec", "Šljivovica", "Žitarice", "KUP2" })
                Assert.DoesNotContain(name, b);
        }
    }

    [Fact]
    public async Task AC6_a_filter_with_another_tenants_id_finds_nothing()
    {
        var seed = await ReportSeed.GetAsync(app);
        var other = await app.NewTenantAsync();

        using var byCustomer = await ReportApi.SendAsync(other.Client, "sales-by-customer", ReportApi.Period(ReportSeed.From, ReportSeed.To, null, ("partnerId", seed.Kup1.ToString())));
        using var byArticle = await ReportApi.SendAsync(other.Client, "stock-on-hand", ("articleId", seed.A.ToString()));

        foreach (var response in new[] { byCustomer, byArticle })
        {
            Assert.True((int)response.StatusCode < 500);
            if (response.StatusCode == HttpStatusCode.OK)
                Assert.Empty((await HttpAssert.JsonAsync(response, HttpStatusCode.OK)).Rows());
            else
                Assert.InRange((int)response.StatusCode, 400, 499);
        }
    }

    [Fact]
    public async Task AC6_over_mcp_an_agent_sees_only_its_own_tenant()
    {
        var seed = await ReportSeed.GetAsync(app);
        var (other, _) = await ReportSeed.OtherTenantAsync(app);
        await using var agent = await AgentApi.AgentAsync(app, other);

        var body = await ReportApi.RunAsync(agent.Mcp, "sales-by-period", new { from = ReportSeed.From, to = ReportSeed.To, groupBy = "month" });

        Assert.Equal(1000m, body.Total("netAmount"));
        Assert.Equal(1m, body.Total("invoiceCount"));
        Assert.Equal(435m, (await ReportApi.RunAsync(seed.Http, "sales-by-period", ReportApi.Period(ReportSeed.From, ReportSeed.To, "month"))).Total("netAmount"));
    }
}
