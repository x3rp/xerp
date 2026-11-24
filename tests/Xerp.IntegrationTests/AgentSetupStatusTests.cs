using System.Text.Json;
using System.Net;
using System.Net.Http.Json;
using Xerp.IntegrationTests.Support;

namespace Xerp.IntegrationTests;

/// <summary>Spec A01, 2.3 and the company's <c>rulesReviewedAt</c> of 2.4: <c>setup_status</c> and <c>setup_mark_rules_reviewed</c>.</summary>
[Collection(XerpCollection.Name)]
public class AgentSetupStatusTests(XerpFixture app)
{
    private static void AssertShape(JsonElement status)
    {
        Assert.Equal(new[] { "done", "items", "ready", "total" }, status.PropertyNames());
        var items = status.Items();
        Assert.Equal(AgentApi.SetupKeys, items.Select(i => i.Str("key")).ToArray());
        Assert.Equal(items.Length, status.GetProperty("total").GetInt32());
        Assert.Equal(items.Count(i => i.Bool("done")), status.GetProperty("done").GetInt32());
        Assert.Equal(status.GetProperty("done").GetInt32() == status.GetProperty("total").GetInt32(), status.Bool("ready"));
        Assert.All(items, i =>
        {
            Assert.Equal(new[] { "detail", "done", "key", "nextStep" }, i.PropertyNames());
            Assert.Equal(JsonValueKind.String, i.GetProperty("detail").ValueKind);
        });
    }

    [Fact]
    public async Task A_new_tenant_has_nothing_done_and_every_open_item_names_its_next_step()
    {
        await using var agent = await AgentApi.AgentAsync(app);

        var http = await AgentApi.StatusAsync(agent.Http);
        var mcp = await agent.Mcp.OkAsync("setup_status");

        McpAssert.JsonEqual(http, mcp, "setup_status differs from GET /setup-status");
        AssertShape(http);
        Assert.False(http.Bool("ready"));
        // The default warehouse alone, the tenant's own name as the legal name, no masters: only what the system seeds can be done.
        foreach (var item in http.Items().Where(i => !i.Bool("done")))
        {
            var next = item.Str("nextStep");
            Assert.False(string.IsNullOrWhiteSpace(next), $"{item.Str("key")}: no nextStep.");
        }
        foreach (var key in new[] { "taxRates", "paymentMethods", "articles", "customer", "supplier", "rulesReviewed" })
            Assert.False(http.Item("key", key).Bool("done"), $"{key} cannot be done in a new tenant.");
        // nextStep names a tool and a guide topic (2.3).
        var tools = (await agent.Mcp.Client.ListToolsAsync()).Select(t => t.ProtocolTool.Name).ToHashSet();
        var topics = (await AgentApi.GuideAsync(agent.Http)).Items().Select(i => i.Str("topic")).ToHashSet();
        foreach (var item in http.Items().Where(i => !i.Bool("done") && i.Str("key") != AgentApi.BackupsKey))
        {
            var next = item.Str("nextStep");
            Assert.True(tools.Any(next.Contains), $"{item.Str("key")}: nextStep '{next}' names no tool.");
            Assert.True(topics.Any(next.Contains), $"{item.Str("key")}: nextStep '{next}' names no guide topic.");
        }
    }

    [Fact]
    public async Task After_the_steps_every_item_but_the_backups_check_is_done_and_the_status_follows_each_step()
    {
        await using var agent = await AgentApi.AgentAsync(app);
        var http = agent.Http;
        var done = (await AgentApi.StatusAsync(http)).GetProperty("done").GetInt32();

        async Task<JsonElement> StepAsync(string key)
        {
            var status = await AgentApi.StatusAsync(http);
            Assert.True(status.Item("key", key).Bool("done"), $"After its step '{key}' is not done: {status.Item("key", key)}");
            var now = status.GetProperty("done").GetInt32();
            Assert.True(now > done, $"The step of '{key}' did not raise 'done' ({done} -> {now}).");
            done = now;
            return status;
        }

        using (var company = await http.PutAsJsonAsync("/api/v1/company", new
        {
            legalName = "Vulco d.o.o.", taxId = "12345678903", addressLine1 = "Ilica 1", postalCode = "10000", city = "Zagreb",
            countryCode = "HR", iban = "HR1234567890123456789",
        }))
            await HttpAssert.JsonAsync(company, HttpStatusCode.OK);
        await StepAsync("companyProfile");

        await MasterApi.Warehouses.CreateAsync(http, "W1", "Second warehouse");
        await StepAsync("warehouses");
        var unit = await Uom.CreateAsync(http, "kom", "Komad");
        await StepAsync("unitsOfMeasure");
        var vat = await SalesMaster.TaxRates.CreateAsync(http, "PDV25", "PDV 25%");
        await StepAsync("taxRates");
        await SalesMaster.PaymentMethods.CreateAsync(http, "CASH", "Cash");
        await StepAsync("paymentMethods");
        var article = await Art.CreateAsync(http, "G1", "Goods", unit.Id());
        using (var sales = await http.PutAsJsonAsync($"/api/v1/articles/{article.Id()}/sales-data",
                   new { categoryId = (string?)null, taxRateIds = new[] { vat.Id() }, salesPrice = 12.5m }))
            await HttpAssert.JsonAsync(sales, HttpStatusCode.OK);
        await StepAsync("articles");
        await Orders.PartnerAsync(http, "C1", "Customer", isSupplier: false, isCustomer: true);
        await StepAsync("customer");
        await Orders.PartnerAsync(http, "S1", "Supplier", isSupplier: true, isCustomer: false);
        await StepAsync("supplier");
        await Users.CreateAsync(http, "ana");
        await StepAsync("users");
        await agent.Mcp.OkAsync("setup_mark_rules_reviewed");
        var final = await StepAsync("rulesReviewed");

        AssertShape(final);
        Assert.All(final.Items().Where(i => i.Str("key") != AgentApi.BackupsKey), i => Assert.True(i.Bool("done"), $"{i.Str("key")} is not done: {i}"));
        McpAssert.JsonEqual(final, await agent.Mcp.OkAsync("setup_status"), "setup_status differs from GET /setup-status");
    }

    [Fact]
    public async Task Marking_the_rules_reviewed_sets_rulesReviewedAt_once_over_http_and_through_mcp()
    {
        await using var agent = await AgentApi.AgentAsync(app);
        JsonBody.AssertNull(await agent.Mcp.OkAsync("company_get"), "rulesReviewedAt");

        var marked = await AgentApi.MarkReviewedAsync(agent.Http);

        Assert.Equal(JsonValueKind.String, marked.GetProperty("rulesReviewedAt").ValueKind);
        var company = await agent.Mcp.OkAsync("company_get");
        Assert.Equal(marked.GetProperty("rulesReviewedAt").GetDateTimeOffset(), company.GetProperty("rulesReviewedAt").GetDateTimeOffset());
        Assert.True((await AgentApi.StatusAsync(agent.Http)).Item("key", "rulesReviewed").Bool("done"));
    }

    [Fact]
    public async Task rule_set_many_marks_the_rules_reviewed_too()
    {
        await using var agent = await AgentApi.AgentAsync(app);

        await agent.Mcp.OkAsync("rule_set_many", AgentApi.Changes((Rules.NegativeStock, true)));

        Assert.Equal(JsonValueKind.String, (await AgentApi.GetAsync(agent.Http, "/api/v1/company")).GetProperty("rulesReviewedAt").ValueKind);
        Assert.True((await AgentApi.StatusAsync(agent.Http)).Item("key", "rulesReviewed").Bool("done"));
    }

    [Fact]
    public async Task The_status_of_one_tenant_knows_nothing_of_another()
    {
        var x = await AgentApi.AgentAsync(app);
        await using var _ = x;
        var y = await app.NewTenantAsync();
        await Orders.PartnerAsync(x.Http, "C1", "Customer", isSupplier: false, isCustomer: true);
        await AgentApi.MarkReviewedAsync(x.Http);

        var other = await AgentApi.StatusAsync(y.Client);

        Assert.False(other.Item("key", "customer").Bool("done"));
        Assert.False(other.Item("key", "rulesReviewed").Bool("done"));
    }
}
