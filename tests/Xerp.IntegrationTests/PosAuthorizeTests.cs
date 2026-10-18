using System.Net;
using System.Text.Json;
using Xerp.IntegrationTests.Support;

namespace Xerp.IntegrationTests;

/// <summary>
/// Spec P02, part A, AC-40 to AC-44: authorize (R20) — the session operator's own right, a second operator's approval,
/// <c>pos.secondOperatorApprovalAllowed</c>, the order of checks, and that it writes nothing but PIN counters (R21).
/// The setup's waiter ANA has <c>voidLine</c> and <c>discount</c>; the manager MARKO has every right.
/// </summary>
[Collection(XerpCollection.Name)]
public class PosAuthorizeTests(XerpFixture app)
{
    private static void AssertSummary(JsonElement summary, JsonElement op)
    {
        Assert.Equal(new[] { "code", "id", "name" }, summary.PropertyNames());
        Assert.Equal((op.Id(), op.Str("code"), op.Str("name")), (summary.Id(), summary.Str("code"), summary.Str("name")));
    }

    // ---- AC-40 ----

    [Fact]
    public async Task AC40_The_operator_s_own_right_is_200_with_approvedBy_null_and_a_missing_one_403()
    {
        var s = await PosSetup.NewAsync(app);
        var session = await Pos.StartAsync(s.Http, "ANA", PosSetup.AnaPin);

        var result = await Pos.AuthorizeAsync(s.Http, session.Id(), "discount");

        Assert.Equal(new[] { "approvedBy", "operator", "right" }, result.PropertyNames());
        Assert.Equal("discount", result.Str("right"));
        AssertSummary(result.GetProperty("operator"), s.Ana);
        Assert.Equal(JsonValueKind.Null, result.GetProperty("approvedBy").ValueKind);
        using var refused = await Pos.SendAuthorizeAsync(s.Http, session.Id(), "refund");
        await Pos.RightMissingAsync(refused, "right");
    }

    // ---- AC-41 ----

    [Fact]
    public async Task AC41_A_manager_with_the_right_approves_and_one_without_it_does_not()
    {
        var s = await PosSetup.NewAsync(app);
        var shiftLead = await Pos.RoleAsync(s.Http, "LEAD", "discount", "voidBill");
        var lead = await Pos.OperatorAsync(s.Http, "LEAD1", shiftLead.Id(), "3333");
        var session = await Pos.StartAsync(s.Http, "ANA", PosSetup.AnaPin);

        var approved = await Pos.AuthorizeAsync(s.Http, session.Id(), "refund", "marko", PosSetup.MarkoPin);

        Assert.Equal("refund", approved.Str("right"));
        AssertSummary(approved.GetProperty("operator"), s.Ana);
        AssertSummary(approved.GetProperty("approvedBy"), s.Marko);
        using var withoutRight = await Pos.SendAuthorizeAsync(s.Http, session.Id(), "refund", "LEAD1", "3333");
        await Pos.RightMissingAsync(withoutRight, "approverCode");
        Assert.Equal(0, await Pos.FailedAttemptsAsync(app, lead.Id()));
    }

    [Fact]
    public async Task AC41_A_wrong_approver_pin_is_OPERATOR_SIGN_IN_FAILED_counted_on_the_approver_and_five_lock_the_approver()
    {
        var s = await PosSetup.NewAsync(app);
        var session = await Pos.StartAsync(s.Http, "ANA", PosSetup.AnaPin);

        for (var i = 1; i <= 5; i++)
        {
            using var wrong = await Pos.SendAuthorizeAsync(s.Http, session.Id(), "refund", "MARKO", "0000");
            await Pos.SignInFailedAsync(wrong, "approverPin");
            if (i < 5)
                Assert.Equal(i, await Pos.FailedAttemptsAsync(app, s.Marko.Id()));
        }

        Assert.NotEqual(JsonValueKind.Null, (await Pos.GetOperatorAsync(s.Http, s.Marko.Id())).GetProperty("lockedUntil").ValueKind);
        using var rightPinWhileLocked = await Pos.SendAuthorizeAsync(s.Http, session.Id(), "refund", "MARKO", PosSetup.MarkoPin);
        await Pos.SignInFailedAsync(rightPinWhileLocked, "approverPin");
        // The session operator's counter is untouched; an unknown approver code answers the same.
        Assert.Equal(0, await Pos.FailedAttemptsAsync(app, s.Ana.Id()));
        using var unknown = await Pos.SendAuthorizeAsync(s.Http, session.Id(), "refund", "NOBODY", "0000");
        await Pos.SignInFailedAsync(unknown, "approverPin");
    }

    [Theory]
    [InlineData("ANA")]
    [InlineData("ana")]
    public async Task AC41_E7_The_operator_is_not_its_own_approver_and_nothing_is_counted(string approverCode)
    {
        var s = await PosSetup.NewAsync(app);
        var session = await Pos.StartAsync(s.Http, "ANA", PosSetup.AnaPin);

        using (var own = await Pos.SendAuthorizeAsync(s.Http, session.Id(), "refund", approverCode, "0000"))
            await Pos.RightMissingAsync(own, "approverCode");
        // Also with the right PIN and a right the operator has: an approver is another operator.
        using (var ownRight = await Pos.SendAuthorizeAsync(s.Http, session.Id(), "discount", approverCode, PosSetup.AnaPin))
            await Pos.RightMissingAsync(ownRight, "approverCode");

        Assert.Equal(0, await Pos.FailedAttemptsAsync(app, s.Ana.Id()));
    }

    [Fact]
    public async Task AC41_E8_An_approver_given_is_always_checked_even_when_the_operator_has_the_right()
    {
        var s = await PosSetup.NewAsync(app);
        var session = await Pos.StartAsync(s.Http, "ANA", PosSetup.AnaPin);

        using var wrong = await Pos.SendAuthorizeAsync(s.Http, session.Id(), "discount", "MARKO", "0000");
        await Pos.SignInFailedAsync(wrong, "approverPin");

        Assert.Equal(1, await Pos.FailedAttemptsAsync(app, s.Marko.Id()));
        AssertSummary((await Pos.AuthorizeAsync(s.Http, session.Id(), "discount", "MARKO", PosSetup.MarkoPin)).GetProperty("approvedBy"), s.Marko);
    }

    [Fact]
    public async Task AC41_E6_An_operator_without_a_role_signs_in_with_no_rights_and_a_manager_approves()
    {
        var s = await PosSetup.NewAsync(app);
        await Rules.SetAsync(s.Http, PosRules.RoleRequired, false);
        var guest = await Pos.OperatorAsync(s.Http, "GUEST", null, "7777");

        var session = await Pos.StartAsync(s.Http, "guest", "7777");

        Assert.Equal(JsonValueKind.Null, session.GetProperty("role").ValueKind);
        Pos.AssertRights(session.GetProperty("rights"));
        using (var own = await Pos.SendAuthorizeAsync(s.Http, session.Id(), "voidLine"))
            await Pos.RightMissingAsync(own, "right");
        var approved = await Pos.AuthorizeAsync(s.Http, session.Id(), "voidLine", "MARKO", PosSetup.MarkoPin);
        AssertSummary(approved.GetProperty("operator"), guest);
        AssertSummary(approved.GetProperty("approvedBy"), s.Marko);
    }

    // ---- AC-42 ----

    [Fact]
    public async Task AC42_With_secondOperatorApprovalAllowed_false_an_approval_is_refused_by_the_rule_and_own_rights_answer()
    {
        var s = await PosSetup.NewAsync(app);
        var session = await Pos.StartAsync(s.Http, "ANA", PosSetup.AnaPin);
        await Rules.SetAsync(s.Http, PosRules.ApprovalAllowed, false);

        using (var refused = await Pos.SendAuthorizeAsync(s.Http, session.Id(), "refund", "MARKO", PosSetup.MarkoPin))
            await Rules.RefusedAsync(refused, HttpStatusCode.Forbidden, "OPERATOR_RIGHT_MISSING", PosRules.ApprovalAllowed, false, "approverCode");
        Assert.Equal(JsonValueKind.Null, (await Pos.AuthorizeAsync(s.Http, session.Id(), "discount")).GetProperty("approvedBy").ValueKind);

        await Rules.ResetAsync(s.Http, PosRules.ApprovalAllowed);
        AssertSummary((await Pos.AuthorizeAsync(s.Http, session.Id(), "refund", "MARKO", PosSetup.MarkoPin)).GetProperty("approvedBy"), s.Marko);
    }

    // ---- AC-43 ----

    public static TheoryData<string, string> BadBodies() => new()
    {
        { """{ "right": "refund" }""", "sessionId" },
        { """{ "sessionId": "not-a-uuid", "right": "refund" }""", "sessionId" },
        { """{ "sessionId": "{S}", "right": "tip" }""", "right" },
        { """{ "sessionId": "{S}" }""", "right" },
        { """{ "sessionId": "{S}", "right": "Refund" }""", "right" },
        { """{ "sessionId": "{S}", "right": "refund", "approverCode": "MARKO" }""", "approverPin" },
        { """{ "sessionId": "{S}", "right": "refund", "approverPin": "9876" }""", "approverCode" },
        { """{ "sessionId": "{S}", "right": "refund", "approverCode": "MARKO", "approverPin": "98" }""", "approverPin" },
        { """{ "sessionId": "{S}", "right": "refund", "approverCode": "a b", "approverPin": "9876" }""", "approverCode" },
    };

    [Theory]
    [MemberData(nameof(BadBodies))]
    public async Task AC43_R20_step_1_An_invalid_body_is_400_with_the_key_of_the_field(string json, string key)
    {
        var s = await PosSetup.NewAsync(app);
        var session = await Pos.StartAsync(s.Http, "ANA", PosSetup.AnaPin);

        await MasterRules.InvariantAsync(s.Http.PostAsync(Pos.Authorize, HttpAssert.Raw(json.Replace("{S}", session.Id().ToString()))), key);

        Assert.Equal(0, await Pos.FailedAttemptsAsync(app, s.Marko.Id()));
    }

    [Fact]
    public async Task AC43_R20_steps_2_and_3_An_unknown_or_foreign_session_is_not_found_and_an_ended_one_not_active()
    {
        var s = await PosSetup.NewAsync(app);
        var b = await Pos.KeyAsync(app, s.Http, null, "tablet-b");
        var ofB = await Pos.StartAsync(b.Client, "ANA", PosSetup.AnaPin);
        var ended = await Pos.StartAsync(s.Http, "ANA", PosSetup.AnaPin);
        await Pos.EndAsync(s.Http, ended.Id());
        var expired = await Pos.StartAsync(s.Http, "ANA", PosSetup.AnaPin);
        await Pos.ExpireAsync(app, expired.Id());

        using (var unknown = await Pos.SendAuthorizeAsync(s.Http, Guid.CreateVersion7(), "discount"))
            Assert.Equal(new[] { "sessionId" }, McpAssert.ErrorKeys(await HttpAssert.ReferenceNotFoundAsync(unknown, "sessionId")));
        using (var foreign = await Pos.SendAuthorizeAsync(s.Http, ofB.Id(), "discount"))
            await HttpAssert.ReferenceNotFoundAsync(foreign, "sessionId");
        using (var notActive = await Pos.SendAuthorizeAsync(s.Http, ended.Id(), "discount"))
            await Pos.NotActiveAsync(notActive);
        using (var notActiveWithApprover = await Pos.SendAuthorizeAsync(s.Http, ended.Id(), "refund", "MARKO", "0000"))
            await Pos.NotActiveAsync(notActiveWithApprover);
        using (var expiredOne = await Pos.SendAuthorizeAsync(s.Http, expired.Id(), "discount"))
            await Pos.NotActiveAsync(expiredOne);
        // Steps 2 and 3 come before the approver is checked: no failure counted.
        Assert.Equal(0, await Pos.FailedAttemptsAsync(app, s.Marko.Id()));
    }

    // ---- AC-44 ----

    [Fact]
    public async Task AC44_R21_A_successful_authorize_writes_nothing()
    {
        var s = await PosSetup.NewAsync(app);
        var session = await Pos.StartAsync(s.Http, "ANA", PosSetup.AnaPin);
        var sessions = await Pos.SessionsAsync(s.Http);
        var operators = await Stock.ListAsync(s.Http, Pos.Operators);

        await Pos.AuthorizeAsync(s.Http, session.Id(), "discount");
        await Pos.AuthorizeAsync(s.Http, session.Id(), "refund", "MARKO", PosSetup.MarkoPin);

        McpAssert.JsonEqual(sessions, await Pos.SessionsAsync(s.Http), "Authorize changed the sessions (or created one for the approver)");
        McpAssert.JsonEqual(operators, await Stock.ListAsync(s.Http, Pos.Operators), "Authorize changed an operator");
    }
}
