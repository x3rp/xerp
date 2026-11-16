using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xerp.IntegrationTests.Support;

namespace Xerp.IntegrationTests;

/// <summary>
/// Spec W01: bills of materials and work orders, end to end over HTTP and MCP. The standard stock setup (spec
/// 005, section 10) plus a stock product P: one P is made from 2 A and 0.5 B.
/// </summary>
[Collection(XerpCollection.Name)]
public class WorkOrderTests(XerpFixture app)
{
    private sealed record Setup(StockSetup Stock, Guid P)
    {
        public HttpClient Http => Stock.Http;
        public Guid A => Stock.A;
        public Guid B => Stock.B;
        public Guid W1 => Stock.W1;
    }

    private async Task<Setup> SetupAsync(bool withBom = true)
    {
        var stock = await Stock.SetupAsync(app);
        var p = (await Art.CreateAsync(stock.Http, "P", "Product P", stock.UnitId)).Id();
        if (withBom)
            await Production.SetBomAsync(stock.Http, p, (stock.A, 2m), (stock.B, 0.5m));
        return new Setup(stock, p);
    }

    // ---- bill of materials

    [Fact]
    public async Task Bom_is_set_read_replaced_and_deleted()
    {
        var s = await SetupAsync(withBom: false);

        var empty = await HttpAssert.JsonAsync(await s.Http.GetAsync(Production.BomPath(s.P)), HttpStatusCode.OK);
        Assert.Empty(empty.GetProperty("components").EnumerateArray());

        var bom = await Production.SetBomAsync(s.Http, s.P, (s.A, 2m), (s.B, 0.5m));
        var components = bom.GetProperty("components").EnumerateArray().ToArray();
        Assert.Equal(2, components.Length);
        Assert.Equal(s.A, components[0].GetProperty("article").Id());
        Assert.Equal(2m, components[0].GetProperty("quantity").GetDecimal());
        Assert.Equal("pcs", components[0].GetProperty("unit").Str("code"));
        Assert.Equal(s.P, bom.GetProperty("article").Id());

        var replaced = await Production.SetBomAsync(s.Http, s.P, (s.B, 3m));
        Assert.Single(replaced.GetProperty("components").EnumerateArray());

        using var deleted = await s.Http.DeleteAsync(Production.BomPath(s.P));
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        using var again = await s.Http.DeleteAsync(Production.BomPath(s.P));
        await HttpAssert.NotFoundAsync(again);
    }

    [Fact]
    public async Task Bom_refuses_bad_components()
    {
        var s = await SetupAsync(withBom: false);

        await HttpAssert.ValidationAsync(await s.Http.PutAsJsonAsync(Production.BomPath(s.P), new JsonObject { ["components"] = new JsonArray() }), "components");
        await HttpAssert.ValidationAsync(await Production.SendSetBomAsync(s.Http, s.P, (s.A, 0m)), "components[0].quantity");
        await HttpAssert.ValidationAsync(await Production.SendSetBomAsync(s.Http, s.P, (s.A, 1m), (s.P, 1m)), "components[1].articleId");
        await HttpAssert.ReferenceNotFoundAsync(await Production.SendSetBomAsync(s.Http, s.P, (Guid.NewGuid(), 1m)), "components[0].articleId");
        await Stock.ConflictAsync(await Production.SendSetBomAsync(s.Http, s.P, (s.Stock.S, 1m)), "ARTICLE_NOT_STOCKED", "components[0].articleId");
        await HttpAssert.NotFoundAsync(await Production.SendSetBomAsync(s.Http, Guid.NewGuid(), (s.A, 1m)));

        // An article in a bill is in use: it cannot be deleted.
        await Production.SetBomAsync(s.Http, s.P, (s.A, 1m));
        await HttpAssert.InUseAsync(await s.Http.DeleteAsync($"{Art.Path}/{s.A}"));
    }

    // ---- work orders

    [Fact]
    public async Task Work_order_takes_its_lines_from_the_bill_and_completion_issues_components_and_receives_the_product()
    {
        var s = await SetupAsync();
        await Stock.ReceiveAsync(s.Http, s.W1, s.A, 100m);
        await Stock.ReceiveAsync(s.Http, s.W1, s.B, 100m);

        var draft = await Production.CreateAsync(s.Http, Production.Order(s.P, 10m, s.W1));
        Assert.Equal("draft", draft.Str("status"));
        Assert.Equal(JsonValueKind.Null, draft.GetProperty("number").ValueKind);
        var lines = draft.WorkLines();
        Assert.Equal([20m, 5m], lines.Select(l => l.GetProperty("plannedQuantity").GetDecimal()));

        var released = await Production.ActionAsync(s.Http, draft.Id(), "release");
        Assert.Equal("released", released.Str("status"));
        Assert.StartsWith("WO-", released.Str("number"));

        var completed = await Production.ActionAsync(s.Http, draft.Id(), "complete", new JsonObject { ["documentDate"] = Stock.Date });
        Assert.Equal("completed", completed.Str("status"));
        Assert.Equal(10m, completed.GetProperty("producedQuantity").GetDecimal());
        Assert.Equal([20m, 5m], completed.WorkLines().Select(l => l.GetProperty("consumedQuantity").GetDecimal()));
        Assert.StartsWith("SI-", completed.GetProperty("issueDocument").Str("number"));
        Assert.StartsWith("SR-", completed.GetProperty("receiptDocument").Str("number"));

        Assert.Equal(80m, await Stock.QuantityAsync(s.Http, s.A, s.W1));
        Assert.Equal(95m, await Stock.QuantityAsync(s.Http, s.B, s.W1));
        Assert.Equal(10m, await Stock.QuantityAsync(s.Http, s.P, s.W1));
        await Stock.AssertStockEqualsLedgerAsync(s.Http);

        var issue = await Stock.GetAsync(s.Http, completed.GetProperty("issueDocument").Id());
        Assert.Equal("posted", issue.Str("status"));
        Assert.Equal(released.Str("number"), issue.Str("reference"));

        // The documents of a work order are reversed only with it.
        await HttpAssert.ProblemAsync(await Stock.SendReverseAsync(s.Http, issue.Id()), HttpStatusCode.Conflict, "INVALID_STATE");
    }

    [Fact]
    public async Task Completing_a_draft_with_less_produced_scales_the_consumption_and_explicit_consumption_wins()
    {
        var s = await SetupAsync();
        await Stock.ReceiveAsync(s.Http, s.W1, s.A, 100m);
        await Stock.ReceiveAsync(s.Http, s.W1, s.B, 100m);
        var draft = await Production.CreateAsync(s.Http, Production.Order(s.P, 10m, s.W1));

        var completed = await Production.ActionAsync(s.Http, draft.Id(), "complete", new JsonObject
        {
            ["documentDate"] = Stock.Date,
            ["producedQuantity"] = 4m,
            ["lines"] = new JsonArray(new JsonObject { ["lineNo"] = 2, ["consumedQuantity"] = 0 }),
        });

        Assert.Equal("completed", completed.Str("status"));
        Assert.StartsWith("WO-", completed.Str("number"));
        Assert.Equal([8m, 0m], completed.WorkLines().Select(l => l.GetProperty("consumedQuantity").GetDecimal()));
        Assert.Equal(92m, await Stock.QuantityAsync(s.Http, s.A, s.W1));
        Assert.Equal(100m, await Stock.QuantityAsync(s.Http, s.B, s.W1));
        Assert.Equal(4m, await Stock.QuantityAsync(s.Http, s.P, s.W1));
    }

    [Fact]
    public async Task Completion_without_stock_is_refused_and_books_nothing()
    {
        var s = await SetupAsync();
        await Stock.ReceiveAsync(s.Http, s.W1, s.B, 100m);
        var draft = await Production.CreateAsync(s.Http, Production.Order(s.P, 10m, s.W1));
        await Production.ActionAsync(s.Http, draft.Id(), "release");

        var refused = await Stock.ConflictAsync(await Production.SendActionAsync(s.Http, draft.Id(), "complete"), "INSUFFICIENT_STOCK", "lines[0].consumedQuantity");
        Assert.Equal(Rules.NegativeStock, refused.GetProperty("rules")[0].Str("key"));

        Assert.Equal("released", (await Production.GetAsync(s.Http, draft.Id())).Str("status"));
        Assert.Equal(100m, await Stock.QuantityAsync(s.Http, s.B, s.W1));
        Assert.Equal(0m, await Stock.QuantityAsync(s.Http, s.P, s.W1));
        Assert.Equal(0, (await Stock.DocumentsAsync(s.Http, "?type=issue")).GetProperty("total").GetInt32());

        // With the tenant's rule switched, the same completion goes through.
        await Rules.SetAsync(s.Http, Rules.NegativeStock, true);
        var completed = await Production.ActionAsync(s.Http, draft.Id(), "complete");
        Assert.Equal(-20m, await Stock.QuantityAsync(s.Http, s.A, s.W1));
        Assert.Equal("completed", completed.Str("status"));
    }

    [Fact]
    public async Task Reversal_takes_the_product_out_and_brings_the_components_back()
    {
        var s = await SetupAsync();
        await Stock.ReceiveAsync(s.Http, s.W1, s.A, 100m);
        await Stock.ReceiveAsync(s.Http, s.W1, s.B, 100m);
        var order = await Production.CreateAsync(s.Http, Production.Order(s.P, 10m, s.W1));
        await Production.ActionAsync(s.Http, order.Id(), "complete", new JsonObject { ["documentDate"] = Stock.Date });

        // The product left again: the reversal would make it negative.
        await Stock.IssueAsync(s.Http, s.W1, s.P, 5m);
        await Stock.ConflictAsync(await Production.SendActionAsync(s.Http, order.Id(), "reverse"), "INSUFFICIENT_STOCK", "producedQuantity");
        await Stock.ReceiveAsync(s.Http, s.W1, s.P, 5m);

        var reversed = await Production.ActionAsync(s.Http, order.Id(), "reverse", new JsonObject { ["documentDate"] = Stock.Date });
        Assert.Equal("reversed", reversed.Str("status"));
        Assert.Equal(100m, await Stock.QuantityAsync(s.Http, s.A, s.W1));
        Assert.Equal(100m, await Stock.QuantityAsync(s.Http, s.B, s.W1));
        Assert.Equal(0m, await Stock.QuantityAsync(s.Http, s.P, s.W1));
        Assert.Equal("reversed", (await Stock.GetAsync(s.Http, reversed.GetProperty("issueDocument").Id())).Str("status"));
        await HttpAssert.ProblemAsync(await Production.SendActionAsync(s.Http, order.Id(), "reverse"), HttpStatusCode.Conflict, "INVALID_STATE");
        await Stock.AssertStockEqualsLedgerAsync(s.Http);
    }

    [Fact]
    public async Task Life_cycle_draft_edit_delete_cancel()
    {
        var s = await SetupAsync();
        var draft = await Production.CreateAsync(s.Http, Production.Order(s.P, 1m, s.W1));

        var replaced = await HttpAssert.JsonAsync(
            await s.Http.PutAsJsonAsync($"{Production.WorkOrders}/{draft.Id()}", Production.Order(s.P, 3m, s.W1, (s.A, 7m))), HttpStatusCode.OK);
        Assert.Single(replaced.WorkLines());
        Assert.Equal(7m, replaced.WorkLines()[0].GetProperty("plannedQuantity").GetDecimal());

        using var deleted = await s.Http.DeleteAsync($"{Production.WorkOrders}/{draft.Id()}");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);

        var second = await Production.CreateAsync(s.Http, Production.Order(s.P, 1m));
        Assert.Equal(await Balance.DefaultIdAsync(s.Http), second.GetProperty("warehouse").Id());
        await HttpAssert.ProblemAsync(await Production.SendActionAsync(s.Http, second.Id(), "cancel"), HttpStatusCode.Conflict, "INVALID_STATE");
        await Production.ActionAsync(s.Http, second.Id(), "release");
        await HttpAssert.ProblemAsync(await s.Http.DeleteAsync($"{Production.WorkOrders}/{second.Id()}"), HttpStatusCode.Conflict, "INVALID_STATE");
        var cancelled = await Production.ActionAsync(s.Http, second.Id(), "cancel");
        Assert.Equal("cancelled", cancelled.Str("status"));
        await HttpAssert.ProblemAsync(await Production.SendActionAsync(s.Http, second.Id(), "complete"), HttpStatusCode.Conflict, "INVALID_STATE");

        var byNumber = await HttpAssert.JsonAsync(await s.Http.GetAsync($"{Production.WorkOrders}/by-number/{cancelled.Str("number").ToLowerInvariant()}"), HttpStatusCode.OK);
        Assert.Equal(second.Id(), byNumber.Id());
        var list = await Stock.ListAsync(s.Http, $"{Production.WorkOrders}?status=cancelled");
        Assert.Equal(1, list.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task Create_refuses_bad_input_and_needs_a_bill_or_lines()
    {
        var s = await SetupAsync(withBom: false);

        await HttpAssert.ValidationAsync(await Production.SendCreateAsync(s.Http, new JsonObject()), "articleId", "quantity", "plannedDate");
        await HttpAssert.ValidationAsync(await Production.SendCreateAsync(s.Http, Production.Order(s.P, 1m)), "lines");
        await HttpAssert.ValidationAsync(await Production.SendCreateAsync(s.Http, Production.Order(s.P, 1m, null, (s.P, 1m))), "lines[0].articleId");
        await Stock.ConflictAsync(await Production.SendCreateAsync(s.Http, Production.Order(s.Stock.S, 1m, null, (s.A, 1m))), "ARTICLE_NOT_STOCKED", "articleId");
        await HttpAssert.ReferenceNotFoundAsync(await Production.SendCreateAsync(s.Http, Production.Order(Guid.NewGuid(), 1m, null, (s.A, 1m))), "articleId");
    }

    [Fact]
    public async Task Production_rules_are_the_tenants()
    {
        var s = await SetupAsync();
        await Stock.ReceiveAsync(s.Http, s.W1, s.A, 100m);
        await Stock.ReceiveAsync(s.Http, s.W1, s.B, 100m);

        await Rules.SetAsync(s.Http, Production.ComponentsRule, false);
        var refused = await HttpAssert.ValidationAsync(await Production.SendCreateAsync(s.Http, Production.Order(s.P, 1m, s.W1, (s.A, 1m))), "lines");
        Assert.Equal(Production.ComponentsRule, refused.GetProperty("rules")[0].Str("key"));
        // The lines the bill gives are accepted, given or not.
        await Production.CreateAsync(s.Http, Production.Order(s.P, 2m, s.W1, (s.A, 4m), (s.B, 1m)));

        var order = await Production.CreateAsync(s.Http, Production.Order(s.P, 10m, s.W1));
        await Rules.SetAsync(s.Http, Production.ProducedRule, false);
        await Rules.SetAsync(s.Http, Production.ConsumptionRule, false);
        var produced = await HttpAssert.ValidationAsync(
            await Production.SendActionAsync(s.Http, order.Id(), "complete", new JsonObject { ["producedQuantity"] = 9m }), "producedQuantity");
        Assert.Equal(Production.ProducedRule, produced.GetProperty("rules")[0].Str("key"));
        await HttpAssert.ValidationAsync(
            await Production.SendActionAsync(s.Http, order.Id(), "complete",
                new JsonObject { ["lines"] = new JsonArray(new JsonObject { ["lineNo"] = 1, ["consumedQuantity"] = 21m }) }),
            "lines[0].consumedQuantity");
        await HttpAssert.ValidationAsync(
            await Production.SendActionAsync(s.Http, order.Id(), "complete",
                new JsonObject { ["lines"] = new JsonArray(new JsonObject { ["lineNo"] = 9, ["consumedQuantity"] = 1m }) }),
            "lines[0].lineNo");
        Assert.Equal("draft", (await Production.GetAsync(s.Http, order.Id())).Str("status"));

        var completed = await Production.ActionAsync(s.Http, order.Id(), "complete");
        Assert.Equal("completed", completed.Str("status"));
    }

    [Fact]
    public async Task A_work_order_of_another_tenant_does_not_exist()
    {
        var s = await SetupAsync();
        var order = await Production.CreateAsync(s.Http, Production.Order(s.P, 1m));
        var other = await app.NewTenantAsync();

        await HttpAssert.NotFoundAsync(await other.Client.GetAsync($"{Production.WorkOrders}/{order.Id()}"));
        await HttpAssert.NotFoundAsync(await other.Client.PostAsync($"{Production.WorkOrders}/{order.Id()}/complete", null));
        await HttpAssert.NotFoundAsync(await other.Client.GetAsync(Production.BomPath(s.P)));
        Assert.Equal(0, (await Stock.ListAsync(other.Client, Production.WorkOrders)).GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task The_mcp_tools_do_what_the_endpoints_do()
    {
        var s = await SetupAsync(withBom: false);
        await Stock.ReceiveAsync(s.Http, s.W1, s.A, 10m);
        await using var mcp = await app.McpAsync(s.Stock.Tenant.Key);

        var bom = await mcp.OkAsync("bom_set", new { articleId = s.P, components = new[] { new { articleId = s.A, quantity = 2m } } });
        Assert.Single(bom.GetProperty("components").EnumerateArray());
        var order = await mcp.OkAsync("work_order_create", new { articleId = s.P, quantity = 3m, plannedDate = Stock.Date, warehouseId = s.W1 });
        var completed = await mcp.OkAsync("work_order_complete", new { id = order.Id(), documentDate = Stock.Date });
        Assert.Equal("completed", completed.Str("status"));
        var found = await mcp.OkAsync("work_order_get", new { number = completed.Str("number") });
        Assert.Equal(order.Id(), found.Id());
        Assert.Equal(4m, await Stock.QuantityAsync(s.Http, s.A, s.W1));
        Assert.Equal(3m, await Stock.QuantityAsync(s.Http, s.P, s.W1));

        await mcp.ErrorAsync("work_order_release", new { id = order.Id() }, "INVALID_STATE");
        var reversed = await mcp.OkAsync("work_order_reverse", new { id = order.Id(), documentDate = Stock.Date });
        Assert.Equal("reversed", reversed.Str("status"));
    }
}
