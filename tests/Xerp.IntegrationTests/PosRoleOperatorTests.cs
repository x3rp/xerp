using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xerp.IntegrationTests.Support;

namespace Xerp.IntegrationTests;

/// <summary>
/// Spec P02, part A, AC-20 to AC-25: POS roles (with seven rights) and operators (with a role and a PIN) are masters
/// as in P01 (R1), with the rights of R2, the role reference of R3, the PIN of R4, set PIN (R5) and delete (R7).
/// </summary>
[Collection(XerpCollection.Name)]
public class PosRoleOperatorTests(XerpFixture app)
{
    public static IEnumerable<object[]> Masters() => PosRules.GroupData();

    /// <summary>One of the two masters: its path, a valid create body and the body that keeps a record as it is.</summary>
    private sealed record Master(string Group, string Path, Func<string, string, JsonObject> Body, Func<JsonElement, JsonObject> BodyOf)
    {
        public string CodeChangeable => $"{Group}.codeChangeable";

        public string NameUnique => $"{Group}.nameUnique";

        public Task<HttpResponseMessage> PostAsync(HttpClient c, JsonObject body) => c.PostAsJsonAsync(Path, body);

        public Task<HttpResponseMessage> PutAsync(HttpClient c, Guid id, JsonObject body) => c.PutAsJsonAsync($"{Path}/{id}", body);

        public async Task<JsonElement> CreateAsync(HttpClient c, string code, string name)
        {
            using var response = await PostAsync(c, Body(code, name));
            return await HttpAssert.JsonAsync(response, HttpStatusCode.Created);
        }

        public async Task<JsonElement> ReplaceAsync(HttpClient c, Guid id, JsonObject body)
        {
            using var response = await PutAsync(c, id, body);
            return await HttpAssert.JsonAsync(response, HttpStatusCode.OK);
        }

        public async Task<JsonElement> GetAsync(HttpClient c, Guid id)
        {
            using var response = await c.GetAsync($"{Path}/{id}");
            return await HttpAssert.JsonAsync(response, HttpStatusCode.OK);
        }

        public Task<JsonElement> ListAsync(HttpClient c, string query = "?limit=500") => Stock.ListAsync(c, Path + query);
    }

    private async Task<(TestTenant Tenant, HttpClient Http, Master M)> OpenAsync(string group)
    {
        var tenant = await app.NewTenantAsync();
        var http = tenant.Client;
        if (group == "posRole")
            return (tenant, http, new Master(group, Pos.Roles, (c, n) => Pos.RoleBody(c, n), Pos.RoleBodyOf));
        // Operators need a role (posOperator.roleRequired is true by default).
        var role = await Pos.RoleAsync(http, "STAFF");
        return (tenant, http, new Master(group, Pos.Operators, (c, n) => Pos.OperatorBody(c, n, role.Id()), Pos.OperatorBodyOf));
    }

    // ---- AC-20 ----

    [Theory]
    [MemberData(nameof(Masters))]
    public async Task AC20_Create_get_by_id_and_by_code_list_replace_and_delete(string group)
    {
        var (tenant, http, m) = await OpenAsync(group);

        using var response = await m.PostAsync(http, m.Body("ANA", "Ana Horvat"));
        var created = await HttpAssert.JsonAsync(response, HttpStatusCode.Created);

        Assert.EndsWith($"{m.Path}/{created.Id()}", response.Headers.Location?.ToString());
        Assert.Equal(("ANA", "Ana Horvat", true), (created.Str("code"), created.Str("name"), created.Bool("isActive")));
        Assert.Equal(tenant.ApiKeyId, created.GetProperty("createdBy").GetGuid());
        Assert.Equal(tenant.ApiKeyId, created.GetProperty("updatedBy").GetGuid());
        McpAssert.JsonEqual(created, await m.GetAsync(http, created.Id()));
        using (var byCode = await http.GetAsync($"{m.Path}/by-code/ana"))
            McpAssert.JsonEqual(created, await HttpAssert.JsonAsync(byCode, HttpStatusCode.OK));
        await m.CreateAsync(http, "BOB", "Bob Babić");
        Assert.Equal(new[] { "ANA", "BOB" }, (await m.ListAsync(http)).Codes());
        Assert.Equal(new[] { "ANA" }, (await m.ListAsync(http, "?search=horv")).Codes());
        Assert.Equal(new[] { "BOB" }, (await m.ListAsync(http, "?search=bo")).Codes());

        await Task.Delay(20);
        var replaced = await m.ReplaceAsync(http, created.Id(), m.BodyOf(created).With("name", "Ana H.").With("isActive", false));
        Assert.Equal(("Ana H.", false), (replaced.Str("name"), replaced.Bool("isActive")));
        Assert.True(replaced.Time("updatedAt") > created.Time("updatedAt"));
        Assert.Equal(new[] { "ANA" }, (await m.ListAsync(http, "?isActive=false")).Codes());

        using (var delete = await http.DeleteAsync($"{m.Path}/{created.Id()}"))
            Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        using (var gone = await http.GetAsync($"{m.Path}/{created.Id()}"))
            await HttpAssert.NotFoundAsync(gone);
    }

    [Theory]
    [MemberData(nameof(Masters))]
    public async Task AC20_Validation_as_a_unit_of_measure_and_CODE_TAKEN_in_another_case(string group)
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
    }

    [Theory]
    [MemberData(nameof(Masters))]
    public async Task AC20_codeChangeable_and_nameUnique_at_both_values(string group)
    {
        var (_, http, m) = await OpenAsync(group);
        var first = await m.CreateAsync(http, "C1", "Same name");

        // Defaults: a code may change, two records share a name.
        var changed = await m.ReplaceAsync(http, first.Id(), m.BodyOf(first).With("code", "C2"));
        Assert.Equal("C2", changed.Str("code"));
        await m.CreateAsync(http, "C3", "Same name");

        await Rules.SetAsync(http, m.CodeChangeable, false);
        await MasterRules.ConflictAsync(m.PutAsync(http, first.Id(), m.BodyOf(changed).With("code", "C9")),
            "CODE_CHANGE_NOT_ALLOWED", m.CodeChangeable, false, "code");
        Assert.Equal("Renamed", (await m.ReplaceAsync(http, first.Id(), m.BodyOf(changed).With("name", "Renamed"))).Str("name"));
        await Rules.ResetAsync(http, m.CodeChangeable);
        Assert.Equal("C9", (await m.ReplaceAsync(http, first.Id(), m.BodyOf(changed).With("name", "Renamed").With("code", "C9"))).Str("code"));

        await Rules.SetAsync(http, m.NameUnique, true);
        await MasterRules.ConflictAsync(m.PostAsync(http, m.Body("C4", "Same name")), "NAME_TAKEN", m.NameUnique, true, "name");
        await Rules.ResetAsync(http, m.NameUnique);
        await m.CreateAsync(http, "C4", "Same name");
    }

    // ---- AC-21 ----

    [Fact]
    public async Task AC21_A_role_with_all_seven_true_and_one_with_all_false_read_back_exactly()
    {
        var http = (await app.NewTenantAsync()).Client;

        var all = await Pos.RoleAsync(http, "MANAGER", Pos.RightNames);
        var none = await Pos.RoleAsync(http, "TRAINEE");

        Pos.AssertRights(all.GetProperty("rights"), Pos.RightNames);
        Pos.AssertRights(none.GetProperty("rights"));
        using (var read = await http.GetAsync($"{Pos.Roles}/{all.Id()}"))
            Pos.AssertRights((await HttpAssert.JsonAsync(read, HttpStatusCode.OK)).GetProperty("rights"), Pos.RightNames);
        var replaced = await Pos.ReplaceRoleAsync(http, none.Id(), Pos.RoleBodyOf(none).With("rights", Pos.Rights("refund", "closeShift")));
        Pos.AssertRights(replaced.GetProperty("rights"), "refund", "closeShift");
    }

    public static TheoryData<string, string> BadRights() => new()
    {
        { """{ "code": "R1", "name": "R" }""", "rights" },
        { """{ "code": "R1", "name": "R", "rights": null }""", "rights" },
        { """{ "code": "R1", "name": "R", "rights": { "changePrice": true, "discount": true, "voidLine": true, "voidBill": true, "refund": true, "cashMovement": true } }""", "rights.closeShift" },
        { """{ "code": "R1", "name": "R", "rights": { "changePrice": "true", "discount": true, "voidLine": true, "voidBill": true, "refund": true, "cashMovement": true, "closeShift": true } }""", "rights.changePrice" },
        { """{ "code": "R1", "name": "R", "rights": { "changePrice": true, "discount": true, "voidLine": true, "voidBill": true, "refund": true, "cashMovement": true, "closeShift": true, "tip": true } }""", "rights.tip" },
    };

    [Theory]
    [MemberData(nameof(BadRights))]
    public async Task AC21_R2_Missing_or_malformed_rights_are_refused_with_the_key_of_R2(string json, string key)
    {
        var http = (await app.NewTenantAsync()).Client;

        await MasterRules.InvariantAsync(http.PostAsync(Pos.Roles, HttpAssert.Raw(json)), key);
        var role = await Pos.RoleAsync(http, "R2");
        await MasterRules.InvariantAsync(http.PutAsync($"{Pos.Roles}/{role.Id()}", HttpAssert.Raw(json.Replace("\"R1\"", "\"R2\"").Replace("{ \"code\"", "{ \"isActive\": true, \"code\""))), key);

        Assert.Equal(1, (await Stock.ListAsync(http, Pos.Roles)).Total());
    }

    // ---- AC-22 ----

    [Fact]
    public async Task AC22_The_operator_has_no_pin_and_no_hash_pinChangedAt_is_set_and_lockedUntil_null()
    {
        var tenant = await app.NewTenantAsync();
        var http = tenant.Client;
        var role = await Pos.RoleAsync(http, "WAITER", "voidLine");

        using var response = await Pos.PostOperatorAsync(http, Pos.OperatorBody("ANA", "Ana", role.Id(), "4711"));
        var text = await response.Content.ReadAsStringAsync();
        var op = await HttpAssert.JsonAsync(response, HttpStatusCode.Created);

        Assert.Equal(new[] { "code", "createdAt", "createdBy", "id", "isActive", "lockedUntil", "name", "pinChangedAt", "role", "updatedAt", "updatedBy" },
            op.PropertyNames());
        Assert.DoesNotContain("4711", text, StringComparison.Ordinal);
        Assert.InRange(op.Time("pinChangedAt"), DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(1));
        Assert.Equal(JsonValueKind.Null, op.GetProperty("lockedUntil").ValueKind);
        Assert.Equal(new[] { "code", "id", "name" }, op.GetProperty("role").PropertyNames());
        Assert.Equal((role.Id(), "WAITER"), (op.GetProperty("role").Id(), op.GetProperty("role").Str("code")));
        var list = await Stock.ListAsync(http, Pos.Operators);
        McpAssert.JsonEqual(op, Assert.Single(list.Items()));
        Assert.DoesNotContain("4711", list.GetRawText(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("\"123\"")]
    [InlineData("\"123456789\"")]
    [InlineData("\"12a4\"")]
    [InlineData("1234")]
    [InlineData("\" 1234\"")]
    [InlineData("\"１２３４\"")]
    [InlineData("null")]
    public async Task AC22_E1_A_malformed_pin_is_refused_with_the_key_pin(string pin)
    {
        var http = (await app.NewTenantAsync()).Client;
        var role = await Pos.RoleAsync(http, "WAITER");

        var json = $$"""{ "code": "ANA", "name": "Ana", "roleId": "{{role.Id()}}", "pin": {{pin}} }""";
        await MasterRules.InvariantAsync(http.PostAsync(Pos.Operators, HttpAssert.Raw(json)), "pin");
        await MasterRules.InvariantAsync(http.PostAsync(Pos.Operators, HttpAssert.Raw($$"""{ "code": "ANA", "name": "Ana", "roleId": "{{role.Id()}}" }""")), "pin");

        Assert.Equal(0, (await Stock.ListAsync(http, Pos.Operators)).Total());
    }

    [Fact]
    public async Task AC22_E1_0000_and_eight_digits_are_valid_and_a_replace_with_pin_is_refused()
    {
        var http = (await app.NewTenantAsync()).Client;
        var role = await Pos.RoleAsync(http, "WAITER");

        var zero = await Pos.OperatorAsync(http, "ZERO", role.Id(), "0000");
        await Pos.OperatorAsync(http, "EIGHT", role.Id(), "12345678");

        await MasterRules.InvariantAsync(Pos.PutOperatorAsync(http, zero.Id(), Pos.OperatorBodyOf(zero).With("pin", "5555")), "pin");
        McpAssert.JsonEqual(zero, await Pos.GetOperatorAsync(http, zero.Id()));
        await Pos.StartAsync(http, "ZERO", "0000");
        await Pos.StartAsync(http, "EIGHT", "12345678");
    }

    // ---- AC-23 ----

    [Fact]
    public async Task AC23_R3_A_null_role_is_refused_by_roleRequired_and_allowed_when_it_is_false()
    {
        var http = (await app.NewTenantAsync()).Client;

        await MasterRules.RequiredAsync(Pos.PostOperatorAsync(http, Pos.OperatorBody("ANA", "Ana", null)), PosRules.RoleRequired, "roleId");
        await Rules.SetAsync(http, PosRules.RoleRequired, false);

        var op = await Pos.OperatorAsync(http, "ANA", null);
        Assert.Equal(JsonValueKind.Null, op.GetProperty("role").ValueKind);
        // No stored record is re-checked: back at the default, a replace of the same record needs a role again.
        await Rules.ResetAsync(http, PosRules.RoleRequired);
        await MasterRules.RequiredAsync(Pos.PutOperatorAsync(http, op.Id(), Pos.OperatorBodyOf(op)), PosRules.RoleRequired, "roleId");
    }

    [Fact]
    public async Task AC23_R3_An_unknown_role_is_not_found_an_inactive_one_is_refused_and_a_kept_one_stays()
    {
        var http = (await app.NewTenantAsync()).Client;
        var role = await Pos.RoleAsync(http, "WAITER");
        var other = await Pos.RoleAsync(http, "BAR");
        var op = await Pos.OperatorAsync(http, "ANA", role.Id());

        using (var unknown = await Pos.PostOperatorAsync(http, Pos.OperatorBody("BOB", "Bob", Guid.CreateVersion7())))
            await HttpAssert.ReferenceNotFoundAsync(unknown, "roleId");
        await Pos.ReplaceRoleAsync(http, role.Id(), Pos.RoleBodyOf(role).With("isActive", false));
        await Pos.ReplaceRoleAsync(http, other.Id(), Pos.RoleBodyOf(other).With("isActive", false));

        using (var inactive = await Pos.PostOperatorAsync(http, Pos.OperatorBody("BOB", "Bob", role.Id())))
            await HttpAssert.ReferenceInactiveAsync(inactive, "roleId");
        using (var newlyInactive = await Pos.PutOperatorAsync(http, op.Id(), Pos.OperatorBodyOf(op).With("roleId", other.Id().ToString())))
            await HttpAssert.ReferenceInactiveAsync(newlyInactive, "roleId");
        var kept = await Pos.ReplaceOperatorAsync(http, op.Id(), Pos.OperatorBodyOf(op).With("name", "Ana H."));
        Assert.Equal(role.Id(), kept.GetProperty("role").Id());
    }

    // ---- AC-24 ----

    [Fact]
    public async Task AC24_R7_A_named_role_and_a_signed_in_operator_are_in_use_a_fresh_operator_is_deleted()
    {
        var http = (await app.NewTenantAsync()).Client;
        var role = await Pos.RoleAsync(http, "WAITER");
        var ana = await Pos.OperatorAsync(http, "ANA", role.Id());
        var bob = await Pos.OperatorAsync(http, "BOB", role.Id());
        var session = await Pos.StartAsync(http, "ANA");
        await Pos.EndAsync(http, session.Id());
        // Also an inactive operator names the role.
        await Pos.ReplaceOperatorAsync(http, ana.Id(), Pos.OperatorBodyOf(ana).With("isActive", false));

        using (var roleInUse = await http.DeleteAsync($"{Pos.Roles}/{role.Id()}"))
            await HttpAssert.InUseAsync(roleInUse);
        using (var anaInUse = await http.DeleteAsync($"{Pos.Operators}/{ana.Id()}"))
            await HttpAssert.InUseAsync(anaInUse);
        using (var bobGone = await http.DeleteAsync($"{Pos.Operators}/{bob.Id()}"))
            Assert.Equal(HttpStatusCode.NoContent, bobGone.StatusCode);
        Assert.Equal(new[] { "ANA" }, (await Stock.ListAsync(http, Pos.Operators)).Codes());
    }

    // ---- AC-25 ----

    [Fact]
    public async Task AC25_R5_Set_pin_changes_the_pin_ends_the_sessions_and_is_attributed()
    {
        var s = await PosSetup.NewAsync(app);
        var session = await Pos.StartAsync(s.Http, "ANA", PosSetup.AnaPin);
        await Task.Delay(20);

        using var response = await Pos.SendSetPinAsync(s.Http, s.Ana.Id(), "2468");
        var text = await response.Content.ReadAsStringAsync();
        var changed = await HttpAssert.JsonAsync(response, HttpStatusCode.OK);

        Assert.DoesNotContain("2468", text, StringComparison.Ordinal);
        Assert.True(changed.Time("pinChangedAt") > s.Ana.Time("pinChangedAt"), "pinChangedAt is not later after set PIN.");
        Assert.True(changed.Time("updatedAt") > s.Ana.Time("updatedAt"), "updatedAt is not later after set PIN.");
        Assert.Equal(s.Tenant.ApiKeyId, changed.GetProperty("updatedBy").GetGuid());
        Assert.Equal("ended", await Pos.StatusAsync(s.Http, session.Id()));
        await Pos.SignInFailsAsync(s.Http, "ANA", PosSetup.AnaPin);
        await Pos.StartAsync(s.Http, "ANA", "2468");
        // The same PIN again is allowed and has the same effects.
        var again = await Pos.StartAsync(s.Http, "ANA", "2468");
        await Pos.SetPinAsync(s.Http, s.Ana.Id(), "2468");
        Assert.Equal("ended", await Pos.StatusAsync(s.Http, again.Id()));
    }

    [Fact]
    public async Task AC25_Set_pin_with_a_malformed_pin_is_400_and_for_an_unknown_operator_404()
    {
        var s = await PosSetup.NewAsync(app);

        await MasterRules.InvariantAsync(Pos.SendSetPinAsync(s.Http, s.Ana.Id(), "12"), "pin");
        await MasterRules.InvariantAsync(s.Http.PutAsync($"{Pos.Operators}/{s.Ana.Id()}/pin", HttpAssert.Raw("{}")), "pin");
        using (var unknown = await Pos.SendSetPinAsync(s.Http, Guid.CreateVersion7(), "2468"))
            await HttpAssert.NotFoundAsync(unknown);
        McpAssert.JsonEqual(s.Ana, await Pos.GetOperatorAsync(s.Http, s.Ana.Id()));
    }

    // ---- R8 ----

    [Fact]
    public async Task R8_Two_operators_may_have_the_same_pin()
    {
        var s = await PosSetup.NewAsync(app);

        await Pos.OperatorAsync(s.Http, "IVA", s.Waiter.Id(), PosSetup.AnaPin);

        Assert.Equal("IVA", (await Pos.StartAsync(s.Http, "IVA", PosSetup.AnaPin)).GetProperty("operator").Str("code"));
        Assert.Equal("ANA", (await Pos.StartAsync(s.Http, "ANA", PosSetup.AnaPin)).GetProperty("operator").Str("code"));
    }
}
