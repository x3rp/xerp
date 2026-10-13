using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xerp.IntegrationTests.Support;

namespace Xerp.IntegrationTests;

/// <summary>
/// Spec 013, AC-50 to AC-53 (<c>&lt;m&gt;.codeChangeable</c>, R9–R11), AC-60 to AC-64 (<c>&lt;m&gt;.nameUnique</c>,
/// R12–R14, R16), AC-70 and AC-71 (<c>partner.taxIdUnique</c>, R15, S2), AC-80 and AC-81 (nothing stored is
/// touched; read-your-writes). The four masters are one theory over the resource.
/// </summary>
[Collection(XerpCollection.Name)]
public class MasterRuleCodeAndUniquenessTests(XerpFixture app)
{
    private const string CodeChange = "CODE_CHANGE_NOT_ALLOWED";
    private const string NameTaken = "NAME_TAKEN";
    private const string TaxIdTaken = "TAX_ID_TAKEN";
    private const string PartnerNameUnique = "partner.nameUnique";

    private static readonly MasterApi P = MasterApi.Partners;

    private async Task<(HttpClient Http, RuledMaster M)> OpenAsync(string group)
    {
        var tenant = await app.NewTenantAsync();
        return (tenant.Client, await RuledMaster.OpenAsync(group, tenant.Client));
    }

    // ---- AC-50 ----

    [Theory]
    [MemberData(nameof(MasterRules.GroupData), MemberType = typeof(MasterRules))]
    public async Task AC50_With_codeChangeable_false_a_replace_that_changes_the_code_is_refused(string group)
    {
        var (http, m) = await OpenAsync(group);
        await Rules.SetAsync(http, m.CodeChangeable, false);
        var created = await m.CreateAsync(http, m.Minimal("AB1", "First name"));

        // The stored code and another name: 200.
        var renamed = await m.ReplaceAsync(http, created.Id(), m.BodyOf(created).With("name", "Second name"));
        Assert.Equal(("AB1", "Second name"), (renamed.Str("code"), renamed.Str("name")));
        // The stored code with spaces around it is the stored code (R9: exact after trimming).
        await m.ReplaceAsync(http, created.Id(), m.BodyOf(renamed).With("code", " AB1 "));
        var before = await m.GetAsync(http, created.Id());

        var refused = await MasterRules.ConflictAsync(
            m.PutAsync(http, created.Id(), m.BodyOf(before).With("code", "AB2").With("name", "Third name")), CodeChange, m.CodeChangeable, false, "code");
        Assert.Equal(409, refused.GetProperty("status").GetInt32());
        await m.AssertUnchangedAsync(http, before);
        // A change of letter case only is a change.
        await MasterRules.ConflictAsync(m.PutAsync(http, created.Id(), m.BodyOf(before).With("code", "ab1")), CodeChange, m.CodeChangeable, false, "code");
        await m.AssertUnchangedAsync(http, before);
        using (var gone = await m.ByCodeAsync(http, "AB2"))
            await HttpAssert.NotFoundAsync(gone);

        // Create is never judged.
        Assert.Equal("AB3", (await m.CreateAsync(http, m.Minimal("AB3", "Another"))).Str("code"));
    }

    // ---- AC-51 ----

    [Theory]
    [MemberData(nameof(MasterRules.GroupData), MemberType = typeof(MasterRules))]
    public async Task AC51_E4_E10_R10_Validation_then_404_then_the_code_change_then_CODE_TAKEN(string group)
    {
        var (http, m) = await OpenAsync(group);
        await Rules.SetAsync(http, m.CodeChangeable, false);
        var record = await m.CreateAsync(http, m.Minimal("AB1", "First"));
        await m.CreateAsync(http, m.Minimal("AB2", "Second"));

        // Changed to another record's code: the rule answers, not CODE_TAKEN.
        await MasterRules.ConflictAsync(m.PutAsync(http, record.Id(), m.BodyOf(record).With("code", "AB2")), CodeChange, m.CodeChangeable, false, "code");
        // Changed and malformed: validation first, and no rule refused.
        await MasterRules.InvariantAsync(m.PutAsync(http, record.Id(), m.BodyOf(record).With("code", "no good!")), "code");
        // An id that does not exist.
        using (var unknown = await m.PutAsync(http, Guid.CreateVersion7(), m.BodyOf(record).With("code", "AB9")))
            Rules.AssertNamesNoRule(await HttpAssert.ProblemAsync(unknown, HttpStatusCode.NotFound, "NOT_FOUND"));
        await m.AssertUnchangedAsync(http, record);
    }

    // ---- AC-52 ----

    [Fact]
    public async Task AC52_The_four_codeChangeable_rules_are_separate()
    {
        var tenant = await app.NewTenantAsync();
        var http = tenant.Client;
        await Rules.SetAsync(http, MasterRules.CodeChangeable("article"), false);

        foreach (var group in new[] { "partner", "uom", "warehouse" })
        {
            var m = await RuledMaster.OpenAsync(group, http);
            var record = await m.CreateAsync(http, m.Minimal("AB1", "Record"));
            Assert.Equal("AB2", (await m.ReplaceAsync(http, record.Id(), m.BodyOf(record).With("code", "AB2"))).Str("code"));
        }
        var articles = await RuledMaster.OpenAsync("article", http);
        var article = await articles.CreateAsync(http, articles.Minimal("AB1", "Article"));
        await MasterRules.ConflictAsync(articles.PutAsync(http, article.Id(), articles.BodyOf(article).With("code", "AB2")),
            CodeChange, articles.CodeChangeable, false, "code");
    }

    // ---- AC-53 ----

    [Theory]
    [MemberData(nameof(MasterRules.GroupData), MemberType = typeof(MasterRules))]
    public async Task AC53_After_reset_the_code_is_changed_and_found_under_the_new_code(string group)
    {
        var (http, m) = await OpenAsync(group);
        await Rules.SetAsync(http, m.CodeChangeable, false);
        var record = await m.CreateAsync(http, m.Minimal("AB1", "Record"));
        await MasterRules.ConflictAsync(m.PutAsync(http, record.Id(), m.BodyOf(record).With("code", "AB2")), CodeChange, m.CodeChangeable, false, "code");

        await Rules.ResetAsync(http, m.CodeChangeable);

        Assert.Equal("AB2", (await m.ReplaceAsync(http, record.Id(), m.BodyOf(record).With("code", "AB2"))).Str("code"));
        using var found = await m.ByCodeAsync(http, "AB2");
        Assert.Equal(record.Id(), (await HttpAssert.JsonAsync(found, HttpStatusCode.OK)).Id());
        using var old = await m.ByCodeAsync(http, "AB1");
        await HttpAssert.NotFoundAsync(old);
    }

    // ---- AC-60 ----

    [Theory]
    [MemberData(nameof(MasterRules.GroupData), MemberType = typeof(MasterRules))]
    public async Task AC60_With_nameUnique_true_a_name_another_record_has_is_refused(string group)
    {
        var (http, m) = await OpenAsync(group);
        await Rules.SetAsync(http, m.NameUnique, true);
        var acme = await m.CreateAsync(http, m.Minimal("N1", "Acme"));
        var before = await m.TotalAsync(http);

        // Compared like codes: trimmed, case-insensitively.
        foreach (var name in new[] { "Acme", "acme", " ACME " })
        {
            var refused = await MasterRules.ConflictAsync(m.PostAsync(http, m.Minimal("N2", name)), NameTaken, m.NameUnique, true, "name");
            Assert.Equal(409, refused.GetProperty("status").GetInt32());
        }
        Assert.Equal(before, await m.TotalAsync(http));
        using (var notCreated = await m.ByCodeAsync(http, "N2"))
            await HttpAssert.NotFoundAsync(notCreated);

        var second = await m.CreateAsync(http, m.Minimal("N2", "Acme 2"));
        await MasterRules.ConflictAsync(m.PutAsync(http, second.Id(), m.BodyOf(second).With("name", "Acme")), NameTaken, m.NameUnique, true, "name");
        await MasterRules.ConflictAsync(m.PutAsync(http, second.Id(), m.BodyOf(second).With("name", "ACME ")), NameTaken, m.NameUnique, true, "name");
        await m.AssertUnchangedAsync(http, second);

        // A record never collides with itself (R14).
        await m.ReplaceAsync(http, acme.Id(), m.BodyOf(acme));
        Assert.Equal("ACME", (await m.ReplaceAsync(http, acme.Id(), m.BodyOf(acme).With("name", "ACME"))).Str("name"));
        Assert.Equal("Acme 2", (await m.ReplaceAsync(http, second.Id(), m.BodyOf(second))).Str("name"));
    }

    // ---- AC-61 ----

    [Fact]
    public async Task AC61_E5_An_inactive_record_counts_and_the_four_resources_are_four_name_spaces()
    {
        var tenant = await app.NewTenantAsync();
        var http = tenant.Client;
        foreach (var group in MasterRules.Groups)
            await Rules.SetAsync(http, MasterRules.NameUnique(group), true);
        var inactive = await P.CreateAsync(http, Orders.NewPartner("P1", "Acme", isSupplier: true, isCustomer: false, isActive: false));
        Assert.False(inactive.Bool("isActive"));

        await MasterRules.ConflictAsync(P.PostAsync(http, P.Minimal("P2", "acme")), NameTaken, PartnerNameUnique, true, "name");

        // The same name in the other three resources: each is its own name space, with its own rule true.
        foreach (var group in new[] { "uom", "article", "warehouse" })
        {
            var m = await RuledMaster.OpenAsync(group, http);
            Assert.Equal("Acme", (await m.CreateAsync(http, m.Minimal("X1", "Acme"))).Str("name"));
            await MasterRules.ConflictAsync(m.PostAsync(http, m.Minimal("X2", "ACME")), NameTaken, m.NameUnique, true, "name");
        }
    }

    // ---- AC-62 ----

    [Fact]
    public async Task AC62_E6_R14_Existing_duplicates_stay_and_each_is_judged_at_its_own_next_replace()
    {
        var tenant = await app.NewTenantAsync();
        var http = tenant.Client;
        var first = await P.CreateAsync(http, "P1", "Acme");
        var second = await P.CreateAsync(http, "P2", "Acme");
        var listBefore = await P.ListAsync(http);

        await Rules.SetAsync(http, PartnerNameUnique, true);

        // Both are read and listed unchanged.
        await P.AssertUnchangedAsync(http, first);
        await P.AssertUnchangedAsync(http, second);
        McpAssert.JsonEqual(listBefore, await P.ListAsync(http), "Setting the rule changed the list of partners");

        // Neither can be replaced until one is renamed - also when the name is not being changed.
        await MasterRules.ConflictAsync(P.PutAsync(http, first.Id(), Orders.PartnerBody(first)), NameTaken, PartnerNameUnique, true, "name");
        await MasterRules.ConflictAsync(P.PutAsync(http, first.Id(), Orders.PartnerBody(first).With("city", "Split")), NameTaken, PartnerNameUnique, true, "name");
        await P.AssertUnchangedAsync(http, first);
        Assert.Equal("Acme d.o.o.", (await P.ReplaceAsync(http, first.Id(), Orders.PartnerBody(first).With("name", "Acme d.o.o."))).Str("name"));
        Assert.Equal("Acme", (await P.ReplaceAsync(http, second.Id(), Orders.PartnerBody(second))).Str("name"));
    }

    // ---- AC-63 ----

    [Fact]
    public async Task AC63_R16_CODE_TAKEN_then_NAME_TAKEN_then_TAX_ID_TAKEN_each_alone()
    {
        var tenant = await app.NewTenantAsync();
        var http = tenant.Client;
        await Rules.SetAsync(http, PartnerNameUnique, true);
        await Rules.SetAsync(http, MasterRules.TaxIdUnique, true);
        var acme = await P.CreateAsync(http, P.Minimal("P1", "Acme").With("taxId", "HR123"));
        var other = await P.CreateAsync(http, P.Minimal("P2", "Other").With("taxId", "HR456"));

        // Code and name both taken: CODE_TAKEN, an invariant.
        using (var codeFirst = await P.PostAsync(http, P.Minimal("p1", "Acme").With("taxId", "HR123")))
            Rules.AssertNamesNoRule(await HttpAssert.ProblemAsync(codeFirst, HttpStatusCode.Conflict, "CODE_TAKEN"));
        // Name and tax id both taken: NAME_TAKEN, naming only its own rule and key.
        await MasterRules.ConflictAsync(P.PostAsync(http, P.Minimal("P3", "Acme").With("taxId", "HR123")), NameTaken, PartnerNameUnique, true, "name");
        // The same order on replace.
        using (var codeFirst = await P.PutAsync(http, other.Id(), Orders.PartnerBody(other).With("code", "P1").With("name", "Acme").With("taxId", "HR123")))
            Rules.AssertNamesNoRule(await HttpAssert.ProblemAsync(codeFirst, HttpStatusCode.Conflict, "CODE_TAKEN"));
        await MasterRules.ConflictAsync(P.PutAsync(http, other.Id(), Orders.PartnerBody(other).With("name", "Acme").With("taxId", "HR123")),
            NameTaken, PartnerNameUnique, true, "name");
        await MasterRules.ConflictAsync(P.PutAsync(http, other.Id(), Orders.PartnerBody(other).With("taxId", "HR123")),
            TaxIdTaken, MasterRules.TaxIdUnique, true, "taxId");
        await P.AssertUnchangedAsync(http, other);
        await P.AssertUnchangedAsync(http, acme);

        // With codeChangeable false as well, the code change stands before CODE_TAKEN (R10).
        await Rules.SetAsync(http, MasterRules.CodeChangeable("partner"), false);
        await MasterRules.ConflictAsync(
            P.PutAsync(http, other.Id(), Orders.PartnerBody(other).With("code", "P1").With("name", "Acme").With("taxId", "HR123")),
            CodeChange, MasterRules.CodeChangeable("partner"), false, "code");
        Assert.Equal(2, (await P.ListAsync(http)).Total());
    }

    // ---- AC-64 ----

    [Fact]
    public async Task AC64_R13_Of_ten_parallel_creates_with_the_same_name_exactly_one_succeeds()
    {
        var tenant = await app.NewTenantAsync();
        var http = tenant.Client;
        await Rules.SetAsync(http, PartnerNameUnique, true);

        var responses = await Task.WhenAll(Enumerable.Range(1, 10).Select(i => P.PostAsync(http, P.Minimal($"P{i}", "Acme parallel"))));

        try
        {
            Assert.DoesNotContain(responses, r => (int)r.StatusCode >= 500);
            Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Created);
            foreach (var refused in responses.Where(r => r.StatusCode != HttpStatusCode.Created))
                await Rules.ConflictAsync(refused, NameTaken, PartnerNameUnique, true, "name");
        }
        finally
        {
            foreach (var response in responses)
                response.Dispose();
        }
        var list = await P.ListAsync(http, "?limit=500");
        Assert.Equal(1, list.Total());
        Assert.Equal("Acme parallel", Assert.Single(list.Items()).Str("name"));
    }

    [Fact]
    public async Task AC64_R13_Parallel_replaces_that_give_the_same_name_to_different_records()
    {
        var tenant = await app.NewTenantAsync();
        var http = tenant.Client;
        await Rules.SetAsync(http, PartnerNameUnique, true);
        var partners = new List<JsonElement>();
        for (var i = 1; i <= 6; i++)
            partners.Add(await P.CreateAsync(http, $"P{i}", $"Partner {i}"));

        var responses = await Task.WhenAll(partners.Select(p => P.PutAsync(http, p.Id(), Orders.PartnerBody(p).With("name", "Acme parallel"))));

        try
        {
            Assert.DoesNotContain(responses, r => (int)r.StatusCode >= 500);
            Assert.Single(responses, r => r.StatusCode == HttpStatusCode.OK);
            foreach (var refused in responses.Where(r => r.StatusCode != HttpStatusCode.OK))
                await Rules.ConflictAsync(refused, NameTaken, PartnerNameUnique, true, "name");
        }
        finally
        {
            foreach (var response in responses)
                response.Dispose();
        }
        Assert.Single((await P.ListAsync(http, "?limit=500")).Items(), p => p.Str("name") == "Acme parallel");
    }

    // ---- AC-70 ----

    [Fact]
    public async Task AC70_E7_With_taxIdUnique_true_a_tax_id_another_partner_has_is_refused_and_null_never_collides()
    {
        var tenant = await app.NewTenantAsync();
        var http = tenant.Client;
        await Rules.SetAsync(http, MasterRules.TaxIdUnique, true);
        var first = await P.CreateAsync(http, P.Minimal("P1", "First").With("taxId", "HR123"));

        foreach (var taxId in new[] { "HR123", "hr123", " HR123 " })
        {
            var refused = await MasterRules.ConflictAsync(P.PostAsync(http, P.Minimal("P2", "Second").With("taxId", taxId)),
                TaxIdTaken, MasterRules.TaxIdUnique, true, "taxId");
            Assert.Equal(409, refused.GetProperty("status").GetInt32());
        }
        Assert.Equal(1, (await P.ListAsync(http)).Total());

        var second = await P.CreateAsync(http, P.Minimal("P2", "Second").With("taxId", "HR124"));
        // Partners without a tax id never collide: omitted, null and empty are all "none".
        JsonBody.AssertNull(await P.CreateAsync(http, P.Minimal("P3", "Third")), "taxId");
        JsonBody.AssertNull(await P.CreateAsync(http, P.Minimal("P4", "Fourth").With("taxId", null)), "taxId");
        var fifth = await P.CreateAsync(http, P.Minimal("P5", "Fifth").With("taxId", "  "));
        JsonBody.AssertNull(fifth, "taxId");
        await P.ReplaceAsync(http, fifth.Id(), Orders.PartnerBody(fifth));

        // A record never collides with itself; a replace that takes another's tax id is refused.
        Assert.Equal("HR123", (await P.ReplaceAsync(http, first.Id(), Orders.PartnerBody(first))).Str("taxId"));
        await MasterRules.ConflictAsync(P.PutAsync(http, second.Id(), Orders.PartnerBody(second).With("taxId", "hr123")),
            TaxIdTaken, MasterRules.TaxIdUnique, true, "taxId");
        await P.AssertUnchangedAsync(http, second);

        // The tax id of an inactive partner counts (E7).
        var inactive = await P.CreateAsync(http, Orders.NewPartner("P6", "Inactive", isSupplier: true, isCustomer: false, isActive: false).With("taxId", "HR777"));
        Assert.False(inactive.Bool("isActive"));
        await MasterRules.ConflictAsync(P.PostAsync(http, P.Minimal("P7", "Seventh").With("taxId", "HR777")),
            TaxIdTaken, MasterRules.TaxIdUnique, true, "taxId");
    }

    [Fact]
    public async Task AC70_With_taxIdUnique_at_its_default_and_nameUnique_true_tax_ids_may_repeat()
    {
        var tenant = await app.NewTenantAsync();
        var http = tenant.Client;
        await Rules.SetAsync(http, PartnerNameUnique, true);

        await P.CreateAsync(http, P.Minimal("P1", "First").With("taxId", "HR123"));
        await P.CreateAsync(http, P.Minimal("P2", "Second").With("taxId", "HR123"));

        Assert.Equal(2, (await P.ListAsync(http)).Total());
    }

    // ---- AC-71 ----

    [Fact]
    public async Task AC71_S2_NAME_TAKEN_and_TAX_ID_TAKEN_name_no_other_record()
    {
        var tenant = await app.NewTenantAsync();
        var http = tenant.Client;
        await Rules.SetAsync(http, PartnerNameUnique, true);
        await Rules.SetAsync(http, MasterRules.TaxIdUnique, true);
        var holder = await P.CreateAsync(http, P.Minimal("HOLDER-77", "Acme").With("taxId", "HR123"));

        var nameTaken = await MasterRules.ConflictAsync(P.PostAsync(http, P.Minimal("NEW-1", "Acme")), NameTaken, PartnerNameUnique, true, "name");
        var taxIdTaken = await MasterRules.ConflictAsync(P.PostAsync(http, P.Minimal("NEW-1", "Other").With("taxId", "HR123")),
            TaxIdTaken, MasterRules.TaxIdUnique, true, "taxId");

        foreach (var problem in new[] { nameTaken, taxIdTaken })
        {
            Assert.DoesNotContain(holder.Id().ToString(), problem.GetRawText(), StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("HOLDER-77", problem.GetRawText(), StringComparison.OrdinalIgnoreCase);
        }
    }

    // ---- AC-80 ----

    [Theory]
    [InlineData("partner", "partner.cityRequired", true)]
    [InlineData("article", "article.descriptionRequired", true)]
    [InlineData("partner", "partner.roleRequired", false)]
    [InlineData("uom", "uom.codeChangeable", false)]
    [InlineData("warehouse", "warehouse.nameUnique", true)]
    [InlineData("partner", "partner.taxIdUnique", true)]
    public async Task AC80_A_set_touches_nothing_stored(string group, string rule, bool value)
    {
        var (http, m) = await OpenAsync(group);
        // Records the new value would refuse: no optional field, the same name, the same tax id.
        var one = m.Minimal("S1", "Same");
        var two = m.Minimal("S2", "Same");
        if (group == "partner")
        {
            one["taxId"] = "HR123";
            two["taxId"] = "HR123";
        }
        await m.CreateAsync(http, one);
        await m.CreateAsync(http, two);
        var before = await m.ListAsync(http);

        await Rules.SetAsync(http, rule, value);

        McpAssert.JsonEqual(before, await m.ListAsync(http), $"Set({rule}, {value}) changed the list of {group}");
        foreach (var item in before.Items())
            await m.AssertUnchangedAsync(http, item);
    }

    // ---- AC-81 ----

    [Fact]
    public async Task AC81_A_rule_change_holds_from_the_next_operation()
    {
        var (http, m) = await OpenAsync("article");

        for (var i = 1; i <= 10; i++)
        {
            await Rules.SetAsync(http, MasterRules.DescriptionRequired, true);
            await MasterRules.RequiredAsync(m.PostAsync(http, m.Minimal($"A{i}", $"Article {i}")), MasterRules.DescriptionRequired, "description");
            await Rules.SetAsync(http, MasterRules.DescriptionRequired, false);
            JsonBody.AssertNull(await m.CreateAsync(http, m.Minimal($"A{i}", $"Article {i}")), "description");
        }

        Assert.Equal(10, await m.TotalAsync(http));
    }
}
