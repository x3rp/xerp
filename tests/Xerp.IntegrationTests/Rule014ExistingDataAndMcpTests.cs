using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xerp.IntegrationTests.Support;

namespace Xerp.IntegrationTests;

/// <summary>
/// Spec 014, AC-95, AC-96, AC-110 to AC-112 and AC-120: existing data is not rewritten, a Set is read at once, the
/// tools answer as HTTP does, and the books stay balanced (S4).
/// </summary>
[Collection(XerpCollection.Name)]
public class Rule014ExistingDataAndMcpTests(XerpFixture app)
{
    private static readonly OrderApi PO = OrderApi.Purchase;

    private sealed record Session(OrderSetup S, McpConnection Mcp) : IAsyncDisposable
    {
        public HttpClient Http => S.Http;

        public ValueTask DisposeAsync() => Mcp.DisposeAsync();
    }

    private async Task<Session> AgentAsync()
    {
        var setup = await Orders.SetupAsync(app);
        var agent = await Keys.CreateAsync(app, setup.Http, "claude-014", "agent");
        return new Session(setup, await app.McpAsync(agent.Key));
    }

    // ---- AC-95 ----

    [Theory]
    [MemberData(nameof(DocRules.KeyData), MemberType = typeof(DocRules))]
    public async Task AC95_Setting_a_rule_rewrites_no_posted_document_draft_order_ledger_or_stock(string key)
    {
        var s = await Orders.SetupAsync(app);
        var http = s.Http;
        var posted = await Stock.ReceiveAsync(http, s.W1, s.A, 10);
        var draft = await Stock.CreateAsync(http, Stock.Draft("receipt", s.W1, (s.A, 1), (s.B, 2)));
        var order = await PO.OrderedAsync(s, (s.A, 5, null, 1m));
        var ledger = await Stock.LedgerAsync(http, "?limit=500");
        var onHand = await Stock.OnHandAsync(http, "?limit=500");

        await DocRules.FlipAsync(http, key);

        McpAssert.JsonEqual(posted, await Stock.GetAsync(http, posted.Id()), $"{key}: the posted document changed");
        McpAssert.JsonEqual(draft, await Stock.GetAsync(http, draft.Id()), $"{key}: the draft changed");
        McpAssert.JsonEqual(order, await PO.GetAsync(http, order.Id()), $"{key}: the order changed");
        McpAssert.JsonEqual(ledger, await Stock.LedgerAsync(http, "?limit=500"), $"{key}: the ledger changed");
        McpAssert.JsonEqual(onHand, await Stock.OnHandAsync(http, "?limit=500"), $"{key}: stock on hand changed");
    }

    // ---- AC-96 ----

    [Fact]
    public async Task AC96_A_Set_is_read_by_the_very_next_posting_ten_times_in_a_row()
    {
        var s = await Orders.SetupAsync(app);
        var http = s.Http;

        for (var i = 0; i < 10; i++)
        {
            var draft = await Stock.CreateAsync(http, Stock.Draft("receipt", s.W1, (s.A, 1)).Dated(DocRules.Past));
            await Rules.SetAsync(http, DocRules.PastDate, false);
            await DocRules.DateRefusedAsync(http, draft.Id(), DocRules.PastDate);
            await Rules.SetAsync(http, DocRules.PastDate, true);
            Assert.Equal("posted", (await Stock.PostDocumentAsync(http, draft.Id())).Str("status"));
        }
        Assert.Equal(10m, await Stock.QuantityAsync(http, s.A, s.W1));
        Assert.Equal("SR-000010", (await Stock.DocumentsAsync(http, "?status=posted&limit=500")).Items().Max(d => d.Number()));
    }

    // ---- AC-110 ----

    [Fact]
    public async Task AC110_A_refusal_by_a_rule_through_the_tools_equals_the_http_refusal()
    {
        await using var s = await AgentAsync();
        var (a, w1) = (s.S.A, s.S.W1);
        await s.Mcp.OkAsync("rule_set", new { key = DocRules.ReceiptPartner, value = true });

        var error = await s.Mcp.ErrorAsync("stock_document_create", Stock.Draft("receipt", w1, (a, 1)), "VALIDATION_FAILED", "partnerId");

        Rules.AssertNames(error, DocRules.ReceiptPartner, true, "partnerId");
        using (var http = await Stock.PostAsync(s.Http, Stock.Draft("receipt", w1, (a, 1))))
        {
            var problem = await DocRules.ValidationRefusedAsync(http, DocRules.ReceiptPartner, true, "partnerId");
            McpAssert.JsonEqual(problem.GetProperty("rules"), error.GetProperty("rules"), "rules of the tool error differ from HTTP");
        }
        var document = await s.Mcp.OkAsync("stock_document_create", Stock.Draft("receipt", w1, (a, 1)).With("partnerId", s.S.Sup.Id().ToString()));
        Assert.Equal(s.S.Sup.Id(), document.GetProperty("partner").Id());
    }

    // ---- AC-111 ----

    [Fact]
    public async Task AC111_DATE_NOT_ALLOWED_and_a_reversal_refused_by_its_masters_rule_equal_their_http_refusals()
    {
        await using var s = await AgentAsync();
        var (a, w1) = (s.S.A, s.S.W1);
        var draft = await Stock.CreateAsync(s.Http, Stock.Draft("receipt", w1, (a, 1)).Dated(DocRules.Past));
        var original = await Stock.ReceiveAsync(s.Http, w1, a, 2);
        await s.Mcp.OkAsync("rule_set", new { key = DocRules.PastDate, value = false });
        await s.Mcp.OkAsync("rule_set", new { key = DocRules.ReversalMasters, value = true });

        var dateError = await s.Mcp.ErrorAsync("stock_document_post", new { id = draft.Id() }, "DATE_NOT_ALLOWED", "documentDate");
        Rules.AssertNames(dateError, DocRules.PastDate, false, "documentDate");
        using (var http = await Stock.SendPostAsync(s.Http, draft.Id()))
        {
            var problem = await Rules.ConflictAsync(http, "DATE_NOT_ALLOWED", DocRules.PastDate, false, "documentDate");
            McpAssert.JsonEqual(problem.GetProperty("rules"), dateError.GetProperty("rules"), "rules differ from HTTP");
            Assert.Equal(McpAssert.ErrorKeys(problem), McpAssert.ErrorKeys(dateError));
        }

        await DocRules.DeactivateArticle(s.Http, a);
        var today = DocRules.Today;
        var reverseError = await s.Mcp.ErrorAsync("stock_document_reverse", new { id = original.Id(), documentDate = today }, "REFERENCE_INACTIVE", "lines[0].articleId");
        Rules.AssertNames(reverseError, DocRules.ReversalMasters, true, "lines[0].articleId");
        using (var http = await Stock.SendReverseAsync(s.Http, original.Id(), today))
        {
            var problem = await Rules.ConflictAsync(http, "REFERENCE_INACTIVE", DocRules.ReversalMasters, true, "lines[0].articleId");
            McpAssert.JsonEqual(problem.GetProperty("rules"), reverseError.GetProperty("rules"), "rules differ from HTTP");
        }
    }

    // ---- AC-112 ----

    [Fact]
    public async Task AC112_The_input_schemas_do_not_change_when_the_rules_are_set()
    {
        var s = await Orders.SetupAsync(app);
        var key = await Keys.CreateAsync(app, s.Http, "claude-schema", "agent");
        string[] names =
            ["stock_document_create", "stock_document_update", "purchase_order_create", "sales_order_create"];

        async Task<string[]> SchemasAsync()
        {
            await using var mcp = await app.McpAsync(key.Key);
            var tools = (await mcp.Client.ListToolsAsync()).ToDictionary(t => t.ProtocolTool.Name, t => t.ProtocolTool);
            return names.Select(n => tools[n].InputSchema.GetRawText()).ToArray();
        }

        var before = await SchemasAsync();
        foreach (var rule in DocRules.Keys)
            await DocRules.FlipAsync(s.Http, rule);
        var after = await SchemasAsync();

        Assert.Equal(before, after);
    }

    // ---- AC-120 ----

    [Fact]
    public async Task AC120_With_the_loosest_values_of_the_masters_rules_negative_stock_and_outdated_counts_the_books_balance()
    {
        var s = await Orders.SetupAsync(app);
        var http = s.Http;
        await Rules.SetAsync(http, Rules.NegativeStock, true);
        await Rules.SetAsync(http, DocRules.ActiveMasters, false);
        await Rules.SetAsync(http, DocRules.InactiveAssignable, true);
        await Rules.SetAsync(http, DocRules.OutdatedCount, true);
        await Rules.SetAsync(http, DocRules.PartnerRole, false);
        await Rules.SetAsync(http, DocRules.ReversalBefore, true);
        await Stock.ReceiveAsync(http, s.W1, s.A, 5);
        await DocRules.DeactivateWarehouse(http, s.W2);
        await DocRules.DeactivateArticle(http, s.B);

        var oversold = await Stock.IssueAsync(http, s.W1, s.A, 8);
        var inInactive = await Stock.PostDocumentAsync(http,
            (await Stock.CreateAsync(http, Stock.Draft("receipt", s.W2, (s.B, 4)).With("partnerId", s.Cus.Id().ToString()))).Id());
        var count = await Counts.CountAsync(http, s.W1, s.A, 2);
        await Stock.ReceiveAsync(http, s.W1, s.A, 6);
        await Stock.PostDocumentAsync(http, count.Id());
        await Stock.ReverseAsync(http, oversold.Id(), DocRules.Past);
        await Stock.ReverseAsync(http, inInactive.Id());

        var ledger = await Rules.AssertBalancedAsync(http, "AC-120");
        Assert.Equal(2m + 8m, ledger[(s.A, s.W1)]);
    }
}
