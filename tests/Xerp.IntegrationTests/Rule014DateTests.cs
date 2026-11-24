using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xerp.IntegrationTests.Support;

namespace Xerp.IntegrationTests;

/// <summary>
/// Spec 014, AC-50 to AC-56, AC-60 and AC-61: <c>document.pastDateAllowed</c>, <c>document.futureDateAllowed</c> and
/// <c>stock.reversalBeforeOriginalAllowed</c>. Today, Past and Future are computed from the UTC clock (R13); no
/// test uses yesterday or tomorrow, and none uses <c>Stock.Date</c> while a date rule is <c>false</c>.
/// </summary>
[Collection(XerpCollection.Name)]
public class Rule014DateTests(XerpFixture app)
{
    private static readonly OrderApi PO = OrderApi.Purchase;
    private static readonly OrderApi SO = OrderApi.Sales;
    private static readonly OrderApi[] Kinds = [OrderApi.Purchase, OrderApi.Sales];

    private static async Task<JsonElement> ReverseRefusedAsync(HttpClient http, Guid id, string date, string rule)
    {
        using var response = await Stock.SendReverseAsync(http, id, date);
        return await DocRules.ValidationRefusedAsync(response, rule, false, "documentDate");
    }

    // ---- AC-50 ----

    [Fact]
    public async Task AC50_A_receipt_dated_Past_is_saved_but_refused_at_posting_and_consumes_no_number()
    {
        var s = await Orders.SetupAsync(app);
        var http = s.Http;
        await Rules.SetAsync(http, DocRules.PastDate, false);

        var draft = await Stock.CreateAsync(http, Stock.Draft("receipt", s.W1, (s.A, 1)).Dated(DocRules.Past));
        var replaced = await Stock.ReplaceAsync(http, draft.Id(), DocRules.Replace(s.W1, s.A, DocRules.Past, null, 2));
        Assert.Equal(DocRules.Past, replaced.Str("documentDate"));

        await DocRules.DateRefusedAsync(http, draft.Id(), DocRules.PastDate);
        McpAssert.JsonEqual(replaced, await Stock.GetAsync(http, draft.Id()), "The refused posting changed the draft");
        Assert.Equal(0m, await Stock.QuantityAsync(http, s.A, s.W1));

        await Stock.ReplaceAsync(http, draft.Id(), DocRules.Replace(s.W1, s.A, DocRules.Today, null, 2));
        Assert.Equal("SR-000001", (await Stock.PostDocumentAsync(http, draft.Id())).Number());

        var future = await Stock.CreateAsync(http, Stock.Draft("receipt", s.W1, (s.A, 1)).Dated(DocRules.Future));
        Assert.Equal("SR-000002", (await Stock.PostDocumentAsync(http, future.Id())).Number());
        await Rules.AssertBalancedAsync(http, "AC-50");
    }

    // ---- AC-51 ----

    [Fact]
    public async Task AC51_An_issue_a_transfer_and_a_count_dated_Past_are_refused_at_posting()
    {
        var s = await Orders.SetupAsync(app);
        var http = s.Http;
        await Stock.ReceiveAsync(http, s.W1, s.A, 10);
        await Rules.SetAsync(http, DocRules.PastDate, false);

        var drafts = new[]
        {
            await Stock.CreateAsync(http, Stock.Draft("issue", s.W1, (s.A, 1)).Dated(DocRules.Past)),
            await Stock.CreateAsync(http, Stock.Transfer(s.W1, s.W2, (s.A, 1)).Dated(DocRules.Past)),
            await Stock.CreateAsync(http, Counts.Draft(s.W1, (s.A, 5, null)).Dated(DocRules.Past)),
        };

        foreach (var draft in drafts)
        {
            await DocRules.DateRefusedAsync(http, draft.Id(), DocRules.PastDate);
            McpAssert.JsonEqual(draft, await Stock.GetAsync(http, draft.Id()), "The refused posting changed the draft");
        }
        Assert.Equal(10m, await Stock.QuantityAsync(http, s.A, s.W1));
    }

    // ---- AC-52 ----

    [Fact]
    public async Task AC52_With_futureDateAllowed_false_only_a_date_after_today_is_refused()
    {
        var s = await Orders.SetupAsync(app);
        var http = s.Http;
        await Rules.SetAsync(http, DocRules.FutureDate, false);
        var future = await Stock.CreateAsync(http, Stock.Draft("receipt", s.W1, (s.A, 1)).Dated(DocRules.Future));
        var past = await Stock.CreateAsync(http, Stock.Draft("receipt", s.W1, (s.A, 1)).Dated(DocRules.Past));
        var today = await Stock.CreateAsync(http, Stock.Draft("receipt", s.W1, (s.A, 1)).Dated(DocRules.Today));

        await DocRules.DateRefusedAsync(http, future.Id(), DocRules.FutureDate);
        await Stock.AssertDraftAsync(http, future.Id());
        Assert.Equal("posted", (await Stock.PostDocumentAsync(http, past.Id())).Str("status"));
        Assert.Equal("posted", (await Stock.PostDocumentAsync(http, today.Id())).Str("status"));
        Assert.Equal(2m, await Stock.QuantityAsync(http, s.A, s.W1));
    }

    // ---- AC-53 ----

    [Fact]
    public async Task AC53_An_order_dated_Past_is_created_but_refused_at_confirmation_and_stays_a_draft()
    {
        var s = await Orders.SetupAsync(app);
        var http = s.Http;
        await Stock.ReceiveAsync(http, s.W1, s.A, 100);
        await Rules.SetAsync(http, DocRules.PastDate, false);

        foreach (var o in Kinds)
        {
            var draft = await o.CreateAsync(http, o.Body(s, (s.A, 1, null, 1m)).OrderDated(DocRules.Past));
            using (var refused = await o.SendAsync(http, draft.Id(), "confirm"))
                await Rules.ConflictAsync(refused, "DATE_NOT_ALLOWED", DocRules.PastDate, false, "orderDate");
            var read = await o.GetAsync(http, draft.Id());
            Assert.Equal("draft", read.Str("status"));
            JsonBody.AssertNull(read, "number");

            await o.ReplaceAsync(http, draft.Id(), o.Replacement(s, (s.A, 1, null, 1m)).OrderDated(DocRules.Today));
            Assert.Equal(o.Number(1), (await o.ConfirmAsync(http, draft.Id())).Number());
        }
    }

    [Fact]
    public async Task AC53_The_due_date_is_never_judged_by_the_date_rules()
    {
        var s = await Orders.SetupAsync(app);
        await Stock.ReceiveAsync(s.Http, s.W1, s.A, 10);
        await Rules.SetAsync(s.Http, DocRules.FutureDate, false);
        await Rules.SetAsync(s.Http, DocRules.PastDate, false);

        foreach (var o in Kinds)
        {
            var draft = await o.CreateAsync(s.Http, o.Body(s, (s.A, 1, null, 1m)).OrderDated(DocRules.Today).With(o.DueDate, DocRules.Future));
            Assert.Equal("confirmed", (await o.ConfirmAsync(s.Http, draft.Id())).Str("status"));
        }
    }

    // ---- AC-54 ----

    [Fact]
    public async Task AC54_A_reversal_is_dated_by_the_same_rules_and_refused_with_400()
    {
        var s = await Orders.SetupAsync(app);
        var http = s.Http;
        var past = await Stock.PostDocumentAsync(http, (await Stock.CreateAsync(http, Stock.Draft("receipt", s.W1, (s.A, 1)).Dated(DocRules.Past))).Id());
        var today = await Stock.PostDocumentAsync(http, (await Stock.CreateAsync(http, Stock.Draft("receipt", s.W1, (s.A, 1)).Dated(DocRules.Today))).Id());
        await Rules.SetAsync(http, DocRules.PastDate, false);

        await ReverseRefusedAsync(http, past.Id(), DocRules.Past, DocRules.PastDate);
        Assert.Equal("posted", (await Stock.GetAsync(http, past.Id())).Str("status"));
        var reversal = await Stock.ReverseAsync(http, past.Id(), DocRules.Today);
        Assert.Equal(DocRules.Today, reversal.Str("documentDate"));

        await Rules.SetAsync(http, DocRules.FutureDate, false);
        await ReverseRefusedAsync(http, today.Id(), DocRules.Future, DocRules.FutureDate);
        Assert.Equal("posted", (await Stock.GetAsync(http, today.Id())).Str("status"));
        await Rules.AssertBalancedAsync(http, "AC-54");
    }

    // ---- AC-55 ----

    [Fact]
    public async Task AC55_Existing_data_made_under_the_looser_rules_is_not_rewritten_and_is_still_usable()
    {
        var s = await Orders.SetupAsync(app);
        var http = s.Http;
        var postedPast = await Stock.PostDocumentAsync(http, (await Stock.CreateAsync(http, Stock.Draft("receipt", s.W1, (s.A, 1)).Dated(DocRules.Past))).Id());
        var orderFuture = await PO.ConfirmAsync(http, (await PO.CreateAsync(http, PO.Body(s, (s.A, 10, null, 1m)).OrderDated(DocRules.Future))).Id());
        var ledgerBefore = await Stock.LedgerAsync(http, "?limit=500");

        await Rules.SetAsync(http, DocRules.PastDate, false);
        await Rules.SetAsync(http, DocRules.FutureDate, false);

        McpAssert.JsonEqual(postedPast, await Stock.GetAsync(http, postedPast.Id()), "The posted document changed");
        McpAssert.JsonEqual(orderFuture, await PO.GetAsync(http, orderFuture.Id()), "The confirmed order changed");
        McpAssert.JsonEqual(ledgerBefore, await Stock.LedgerAsync(http, "?limit=500"), "The ledger changed");
        var receipt = await Stock.CreateAsync(http, PO.Document(s.W1, orderFuture.Id(), (s.A, 5, 1, null)).Dated(DocRules.Today));
        Assert.Equal("posted", (await Stock.PostDocumentAsync(http, receipt.Id())).Str("status"));
        Assert.Equal("closed", (await PO.CloseAsync(http, orderFuture.Id())).Str("status"));
        await Rules.AssertBalancedAsync(http, "AC-55");
    }

    // ---- AC-56 ----

    [Fact]
    public async Task AC56_A_linked_receipt_is_judged_by_its_own_date_not_its_orders()
    {
        var s = await Orders.SetupAsync(app);
        var http = s.Http;
        await Rules.SetAsync(http, DocRules.PastDate, false);
        var order = await PO.ConfirmAsync(http, (await PO.CreateAsync(http, PO.Body(s, (s.A, 10, null, 1m)).OrderDated(DocRules.Today))).Id());
        var receipt = await Stock.CreateAsync(http, PO.Document(s.W1, order.Id(), (s.A, 5, 1, null)).Dated(DocRules.Past));

        await DocRules.DateRefusedAsync(http, receipt.Id(), DocRules.PastDate);
        await Stock.AssertDraftAsync(http, receipt.Id());
        McpAssert.JsonEqual(order, await PO.GetAsync(http, order.Id()), "The refused posting changed the order");
    }

    // ---- AC-60, AC-61 ----

    [Fact]
    public async Task AC60_With_reversalBeforeOriginalAllowed_true_a_reversal_may_be_dated_before_its_original()
    {
        var s = await Orders.SetupAsync(app);
        var http = s.Http;
        await Rules.SetAsync(http, DocRules.ReversalBefore, true);
        var original = await Stock.PostDocumentAsync(http, (await Stock.CreateAsync(http, Stock.Draft("receipt", s.W1, (s.A, 3)).Dated(DocRules.Today))).Id());

        var reversal = await Stock.ReverseAsync(http, original.Id(), DocRules.Past);

        Assert.Equal(DocRules.Past, reversal.Str("documentDate"));
        Assert.Equal(0m, await Stock.QuantityAsync(http, s.A, s.W1));
        await Rules.AssertBalancedAsync(http, "AC-60");
    }

    [Fact]
    public async Task AC61_Two_date_rules_refusing_one_reversal_give_one_400_with_two_rules_elements()
    {
        var s = await Orders.SetupAsync(app);
        var http = s.Http;
        var original = await Stock.PostDocumentAsync(http, (await Stock.CreateAsync(http, Stock.Draft("receipt", s.W1, (s.A, 3)).Dated(DocRules.Today))).Id());
        await Rules.SetAsync(http, DocRules.PastDate, false);

        using var response = await Stock.SendReverseAsync(http, original.Id(), DocRules.Past);

        var problem = await HttpAssert.ValidationAsync(response, "documentDate");
        Assert.Equal(new[] { "documentDate" }, McpAssert.ErrorKeys(problem));
        var rules = problem.GetProperty("rules").EnumerateArray().ToArray();
        Assert.Equal(2, rules.Length);
        Assert.Equal(new[] { DocRules.PastDate, DocRules.ReversalBefore }, rules.Select(r => r.Str("key")).Order(StringComparer.Ordinal));
        Assert.All(rules, r =>
        {
            Assert.Equal(new[] { "fields", "key", "value" }, r.PropertyNames());
            Rules.AssertBool(r, "value", false);
            Assert.Equal(new[] { "documentDate" }, Rules.Fields(r));
        });
        Assert.Equal("posted", (await Stock.GetAsync(http, original.Id())).Str("status"));
        Assert.Equal(3m, await Stock.QuantityAsync(http, s.A, s.W1));
    }
}
