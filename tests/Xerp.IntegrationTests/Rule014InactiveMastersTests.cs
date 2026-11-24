using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xerp.IntegrationTests.Support;

namespace Xerp.IntegrationTests;

/// <summary>
/// Spec 014, AC-80 to AC-86: the three independent rules of inactive masters — <c>references.inactiveAssignable</c>
/// (saving), <c>posting.activeMastersRequired</c> (posting and confirmation) and
/// <c>stock.reversalActiveMastersRequired</c> (reversal).
/// </summary>
[Collection(XerpCollection.Name)]
public class Rule014InactiveMastersTests(XerpFixture app)
{
    private static readonly OrderApi PO = OrderApi.Purchase;

    /// <summary>An inactive warehouse, article, supplier and unit.</summary>
    private sealed record Inactive(Guid Warehouse, Guid Article, JsonElement Supplier, JsonElement Unit);

    private static async Task<Inactive> InactiveAsync(OrderSetup s)
    {
        var warehouse = await MasterApi.Warehouses.CreateAsync(s.Http, "WI", "Inactive warehouse");
        await DocRules.DeactivateWarehouse(s.Http, warehouse.Id());
        var article = await Art.CreateAsync(s.Http, "AI", "Inactive article", s.Pcs);
        await DocRules.DeactivateArticle(s.Http, article.Id());
        var supplier = await Orders.PartnerAsync(s.Http, "SI", "Inactive supplier", isSupplier: true, isCustomer: false, isActive: false);
        var unit = await Uom.CreateAsync(s.Http, "old", "Obsolete", isActive: false);
        return new Inactive(warehouse.Id(), article.Id(), supplier, unit);
    }

    private static JsonObject InactiveReceipt(OrderSetup s, Inactive i) =>
        Stock.Draft("receipt", i.Warehouse, (i.Article, 1)).With("partnerId", i.Supplier.Id().ToString());

    private static JsonObject InactiveOrder(OrderSetup s, Inactive i) => PO.Body(i.Supplier.Id(), i.Warehouse, (i.Article, 1, null, 1m));

    // ---- AC-80 ----

    [Fact]
    public async Task AC80_With_inactiveAssignable_true_an_inactive_record_may_be_newly_named_but_an_unknown_one_not()
    {
        var s = await Orders.SetupAsync(app);
        var http = s.Http;
        var inactive = await InactiveAsync(s);
        await Rules.SetAsync(http, DocRules.InactiveAssignable, true);

        var receipt = await Stock.CreateAsync(http, InactiveReceipt(s, inactive));
        Assert.Equal(inactive.Warehouse, receipt.GetProperty("warehouse").Id());
        var order = await PO.CreateAsync(http, InactiveOrder(s, inactive));
        Assert.Equal(inactive.Supplier.Id(), order.GetProperty("supplier").Id());
        var article = await ArticleApi.CreateAsync(http, "X1", "Article on an old unit", inactive.Unit.Id());
        Assert.Equal(inactive.Unit.Id(), article.BaseUnitId());
        using (var conversion = await Units.PutAsync(http, s.A, inactive.Unit.Id(), 5))
            Assert.True(conversion.StatusCode is HttpStatusCode.Created or HttpStatusCode.OK, $"The conversion was refused: {await conversion.Content.ReadAsStringAsync()}");
        var replaced = await Stock.ReplaceAsync(http, receipt.Id(), DocRules.Replace(inactive.Warehouse, inactive.Article, Stock.Date, inactive.Supplier.Id().ToString()));
        Assert.Equal(inactive.Article, replaced.DocumentLines()[0].GetProperty("article").Id());

        var unknown = () => Guid.CreateVersion7().ToString();
        async Task NotFoundAsync(Task<HttpResponseMessage> request, string key)
        {
            using var response = await request;
            await DocRules.PlainAsync(response, HttpStatusCode.Conflict, "REFERENCE_NOT_FOUND", key);
        }
        await NotFoundAsync(Stock.PostAsync(http, Stock.Draft("receipt", s.W1, (s.A, 1)).With("warehouseId", unknown())), "warehouseId");
        await NotFoundAsync(Stock.PostAsync(http, Stock.Draft("receipt", s.W1, (Guid.CreateVersion7(), 1))), "lines[0].articleId");
        await NotFoundAsync(Stock.PostAsync(http, Stock.Draft("receipt", s.W1, (s.A, 1)).With("partnerId", unknown())), "partnerId");
        await NotFoundAsync(PO.PostAsync(http, PO.Body(s, (s.A, 1, null, 1m)).With("supplierId", unknown())), "supplierId");
        await NotFoundAsync(PO.PostAsync(http, PO.Body(s, (s.A, 1, null, 1m)).With("warehouseId", unknown())), "warehouseId");
        await NotFoundAsync(ArticleApi.PostAsync(http, "X2", "Unknown unit", Guid.CreateVersion7()), "baseUnitId");
        await NotFoundAsync(Units.PutAsync(http, s.A, Guid.CreateVersion7(), 5), "unitId");
    }

    [Fact]
    public async Task AC80_With_the_rule_at_its_default_each_new_reference_to_an_inactive_record_is_refused_by_it()
    {
        var s = await Orders.SetupAsync(app);
        var http = s.Http;
        var inactive = await InactiveAsync(s);

        async Task RefusedAsync(Task<HttpResponseMessage> request, params string[] keys)
        {
            using var response = await request;
            await Rules.ConflictAsync(response, "REFERENCE_INACTIVE", DocRules.InactiveAssignable, false, keys);
        }
        await RefusedAsync(Stock.PostAsync(http, Stock.Draft("receipt", inactive.Warehouse, (s.A, 1))), "warehouseId");
        await RefusedAsync(Stock.PostAsync(http, Stock.Draft("receipt", s.W1, (inactive.Article, 1))), "lines[0].articleId");
        await RefusedAsync(Stock.PostAsync(http, Stock.Draft("receipt", s.W1, (s.A, 1)).With("partnerId", inactive.Supplier.Id().ToString())), "partnerId");
        await RefusedAsync(PO.PostAsync(http, PO.Body(inactive.Supplier.Id(), s.W1, (s.A, 1, null, 1m))), "supplierId");
        await RefusedAsync(PO.PostAsync(http, PO.Body(s, (inactive.Article, 1, null, 1m))), "lines[0].articleId");
        await RefusedAsync(ArticleApi.PostAsync(http, "X1", "Article on an old unit", inactive.Unit.Id()), "baseUnitId");
        await RefusedAsync(Units.PutAsync(http, s.A, inactive.Unit.Id(), 5), "unitId");
    }

    // ---- AC-81, AC-82 ----

    [Fact]
    public async Task AC81_The_three_rules_are_independent_posting_and_confirmation_are_judged_by_their_own_rule()
    {
        var s = await Orders.SetupAsync(app);
        var http = s.Http;
        var inactive = await InactiveAsync(s);
        await Rules.SetAsync(http, DocRules.InactiveAssignable, true);
        var receipt = await Stock.CreateAsync(http, InactiveReceipt(s, inactive));
        var order = await PO.CreateAsync(http, InactiveOrder(s, inactive));

        using (var posting = await Stock.SendPostAsync(http, receipt.Id()))
            await Rules.ConflictAsync(posting, "REFERENCE_INACTIVE", DocRules.ActiveMasters, true, "warehouseId", "partnerId");
        await Stock.AssertDraftAsync(http, receipt.Id());
        using (var confirmation = await PO.SendAsync(http, order.Id(), "confirm"))
            await Rules.ConflictAsync(confirmation, "REFERENCE_INACTIVE", DocRules.ActiveMasters, true, "supplierId", "warehouseId");
        await PO.AssertUnchangedAsync(http, order);
    }

    [Fact]
    public async Task AC82_With_activeMastersRequired_false_too_the_document_posts_and_the_order_confirms()
    {
        var s = await Orders.SetupAsync(app);
        var http = s.Http;
        var inactive = await InactiveAsync(s);
        await Rules.SetAsync(http, DocRules.InactiveAssignable, true);
        var receipt = await Stock.CreateAsync(http, InactiveReceipt(s, inactive));
        var order = await PO.CreateAsync(http, InactiveOrder(s, inactive));
        await Rules.SetAsync(http, DocRules.ActiveMasters, false);

        Assert.Equal("posted", (await Stock.PostDocumentAsync(http, receipt.Id())).Str("status"));
        Assert.Equal(1m, await Stock.QuantityAsync(http, inactive.Article, inactive.Warehouse));
        var confirmed = await PO.ConfirmAsync(http, order.Id());
        Assert.Equal("PO-000001", confirmed.Number());
        await Rules.AssertBalancedAsync(http, "AC-82");
    }

    [Fact]
    public async Task AC82_With_only_activeMastersRequired_false_a_draft_whose_article_was_deactivated_after_saving_posts()
    {
        var s = await Orders.SetupAsync(app);
        var http = s.Http;
        var draft = await Stock.CreateAsync(http, Stock.Draft("receipt", s.W1, (s.A, 2)));
        await DocRules.DeactivateArticle(http, s.A);
        await Rules.SetAsync(http, DocRules.ActiveMasters, false);

        Assert.Equal("posted", (await Stock.PostDocumentAsync(http, draft.Id())).Str("status"));
        Assert.Equal(2m, await Stock.QuantityAsync(http, s.A, s.W1));
        await Rules.AssertBalancedAsync(http, "AC-82");
    }

    // ---- AC-83 ----

    [Fact]
    public async Task AC83_activeMastersRequired_false_does_not_lift_the_role()
    {
        var s = await Orders.SetupAsync(app);
        var http = s.Http;
        var supplier = await PO.NewPartnerAsync(http, "SUP9", "Ninth supplier");
        var draft = await Stock.CreateAsync(http, Stock.Draft("receipt", s.W1, (s.A, 1)).With("partnerId", supplier.Id().ToString()));
        await PO.RemoveRoleAsync(http, supplier.Id());
        await Rules.SetAsync(http, DocRules.ActiveMasters, false);

        using var posting = await Stock.SendPostAsync(http, draft.Id());

        await Rules.ConflictAsync(posting, "PARTNER_ROLE_MISSING", DocRules.PartnerRole, true, "partnerId");
        await Stock.AssertDraftAsync(http, draft.Id());
    }

    // ---- AC-84, AC-85 ----

    [Fact]
    public async Task AC84_A_reversal_is_refused_while_an_article_of_the_original_is_inactive()
    {
        var s = await Orders.SetupAsync(app);
        var http = s.Http;
        await Rules.SetAsync(http, DocRules.ReversalMasters, true);
        var original = await Stock.ReceiveAsync(http, s.W1, s.A, 3);
        Assert.Equal("SR-000001", original.Number());
        await DocRules.DeactivateArticle(http, s.A);

        using (var refused = await Stock.SendReverseAsync(http, original.Id()))
            await Rules.ConflictAsync(refused, "REFERENCE_INACTIVE", DocRules.ReversalMasters, true, "lines[0].articleId");
        Assert.Equal("posted", (await Stock.GetAsync(http, original.Id())).Str("status"));

        await DocRules.ActivateArticle(http, s.A);
        Assert.Equal("SR-000002", (await Stock.ReceiveAsync(http, s.W1, s.A, 1)).Number());
        var reversal = await Stock.ReverseAsync(http, original.Id());
        Assert.Equal("posted", reversal.Str("status"));
        await Rules.AssertBalancedAsync(http, "AC-84");
    }

    [Fact]
    public async Task AC85_Header_keys_come_first_and_alone_and_the_partner_of_a_linked_document_is_not_judged()
    {
        var s = await Orders.SetupAsync(app);
        var http = s.Http;
        await Rules.SetAsync(http, DocRules.ReversalMasters, true);
        await Stock.ReceiveAsync(http, s.W1, s.A, 10);
        var transfer = await Stock.TransferAsync(http, s.W1, s.W2, s.A, 2);
        await DocRules.DeactivateWarehouse(http, s.W2);
        await DocRules.DeactivateArticle(http, s.A);

        using (var refused = await Stock.SendReverseAsync(http, transfer.Id()))
            await Rules.ConflictAsync(refused, "REFERENCE_INACTIVE", DocRules.ReversalMasters, true, "toWarehouseId");
        Assert.Equal("posted", (await Stock.GetAsync(http, transfer.Id())).Str("status"));
        await DocRules.ActivateWarehouse(http, s.W2);
        await DocRules.ActivateArticle(http, s.A);

        // The partner of an unlinked document is judged.
        var withPartner = await Stock.PostDocumentAsync(http,
            (await Stock.CreateAsync(http, Stock.Draft("receipt", s.W1, (s.A, 1)).With("partnerId", s.Sup.Id().ToString()))).Id());
        await DocRules.DeactivatePartner(http, s.Sup.Id());
        using (var refused = await Stock.SendReverseAsync(http, withPartner.Id()))
            await Rules.ConflictAsync(refused, "REFERENCE_INACTIVE", DocRules.ReversalMasters, true, "partnerId");

        // The partner of a linked document is the order's: it is not judged.
        var other = await Orders.PartnerAsync(http, "SUP3", "Third supplier", isSupplier: true, isCustomer: false);
        var order = await PO.ConfirmAsync(http, (await PO.CreateAsync(http, PO.Body(other.Id(), s.W1, (s.A, 4, null, 1m)))).Id());
        var linked = await PO.FulfilAsync(http, order, 1, 4);
        await DocRules.DeactivatePartner(http, other.Id());
        var reversal = await Stock.ReverseAsync(http, linked.Id(), Stock.NextDay);
        Assert.Equal("posted", reversal.Str("status"));
        await Rules.AssertBalancedAsync(http, "AC-85");
    }

    // ---- AC-86 ----

    [Fact]
    public async Task AC86_set_default_of_an_inactive_warehouse_is_never_lifted_by_a_rule()
    {
        var s = await Orders.SetupAsync(app);
        var http = s.Http;
        await Rules.SetAsync(http, DocRules.InactiveAssignable, true);
        await DocRules.DeactivateWarehouse(http, s.W2);

        using var response = await Balance.SendSetDefaultAsync(http, s.W2);

        Rules.AssertNamesNoRule(await HttpAssert.ProblemAsync(response, HttpStatusCode.Conflict, "DEFAULT_WAREHOUSE"));
        Assert.NotEqual(s.W2, await Balance.DefaultIdAsync(http));
    }
}
