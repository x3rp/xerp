using System.Text.Json;
using System.Text.Json.Nodes;
using Xerp.Application.Articles;
using Xerp.Application.Common;
using Xerp.Application.Partners;
using Xerp.Application.Ports;
using Xerp.Application.Warehouses;
using Xerp.Domain.Common;
using Xerp.Domain.Partners;
using Xerp.Domain.Rules;

namespace Xerp.UnitTests;

/// <summary>
/// Spec 013, AC-03: the required-field, role, code-change and uniqueness decisions as pure functions of the
/// values and the rule's boolean, and the validations of the masters judged by a tenant's values.
/// </summary>
public class MasterDataRuleTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static readonly string[] AddressFields = ["addressLine1", "addressLine2", "postalCode", "city", "region", "countryCode"];

    private static TenantRules With(params (RuleDefinition Rule, bool Value)[] values) =>
        new(values.ToDictionary(v => v.Rule.Key, v => v.Value));

    private static PartnerInput Partner(JsonObject json) => JsonSerializer.Deserialize<PartnerInput>(json.ToJsonString(), Web)!;

    private static WarehouseInput Warehouse(JsonObject json) => JsonSerializer.Deserialize<WarehouseInput>(json.ToJsonString(), Web)!;

    private static JsonObject MinimalPartner() => new() { ["code"] = "P-1", ["name"] = "Acme", ["isSupplier"] = true };

    private static JsonObject FullPartner() => new()
    {
        ["code"] = "P-1", ["name"] = "Acme", ["isCustomer"] = false, ["isSupplier"] = true, ["taxId"] = null,
        ["addressLine1"] = null, ["addressLine2"] = null, ["postalCode"] = null, ["city"] = null, ["region"] = null,
        ["countryCode"] = null, ["isActive"] = true,
    };

    private static void AssertRefusedBy(AppError error, RuleDefinition rule, bool value, params string[] fields)
    {
        Assert.Equal(ErrorCodes.ValidationFailed, error.Code);
        var refusal = Assert.Single(error.Rules!);
        Assert.Equal(rule.Key, refusal.Key);
        Assert.Equal(value, refusal.Value);
        Assert.Equal(fields, refusal.Fields);
        Assert.Equal(fields, error.Errors!.Keys);
    }

    // ---- the registry (R1)

    [Fact]
    public void R1_Each_master_has_its_code_and_name_rule_and_one_rule_per_optional_field()
    {
        Assert.Equal(24, RuleRegistry.Masters.Sum(m => m.All.Count()) + 2);
        Assert.Empty(RuleRegistry.Uom.RequiredFields);
        Assert.Equal(["description"], RuleRegistry.Article.RequiredFields.Keys);
        Assert.Equal(["taxId", .. AddressFields], RuleRegistry.Partner.RequiredFields.Keys);
        Assert.Equal(AddressFields, RuleRegistry.Warehouse.RequiredFields.Keys);
        foreach (var (group, master) in new[] { ("uom", RuleRegistry.Uom), ("article", RuleRegistry.Article), ("partner", RuleRegistry.Partner), ("warehouse", RuleRegistry.Warehouse) })
        {
            Assert.Equal($"{group}.codeChangeable", master.CodeChangeable.Key);
            Assert.True(master.CodeChangeable.Default);
            Assert.Equal($"{group}.nameUnique", master.NameUnique.Key);
            Assert.False(master.NameUnique.Default);
            foreach (var (field, rule) in master.RequiredFields)
            {
                Assert.Equal($"{group}.{field}Required", rule.Key);
                Assert.False(rule.Default);
            }
        }
        Assert.All(RuleRegistry.All, r => Assert.Same(r, RuleRegistry.Find(r.Key)));
    }

    // ---- the decisions, as pure functions (AC-03)

    [Theory]
    [InlineData(true, null, true)]
    [InlineData(true, "Split", false)]
    [InlineData(false, null, false)]
    [InlineData(false, "Split", false)]
    public void R5_A_required_field_is_missing_only_when_the_rule_is_true_and_there_is_no_value(bool required, string? value, bool missing) =>
        Assert.Equal(missing, MasterDataRules.RequiredFieldMissing(required, value));

    [Theory]
    [InlineData(true, false, false, true)]
    [InlineData(true, true, false, false)]
    [InlineData(true, false, true, false)]
    [InlineData(true, true, true, false)]
    [InlineData(false, false, false, false)]
    [InlineData(false, true, true, false)]
    public void R8_A_role_is_missing_only_when_the_rule_is_true_and_the_partner_has_neither(bool required, bool isCustomer, bool isSupplier, bool missing) =>
        Assert.Equal(missing, PartnerRules.RoleMissing(required, isCustomer, isSupplier));

    [Theory]
    [InlineData(false, "A-1", "A-1", false)]
    [InlineData(false, "A-1", "a-1", true)] // a change of letter case only is a change (R9)
    [InlineData(false, "A-1", "A-2", true)]
    [InlineData(true, "A-1", "a-1", false)]
    [InlineData(true, "A-1", "A-2", false)]
    public void R9_A_code_change_is_refused_only_when_the_code_is_not_changeable(bool changeable, string stored, string given, bool refused) =>
        Assert.Equal(refused, MasterDataRules.CodeChangeRefused(changeable, stored, given));

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, false)]
    public void R12_A_value_is_taken_only_when_it_must_be_unique_and_another_record_has_it(bool unique, bool anotherHasIt, bool taken) =>
        Assert.Equal(taken, MasterDataRules.Taken(unique, anotherHasIt));

    [Fact]
    public void R9_The_code_change_names_its_rule_and_the_key_code()
    {
        var off = With((RuleRegistry.Article.CodeChangeable, false));
        Assert.Null(MasterWriteChecks.CodeChange(off, RuleRegistry.Article, "an article", "A-1", "A-1"));
        Assert.Null(MasterWriteChecks.CodeChange(TenantRules.Defaults, RuleRegistry.Article, "an article", "A-1", "A-2"));
        // R7: the rule of another master says nothing here.
        Assert.Null(MasterWriteChecks.CodeChange(off, RuleRegistry.Partner, "a partner", "A-1", "A-2"));

        var error = MasterWriteChecks.CodeChange(off, RuleRegistry.Article, "an article", "A-1", "a-1")!;
        Assert.Equal(ErrorCodes.CodeChangeNotAllowed, error.Code);
        Assert.Equal(["code"], error.Errors!.Keys);
        Assert.Equal(new RuleRefusal("article.codeChangeable", false, ["code"]), Assert.Single(error.Rules!), RefusalComparer.Instance);
    }

    [Fact]
    public void S2_Name_and_tax_id_refusals_name_the_rule_and_repeat_only_the_value_sent()
    {
        var name = AppError.NameTaken("partner", "Acme", RuleRegistry.Partner.NameUnique);
        Assert.Equal(ErrorCodes.NameTaken, name.Code);
        Assert.Equal(["name"], name.Errors!.Keys);
        Assert.Equal(new RuleRefusal("partner.nameUnique", true, ["name"]), Assert.Single(name.Rules!), RefusalComparer.Instance);

        var taxId = AppError.TaxIdTaken("HR123", RuleRegistry.PartnerTaxIdUnique);
        Assert.Equal(ErrorCodes.TaxIdTaken, taxId.Code);
        Assert.Equal(["taxId"], taxId.Errors!.Keys);
        Assert.Equal(new RuleRefusal("partner.taxIdUnique", true, ["taxId"]), Assert.Single(taxId.Rules!), RefusalComparer.Instance);
    }

    // ---- validation judged by a tenant's values (R5-R8, R17, R18)

    public static TheoryData<string> PartnerFields => [.. RuleRegistry.Partner.RequiredFields.Keys];

    public static TheoryData<string> WarehouseFields => [.. RuleRegistry.Warehouse.RequiredFields.Keys];

    [Theory]
    [MemberData(nameof(PartnerFields))]
    public void R5_A_partner_field_its_rule_requires_is_refused_when_omitted_null_or_blank_and_names_the_rule(string field)
    {
        var rule = RuleRegistry.Partner.RequiredFields[field];
        var rules = With((rule, true));
        var valid = field == "countryCode" ? "HR" : "x";

        AssertRefusedBy(PartnerValidation.Create(Partner(MinimalPartner()), rules).Error!, rule, true, field);
        foreach (var missing in new string?[] { null, "", "   " })
        {
            var create = MinimalPartner();
            create[field] = missing;
            AssertRefusedBy(PartnerValidation.Create(Partner(create), rules).Error!, rule, true, field);
            var replace = FullPartner();
            replace[field] = missing;
            AssertRefusedBy(PartnerValidation.Replace(Partner(replace), rules).Error!, rule, true, field);
        }

        var given = MinimalPartner();
        given[field] = valid;
        Assert.True(PartnerValidation.Create(Partner(given), rules).IsSuccess);
        // R6: with the rule false the field is optional, as before.
        Assert.True(PartnerValidation.Create(Partner(MinimalPartner()), With((rule, false))).IsSuccess);
        // R7: the same field's rule of a warehouse says nothing about a partner.
        if (RuleRegistry.Warehouse.RequiredFields.TryGetValue(field, out var other))
            Assert.True(PartnerValidation.Create(Partner(MinimalPartner()), With((other, true))).IsSuccess);
    }

    [Theory]
    [MemberData(nameof(WarehouseFields))]
    public void R5_A_warehouse_field_its_rule_requires_is_refused_and_names_the_rule(string field)
    {
        var rule = RuleRegistry.Warehouse.RequiredFields[field];
        var rules = With((rule, true));
        var minimal = new JsonObject { ["code"] = "W-1", ["name"] = "Main" };

        AssertRefusedBy(WarehouseValidation.Create(Warehouse(minimal), rules).Error!, rule, true, field);
        var given = new JsonObject { ["code"] = "W-1", ["name"] = "Main", [field] = field == "countryCode" ? "HR" : "x" };
        Assert.True(WarehouseValidation.Create(Warehouse(given), rules).IsSuccess);
        // R7: the partner's rule for the same field says nothing about a warehouse.
        Assert.True(WarehouseValidation.Create(Warehouse(minimal), With((RuleRegistry.Partner.RequiredFields[field], true))).IsSuccess);
        Assert.True(WarehouseValidation.Create(Warehouse(minimal), TenantRules.Defaults).IsSuccess);
    }

    [Fact]
    public void R5_An_article_description_is_required_by_its_rule()
    {
        var rule = RuleRegistry.Article.RequiredFields["description"];
        var rules = With((rule, true));
        var unit = Guid.CreateVersion7().ToString();

        foreach (var missing in new string?[] { null, "", "  " })
        {
            AssertRefusedBy(ArticleValidation.Create(new CreateArticleInput("A-1", "Bolt", "stock", unit, missing), rules).Error!, rule, true, "description");
            AssertRefusedBy(
                ArticleValidation.Replace(new ReplaceArticleInput("A-1", "Bolt", "stock", unit, true) { Description = missing }, rules).Error!,
                rule, true, "description");
        }
        Assert.True(ArticleValidation.Create(new CreateArticleInput("A-1", "Bolt", "stock", unit, "M8"), rules).IsSuccess);
        Assert.True(ArticleValidation.Create(new CreateArticleInput("A-1", "Bolt", "stock", unit), TenantRules.Defaults).IsSuccess);

        // R6: a replace body without the property is the form of the request - refused, and by no rule.
        var absent = ArticleValidation.Replace(new ReplaceArticleInput("A-1", "Bolt", "stock", unit, true), rules).Error!;
        Assert.Equal(["description"], absent.Errors!.Keys);
        Assert.Null(absent.Rules);
    }

    [Fact]
    public void R6_The_form_of_a_given_value_and_a_property_absent_from_a_replace_are_refused_by_no_rule()
    {
        var rules = With([.. RuleRegistry.Partner.RequiredFields.Values.Select(r => (r, true))]);

        var malformed = MinimalPartner();
        foreach (var field in RuleRegistry.Partner.RequiredFields.Keys)
            malformed[field] = "x";
        malformed["countryCode"] = "Croatia"; // E2
        var error = PartnerValidation.Create(Partner(malformed), rules).Error!;
        Assert.Equal(["countryCode"], error.Errors!.Keys);
        Assert.Null(error.Rules);

        var withoutCity = FullPartner();
        foreach (var field in RuleRegistry.Partner.RequiredFields.Keys)
            withoutCity[field] = field == "countryCode" ? "HR" : "x";
        withoutCity.Remove("city");
        error = PartnerValidation.Replace(Partner(withoutCity), rules).Error!;
        Assert.Equal(["city"], error.Errors!.Keys);
        Assert.Null(error.Rules);
    }

    [Fact]
    public void R18_Several_rules_are_named_together_each_with_its_own_fields_and_an_invariants_field_is_in_none()
    {
        // E3: tax id and city required, both missing, and an over-long name.
        var rules = With((RuleRegistry.Partner.RequiredFields["taxId"], true), (RuleRegistry.Partner.RequiredFields["city"], true));
        var body = MinimalPartner();
        body["name"] = new string('n', NameRules.MaxLength + 1);

        var error = PartnerValidation.Create(Partner(body), rules).Error!;

        Assert.Equal(["name", "taxId", "city"], error.Errors!.Keys);
        Assert.Equal(
            [new RuleRefusal("partner.taxIdRequired", true, ["taxId"]), new RuleRefusal("partner.cityRequired", true, ["city"])],
            error.Rules!, RefusalComparer.Instance);
    }

    [Fact]
    public void R8_The_role_rule_refuses_by_default_names_itself_and_is_lifted_by_false()
    {
        var body = MinimalPartner();
        body["isSupplier"] = false;

        AssertRefusedBy(PartnerValidation.Create(Partner(body), TenantRules.Defaults).Error!, RuleRegistry.PartnerRoleRequired, true, "isCustomer", "isSupplier");

        var off = With((RuleRegistry.PartnerRoleRequired, false));
        var created = PartnerValidation.Create(Partner(body), off);
        Assert.True(created.IsSuccess);
        Assert.False(created.Value.IsCustomer);
        Assert.False(created.Value.IsSupplier);
        var omitted = PartnerValidation.Create(Partner(new JsonObject { ["code"] = "P-1", ["name"] = "Acme" }), off);
        Assert.True(omitted.IsSuccess);

        // A wrong-typed role field is the form of the request: reported alone, without rules, whatever the value.
        body["isCustomer"] = null;
        foreach (var rules in new[] { TenantRules.Defaults, off })
        {
            var error = PartnerValidation.Create(Partner(body), rules).Error!;
            Assert.Equal(["isCustomer"], error.Errors!.Keys);
            Assert.Null(error.Rules);
        }
    }

    [Fact]
    public void R18_The_role_rule_and_a_required_field_are_two_elements_in_the_order_of_their_first_field()
    {
        var rules = With((RuleRegistry.Partner.RequiredFields["city"], true));
        var body = new JsonObject { ["code"] = "P-1", ["name"] = "Acme" };

        var error = PartnerValidation.Create(Partner(body), rules).Error!;

        Assert.Equal(["isCustomer", "isSupplier", "city"], error.Errors!.Keys);
        Assert.Equal(
            [new RuleRefusal("partner.roleRequired", true, ["isCustomer", "isSupplier"]), new RuleRefusal("partner.cityRequired", true, ["city"])],
            error.Rules!, RefusalComparer.Instance);
    }

    /// <summary>A record with a list compares by reference; a refusal is equal when its key, value and fields are.</summary>
    private sealed class RefusalComparer : IEqualityComparer<RuleRefusal>
    {
        public static readonly RefusalComparer Instance = new();

        public bool Equals(RuleRefusal? x, RuleRefusal? y) =>
            x is not null && y is not null && x.Key == y.Key && x.Value == y.Value && x.Fields.SequenceEqual(y.Fields);

        public int GetHashCode(RuleRefusal obj) => obj.Key.GetHashCode();
    }
}
