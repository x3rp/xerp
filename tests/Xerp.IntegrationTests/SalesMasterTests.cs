using System.Net;
using System.Text.Json;
using Xerp.IntegrationTests.Support;

namespace Xerp.IntegrationTests;

/// <summary>
/// Spec P01, part A, AC-20 to AC-29: article categories, tax rates and payment methods are masters as a unit of
/// measure (R1–R5), with <c>&lt;m&gt;.codeChangeable</c> and <c>&lt;m&gt;.nameUnique</c> as spec 013 describes (R6).
/// One theory over the three resources (section 11).
/// </summary>
[Collection(XerpCollection.Name)]
public class SalesMasterTests(XerpFixture app)
{
    private static readonly SalesMaster Tax = SalesMaster.TaxRates;
    private static readonly SalesMaster Pay = SalesMaster.PaymentMethods;

    public static IEnumerable<object[]> Masters() => SalesRules.GroupData();

    private async Task<(TestTenant Tenant, HttpClient Http, SalesMaster M)> OpenAsync(string group)
    {
        var tenant = await app.NewTenantAsync();
        return (tenant, tenant.Client, SalesMaster.Of(group));
    }

    // ---- AC-20 ----

    [Theory]
    [MemberData(nameof(Masters))]
    public async Task AC20_Create_returns_the_record_and_it_is_read_by_id_by_code_and_in_the_list(string group)
    {
        var (tenant, http, m) = await OpenAsync(group);

        using var response = await m.PostAsync(http, m.Body("DRINKS", "Drinks"));
        var created = await HttpAssert.JsonAsync(response, HttpStatusCode.Created);

        Assert.EndsWith($"{m.Path}/{created.Id()}", response.Headers.Location?.ToString());
        Assert.Equal(("DRINKS", "Drinks", true), (created.Str("code"), created.Str("name"), created.Bool("isActive")));
        if (m.Extra == "rate")
        {
            Assert.Equal(JsonValueKind.Number, created.GetProperty("rate").ValueKind);
            Assert.Equal(25m, created.GetProperty("rate").GetDecimal());
        }
        if (m.Extra == "kind")
            Assert.Equal("cash", created.Str("kind"));
        // Audit members: the actors are the key's id as a string (architecture §8).
        Assert.Equal(JsonValueKind.String, created.GetProperty("createdAt").ValueKind);
        Assert.Equal(JsonValueKind.String, created.GetProperty("updatedAt").ValueKind);
        Assert.Equal(tenant.ApiKeyId, created.GetProperty("createdBy").GetGuid());
        Assert.Equal(tenant.ApiKeyId, created.GetProperty("updatedBy").GetGuid());

        McpAssert.JsonEqual(created, await m.GetAsync(http, created.Id()));
        using var byCode = await m.ByCodeAsync(http, "drinks");
        McpAssert.JsonEqual(created, await HttpAssert.JsonAsync(byCode, HttpStatusCode.OK));
        var list = await m.ListAsync(http, "");
        Assert.Equal(new[] { "items", "limit", "offset", "total" }, list.PropertyNames());
        McpAssert.JsonEqual(created, Assert.Single(list.Items()));
    }

    // ---- AC-21 ----

    [Theory]
    [MemberData(nameof(Masters))]
    public async Task AC21_Validation_as_a_unit_of_measure_and_CODE_TAKEN_in_another_case(string group)
    {
        var (_, http, m) = await OpenAsync(group);

        await MasterRules.InvariantAsync(m.PostAsync(http, m.Body("X1", "Name").Without("code")), "code");
        await MasterRules.InvariantAsync(m.PostAsync(http, m.Body("a b", "Name")), "code");
        await MasterRules.InvariantAsync(m.PostAsync(http, m.Body("X1", "Name").Without("name")), "name");
        await MasterRules.InvariantAsync(m.PostAsync(http, m.Body("X1", new string('n', 201))), "name");
        await MasterRules.InvariantAsync(m.PostAsync(http, m.Body("X1", "Name").With("foo", 1)), "foo");
        Assert.Equal(0, (await m.ListAsync(http)).Total());

        await m.CreateAsync(http, "Abc", "First");
        using var sameCode = await m.PostAsync(http, m.Body("aBC", "Second"));
        await HttpAssert.CodeTakenAsync(sameCode);
        Assert.Equal(1, (await m.ListAsync(http)).Total());
    }

    // ---- AC-22 ----

    [Theory]
    [MemberData(nameof(Masters))]
    public async Task AC22_Replace_with_every_field_and_the_list_filters(string group)
    {
        var (_, http, m) = await OpenAsync(group);
        var first = await m.CreateAsync(http, "FOOD", "Meals");
        await m.CreateAsync(http, "DRINKS", "Beverages");
        await Task.Delay(20);

        var replaced = await m.ReplaceAsync(http, first.Id(), m.BodyOf(first).With("name", "Warm meals").With("isActive", false));

        Assert.Equal(("FOOD", "Warm meals", false), (replaced.Str("code"), replaced.Str("name"), replaced.Bool("isActive")));
        Assert.True(replaced.Time("updatedAt") > first.Time("updatedAt"), "updatedAt is not later after a replace.");
        Assert.Equal(first.Time("createdAt"), replaced.Time("createdAt"));
        await MasterRules.InvariantAsync(m.PutAsync(http, first.Id(), m.BodyOf(replaced).Without("isActive")), "isActive");
        using (var unknown = await m.PutAsync(http, Guid.CreateVersion7(), m.BodyOf(replaced)))
            await HttpAssert.NotFoundAsync(unknown);
        await m.AssertUnchangedAsync(http, replaced);

        // Ordered by code; search over part of the code and of the name; isActive.
        Assert.Equal(new[] { "DRINKS", "FOOD" }, (await m.ListAsync(http)).Codes());
        Assert.Equal(new[] { "DRINKS" }, (await m.ListAsync(http, "?search=rink")).Codes());
        Assert.Equal(new[] { "FOOD" }, (await m.ListAsync(http, "?search=warm")).Codes());
        Assert.Equal(new[] { "FOOD" }, (await m.ListAsync(http, "?isActive=false")).Codes());
        Assert.Equal(new[] { "DRINKS" }, (await m.ListAsync(http, "?isActive=true")).Codes());
    }

    // ---- AC-23 ----

    [Theory]
    [MemberData(nameof(Masters))]
    public async Task AC23_Delete_then_not_found(string group)
    {
        var (_, http, m) = await OpenAsync(group);
        var record = await m.CreateAsync(http, "GONE", "Gone");

        await m.RemoveAsync(http, record.Id());

        using var read = await m.SendGetAsync(http, record.Id());
        await HttpAssert.NotFoundAsync(read);
        using var again = await m.DeleteAsync(http, record.Id());
        await HttpAssert.NotFoundAsync(again);
        Assert.Equal(0, (await m.ListAsync(http)).Total());
    }

    // ---- AC-24 ----

    [Fact]
    public async Task AC24_E1_A_rate_from_0_to_100_with_four_decimals_is_accepted_and_read_back_equal()
    {
        var http = (await app.NewTenantAsync()).Client;

        var n = 0;
        foreach (var rate in new[] { "0", "5", "13", "25", "100", "12.3456", "25.0000" })
        {
            var body = $$"""{ "code": "R{{++n}}", "name": "Rate {{rate}}", "rate": {{rate}} }""";
            using var response = await http.PostAsync(Tax.Path, HttpAssert.Raw(body));
            var created = await HttpAssert.JsonAsync(response, HttpStatusCode.Created);
            Assert.Equal(JsonValueKind.Number, created.GetProperty("rate").ValueKind);
            Assert.Equal(decimal.Parse(rate, System.Globalization.CultureInfo.InvariantCulture), created.GetProperty("rate").GetDecimal());
            Assert.Equal(created.GetProperty("rate").GetDecimal(), (await Tax.GetAsync(http, created.Id())).GetProperty("rate").GetDecimal());
        }
    }

    [Theory]
    [InlineData("""{ "code": "BAD", "name": "Bad" }""")]
    [InlineData("""{ "code": "BAD", "name": "Bad", "rate": null }""")]
    [InlineData("""{ "code": "BAD", "name": "Bad", "rate": -1 }""")]
    [InlineData("""{ "code": "BAD", "name": "Bad", "rate": 100.01 }""")]
    [InlineData("""{ "code": "BAD", "name": "Bad", "rate": 12.34567 }""")]
    [InlineData("""{ "code": "BAD", "name": "Bad", "rate": "25" }""")]
    public async Task AC24_E1_A_missing_or_invalid_rate_is_refused_with_the_key_rate(string json)
    {
        var http = (await app.NewTenantAsync()).Client;

        await MasterRules.InvariantAsync(http.PostAsync(Tax.Path, HttpAssert.Raw(json)), "rate");

        Assert.Equal(0, (await Tax.ListAsync(http)).Total());
    }

    [Fact]
    public async Task AC24_R2_R4_A_replace_changes_the_rate_and_needs_it()
    {
        var http = (await app.NewTenantAsync()).Client;
        var rate = await Tax.CreateAsync(http, "VAT", "VAT");

        var replaced = await Tax.ReplaceAsync(http, rate.Id(), Tax.BodyOf(rate).With("rate", 13));

        Assert.Equal(13m, replaced.GetProperty("rate").GetDecimal());
        await MasterRules.InvariantAsync(Tax.PutAsync(http, rate.Id(), Tax.BodyOf(replaced).Without("rate")), "rate");
        await Tax.AssertUnchangedAsync(http, replaced);
    }

    // ---- AC-25 ----

    [Fact]
    public async Task AC25_Each_of_the_four_kinds_is_accepted_and_the_list_filters_by_kind()
    {
        var http = (await app.NewTenantAsync()).Client;

        foreach (var kind in new[] { "cash", "card", "transfer", "other" })
            Assert.Equal(kind, (await Pay.CreateAsync(http, Pay.Body($"PM-{kind}", $"By {kind}").With("kind", kind))).Str("kind"));
        var second = await Pay.CreateAsync(http, Pay.Body("AMEX", "Amex").With("kind", "card"));

        var cards = await Pay.ListAsync(http, "?kind=card");
        Assert.Equal(new[] { "AMEX", "PM-card" }, cards.Codes());
        Assert.All(cards.Items(), i => Assert.Equal("card", i.Str("kind")));
        Assert.Equal(5, (await Pay.ListAsync(http)).Total());
        using var unknownKind = await http.GetAsync($"{Pay.Path}?kind=x");
        await HttpAssert.ValidationAsync(unknownKind, "kind");

        // R4: the kind may be changed by a replace; R3: it is required there too.
        Assert.Equal("other", (await Pay.ReplaceAsync(http, second.Id(), Pay.BodyOf(second).With("kind", "other"))).Str("kind"));
        await MasterRules.InvariantAsync(Pay.PutAsync(http, second.Id(), Pay.BodyOf(second).Without("kind")), "kind");
    }

    [Theory]
    [InlineData("""{ "code": "BAD", "name": "Bad" }""")]
    [InlineData("""{ "code": "BAD", "name": "Bad", "kind": null }""")]
    [InlineData("""{ "code": "BAD", "name": "Bad", "kind": "Cash" }""")]
    [InlineData("""{ "code": "BAD", "name": "Bad", "kind": "cheque" }""")]
    public async Task AC25_A_missing_or_unknown_kind_is_refused_with_the_key_kind(string json)
    {
        var http = (await app.NewTenantAsync()).Client;

        await MasterRules.InvariantAsync(http.PostAsync(Pay.Path, HttpAssert.Raw(json)), "kind");

        Assert.Equal(0, (await Pay.ListAsync(http)).Total());
    }

    // ---- AC-26 ----

    [Theory]
    [MemberData(nameof(Masters))]
    public async Task AC26_By_default_a_code_may_be_changed_and_two_records_share_a_name(string group)
    {
        var (_, http, m) = await OpenAsync(group);
        var first = await m.CreateAsync(http, "C1", "Same name");

        var changed = await m.ReplaceAsync(http, first.Id(), m.BodyOf(first).With("code", "C2"));
        var second = await m.CreateAsync(http, "C3", "Same name");

        Assert.Equal("C2", changed.Str("code"));
        using var byOldCode = await m.ByCodeAsync(http, "C1");
        await HttpAssert.NotFoundAsync(byOldCode);
        Assert.Equal(("Same name", "Same name"), (changed.Str("name"), second.Str("name")));
        Assert.Equal(2, (await m.ListAsync(http)).Total());
    }

    // ---- AC-27 ----

    [Theory]
    [MemberData(nameof(Masters))]
    public async Task AC27_With_codeChangeable_false_a_replace_that_changes_the_code_is_refused(string group)
    {
        var (_, http, m) = await OpenAsync(group);
        var record = await m.CreateAsync(http, "C1", "First");
        await m.CreateAsync(http, "C2", "Second");
        await Rules.SetAsync(http, m.CodeChangeable, false);

        // The stored code: the rest of the record may still change.
        var kept = await m.ReplaceAsync(http, record.Id(), m.BodyOf(record).With("name", "Renamed"));
        Assert.Equal(("C1", "Renamed"), (kept.Str("code"), kept.Str("name")));

        await MasterRules.ConflictAsync(m.PutAsync(http, record.Id(), m.BodyOf(kept).With("code", "C9")),
            "CODE_CHANGE_NOT_ALLOWED", m.CodeChangeable, false, "code");
        // Another record's code: the change is refused before the code is looked up (CODE_CHANGE_NOT_ALLOWED, not CODE_TAKEN).
        await MasterRules.ConflictAsync(m.PutAsync(http, record.Id(), m.BodyOf(kept).With("code", "C2")),
            "CODE_CHANGE_NOT_ALLOWED", m.CodeChangeable, false, "code");
        await m.AssertUnchangedAsync(http, kept);
    }

    // ---- AC-28 ----

    [Theory]
    [MemberData(nameof(Masters))]
    public async Task AC28_With_nameUnique_true_a_taken_name_is_refused_whatever_its_case_or_spaces(string group)
    {
        var (_, http, m) = await OpenAsync(group);
        await Rules.SetAsync(http, m.NameUnique, true);
        var acme = await m.CreateAsync(http, "N1", "Acme");
        var other = await m.CreateAsync(http, "N2", "Other");

        await MasterRules.ConflictAsync(m.PostAsync(http, m.Body("N3", "acme")), "NAME_TAKEN", m.NameUnique, true, "name");
        await MasterRules.ConflictAsync(m.PostAsync(http, m.Body("N3", " ACME ")), "NAME_TAKEN", m.NameUnique, true, "name");
        await MasterRules.ConflictAsync(m.PutAsync(http, other.Id(), m.BodyOf(other).With("name", "ACME")), "NAME_TAKEN", m.NameUnique, true, "name");
        Assert.Equal(2, (await m.ListAsync(http)).Total());
        await m.AssertUnchangedAsync(http, other);

        // A record does not collide with itself.
        await m.ReplaceAsync(http, acme.Id(), m.BodyOf(acme));

        // Name spaces are per resource (013/R12, E10): another master and a unit of measure may have the name.
        var neighbour = SalesMaster.Of(SalesRules.Groups.First(g => g != group));
        await Rules.SetAsync(http, neighbour.NameUnique, true);
        await neighbour.CreateAsync(http, "N1", "Acme");
        await Uom.CreateAsync(http, "acme", "Acme");
    }

    [Theory]
    [MemberData(nameof(Masters))]
    public async Task AC28_Ten_parallel_creates_with_one_name_give_exactly_one_record(string group)
    {
        var (_, http, m) = await OpenAsync(group);
        await Rules.SetAsync(http, m.NameUnique, true);

        var responses = await Task.WhenAll(Enumerable.Range(0, 10).Select(i => m.PostAsync(http, m.Body($"PAR{i}", "Parallel"))));

        try
        {
            Assert.DoesNotContain(responses, r => (int)r.StatusCode >= 500);
            Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Created);
            foreach (var refused in responses.Where(r => r.StatusCode != HttpStatusCode.Created))
                await Rules.ConflictAsync(refused, "NAME_TAKEN", m.NameUnique, true, "name");
            Assert.Equal(1, (await m.ListAsync(http)).Total());
        }
        finally
        {
            foreach (var response in responses)
                response.Dispose();
        }
    }

    // ---- AC-29 ----

    [Fact]
    public async Task AC29_The_rule_of_one_master_does_not_judge_another()
    {
        var http = (await app.NewTenantAsync()).Client;
        await Rules.SetAsync(http, Tax.NameUnique, true);
        await Rules.SetAsync(http, Tax.CodeChangeable, false);
        var categories = SalesMaster.Categories;

        var first = await categories.CreateAsync(http, "K1", "Same");
        await categories.CreateAsync(http, "K2", "Same");
        await categories.ReplaceAsync(http, first.Id(), categories.BodyOf(first).With("code", "K3"));
        await Pay.CreateAsync(http, "P1", "Same");
        await Pay.CreateAsync(http, "P2", "Same");

        // The tax rates themselves are judged.
        await Tax.CreateAsync(http, "T1", "Same");
        await MasterRules.ConflictAsync(Tax.PostAsync(http, Tax.Body("T2", "same")), "NAME_TAKEN", Tax.NameUnique, true, "name");
    }
}
