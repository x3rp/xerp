using System.Text.Json;
using System.Net;
using Xerp.IntegrationTests.Support;

namespace Xerp.IntegrationTests;

/// <summary>Spec A01, 2.5: <c>search</c> — one call that finds articles, partners, warehouses, documents, orders and invoices.</summary>
[Collection(XerpCollection.Name)]
public class AgentSearchTests(XerpFixture app)
{
    private static void AssertHit(JsonElement hit, string type, Guid id)
    {
        Assert.Equal(type, hit.Str("type"));
        Assert.Equal(id, hit.Id());
        Assert.False(string.IsNullOrWhiteSpace(hit.Ref()), $"No code or number: {hit}");
    }

    [Fact]
    public async Task An_article_is_found_by_part_of_its_name_over_http_and_through_mcp()
    {
        await using var agent = await AgentApi.AgentAsync(app);
        var unit = await Uom.CreateAsync(agent.Http, "kom", "Komad");
        var bolt = await Art.CreateAsync(agent.Http, "BLT8", "Galvanised bolt M8", unit.Id());
        await Art.CreateAsync(agent.Http, "NUT8", "Hex nut M8", unit.Id());

        var http = await AgentApi.SearchAsync(agent.Http, "bolt");
        var mcp = await AgentApi.SearchAsync(agent.Mcp, "bolt");

        McpAssert.JsonEqual(http, mcp, "search differs from GET /search");
        var hit = Assert.Single(http.Items());
        AssertHit(hit, "article", bolt.Id());
        Assert.Equal("BLT8", hit.Ref());
        Assert.Equal("Galvanised bolt M8", hit.Label());
        // By code too, whatever the case.
        AssertHit(Assert.Single((await AgentApi.SearchAsync(agent.Http, "nut8")).Items()), "article", (await Art.ListAsync(agent.Http, "?search=nut")).Items()[0].Id());
    }

    [Fact]
    public async Task A_partner_is_found_by_its_tax_id()
    {
        await using var agent = await AgentApi.AgentAsync(app);
        var partner = await MasterApi.Partners.CreateAsync(agent.Http, MasterApi.Partners.Full("ACME").With("name", "Acme d.o.o.").With("taxId", "HR98765432109"));
        await MasterApi.Partners.CreateAsync(agent.Http, MasterApi.Partners.Full("OTHER").With("name", "Other Ltd").With("taxId", "HR11111111111"));

        var result = await AgentApi.SearchAsync(agent.Mcp, "98765432109");

        AssertHit(Assert.Single(result.Items()), "partner", partner.Id());
        McpAssert.JsonEqual(result, await AgentApi.SearchAsync(agent.Http, "98765432109"), "search differs from GET /search");
    }

    [Fact]
    public async Task An_invoice_is_found_by_its_number()
    {
        var s = await Invoices.SetupAsync(app);
        var draft = await Invoices.CreateAsync(s.Http, Invoices.Body(s));
        var issued = await Invoices.IssueAsync(s.Http, draft.Id());
        Assert.Equal("RN-000001", issued.Str("number"));

        var result = await AgentApi.SearchAsync(s.Http, "RN-000001");

        var hit = Assert.Single(result.Items(), h => h.Str("type") == "invoice");
        AssertHit(hit, "invoice", draft.Id());
        Assert.Equal("RN-000001", hit.Ref());
    }

    [Fact]
    public async Task A_document_and_an_order_are_found_by_their_number_and_reference()
    {
        var s = await Orders.SetupAsync(app);
        var receipt = await Stock.PostDocumentAsync(s.Http, (await Stock.CreateAsync(s.Http, Stock.Draft("receipt", s.W1, (s.A, 1)).With("reference", "DELIVERY-NOTE-77"))).Id());
        var order = await OrderApi.Purchase.CreateAsync(s.Http, OrderApi.Purchase.Body(s, (s.A, 1, null, 1m)).With("reference", "RFQ-4242"));

        AssertHit(Assert.Single((await AgentApi.SearchAsync(s.Http, receipt.Number())).Items()), "document", receipt.Id());
        AssertHit(Assert.Single((await AgentApi.SearchAsync(s.Http, "delivery-note-77")).Items()), "document", receipt.Id());
        AssertHit(Assert.Single((await AgentApi.SearchAsync(s.Http, "RFQ-4242")).Items()), "order", order.Id());
    }

    [Fact]
    public async Task A_warehouse_is_found_by_name()
    {
        await using var agent = await AgentApi.AgentAsync(app);
        var warehouse = await MasterApi.Warehouses.CreateAsync(agent.Http, "ZG1", "Zagreb distribution centre");

        AssertHit(Assert.Single((await AgentApi.SearchAsync(agent.Http, "distribution")).Items()), "warehouse", warehouse.Id());
    }

    [Fact]
    public async Task At_most_ten_hits_per_type_and_the_types_filter_narrows_the_search()
    {
        await using var agent = await AgentApi.AgentAsync(app);
        var unit = await Uom.CreateAsync(agent.Http, "kom", "Komad");
        for (var i = 0; i < 12; i++)
            await Art.CreateAsync(agent.Http, $"WID{i:00}", $"Widget {i:00}", unit.Id());
        await MasterApi.Partners.CreateAsync(agent.Http, MasterApi.Partners.Minimal("WIDCO", "Widget Co"));

        var all = await AgentApi.SearchAsync(agent.Http, "widget");
        var articlesOnly = await AgentApi.SearchAsync(agent.Http, "widget", "article");
        var partnersOnly = await AgentApi.SearchAsync(agent.Mcp, "widget", ["partner"]);

        Assert.Equal(10, all.Items().Count(h => h.Str("type") == "article"));
        Assert.Single(all.Items(), h => h.Str("type") == "partner");
        Assert.Equal(10, articlesOnly.Items().Length);
        Assert.All(articlesOnly.Items(), h => Assert.Equal("article", h.Str("type")));
        Assert.Equal("partner", Assert.Single(partnersOnly.Items()).Str("type"));
    }

    [Fact]
    public async Task Search_never_returns_another_tenants_records()
    {
        await using var x = await AgentApi.AgentAsync(app);
        await using var y = await AgentApi.AgentAsync(app);
        var unit = await Uom.CreateAsync(x.Http, "kom", "Komad");
        await Art.CreateAsync(x.Http, "SECRET1", "Secret prototype", unit.Id());
        await MasterApi.Partners.CreateAsync(x.Http, MasterApi.Partners.Full("SEC").With("taxId", "HR55555555555"));
        var invoices = await Invoices.SetupAsync(app);
        await Invoices.IssueAsync(invoices.Http, (await Invoices.CreateAsync(invoices.Http, Invoices.Body(invoices))).Id());

        foreach (var text in new[] { "secret", "55555555555", "RN-000001" })
        {
            Assert.Empty((await AgentApi.SearchAsync(y.Http, text)).Items());
            Assert.Empty((await AgentApi.SearchAsync(y.Mcp, text)).Items());
        }
        Assert.NotEmpty((await AgentApi.SearchAsync(x.Http, "secret")).Items());
    }

    [Fact]
    public async Task A_missing_or_blank_text_is_a_validation_failure_and_an_unknown_type_too()
    {
        await using var agent = await AgentApi.AgentAsync(app);

        using (var missing = await agent.Http.GetAsync(AgentApi.Search))
            await HttpAssert.ValidationAsync(missing, "q");
        using (var blank = await agent.Http.GetAsync($"{AgentApi.Search}?q=%20"))
            await HttpAssert.ValidationAsync(blank, "q");
        using (var type = await agent.Http.GetAsync($"{AgentApi.Search}?q=x&types=spaceship"))
            await HttpAssert.ValidationAsync(type, "types");
        await agent.Mcp.ErrorAsync("search", new { text = "  " }, "VALIDATION_FAILED", "text");
        Assert.Empty((await AgentApi.SearchAsync(agent.Http, "nothing-matches-this")).Items());
    }
}
