using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xerp.IntegrationTests.Support;

namespace Xerp.IntegrationTests;

/// <summary>
/// Spec 013, AC-10 to AC-12 and AC-20 to AC-23: the registry has 30 rules; each of the 24 new ones accepts both
/// values and resets; a tenant that sets nothing behaves exactly as specs 001–012 say; the one default refusal
/// names its rule, and a refusal no rule caused has no <c>rules</c> member.
/// </summary>
[Collection(XerpCollection.Name)]
public class MasterRuleRegistryAndDefaultsTests(XerpFixture app)
{
    private static readonly MasterApi P = MasterApi.Partners;

    // ---- AC-10 ----

    [Fact]
    public async Task AC10_A_new_tenant_lists_thirty_rules_in_ordinal_order_at_their_defaults()
    {
        var tenant = await app.NewTenantAsync();

        var list = await Rules.ListAsync(tenant.Client, "?limit=500");

        Assert.Equal(48, list.Total());
        // The literal inventory is in RuleRegistryTests (012 AC-20, 013 AC-10); Rules.All is pinned to it there.
        Assert.Equal(Rules.All, list.RuleKeys());
        Assert.Equal(24, MasterRules.Keys.Distinct().Count());
        foreach (var rule in list.Items())
        {
            var key = rule.Str("key");
            Rules.AssertAtDefault(rule, key);
            Assert.Equal(key.Split('.')[0], rule.Str("group"));
            JsonBody.AssertNull(rule, "updatedAt", "updatedBy");
            Assert.False(string.IsNullOrWhiteSpace(rule.Str("name")), $"{key}: name is empty.");
            Assert.False(string.IsNullOrWhiteSpace(rule.Str("description")), $"{key}: description is empty.");
        }
        // The only rules of this spec whose default is true: the four codeChangeable and partner.roleRequired.
        Assert.Equal(
            new[] { "article.codeChangeable", "partner.codeChangeable", "partner.roleRequired", "uom.codeChangeable", "warehouse.codeChangeable" },
            list.Items().Where(r => r.Bool("default") && MasterRules.Keys.Contains(r.Str("key"))).Select(r => r.Str("key")));
    }

    // ---- AC-11 ----

    [Theory]
    [InlineData("partner", 11)]
    [InlineData("uom", 2)]
    [InlineData("article", 7)] // 3 of spec 013 and 4 of spec P01 (P01 AC-01, item 2)
    [InlineData("warehouse", 8)]
    public async Task AC11_The_group_filter_returns_the_rules_of_one_master(string group, int count)
    {
        var tenant = await app.NewTenantAsync();

        var list = await Rules.ListAsync(tenant.Client, $"?group={group}&limit=500");

        Assert.Equal(count, list.Total());
        Assert.Equal(Rules.All.Where(k => k.StartsWith(group + ".", StringComparison.Ordinal)), list.RuleKeys());
        Assert.All(list.Items(), rule => Assert.Equal(group, rule.Str("group")));
    }

    // ---- AC-12 ----

    [Theory]
    [MemberData(nameof(MasterRules.KeyData), MemberType = typeof(MasterRules))]
    public async Task AC12_Each_of_the_24_accepts_both_values_and_resets(string key)
    {
        var tenant = await app.NewTenantAsync();
        var http = tenant.Client;

        Rules.AssertTenantValue(await Rules.SetAsync(http, key, true), key, true, tenant.ApiKeyId);
        Rules.AssertBool(await Rules.GetAsync(http, key), "value", true);
        Rules.AssertTenantValue(await Rules.SetAsync(http, key, false), key, false, tenant.ApiKeyId);
        Rules.AssertBool(await Rules.GetAsync(http, key), "value", false);
        Rules.AssertAtDefault(await Rules.ResetAsync(http, key), key);
        Rules.AssertAtDefault(await Rules.GetAsync(http, key), key);

        var history = await Rules.ChangesAsync(http);
        Assert.Equal(3, history.Total());
        Assert.All(history.Items(), change => Assert.Equal(key, change.Str("key")));
        Assert.Equal(new[] { "reset", "set", "set" }, history.Items().Select(c => c.Str("action")).Order(StringComparer.Ordinal));
        // The other 29 are untouched.
        Assert.Equal(0, (await Rules.ListAsync(http, "?source=tenant")).Total());
    }

    // ---- AC-20, AC-21, AC-22 ----

    [Fact]
    public async Task AC20_A_tenant_that_sets_nothing_behaves_as_specs_001_to_012_say()
    {
        var tenant = await app.NewTenantAsync();

        await MasterRules.AssertDefaultBehaviourAsync(tenant.Client, named: false);

        Assert.Equal(0, (await Rules.ChangesAsync(tenant.Client)).Total());
    }

    [Fact]
    public async Task AC21_The_same_after_every_rule_was_set_to_its_other_value_and_reset()
    {
        var tenant = await app.NewTenantAsync();
        var http = tenant.Client;
        await MasterRules.SetAllToOtherAsync(http);
        Assert.Equal(MasterRules.Keys.Order(StringComparer.Ordinal), (await Rules.ListAsync(http, "?source=tenant&limit=500")).RuleKeys());
        foreach (var key in MasterRules.Keys)
            Rules.AssertAtDefault(await Rules.ResetAsync(http, key), key);

        await MasterRules.AssertDefaultBehaviourAsync(http, named: true);

        Assert.Equal(0, (await Rules.ListAsync(http, "?source=tenant")).Total());
    }

    [Fact]
    public async Task AC22_The_default_refusal_of_a_partner_without_a_role_names_its_rule()
    {
        var tenant = await app.NewTenantAsync();
        var http = tenant.Client;

        await MasterRules.RoleRequiredAsync(P.PostAsync(http, Orders.NewPartner("P1", "No role", isSupplier: false, isCustomer: false)));
        await MasterRules.RoleRequiredAsync(P.PostAsync(http, new JsonObject { ["code"] = "P1", ["name"] = "No role" }));

        // On replace as well (004/R15), the record unchanged.
        var supplier = await P.CreateAsync(http, Orders.NewPartner("P1", "Supplier", isSupplier: true, isCustomer: false));
        await MasterRules.RoleRequiredAsync(P.PutAsync(http, supplier.Id(), Orders.PartnerBody(supplier).With("isSupplier", false)));
        await P.AssertUnchangedAsync(http, supplier);
        Assert.Equal(1, (await P.ListAsync(http)).Total());
    }

    [Fact]
    public async Task AC22_R18_A_missing_role_and_an_invalid_field_are_reported_together_and_only_the_role_keys_are_the_rules()
    {
        var tenant = await app.NewTenantAsync();

        using var response = await P.PostAsync(tenant.Client,
            Orders.NewPartner("P1", new string('n', 201), isSupplier: false, isCustomer: false).With("countryCode", "Croatia"));

        var problem = await HttpAssert.ValidationAsync(response, "name", "countryCode", "isCustomer", "isSupplier");
        Assert.Equal(new[] { "countryCode", "isCustomer", "isSupplier", "name" }, McpAssert.ErrorKeys(problem));
        Assert.Equal(new[] { "isCustomer", "isSupplier" }, Rules.Fields(Rules.AssertRule(problem, MasterRules.RoleRequired, true)));
    }

    // ---- AC-23 ----

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AC23_A_refusal_no_rule_caused_has_no_rules_member(bool everyRequiredRuleIsTrue)
    {
        var tenant = await app.NewTenantAsync();
        var http = tenant.Client;
        if (everyRequiredRuleIsTrue)
            foreach (var (group, field) in MasterRules.RequiredFields)
                await Rules.SetAsync(http, MasterRules.Required(group, field), true);

        // A partner body with every field given satisfies every "…Required" rule: what is refused is the form.
        await MasterRules.InvariantAsync(P.PostAsync(http, P.Full("V1").Without("code")), "code");
        await MasterRules.InvariantAsync(P.PostAsync(http, P.Full("no good!")), "code");
        await MasterRules.InvariantAsync(P.PostAsync(http, P.Full("V1").Without("name")), "name");
        await MasterRules.InvariantAsync(P.PostAsync(http, P.Full("V1").With("name", new string('n', 201))), "name");
        await MasterRules.InvariantAsync(P.PostAsync(http, P.Full("V1").With("countryCode", "Croatia")), "countryCode");
        await MasterRules.InvariantAsync(P.PostAsync(http, P.Full("V1").With("isCustomer", "yes")), "isCustomer");
        Assert.Equal(0, (await P.ListAsync(http)).Total());

        // CODE_TAKEN is an invariant (I7): without errors and without rules.
        var partner = await P.CreateAsync(http, P.Full("V2"));
        using (var taken = await P.PostAsync(http, P.Full("v2")))
        {
            var problem = await HttpAssert.ProblemAsync(taken, HttpStatusCode.Conflict, "CODE_TAKEN");
            Rules.AssertNamesNoRule(problem);
            Assert.False(problem.TryGetProperty("errors", out _), $"CODE_TAKEN has no 'errors': {problem}");
        }

        // A replace carries every field (I9): a body without the property is the form of the request (R6).
        await MasterRules.InvariantAsync(P.PutAsync(http, partner.Id(), P.Full("V2").Without("city")), "city");
        await P.AssertUnchangedAsync(http, partner);

        // The same for the other three masters: a missing name.
        foreach (var group in new[] { "uom", "article", "warehouse" })
        {
            var m = await RuledMaster.OpenAsync(group, http);
            var body = m.Minimal("V3", "x").Without("name");
            foreach (var field in MasterRules.RequiredFields.Where(r => r.Group == group).Select(r => r.Field))
                body[field] = MasterRules.ValidValue(field);
            await MasterRules.InvariantAsync(m.PostAsync(http, body), "name");
        }
    }

    [Fact]
    public async Task AC23_R6_E2_With_the_rule_true_a_given_value_of_a_wrong_form_is_refused_without_rules()
    {
        var tenant = await app.NewTenantAsync();
        var http = tenant.Client;
        await Rules.SetAsync(http, MasterRules.Required("partner", "countryCode"), true);
        await Rules.SetAsync(http, MasterRules.TaxIdRequired, true);

        await MasterRules.InvariantAsync(
            P.PostAsync(http, P.Minimal("P1").With("taxId", "HR1").With("countryCode", "Croatia")), "countryCode");
        await MasterRules.InvariantAsync(
            P.PostAsync(http, P.Minimal("P1").With("taxId", new string('7', 51)).With("countryCode", "HR")), "taxId");

        Assert.Equal(0, (await P.ListAsync(http)).Total());
    }
}
