using System.Net;
using System.Text.Json.Nodes;
using Xerp.IntegrationTests.Support;

namespace Xerp.IntegrationTests;

/// <summary>Spec 014, AC-30 to AC-34 (partner required on a receipt / an issue) and AC-40, AC-41 (<c>stock.partnerRoleRequired</c>).</summary>
[Collection(XerpCollection.Name)]
public class Rule014PartnerTests(XerpFixture app)
{
    private static readonly OrderApi PO = OrderApi.Purchase;

    private static async Task<int> DocumentCountAsync(HttpClient http) => (await Stock.DocumentsAsync(http)).Total();

    // ---- AC-30 ----

    [Fact]
    public async Task AC30_An_unlinked_receipt_without_a_partner_is_refused_when_the_rule_is_true()
    {
        var s = await Orders.SetupAsync(app);
        var http = s.Http;
        await Rules.SetAsync(http, DocRules.ReceiptPartner, true);

        using (var omitted = await Stock.PostAsync(http, Stock.Draft("receipt", s.W1, (s.A, 1))))
            await DocRules.ValidationRefusedAsync(omitted, DocRules.ReceiptPartner, true, "partnerId");
        using (var asNull = await Stock.PostAsync(http, Stock.Draft("receipt", s.W1, (s.A, 1)).With("partnerId", null)))
            await DocRules.ValidationRefusedAsync(asNull, DocRules.ReceiptPartner, true, "partnerId");
        Assert.Equal(0, await DocumentCountAsync(http));

        var draft = await Stock.CreateAsync(http, Stock.Draft("receipt", s.W1, (s.A, 1)).With("partnerId", s.Sup.Id().ToString()));
        Assert.Equal(s.Sup.Id(), draft.GetProperty("partner").Id());

        using (var replaceNull = await Stock.PutAsync(http, draft.Id(), Stock.Replacement(s.W1, (s.A, 2))))
            await DocRules.ValidationRefusedAsync(replaceNull, DocRules.ReceiptPartner, true, "partnerId");
        McpAssert.JsonEqual(draft, await Stock.GetAsync(http, draft.Id()), "The refused replace changed the draft");

        using (var withoutProperty = await Stock.PutAsync(http, draft.Id(), Stock.Replacement(s.W1, (s.A, 2)).Without("partnerId")))
            await DocRules.PlainAsync(withoutProperty, HttpStatusCode.BadRequest, "VALIDATION_FAILED", "partnerId");
        McpAssert.JsonEqual(draft, await Stock.GetAsync(http, draft.Id()), "The refused replace changed the draft");
    }

    // ---- AC-31 ----

    [Fact]
    public async Task AC31_The_two_rules_are_separate_and_typed()
    {
        var s = await Orders.SetupAsync(app);
        var http = s.Http;
        await Rules.SetAsync(http, DocRules.ReceiptPartner, true);

        await Stock.CreateAsync(http, Stock.Draft("issue", s.W1, (s.A, 1)));
        await Stock.CreateAsync(http, Stock.Transfer(s.W1, s.W2, (s.A, 1)));
        await Stock.CreateAsync(http, Counts.Draft(s.W1, (s.A, 1, null)));

        await Rules.ResetAsync(http, DocRules.ReceiptPartner);
        await Rules.SetAsync(http, DocRules.IssuePartner, true);

        using (var issue = await Stock.PostAsync(http, Stock.Draft("issue", s.W1, (s.A, 1))))
            await DocRules.ValidationRefusedAsync(issue, DocRules.IssuePartner, true, "partnerId");
        using (var issueNull = await Stock.PostAsync(http, Stock.Draft("issue", s.W1, (s.A, 1)).With("partnerId", null)))
            await DocRules.ValidationRefusedAsync(issueNull, DocRules.IssuePartner, true, "partnerId");
        await Stock.CreateAsync(http, Stock.Draft("receipt", s.W1, (s.A, 1)));
        await Stock.CreateAsync(http, Stock.Draft("issue", s.W1, (s.A, 1)).With("partnerId", s.Cus.Id().ToString()));
        await Stock.CreateAsync(http, Stock.Transfer(s.W1, s.W2, (s.A, 1)));
        await Stock.CreateAsync(http, Counts.Draft(s.W1, (s.A, 1, null)));
    }

    // ---- AC-32 ----

    [Fact]
    public async Task AC32_A_receipt_linked_to_an_order_without_a_supplier_is_never_judged()
    {
        var s = await Orders.SetupAsync(app);
        var http = s.Http;
        await Rules.SetAsync(http, Rules.PurchasePartner, false);
        await Rules.SetAsync(http, DocRules.ReceiptPartner, true);
        var order = await PO.ConfirmAsync(http, (await PO.CreateAsync(http, PO.NoPartner(s, (s.A, 10, null, 1m)))).Id());

        var receipt = await Stock.CreateAsync(http, PO.Document(s.W1, order.Id(), (s.A, 10, 1, null)));

        JsonBody.AssertNull(receipt, "partner");
        var posted = await Stock.PostDocumentAsync(http, receipt.Id());
        JsonBody.AssertNull(posted, "partner");
        Assert.Equal(10m, await Stock.QuantityAsync(http, s.A, s.W1));
    }

    // ---- AC-33 ----

    [Fact]
    public async Task AC33_The_rule_judges_saving_not_the_life_of_the_draft()
    {
        var s = await Orders.SetupAsync(app);
        var http = s.Http;
        var toPost = await Stock.CreateAsync(http, Stock.Draft("receipt", s.W1, (s.A, 3)));
        var toReplace = await Stock.CreateAsync(http, Stock.Draft("receipt", s.W1, (s.A, 4)));
        await Rules.SetAsync(http, DocRules.ReceiptPartner, true);

        McpAssert.JsonEqual(toPost, await Stock.GetAsync(http, toPost.Id()), "Setting the rule changed a draft");
        var posted = await Stock.PostDocumentAsync(http, toPost.Id());
        JsonBody.AssertNull(posted, "partner");
        using (var replace = await Stock.PutAsync(http, toReplace.Id(), Stock.Replacement(s.W1, (s.A, 4))))
            await DocRules.ValidationRefusedAsync(replace, DocRules.ReceiptPartner, true, "partnerId");
        McpAssert.JsonEqual(toReplace, await Stock.GetAsync(http, toReplace.Id()), "The refused replace changed the draft");

        var reversal = await Stock.ReverseAsync(http, posted.Id());
        JsonBody.AssertNull(reversal, "partner");
        await Rules.AssertBalancedAsync(http, "AC-33");
    }

    // ---- AC-34 ----

    [Fact]
    public async Task AC34_A_mixed_failure_reports_every_key_and_the_rule_names_only_its_own_field()
    {
        var s = await Orders.SetupAsync(app);
        await Rules.SetAsync(s.Http, DocRules.ReceiptPartner, true);

        using var response = await Stock.PostAsync(s.Http, Stock.Draft("receipt", s.W1, (s.A, -1)).With("documentDate", "2026-02-30"));

        var problem = await HttpAssert.ValidationAsync(response, "partnerId", "documentDate", "lines[0].quantity");
        Assert.Equal(new[] { "documentDate", "lines[0].quantity", "partnerId" }, McpAssert.ErrorKeys(problem));
        Assert.Equal(new[] { "partnerId" }, Rules.Fields(Rules.AssertRule(problem, DocRules.ReceiptPartner, true)));
        Assert.Equal(0, await DocumentCountAsync(s.Http));
    }

    // ---- AC-40 ----

    [Fact]
    public async Task AC40_With_the_role_rule_off_any_existing_partner_may_be_named_but_an_unknown_one_not()
    {
        var s = await Orders.SetupAsync(app);
        var http = s.Http;
        await Stock.ReceiveAsync(http, s.W1, s.A, 10);
        await Rules.SetAsync(http, DocRules.PartnerRole, false);
        await Rules.SetAsync(http, MasterRules.RoleRequired, false);
        var roleless = await Orders.PartnerAsync(http, "NONE", "No role", isSupplier: false, isCustomer: false);

        var receiptOfCustomer = await Stock.CreateAsync(http, Stock.Draft("receipt", s.W1, (s.A, 1)).With("partnerId", s.Cus.Id().ToString()));
        var issueToSupplier = await Stock.CreateAsync(http, Stock.Draft("issue", s.W1, (s.A, 1)).With("partnerId", s.Sup.Id().ToString()));
        var receiptOfNone = await Stock.CreateAsync(http, Stock.Draft("receipt", s.W1, (s.A, 1)).With("partnerId", roleless.Id().ToString()));
        foreach (var draft in new[] { receiptOfCustomer, issueToSupplier, receiptOfNone })
            Assert.Equal("posted", (await Stock.PostDocumentAsync(http, draft.Id())).Str("status"));
        Assert.Equal(s.Cus.Id(), (await Stock.GetAsync(http, receiptOfCustomer.Id())).GetProperty("partner").Id());

        using (var unknown = await Stock.PostAsync(http, Stock.Draft("receipt", s.W1, (s.A, 1)).With("partnerId", Guid.CreateVersion7().ToString())))
            await DocRules.PlainAsync(unknown, HttpStatusCode.Conflict, "REFERENCE_NOT_FOUND", "partnerId");
        await Rules.AssertBalancedAsync(http, "AC-40");
    }

    // ---- AC-41 ----

    [Fact]
    public async Task AC41_Back_to_true_a_posted_receipt_keeps_its_partner_and_a_draft_like_it_is_refused()
    {
        var s = await Orders.SetupAsync(app);
        var http = s.Http;
        await Rules.SetAsync(http, DocRules.PartnerRole, false);
        var posted = await Stock.PostDocumentAsync(http,
            (await Stock.CreateAsync(http, Stock.Draft("receipt", s.W1, (s.A, 1)).With("partnerId", s.Cus.Id().ToString()))).Id());
        var draft = await Stock.CreateAsync(http, Stock.Draft("receipt", s.W1, (s.A, 1)).With("partnerId", s.Cus.Id().ToString()));
        await Rules.SetAsync(http, DocRules.PartnerRole, true);

        McpAssert.JsonEqual(posted, await Stock.GetAsync(http, posted.Id()), "The posted receipt changed");
        using (var posting = await Stock.SendPostAsync(http, draft.Id()))
            await Rules.ConflictAsync(posting, "PARTNER_ROLE_MISSING", DocRules.PartnerRole, true, "partnerId");
        using (var replace = await Stock.PutAsync(http, draft.Id(), DocRules.Replace(s.W1, s.A, Stock.Date, s.Cus.Id().ToString())))
            await Rules.ConflictAsync(replace, "PARTNER_ROLE_MISSING", DocRules.PartnerRole, true, "partnerId");
        McpAssert.JsonEqual(draft, await Stock.GetAsync(http, draft.Id()), "The refused operations changed the draft");
    }
}
