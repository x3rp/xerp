using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xerp.IntegrationTests.Support;

namespace Xerp.IntegrationTests;

/// <summary>
/// Spec 014, AC-20 to AC-23 and AC-100: a tenant that sets nothing behaves as specs 001–013 say, its refusals name
/// their rule, a refusal no rule caused has no <c>rules</c>, and one tenant's values touch no other.
/// </summary>
[Collection(XerpCollection.Name)]
public class Rule014DefaultsTests(XerpFixture app)
{
    private static readonly OrderApi PO = OrderApi.Purchase;

    [Fact]
    public async Task AC20_A_tenant_that_sets_nothing_behaves_as_before()
    {
        var s = await Orders.SetupAsync(app);

        await DocDefaults.AssertAsync(s, named: false);
    }

    [Fact]
    public async Task AC21_The_same_after_every_rule_was_set_to_its_other_value_and_reset()
    {
        var s = await Orders.SetupAsync(app);
        foreach (var key in DocRules.Keys)
            await DocRules.FlipAsync(s.Http, key);
        foreach (var key in DocRules.Keys)
            await Rules.ResetAsync(s.Http, key);

        await DocDefaults.AssertAsync(s, named: true);
    }

    [Fact]
    public async Task AC22_The_five_default_refusals_name_their_rule()
    {
        var s = await Orders.SetupAsync(app);

        await DocDefaults.AssertAsync(s, named: true);
    }

    // ---- AC-23 ----

    /// <summary>What AC-23 needs from before the rules are flipped: a posted receipt and a confirmed order.</summary>
    private sealed record Prepared(Guid Posted, JsonElement Order, JsonElement Sup2);

    private static async Task<Prepared> PrepareAsync(OrderSetup s)
    {
        var posted = await Stock.PostDocumentAsync(s.Http, (await Stock.CreateAsync(s.Http, Stock.Draft("receipt", s.W1, (s.A, 5)).Dated(DocRules.Today))).Id());
        var order = await PO.OrderedAsync(s, (s.A, 10, null, 1m));
        var sup2 = await Orders.PartnerAsync(s.Http, "SUP2", "Second supplier", isSupplier: true, isCustomer: false);
        return new Prepared(posted.Id(), order, sup2);
    }

    /// <summary>The bodies are valid for both values of every rule (reference, partner, warehouse named, no repeats).</summary>
    private static async Task AssertNoRuleRefusalsAsync(OrderSetup s, Prepared p)
    {
        var http = s.Http;
        JsonObject Receipt() => Stock.Draft("receipt", s.W1, (s.A, 1)).Dated(DocRules.Today).With("reference", "R-1").With("partnerId", s.Sup.Id().ToString());

        async Task<JsonElement> PlainAsync(HttpResponseMessage response, HttpStatusCode status, string code, params string[] keys)
        {
            using (response)
                return await DocRules.PlainAsync(response, status, code, keys);
        }

        await PlainAsync(await Stock.PostAsync(http, Receipt().Dated("2026-02-30")), HttpStatusCode.BadRequest, "VALIDATION_FAILED", "documentDate");
        var draft = await Stock.CreateAsync(http, Receipt());
        await PlainAsync(await Stock.PutAsync(http, draft.Id(), DocRules.Replace(s.W1, s.A, DocRules.Today, s.Sup.Id().ToString()).Without("reference")),
            HttpStatusCode.BadRequest, "VALIDATION_FAILED", "reference");
        await PlainAsync(await Stock.PostAsync(http, Receipt().With("reference", new string('r', 101))), HttpStatusCode.BadRequest, "VALIDATION_FAILED", "reference");
        await PlainAsync(await Stock.PostAsync(http, Units.Draft("count", s.W1, (s.A, 1, null), (s.A, 2, null)).With("reference", "R-1")),
            HttpStatusCode.BadRequest, "VALIDATION_FAILED", "lines[0].articleId", "lines[1].articleId");
        await PlainAsync(await Stock.PostAsync(http, Stock.Transfer(s.W1, s.W2, (s.A, 1)).With("reference", "R-1").Without("warehouseId")),
            HttpStatusCode.BadRequest, "VALIDATION_FAILED", "warehouseId");
        await PlainAsync(await Stock.PutAsync(http, draft.Id(), DocRules.Replace(s.W1, s.A, DocRules.Today, s.Sup.Id().ToString()).With("reference", "R-1").Without("warehouseId")),
            HttpStatusCode.BadRequest, "VALIDATION_FAILED", "warehouseId");
        await PlainAsync(await Stock.PostAsync(http, Receipt().With("partnerId", "abc")), HttpStatusCode.BadRequest, "VALIDATION_FAILED", "partnerId");
        await PlainAsync(await Stock.PostAsync(http, Stock.Transfer(s.W1, s.W2, (s.A, 1)).With("reference", "R-1").With("partnerId", s.Sup.Id().ToString())),
            HttpStatusCode.BadRequest, "VALIDATION_FAILED", "partnerId");
        await PlainAsync(await Stock.PostAsync(http, Stock.Draft("receipt", s.W1, (Guid.CreateVersion7(), 1)).Dated(DocRules.Today)
            .With("reference", "R-1").With("partnerId", s.Sup.Id().ToString())), HttpStatusCode.Conflict, "REFERENCE_NOT_FOUND", "lines[0].articleId");
        await PlainAsync(await Stock.PostAsync(http, PO.Document(s.W1, p.Order.Id(), (s.A, 1, 1, null)).With("reference", "R-1").With("partnerId", p.Sup2.Id().ToString())),
            HttpStatusCode.Conflict, "ORDER_MISMATCH", "partnerId");
        await PlainAsync(await Stock.SendPostAsync(http, p.Posted), HttpStatusCode.Conflict, "INVALID_STATE");
    }

    [Fact]
    public async Task AC23_A_refusal_no_rule_caused_has_no_rules_member_in_a_default_tenant()
    {
        var s = await Orders.SetupAsync(app);
        var prepared = await PrepareAsync(s);

        await AssertNoRuleRefusalsAsync(s, prepared);
    }

    [Fact]
    public async Task AC23_And_with_every_rule_of_this_spec_at_its_other_value()
    {
        var s = await Orders.SetupAsync(app);
        var prepared = await PrepareAsync(s);
        foreach (var key in DocRules.Keys)
            await DocRules.FlipAsync(s.Http, key);

        await AssertNoRuleRefusalsAsync(s, prepared);
    }

    // ---- AC-100 ----

    [Fact]
    public async Task AC100_Values_set_by_tenant_X_change_nothing_for_tenant_Y()
    {
        var x = await Orders.SetupAsync(app);
        var y = await Orders.SetupAsync(app);
        foreach (var key in DocRules.Keys)
            await DocRules.FlipAsync(x.Http, key);

        // X lives under its own values: a receipt without a reference is refused there.
        using (var refused = await Stock.PostAsync(x.Http, Stock.Draft("receipt", x.W1, (x.A, 1)).With("partnerId", x.Sup.Id().ToString())))
            await DocRules.ValidationRefusedAsync(refused, DocRules.ReferenceRequired, true, "reference");

        await DocDefaults.AssertAsync(y, named: true);

        var ofY = await Rules.ListAsync(y.Http, "?limit=500");
        Assert.Equal(70, ofY.Total());
        Assert.Equal(0, (await Rules.ListAsync(y.Http, "?source=tenant")).Total());
        foreach (var (key, expected) in DocRules.Inventory)
        {
            var rule = Assert.Single(ofY.Items(), r => r.Str("key") == key);
            Rules.AssertAtDefault(rule, key);
            Rules.AssertBool(rule, "value", expected);
        }
        await Rules.AssertBalancedAsync(x.Http, "tenant X");
        await Rules.AssertBalancedAsync(y.Http, "tenant Y");
    }
}
