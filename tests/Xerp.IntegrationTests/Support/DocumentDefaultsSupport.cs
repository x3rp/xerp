using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Xerp.IntegrationTests.Support;

/// <summary>
/// Spec 014, AC-20 and AC-22: what a tenant that sets none of the 13 rules gets, as one scripted walk. With
/// <c>named</c> every default refusal is "refused by" its rule (AC-22). The tenant must be fresh.
/// </summary>
public static class DocDefaults
{
    private static readonly OrderApi PO = OrderApi.Purchase;

    private static async Task NamedAsync(HttpResponseMessage response, HttpStatusCode status, string code, bool named, string rule, bool value, params string[] keys)
    {
        using (response)
        {
            var problem = await HttpAssert.ProblemAsync(response, status, code);
            Assert.Equal(keys.Order(StringComparer.Ordinal), McpAssert.ErrorKeys(problem));
            if (named)
                Rules.AssertNames(problem, rule, value, keys);
        }
    }

    public static async Task AssertAsync(OrderSetup s, bool named)
    {
        var http = s.Http;
        var (a, w1, w2) = (s.A, s.W1, s.W2);
        var (today, past, future) = (DocRules.Today, DocRules.Past, DocRules.Future);
        var c = await Art.CreateAsync(http, "C", "Article C", s.Pcs);
        var w3 = await MasterApi.Warehouses.CreateAsync(http, "W3", "Warehouse three");
        var defaultWarehouse = await Balance.DefaultIdAsync(http);

        // An unlinked receipt and an unlinked issue without a partner are saved and posted.
        var receipt = await Stock.CreateAsync(http, Stock.Draft("receipt", w1, (a, 10)).Dated(today));
        JsonBody.AssertNull(receipt, "partner");
        await Stock.PostDocumentAsync(http, receipt.Id());
        var issue = await Stock.CreateAsync(http, Stock.Draft("issue", w1, (a, 1)).Dated(today));
        JsonBody.AssertNull(issue, "partner");
        await Stock.PostDocumentAsync(http, issue.Id());

        // A receipt naming CUS -> PARTNER_ROLE_MISSING.
        using (var wrongRole = await Stock.PostAsync(http, Stock.Draft("receipt", w1, (a, 1)).With("partnerId", s.Cus.Id().ToString())))
            await NamedAsync(wrongRole, HttpStatusCode.Conflict, "PARTNER_ROLE_MISSING", named, DocRules.PartnerRole, true, "partnerId");

        // Documents and orders dated Past and Future are posted and confirmed.
        foreach (var date in new[] { past, future })
        {
            await Stock.PostDocumentAsync(http, (await Stock.CreateAsync(http, Stock.Draft("receipt", w1, (a, 1)).Dated(date))).Id());
            await PO.ConfirmAsync(http, (await PO.CreateAsync(http, PO.Body(s, (a, 1, null, 1m)).OrderDated(date))).Id());
        }

        // A reversal dated before its original -> 400 documentDate.
        var original = await Stock.PostDocumentAsync(http, (await Stock.CreateAsync(http, Stock.Draft("receipt", w1, (a, 1)).Dated(today))).Id());
        using (var before = await Stock.SendReverseAsync(http, original.Id(), past))
            await NamedAsync(before, HttpStatusCode.BadRequest, "VALIDATION_FAILED", named, DocRules.ReversalBefore, false, "documentDate");
        Assert.Equal("posted", (await Stock.GetAsync(http, original.Id())).Str("status"));

        // A document and an order without a reference are saved.
        JsonBody.AssertNull(await Stock.CreateAsync(http, Stock.Draft("receipt", w1, (a, 1))), "reference");
        JsonBody.AssertNull(await PO.CreateAsync(http, PO.Body(s, (a, 1, null, 1m))), "reference");

        // An issue and an order with A on two lines are saved.
        Assert.Equal(2, (await Stock.CreateAsync(http, Stock.Draft("issue", w1, (a, 1), (a, 1)))).DocumentLines().Length);
        Assert.Equal(2, (await PO.CreateAsync(http, PO.Body(s, (a, 1, null, 1m), (a, 2, null, 1m)))).OrderLines().Length);

        // A receipt and an order without warehouseId are created in the default warehouse.
        var noWarehouse = await Stock.CreateAsync(http, Stock.Draft("receipt", w1, (a, 1)).NoWarehouse(false));
        Assert.Equal(defaultWarehouse, noWarehouse.GetProperty("warehouse").Id());
        var orderNoWarehouse = await PO.CreateAsync(http, PO.Body(s, (a, 1, null, 1m)).NoWarehouse(false));
        Assert.Equal(defaultWarehouse, orderNoWarehouse.GetProperty("warehouse").Id());

        // A receipt of C is posted before C is deactivated; then a draft that newly names C is refused.
        var receiptOfC = await Stock.PostDocumentAsync(http, (await Stock.CreateAsync(http, Stock.Draft("receipt", w1, (c.Id(), 2)).Dated(today))).Id());
        await DocRules.DeactivateArticle(http, c.Id());
        using (var inactive = await Stock.PostAsync(http, Stock.Draft("receipt", w1, (c.Id(), 1))))
            await NamedAsync(inactive, HttpStatusCode.Conflict, "REFERENCE_INACTIVE", named, DocRules.InactiveAssignable, false, "lines[0].articleId");

        // Reversing a posted receipt whose article was deactivated -> 201.
        var reversal = await Stock.ReverseAsync(http, receiptOfC.Id(), today);
        Assert.Equal("posted", reversal.Str("status"));

        // Posting a draft whose warehouse was deactivated -> REFERENCE_INACTIVE.
        var draftInW3 = await Stock.CreateAsync(http, Stock.Draft("receipt", w3.Id(), (a, 1)).Dated(today));
        await DocRules.DeactivateWarehouse(http, w3.Id());
        using (var posting = await Stock.SendPostAsync(http, draftInW3.Id()))
            await NamedAsync(posting, HttpStatusCode.Conflict, "REFERENCE_INACTIVE", named, DocRules.ActiveMasters, true, "warehouseId");
        await Stock.AssertDraftAsync(http, draftInW3.Id());

        // Posting a count after stock moved -> COUNT_OUTDATED.
        var count = await Counts.CountAsync(http, w2, a, 3);
        await Stock.ReceiveAsync(http, w2, a, 1);
        using (var outdated = await Stock.SendPostAsync(http, count.Id()))
            await NamedAsync(outdated, HttpStatusCode.Conflict, "COUNT_OUTDATED", named, DocRules.OutdatedCount, false, "lines[0].quantity");
        await Stock.AssertDraftAsync(http, count.Id());

        await Rules.AssertBalancedAsync(http, "after the default behaviour of documents");
    }
}
