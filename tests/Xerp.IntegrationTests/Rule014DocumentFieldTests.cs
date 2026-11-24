using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xerp.IntegrationTests.Support;

namespace Xerp.IntegrationTests;

/// <summary>
/// Spec 014, AC-70 to AC-75: <c>document.referenceRequired</c>, <c>document.repeatedArticleAllowed</c> and
/// <c>document.warehouseRequired</c>, for the four kinds of document and the two kinds of order.
/// </summary>
[Collection(XerpCollection.Name)]
public class Rule014DocumentFieldTests(XerpFixture app)
{
    private static readonly OrderApi PO = OrderApi.Purchase;
    private static readonly OrderApi SO = OrderApi.Sales;

    /// <summary>One thing that can be created: a name, a way to build its body and a way to send it.</summary>
    public sealed record Kind(string Name, Func<OrderSetup, JsonObject> Body, Func<HttpClient, JsonObject, Task<HttpResponseMessage>> Send)
    {
        public override string ToString() => Name;
    }

    private static readonly Kind Receipt = new("receipt", s => Stock.Draft("receipt", s.W1, (s.A, 1)), Stock.PostAsync);
    private static readonly Kind Issue = new("issue", s => Stock.Draft("issue", s.W1, (s.A, 1)), Stock.PostAsync);
    private static readonly Kind Transfer = new("transfer", s => Stock.Transfer(s.W1, s.W2, (s.A, 1)), Stock.PostAsync);
    private static readonly Kind Count = new("count", s => Counts.Draft(s.W1, (s.A, 1, null)), Stock.PostAsync);
    private static readonly Kind Purchase = new("purchase order", s => PO.Body(s, (s.A, 1, null, 1m)), PO.PostAsync);
    private static readonly Kind Sales = new("sales order", s => SO.Body(s, (s.A, 1, null, 1m)), SO.PostAsync);

    public static IEnumerable<object[]> AllKinds() => new[] { Receipt, Issue, Transfer, Count, Purchase, Sales }.Select(k => new object[] { k });

    public static IEnumerable<object[]> KindsWithWarehouseRule() => new[] { Receipt, Issue, Count, Purchase, Sales }.Select(k => new object[] { k });

    public static IEnumerable<object[]> KindsWithRepeats() => new[] { Receipt, Issue, Transfer, Purchase, Sales }.Select(k => new object[] { k });

    private static JsonObject WithLines(Kind kind, OrderSetup s, params Guid[] articles) => kind.Name switch
    {
        "purchase order" => PO.Body(s).With("lines", Orders.Lines(articles.Select(a => (a, 1m, (Guid?)null, 1m)).ToArray())),
        "sales order" => SO.Body(s).With("lines", Orders.Lines(articles.Select(a => (a, 1m, (Guid?)null, 1m)).ToArray())),
        _ => kind.Body(s).With("lines", Stock.Lines(articles.Select(a => (a, 1m)).ToArray())),
    };

    private static async Task<int> TotalAsync(HttpClient http, Kind kind) =>
        kind.Name switch
        {
            "purchase order" => (await PO.ListAsync(http)).Total(),
            "sales order" => (await SO.ListAsync(http)).Total(),
            _ => (await Stock.DocumentsAsync(http)).Total(),
        };

    // ---- AC-70 ----

    [Theory]
    [MemberData(nameof(AllKinds))]
    public async Task AC70_With_referenceRequired_a_create_without_a_reference_is_refused(Kind kind)
    {
        var s = await Orders.SetupAsync(app);
        var http = s.Http;
        await Rules.SetAsync(http, DocRules.ReferenceRequired, true);

        foreach (var body in new[] { kind.Body(s), kind.Body(s).With("reference", null), kind.Body(s).With("reference", "  ") })
        {
            using var response = await kind.Send(http, body);
            await DocRules.ValidationRefusedAsync(response, DocRules.ReferenceRequired, true, "reference");
        }
        Assert.Equal(0, await TotalAsync(http, kind));

        using var created = await kind.Send(http, kind.Body(s).With("reference", "DN-1"));
        var document = await HttpAssert.JsonAsync(created, HttpStatusCode.Created);
        Assert.Equal("DN-1", document.Str("reference"));
    }

    [Fact]
    public async Task AC70_A_replace_with_a_null_reference_is_refused_and_changes_nothing()
    {
        var s = await Orders.SetupAsync(app);
        var http = s.Http;
        await Rules.SetAsync(http, DocRules.ReferenceRequired, true);
        var receipt = await Stock.CreateAsync(http, Receipt.Body(s).With("reference", "DN-1"));
        var order = await PO.CreateAsync(http, PO.Body(s, (s.A, 1, null, 1m)).With("reference", "PO-REF"));

        using (var document = await Stock.PutAsync(http, receipt.Id(), Stock.Replacement(s.W1, (s.A, 1)).With("reference", null)))
            await DocRules.ValidationRefusedAsync(document, DocRules.ReferenceRequired, true, "reference");
        using (var blank = await Stock.PutAsync(http, receipt.Id(), Stock.Replacement(s.W1, (s.A, 1)).With("reference", " ")))
            await DocRules.ValidationRefusedAsync(blank, DocRules.ReferenceRequired, true, "reference");
        using (var purchase = await PO.PutAsync(http, order.Id(), PO.Replacement(s, (s.A, 1, null, 1m)).With("reference", null)))
            await DocRules.ValidationRefusedAsync(purchase, DocRules.ReferenceRequired, true, "reference");
        McpAssert.JsonEqual(receipt, await Stock.GetAsync(http, receipt.Id()), "The refused replace changed the receipt");
        await PO.AssertUnchangedAsync(http, order);
    }

    // ---- AC-71 ----

    [Fact]
    public async Task AC71_Drafts_made_before_the_rule_post_and_confirm_and_a_reversal_needs_no_reference()
    {
        var s = await Orders.SetupAsync(app);
        var http = s.Http;
        var draft = await Stock.CreateAsync(http, Receipt.Body(s));
        var order = await PO.CreateAsync(http, PO.Body(s, (s.A, 1, null, 1m)));
        var posted = await Stock.ReceiveAsync(http, s.W1, s.A, 5);
        await Rules.SetAsync(http, DocRules.ReferenceRequired, true);

        Assert.Equal("posted", (await Stock.PostDocumentAsync(http, draft.Id())).Str("status"));
        Assert.Equal("confirmed", (await PO.ConfirmAsync(http, order.Id())).Str("status"));
        var reversal = await Stock.ReverseAsync(http, posted.Id());
        JsonBody.AssertNull(reversal, "reference");
        await Rules.AssertBalancedAsync(http, "AC-71");
    }

    // ---- AC-72 ----

    [Theory]
    [MemberData(nameof(KindsWithRepeats))]
    public async Task AC72_With_repeatedArticleAllowed_false_a_repeated_article_is_refused_on_every_line_of_it(Kind kind)
    {
        var s = await Orders.SetupAsync(app);
        var http = s.Http;
        await Rules.SetAsync(http, DocRules.RepeatedArticle, false);

        using (var repeated = await kind.Send(http, WithLines(kind, s, s.A, s.B, s.A)))
            await DocRules.ValidationRefusedAsync(repeated, DocRules.RepeatedArticle, false, "lines[0].articleId", "lines[2].articleId");
        Assert.Equal(0, await TotalAsync(http, kind));
        using var distinct = await kind.Send(http, WithLines(kind, s, s.A, s.B));
        await HttpAssert.JsonAsync(distinct, HttpStatusCode.Created);
    }

    [Fact]
    public async Task AC72_A_replace_with_a_repeat_is_refused_and_a_count_with_a_repeat_is_refused_without_a_rule()
    {
        var s = await Orders.SetupAsync(app);
        var http = s.Http;
        await Rules.SetAsync(http, DocRules.RepeatedArticle, false);
        var draft = await Stock.CreateAsync(http, Receipt.Body(s));

        using (var replace = await Stock.PutAsync(http, draft.Id(), Stock.Replacement(s.W1, (s.A, 1), (s.A, 2))))
            await DocRules.ValidationRefusedAsync(replace, DocRules.RepeatedArticle, false, "lines[0].articleId", "lines[1].articleId");
        McpAssert.JsonEqual(draft, await Stock.GetAsync(http, draft.Id()), "The refused replace changed the draft");

        using var count = await Stock.PostAsync(http, Counts.Draft(s.W1, (s.A, 1, null), (s.A, 2, null)));
        await DocRules.PlainAsync(count, HttpStatusCode.BadRequest, "VALIDATION_FAILED", "lines[0].articleId", "lines[1].articleId");
    }

    [Fact]
    public async Task AC72_A_linked_document_is_judged_like_any_other()
    {
        var s = await Orders.SetupAsync(app);
        var http = s.Http;
        var order = await PO.OrderedAsync(s, (s.A, 10, null, 1m), (s.A, 10, null, 1m));
        await Rules.SetAsync(http, DocRules.RepeatedArticle, false);

        using var twoForOneLine = await Stock.PostAsync(http, PO.Document(s.W1, order.Id(), (s.A, 1, 1, null), (s.A, 1, 1, null)));
        await DocRules.ValidationRefusedAsync(twoForOneLine, DocRules.RepeatedArticle, false, "lines[0].articleId", "lines[1].articleId");
        using var twoOrderLines = await Stock.PostAsync(http, PO.Document(s.W1, order.Id(), (s.A, 1, 1, null), (s.A, 1, 2, null)));
        await DocRules.ValidationRefusedAsync(twoOrderLines, DocRules.RepeatedArticle, false, "lines[0].articleId", "lines[1].articleId");
    }

    // ---- AC-73 ----

    [Fact]
    public async Task AC73_A_draft_with_a_repeated_article_made_before_the_rule_posts_and_both_lines_write_entries()
    {
        var s = await Orders.SetupAsync(app);
        var http = s.Http;
        await Stock.ReceiveAsync(http, s.W1, s.A, 10);
        var draft = await Stock.CreateAsync(http, Stock.Draft("issue", s.W1, (s.A, 2), (s.A, 3)));
        await Rules.SetAsync(http, DocRules.RepeatedArticle, false);

        var posted = await Stock.PostDocumentAsync(http, draft.Id());

        Assert.Equal("posted", posted.Str("status"));
        Assert.Equal(new[] { -2m, -3m }, (await Stock.EntriesAsync(http, draft.Id())).Select(e => e.Quantity()).ToArray());
        Assert.Equal(5m, await Stock.QuantityAsync(http, s.A, s.W1));
        await Rules.AssertBalancedAsync(http, "AC-73");
    }

    // ---- AC-74, AC-75 ----

    [Theory]
    [MemberData(nameof(KindsWithWarehouseRule))]
    public async Task AC74_With_warehouseRequired_a_create_without_a_warehouse_is_refused(Kind kind)
    {
        var s = await Orders.SetupAsync(app);
        var http = s.Http;
        await Rules.SetAsync(http, DocRules.WarehouseRequired, true);

        foreach (var asNull in new[] { false, true })
        {
            using var response = await kind.Send(http, kind.Body(s).NoWarehouse(asNull));
            await DocRules.ValidationRefusedAsync(response, DocRules.WarehouseRequired, true, "warehouseId");
        }
        Assert.Equal(0, await TotalAsync(http, kind));
        using var named = await kind.Send(http, kind.Body(s));
        await HttpAssert.JsonAsync(named, HttpStatusCode.Created);
    }

    [Fact]
    public async Task AC74_A_linked_receipt_must_name_the_warehouse_too_and_a_transfer_without_one_is_refused_without_a_rule()
    {
        var s = await Orders.SetupAsync(app);
        var http = s.Http;
        var order = await PO.OrderedAsync(s, (s.A, 10, null, 1m));
        await Rules.SetAsync(http, DocRules.WarehouseRequired, true);

        using (var without = await Stock.PostAsync(http, PO.Document(s.W1, order.Id(), (s.A, 1, 1, null)).Without("warehouseId")))
            await DocRules.ValidationRefusedAsync(without, DocRules.WarehouseRequired, true, "warehouseId");
        var linked = await Stock.CreateAsync(http, PO.Document(order.GetProperty("warehouse").Id(), order.Id(), (s.A, 1, 1, null)));
        Assert.Equal(order.GetProperty("warehouse").Id(), linked.GetProperty("warehouse").Id());

        using var transfer = await Stock.PostAsync(http, Transfer.Body(s).Without("warehouseId"));
        await DocRules.PlainAsync(transfer, HttpStatusCode.BadRequest, "VALIDATION_FAILED", "warehouseId");
    }

    [Fact]
    public async Task AC75_After_a_reset_a_receipt_without_a_warehouse_is_created_in_the_default_warehouse()
    {
        var s = await Orders.SetupAsync(app);
        var http = s.Http;
        await Rules.SetAsync(http, DocRules.WarehouseRequired, true);
        await Rules.ResetAsync(http, DocRules.WarehouseRequired);

        var receipt = await Stock.CreateAsync(http, Receipt.Body(s).NoWarehouse(false));

        Assert.Equal(await Balance.DefaultIdAsync(http), receipt.GetProperty("warehouse").Id());
    }
}
