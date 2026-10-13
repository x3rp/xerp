using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xerp.IntegrationTests.Support;

namespace Xerp.IntegrationTests;

/// <summary>
/// Spec 013, AC-30 to AC-34 and AC-40, AC-41: the fourteen required-field rules (R5–R7), what they do to records
/// written before (R3), and <c>partner.roleRequired</c> (R8).
/// </summary>
[Collection(XerpCollection.Name)]
public class MasterRuleRequiredFieldTests(XerpFixture app)
{
    private static readonly MasterApi P = MasterApi.Partners;
    private static readonly MasterApi W = MasterApi.Warehouses;

    // ---- AC-30 ----

    [Theory]
    [MemberData(nameof(MasterRules.RequiredData), MemberType = typeof(MasterRules))]
    public async Task AC30_With_the_rule_true_the_field_is_required_on_create_and_on_replace(string group, string field)
    {
        var tenant = await app.NewTenantAsync();
        var http = tenant.Client;
        var rule = MasterRules.Required(group, field);
        var m = await RuledMaster.OpenAsync(group, http);
        await Rules.SetAsync(http, rule, true);
        var before = await m.TotalAsync(http);

        // Omitted, null, and empty after trimming (R5, E1).
        await MasterRules.RequiredAsync(m.PostAsync(http, m.Minimal("R1", "Record")), rule, field);
        await MasterRules.RequiredAsync(m.PostAsync(http, m.Minimal("R1", "Record").With(field, null)), rule, field);
        await MasterRules.RequiredAsync(m.PostAsync(http, m.Minimal("R1", "Record").With(field, "  ")), rule, field);
        await MasterRules.RequiredAsync(m.PostAsync(http, m.Minimal("R1", "Record").With(field, "")), rule, field);
        Assert.Equal(before, await m.TotalAsync(http));

        var created = await m.CreateAsync(http, m.Minimal("R1", "Record").With(field, MasterRules.ValidValue(field)));
        Assert.Equal(MasterRules.ValidValue(field), created.Str(field));

        await MasterRules.RequiredAsync(m.PutAsync(http, created.Id(), m.BodyOf(created).With(field, null)), rule, field);
        await MasterRules.RequiredAsync(m.PutAsync(http, created.Id(), m.BodyOf(created).With(field, " ")), rule, field);
        await m.AssertUnchangedAsync(http, created);

        // A replace that gives the field passes.
        var replaced = await m.ReplaceAsync(http, created.Id(), m.BodyOf(created).With("name", "Renamed"));
        Assert.Equal((MasterRules.ValidValue(field), "Renamed"), (replaced.Str(field), replaced.Str("name")));
    }

    // ---- AC-31 ----

    [Fact]
    public async Task AC31_R7_Each_rule_is_separate()
    {
        var tenant = await app.NewTenantAsync();
        var http = tenant.Client;
        await Rules.SetAsync(http, MasterRules.PartnerCityRequired, true);

        // partner.cityRequired says nothing about a warehouse's city, nor about a partner's postal code.
        var warehouse = await W.CreateAsync(http, "W1", "No city");
        var partner = await P.CreateAsync(http, P.Minimal("P1", "No postal code").With("city", "Split"));

        JsonBody.AssertNull(warehouse, MasterApi.AddressFields);
        JsonBody.AssertNull(partner, "postalCode", "addressLine1", "addressLine2", "region", "countryCode", "taxId");
        Assert.Equal("Split", partner.Str("city"));
        await MasterRules.RequiredAsync(P.PostAsync(http, P.Minimal("P2", "No city")), MasterRules.PartnerCityRequired, "city");
    }

    // ---- AC-32 ----

    [Fact]
    public async Task AC32_E3_Several_rules_and_an_invariant_in_one_response()
    {
        var tenant = await app.NewTenantAsync();
        var http = tenant.Client;
        await Rules.SetAsync(http, MasterRules.TaxIdRequired, true);
        await Rules.SetAsync(http, MasterRules.PartnerCityRequired, true);

        using var response = await P.PostAsync(http, P.Minimal("P1", new string('n', 201)));

        var problem = await HttpAssert.ValidationAsync(response, "name", "taxId", "city");
        Assert.Equal(new[] { "city", "name", "taxId" }, McpAssert.ErrorKeys(problem));
        var rules = problem.GetProperty("rules").EnumerateArray().ToArray();
        Assert.Equal(2, rules.Length);
        Assert.Equal(
            new[] { (MasterRules.PartnerCityRequired, "city"), (MasterRules.TaxIdRequired, "taxId") },
            rules.Select(r => (r.Str("key"), Assert.Single(Rules.Fields(r)))).Order());
        Assert.All(rules, r =>
        {
            Assert.Equal(new[] { "fields", "key", "value" }, r.PropertyNames());
            Rules.AssertBool(r, "value", true);
        });
        // R18: ordered by the first of each rule's fields in errors.
        var order = Rules.ErrorKeysInOrder(problem);
        Assert.Equal(rules.OrderBy(r => Array.IndexOf(order, Rules.Fields(r)[0])).Select(r => r.Str("key")), rules.Select(r => r.Str("key")));
        Assert.Equal(0, (await P.ListAsync(http)).Total());
    }

    // ---- AC-33 ----

    [Fact]
    public async Task AC33_R3_An_article_written_before_the_rule_stays_valid_and_usable_until_its_next_replace()
    {
        var s = await Stock.SetupAsync(app);
        var http = s.Http;
        var before = await Art.GetAsync(http, s.A);
        JsonBody.AssertNull(before, "description");

        await Rules.SetAsync(http, MasterRules.DescriptionRequired, true);

        McpAssert.JsonEqual(before, await Art.GetAsync(http, s.A), "Setting the rule changed a stored article");
        // It can be put on a stock document and the document posted.
        await Stock.ReceiveAsync(http, s.W1, s.A, 5);
        Assert.Equal(5m, await Stock.QuantityAsync(http, s.A, s.W1));

        // The replace is judged whole: also in the field it leaves as it was.
        await MasterRules.RequiredAsync(Stock.PutArticleAsync(http, s.A, Stock.ArticleBody(before)), MasterRules.DescriptionRequired, "description");
        await MasterRules.RequiredAsync(Stock.PutArticleAsync(http, s.A, Stock.ArticleBody(before).With("name", "Renamed")),
            MasterRules.DescriptionRequired, "description");
        McpAssert.JsonEqual(before, await Art.GetAsync(http, s.A), "A refused replace changed the article");
        var described = await Stock.ReplaceArticleAsync(http, s.A, Stock.ArticleBody(before).With("description", "Now described"));
        Assert.Equal("Now described", described.Str("description"));

        await Rules.ResetAsync(http, MasterRules.DescriptionRequired);
        var cleared = await Stock.ReplaceArticleAsync(http, s.A, Stock.ArticleBody(before).With("description", null));
        JsonBody.AssertNull(cleared, "description");
    }

    // ---- AC-34 ----

    [Fact]
    public async Task AC34_E9_R2_The_central_warehouse_without_a_city_stays_usable_and_set_default_is_not_judged()
    {
        var s = await Stock.SetupAsync(app);
        var http = s.Http;
        var central = await Balance.DefaultAsync(http);
        Assert.Equal("CENTRAL", central.Str("code"));
        JsonBody.AssertNull(central, "city");

        await Rules.SetAsync(http, MasterRules.WarehouseCityRequired, true);

        McpAssert.JsonEqual(central, await W.GetAsync(http, central.Id()), "Setting the rule changed the central warehouse");
        await Stock.ReceiveAsync(http, central.Id(), s.A, 3);
        Assert.Equal(3m, await Stock.QuantityAsync(http, s.A, central.Id()));

        // Its next replace is judged (R3) ...
        await MasterRules.RequiredAsync(W.PutAsync(http, central.Id(), Stock.WarehouseBody(central)), MasterRules.WarehouseCityRequired, "city");
        await W.AssertUnchangedAsync(http, central);

        // ... but set-default, deactivation by replace aside, is never judged (R2): W1 has no city either.
        using (var setDefault = await Balance.SendSetDefaultAsync(http, s.W1))
            Assert.True((await HttpAssert.JsonAsync(setDefault, HttpStatusCode.OK)).Bool("isDefault"));
        Assert.Equal(s.W1, await Balance.DefaultIdAsync(http));
        JsonBody.AssertNull(await W.GetAsync(http, s.W1), "city");
    }

    // ---- AC-40 ----

    [Fact]
    public async Task AC40_With_roleRequired_false_a_partner_with_neither_role_is_saved()
    {
        var tenant = await app.NewTenantAsync();
        var http = tenant.Client;
        await Rules.SetAsync(http, MasterRules.RoleRequired, false);

        var bothFalse = await P.CreateAsync(http, Orders.NewPartner("P1", "Both false", isSupplier: false, isCustomer: false));
        var omitted = await P.CreateAsync(http, new JsonObject { ["code"] = "P2", ["name"] = "Both omitted" });
        var supplier = await P.CreateAsync(http, Orders.NewPartner("P3", "Supplier", isSupplier: true, isCustomer: false));
        var noLonger = await P.ReplaceAsync(http, supplier.Id(), Orders.PartnerBody(supplier).With("isSupplier", false));

        foreach (var partner in new[] { bothFalse, omitted, noLonger })
            Assert.Equal((false, false), (partner.Bool("isCustomer"), partner.Bool("isSupplier")));
        Assert.Equal((false, false), ((await P.GetAsync(http, supplier.Id())).Bool("isCustomer"), (await P.GetAsync(http, supplier.Id())).Bool("isSupplier")));

        // A missing or wrong-typed role field is the form of the request: reported alone, without rules.
        await MasterRules.InvariantAsync(
            http.PostAsync(P.Path, HttpAssert.Raw("""{ "code": "P4", "name": "Null role", "isSupplier": true, "isCustomer": null }""")), "isCustomer");
        await MasterRules.InvariantAsync(
            http.PostAsync(P.Path, HttpAssert.Raw("""{ "code": "P4", "name": "Null role", "isSupplier": false, "isCustomer": null }""")), "isCustomer");
        Assert.Equal(3, (await P.ListAsync(http)).Total());
    }

    // ---- AC-41 ----

    [Fact]
    public async Task AC41_E8_A_partner_without_a_role_cannot_be_named_on_an_order_and_its_next_replace_needs_a_role()
    {
        var s = await Orders.SetupAsync(app);
        var http = s.Http;
        await Rules.SetAsync(http, MasterRules.RoleRequired, false);
        var noRole = await P.CreateAsync(http, Orders.NewPartner("N1", "No role", isSupplier: false, isCustomer: false));

        // The role checks of orders are untouched (009/R2, 010): no rule of this spec judges them.
        foreach (var o in new[] { OrderApi.Purchase, OrderApi.Sales })
        {
            using var response = await o.PostAsync(http, o.Body(noRole.Id(), s.W1, (s.A, 1, null, 1m)));
            Rules.AssertNamesNoRule(await Stock.ConflictAsync(response, "PARTNER_ROLE_MISSING", o.PartnerId));
            Assert.Equal(0, (await o.ListAsync(http)).Total());
        }

        await Rules.SetAsync(http, MasterRules.RoleRequired, true);

        // Stays and is read as before; only its next replace is judged.
        McpAssert.JsonEqual(noRole, await P.GetAsync(http, noRole.Id()), "Setting the rule changed a stored partner");
        Assert.Contains("N1", (await P.ListAsync(http, "?isCustomer=false&isSupplier=false")).Codes());
        await MasterRules.RoleRequiredAsync(P.PutAsync(http, noRole.Id(), Orders.PartnerBody(noRole)));
        await P.AssertUnchangedAsync(http, noRole);
        var customer = await P.ReplaceAsync(http, noRole.Id(), Orders.PartnerBody(noRole).With("isCustomer", true));
        Assert.Equal((true, false), (customer.Bool("isCustomer"), customer.Bool("isSupplier")));
    }
}
