using System.Net;
using System.Text.Json;
using Xerp.IntegrationTests.Support;

namespace Xerp.IntegrationTests;

/// <summary>
/// Spec P02, part A, AC-30 to AC-38: operator sessions — start with code and PIN (R10), one answer for every failure
/// (R10, S1), the lockout (R11), sessions bound to the key (R13), status and live rights (R14), end (R15), deactivation
/// (R6) and the two device rules (R12).
/// </summary>
[Collection(XerpCollection.Name)]
public class PosSessionTests(XerpFixture app)
{
    // ---- AC-30 ----

    [Fact]
    public async Task AC30_Start_with_the_code_in_another_case_returns_an_active_session_of_the_calling_key()
    {
        var s = await PosSetup.NewAsync(app);

        using var response = await Pos.SendStartAsync(s.Http, "ana", PosSetup.AnaPin);
        var text = await response.Content.ReadAsStringAsync();
        var session = await HttpAssert.JsonAsync(response, HttpStatusCode.Created);

        Assert.EndsWith($"{Pos.Sessions}/{session.Id()}", response.Headers.Location?.ToString());
        Assert.DoesNotContain(PosSetup.AnaPin, text, StringComparison.Ordinal);
        Assert.Equal(new[] { "apiKeyId", "endedAt", "expiresAt", "id", "operator", "rights", "role", "startedAt", "status" }, session.PropertyNames());
        Assert.Equal("active", session.Str("status"));
        Assert.Equal(new[] { "code", "id", "name" }, session.GetProperty("operator").PropertyNames());
        Assert.Equal((s.Ana.Id(), "ANA"), (session.GetProperty("operator").Id(), session.GetProperty("operator").Str("code")));
        Assert.Equal((s.Waiter.Id(), "WAITER"), (session.GetProperty("role").Id(), session.GetProperty("role").Str("code")));
        Pos.AssertRights(session.GetProperty("rights"), "voidLine", "discount");
        Assert.Equal(s.Tenant.ApiKeyId, session.GetProperty("apiKeyId").GetGuid());
        Assert.Equal(JsonValueKind.Null, session.GetProperty("endedAt").ValueKind);
        Assert.InRange(session.Time("startedAt"), DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(1));
        var lifetime = session.Time("expiresAt") - session.Time("startedAt");
        Assert.InRange(lifetime, TimeSpan.FromHours(12) - TimeSpan.FromSeconds(1), TimeSpan.FromHours(12) + TimeSpan.FromSeconds(1));
        McpAssert.JsonEqual(session, await Pos.GetSessionAsync(s.Http, session.Id()));
    }

    [Theory]
    [InlineData("""{ "pin": "1234" }""", "operatorCode")]
    [InlineData("""{ "operatorCode": "a b", "pin": "1234" }""", "operatorCode")]
    [InlineData("""{ "operatorCode": "ANA" }""", "pin")]
    [InlineData("""{ "operatorCode": "ANA", "pin": "12" }""", "pin")]
    [InlineData("""{ "operatorCode": "ANA", "pin": 1234 }""", "pin")]
    public async Task AC30_R10_An_invalid_start_body_is_400_and_counts_nothing(string json, string key)
    {
        var s = await PosSetup.NewAsync(app);

        await MasterRules.InvariantAsync(s.Http.PostAsync(Pos.Sessions, HttpAssert.Raw(json)), key);

        Assert.Equal(0, await Pos.FailedAttemptsAsync(app, s.Ana.Id()));
        Assert.Equal(0, (await Pos.SessionsAsync(s.Http)).Total());
    }

    // ---- AC-31 ----

    [Fact]
    public async Task AC31_Unknown_code_wrong_pin_inactive_and_locked_answer_identically()
    {
        var s = await PosSetup.NewAsync(app);
        var inactive = await Pos.OperatorAsync(s.Http, "OLD", s.Waiter.Id(), "1111");
        await Pos.ReplaceOperatorAsync(s.Http, inactive.Id(), Pos.OperatorBodyOf(inactive).With("isActive", false));
        await Pos.OperatorAsync(s.Http, "LOCKED", s.Waiter.Id(), "2222");
        for (var i = 0; i < 5; i++)
            await Pos.SignInFailsAsync(s.Http, "LOCKED", "0000");

        var unknown = await Pos.SignInFailsAsync(s.Http, "NOBODY", "1234");
        var wrongPin = await Pos.SignInFailsAsync(s.Http, "ANA", "4321");
        var ofInactive = await Pos.SignInFailsAsync(s.Http, "OLD", "1111");
        var ofLocked = await Pos.SignInFailsAsync(s.Http, "LOCKED", "2222");

        foreach (var (name, other) in new[] { ("wrong PIN", wrongPin), ("inactive", ofInactive), ("locked", ofLocked) })
            foreach (var member in new[] { "status", "code", "title", "detail", "errors" })
                McpAssert.JsonEqual(unknown.GetProperty(member), other.GetProperty(member), $"'{member}' of the {name} answer differs from the unknown-code answer");
        Assert.Equal(0, (await Pos.SessionsAsync(s.Http)).Total());
    }

    // ---- AC-32 ----

    [Fact]
    public async Task AC32_E3_Five_wrong_pins_lock_the_operator_for_15_minutes_and_the_lock_runs_out()
    {
        var s = await PosSetup.NewAsync(app);

        for (var i = 0; i < 5; i++)
            await Pos.SignInFailsAsync(s.Http, "ANA", "0000");

        var locked = await Pos.GetOperatorAsync(s.Http, s.Ana.Id());
        Assert.InRange(locked.Time("lockedUntil"), DateTimeOffset.UtcNow.AddMinutes(14), DateTimeOffset.UtcNow.AddMinutes(16));
        // While locked even the right PIN fails and nothing is counted.
        await Pos.SignInFailsAsync(s.Http, "ANA", PosSetup.AnaPin);
        Assert.Equal(0, await Pos.FailedAttemptsAsync(app, s.Ana.Id()));
        McpAssert.JsonEqual(locked, await Pos.GetOperatorAsync(s.Http, s.Ana.Id()), "A sign-in of a locked operator changed it");

        await Pos.UnlockInThePastAsync(app, s.Ana.Id());
        Assert.Equal("active", (await Pos.StartAsync(s.Http, "ANA", PosSetup.AnaPin)).Str("status"));
    }

    [Fact]
    public async Task AC32_E4_A_success_resets_the_count()
    {
        var s = await PosSetup.NewAsync(app);

        for (var i = 0; i < 4; i++)
            await Pos.SignInFailsAsync(s.Http, "ANA", "0000");
        Assert.Equal(4, await Pos.FailedAttemptsAsync(app, s.Ana.Id()));
        await Pos.StartAsync(s.Http, "ANA", PosSetup.AnaPin);
        Assert.Equal(0, await Pos.FailedAttemptsAsync(app, s.Ana.Id()));
        for (var i = 0; i < 4; i++)
            await Pos.SignInFailsAsync(s.Http, "ANA", "0000");

        Assert.Equal(JsonValueKind.Null, (await Pos.GetOperatorAsync(s.Http, s.Ana.Id())).GetProperty("lockedUntil").ValueKind);
        await Pos.StartAsync(s.Http, "ANA", PosSetup.AnaPin);
    }

    [Fact]
    public async Task AC32_S3_Ten_parallel_wrong_pins_are_all_refused_none_500_and_the_operator_is_locked()
    {
        var s = await PosSetup.NewAsync(app);

        var responses = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => Pos.SendStartAsync(s.Http, "ANA", "0000")));
        try
        {
            foreach (var response in responses)
                await Pos.SignInFailedAsync(response, "pin");
        }
        finally
        {
            foreach (var response in responses)
                response.Dispose();
        }

        Assert.NotEqual(JsonValueKind.Null, (await Pos.GetOperatorAsync(s.Http, s.Ana.Id())).GetProperty("lockedUntil").ValueKind);
        await Pos.SignInFailsAsync(s.Http, "ANA", PosSetup.AnaPin);
    }

    [Fact]
    public async Task AC32_R11_Unknown_codes_and_inactive_operators_count_nothing()
    {
        var s = await PosSetup.NewAsync(app);
        await Pos.ReplaceOperatorAsync(s.Http, s.Marko.Id(), Pos.OperatorBodyOf(s.Marko).With("isActive", false));

        for (var i = 0; i < 6; i++)
            await Pos.SignInFailsAsync(s.Http, "MARKO", "0000");

        Assert.Equal(0, await Pos.FailedAttemptsAsync(app, s.Marko.Id()));
        Assert.Equal(JsonValueKind.Null, (await Pos.GetOperatorAsync(s.Http, s.Marko.Id())).GetProperty("lockedUntil").ValueKind);
    }

    // ---- AC-33 ----

    [Fact]
    public async Task AC33_Set_pin_unlocks_and_the_new_pin_signs_in()
    {
        var s = await PosSetup.NewAsync(app);
        for (var i = 0; i < 5; i++)
            await Pos.SignInFailsAsync(s.Http, "ANA", "0000");

        var unlocked = await Pos.SetPinAsync(s.Http, s.Ana.Id(), "5555");

        Assert.Equal(JsonValueKind.Null, unlocked.GetProperty("lockedUntil").ValueKind);
        Assert.Equal(0, await Pos.FailedAttemptsAsync(app, s.Ana.Id()));
        Assert.Equal("active", (await Pos.StartAsync(s.Http, "ANA", "5555")).Str("status"));
    }

    // ---- AC-34 ----

    [Fact]
    public async Task AC34_R13_Another_key_of_the_tenant_cannot_see_or_end_a_session_and_the_list_filters()
    {
        var s = await PosSetup.NewAsync(app);
        var b = await Pos.KeyAsync(app, s.Http, null, "tablet-b");
        var ofA = await Pos.StartAsync(s.Http, "ANA", PosSetup.AnaPin);
        var ended = await Pos.StartAsync(s.Http, "MARKO", PosSetup.MarkoPin);
        await Pos.EndAsync(s.Http, ended.Id());
        var ofB = await Pos.StartAsync(b.Client, "ANA", PosSetup.AnaPin);

        using (var get = await Pos.SendGetSessionAsync(b.Client, ofA.Id()))
            await HttpAssert.NotFoundAsync(get);
        using (var end = await Pos.SendEndAsync(b.Client, ofA.Id()))
            await HttpAssert.NotFoundAsync(end);
        Assert.Equal("active", await Pos.StatusAsync(s.Http, ofA.Id()));
        Assert.Equal(new[] { ofB.Id() }, (await Pos.SessionsAsync(b.Client)).Ids());

        var list = await Pos.SessionsAsync(s.Http);
        Assert.Equal(new[] { "items", "limit", "offset", "total" }, list.PropertyNames());
        // Newest startedAt first.
        Assert.Equal(new[] { ended.Id(), ofA.Id() }, list.Ids());
        Assert.Equal(new[] { ofA.Id() }, (await Pos.SessionsAsync(s.Http, "?status=active")).Ids());
        Assert.Equal(new[] { ended.Id() }, (await Pos.SessionsAsync(s.Http, "?status=ended")).Ids());
        Assert.Equal(new[] { ofA.Id() }, (await Pos.SessionsAsync(s.Http, "?limit=1&offset=1")).Ids());
        using var unknownStatus = await s.Http.GetAsync($"{Pos.Sessions}?status=x");
        await HttpAssert.ValidationAsync(unknownStatus, "status");
    }

    // ---- AC-35 ----

    [Fact]
    public async Task AC35_R15_End_sets_endedAt_and_ending_again_changes_nothing()
    {
        var s = await PosSetup.NewAsync(app);
        var session = await Pos.StartAsync(s.Http, "ANA", PosSetup.AnaPin);

        var ended = await Pos.EndAsync(s.Http, session.Id());
        await Task.Delay(20);
        var again = await Pos.EndAsync(s.Http, session.Id());

        Assert.Equal("ended", ended.Str("status"));
        Assert.InRange(ended.Time("endedAt"), session.Time("startedAt"), DateTimeOffset.UtcNow.AddMinutes(1));
        McpAssert.JsonEqual(ended, again, "Ending an ended session changed it");
        McpAssert.JsonEqual(ended, await Pos.GetSessionAsync(s.Http, session.Id()));
        using var unknown = await Pos.SendEndAsync(s.Http, Guid.CreateVersion7());
        await HttpAssert.NotFoundAsync(unknown);
    }

    [Fact]
    public async Task AC35_R14_An_expired_session_reads_ended_with_endedAt_equal_to_expiresAt()
    {
        var s = await PosSetup.NewAsync(app);
        var session = await Pos.StartAsync(s.Http, "ANA", PosSetup.AnaPin);
        await Pos.ExpireAsync(app, session.Id());

        var read = await Pos.GetSessionAsync(s.Http, session.Id());
        var ended = await Pos.EndAsync(s.Http, session.Id());

        Assert.Equal("ended", read.Str("status"));
        Assert.Equal(read.Time("expiresAt"), read.Time("endedAt"));
        McpAssert.JsonEqual(read, ended, "Ending an expired session changed it");
        Assert.Equal(new[] { session.Id() }, (await Pos.SessionsAsync(s.Http, "?status=ended")).Ids());
        Assert.Equal(0, (await Pos.SessionsAsync(s.Http, "?status=active")).Total());
    }

    // ---- AC-36 ----

    [Fact]
    public async Task AC36_E5_Rights_are_read_live_and_a_deactivated_role_leaves_the_session_active_with_no_rights()
    {
        var tenant = await app.NewTenantAsync();
        var http = tenant.Client;
        var role = await Pos.RoleAsync(http, "WAITER", "discount");
        await Pos.OperatorAsync(http, "ANA", role.Id());
        var session = await Pos.StartAsync(http, "ANA");
        Pos.AssertRights(session.GetProperty("rights"), "discount");

        role = await Pos.ReplaceRoleAsync(http, role.Id(), Pos.RoleBodyOf(role).With("rights", Pos.Rights("discount", "voidLine")));
        Pos.AssertRights((await Pos.GetSessionAsync(http, session.Id())).GetProperty("rights"), "discount", "voidLine");
        Assert.Equal("voidLine", (await Pos.AuthorizeAsync(http, session.Id(), "voidLine")).Str("right"));

        await Pos.ReplaceRoleAsync(http, role.Id(), Pos.RoleBodyOf(role).With("isActive", false));
        var read = await Pos.GetSessionAsync(http, session.Id());
        Assert.Equal("active", read.Str("status"));
        Assert.Equal(JsonValueKind.Null, read.GetProperty("role").ValueKind);
        Pos.AssertRights(read.GetProperty("rights"));
        using var refused = await Pos.SendAuthorizeAsync(http, session.Id(), "discount");
        await Pos.RightMissingAsync(refused, "right");
    }

    // ---- AC-37 ----

    [Fact]
    public async Task AC37_R6_A_deactivated_operator_has_its_sessions_ended_and_cannot_sign_in()
    {
        var s = await PosSetup.NewAsync(app);
        var first = await Pos.StartAsync(s.Http, "ANA", PosSetup.AnaPin);
        var second = await Pos.StartAsync(s.Http, "ANA", PosSetup.AnaPin);
        var other = await Pos.StartAsync(s.Http, "MARKO", PosSetup.MarkoPin);

        await Pos.ReplaceOperatorAsync(s.Http, s.Ana.Id(), Pos.OperatorBodyOf(s.Ana).With("isActive", false));

        Assert.Equal("ended", await Pos.StatusAsync(s.Http, first.Id()));
        Assert.Equal("ended", await Pos.StatusAsync(s.Http, second.Id()));
        Assert.Equal("active", await Pos.StatusAsync(s.Http, other.Id()));
        await Pos.SignInFailsAsync(s.Http, "ANA", PosSetup.AnaPin);
    }

    // ---- AC-38 ----

    [Fact]
    public async Task AC38_R12_By_default_several_operators_per_key_and_one_operator_on_several_keys_stay_active()
    {
        var s = await PosSetup.NewAsync(app);
        var b = await Pos.KeyAsync(app, s.Http, null, "tablet-b");

        var anaOnA = await Pos.StartAsync(s.Http, "ANA", PosSetup.AnaPin);
        var markoOnA = await Pos.StartAsync(s.Http, "MARKO", PosSetup.MarkoPin);
        var anaAgainOnA = await Pos.StartAsync(s.Http, "ANA", PosSetup.AnaPin);
        var anaOnB = await Pos.StartAsync(b.Client, "ANA", PosSetup.AnaPin);

        foreach (var id in new[] { anaOnA.Id(), markoOnA.Id(), anaAgainOnA.Id() })
            Assert.Equal("active", await Pos.StatusAsync(s.Http, id));
        Assert.Equal("active", await Pos.StatusAsync(b.Client, anaOnB.Id()));
        Assert.Equal(b.Id, anaOnB.GetProperty("apiKeyId").GetGuid());
    }

    [Fact]
    public async Task AC38_R12_With_severalOperatorsPerDeviceAllowed_false_a_sign_in_ends_the_other_sessions_of_the_key()
    {
        var s = await PosSetup.NewAsync(app);
        var b = await Pos.KeyAsync(app, s.Http, null, "tablet-b");
        var markoOnB = await Pos.StartAsync(b.Client, "MARKO", PosSetup.MarkoPin);
        var anaOnA = await Pos.StartAsync(s.Http, "ANA", PosSetup.AnaPin);
        await Rules.SetAsync(s.Http, PosRules.SeveralOperatorsPerDevice, false);

        var markoOnA = await Pos.StartAsync(s.Http, "MARKO", PosSetup.MarkoPin);

        Assert.Equal("ended", await Pos.StatusAsync(s.Http, anaOnA.Id()));
        Assert.Equal("active", await Pos.StatusAsync(s.Http, markoOnA.Id()));
        // Another key's sessions are not this rule's business.
        Assert.Equal("active", await Pos.StatusAsync(b.Client, markoOnB.Id()));
        // The same operator again on the same key: the first session ends too.
        var markoAgain = await Pos.StartAsync(s.Http, "MARKO", PosSetup.MarkoPin);
        Assert.Equal("ended", await Pos.StatusAsync(s.Http, markoOnA.Id()));
        Assert.Equal(new[] { markoAgain.Id() }, (await Pos.SessionsAsync(s.Http, "?status=active")).Ids());
    }

    [Fact]
    public async Task AC38_R12_With_operatorOnSeveralDevicesAllowed_false_a_sign_in_on_B_ends_only_that_operator_s_session_on_A()
    {
        var s = await PosSetup.NewAsync(app);
        var b = await Pos.KeyAsync(app, s.Http, null, "tablet-b");
        var anaOnA = await Pos.StartAsync(s.Http, "ANA", PosSetup.AnaPin);
        var markoOnA = await Pos.StartAsync(s.Http, "MARKO", PosSetup.MarkoPin);
        await Rules.SetAsync(s.Http, PosRules.OperatorOnSeveralDevices, false);

        var anaOnB = await Pos.StartAsync(b.Client, "ANA", PosSetup.AnaPin);

        Assert.Equal("ended", await Pos.StatusAsync(s.Http, anaOnA.Id()));
        Assert.Equal("active", await Pos.StatusAsync(s.Http, markoOnA.Id()));
        Assert.Equal("active", await Pos.StatusAsync(b.Client, anaOnB.Id()));
        // On the same key a second session of the operator is allowed by this rule.
        var anaAgainOnB = await Pos.StartAsync(b.Client, "ANA", PosSetup.AnaPin);
        Assert.Equal("active", await Pos.StatusAsync(b.Client, anaOnB.Id()));
        Assert.Equal("active", await Pos.StatusAsync(b.Client, anaAgainOnB.Id()));
    }
}
