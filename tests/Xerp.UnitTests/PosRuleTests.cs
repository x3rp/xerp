using Xerp.Application.Pos;
using Xerp.Application.Ports;
using Xerp.Domain.Pos;
using Xerp.Domain.Rules;

namespace Xerp.UnitTests;

/// <summary>
/// Spec P02, AC-03: PIN form, the lockout arithmetic, session status and the authorize decision of R20 as pure
/// functions with a fixed clock; the allow-list of a <c>pos</c> key (R32, R33) as one table for both transports.
/// </summary>
public class PosRuleTests
{
    private static readonly DateTime Now = new(2026, 10, 18, 12, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData("0000", true)]
    [InlineData("12345678", true)]
    [InlineData("123", false)]
    [InlineData("123456789", false)]
    [InlineData("12a4", false)]
    [InlineData(" 1234", false)]
    [InlineData("1234 ", false)]
    [InlineData("１２３４", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Pin_is_four_to_eight_ascii_digits_untrimmed(string? pin, bool valid) =>
        Assert.Equal(valid, PosRules.IsValidPin(pin));

    [Fact]
    public void Fifth_failure_locks_for_fifteen_minutes_and_resets_the_count()
    {
        var failed = 0;
        DateTime? lockedUntil = null;
        for (var i = 1; i <= 4; i++)
        {
            (failed, lockedUntil) = PosRules.AfterFailedPin(failed, Now);
            Assert.Equal(i, failed);
            Assert.Null(lockedUntil);
        }
        (failed, lockedUntil) = PosRules.AfterFailedPin(failed, Now);
        Assert.Equal(0, failed);
        Assert.Equal(Now.AddMinutes(15), lockedUntil);
        Assert.True(PosRules.IsLocked(lockedUntil, Now.AddMinutes(14)));
        Assert.False(PosRules.IsLocked(lockedUntil, Now.AddMinutes(15)));
        Assert.False(PosRules.IsLocked(null, Now));
    }

    [Fact]
    public void Session_lasts_twelve_hours_and_reads_ended_at_expiry()
    {
        var expiresAt = PosRules.SessionExpiresAt(Now);
        Assert.Equal(Now.AddHours(12), expiresAt);
        Assert.Equal(PosSessionStatus.Active, PosRules.SessionStatus(null, expiresAt, Now.AddHours(11)));
        Assert.Null(PosRules.SessionEndedAt(null, expiresAt, Now.AddHours(11)));
        Assert.Equal(PosSessionStatus.Ended, PosRules.SessionStatus(null, expiresAt, expiresAt));
        Assert.Equal(expiresAt, PosRules.SessionEndedAt(null, expiresAt, Now.AddHours(13)));
        Assert.Equal(PosSessionStatus.Ended, PosRules.SessionStatus(Now.AddHours(1), expiresAt, Now.AddHours(2)));
        Assert.Equal(Now.AddHours(1), PosRules.SessionEndedAt(Now.AddHours(1), expiresAt, Now.AddHours(13)));
    }

    [Theory]
    // operatorHasRight, approverGiven, approvalAllowed, approverIsOperator, approverSignedIn, approverHasRight
    [InlineData(true, false, true, false, false, false, AuthorizeOutcome.Allowed)]
    [InlineData(false, false, true, false, false, false, AuthorizeOutcome.OperatorLacksRight)]
    [InlineData(false, true, true, false, true, true, AuthorizeOutcome.Approved)]
    [InlineData(false, true, true, false, true, false, AuthorizeOutcome.ApproverLacksRight)]
    [InlineData(true, true, true, false, false, true, AuthorizeOutcome.ApproverSignInFailed)] // E8
    [InlineData(false, true, false, false, true, true, AuthorizeOutcome.ApprovalNotAllowed)]
    [InlineData(true, true, false, false, true, true, AuthorizeOutcome.ApprovalNotAllowed)]
    [InlineData(false, true, true, true, true, true, AuthorizeOutcome.ApproverIsOperator)] // E7
    public void Authorize_decision(
        bool operatorHasRight, bool approverGiven, bool approvalAllowed, bool approverIsOperator, bool approverSignedIn, bool approverHasRight,
        AuthorizeOutcome expected) =>
        Assert.Equal(expected, PosRules.Decide(operatorHasRight, approverGiven, approvalAllowed, approverIsOperator, approverSignedIn, approverHasRight));

    [Fact]
    public void Role_required_is_the_tenants_rule()
    {
        var write = new PosOperatorWrite("ANA", "Ana", RoleIdGiven: true, RoleId: null, Pin: "1234", IsActive: null, Replace: false);
        var refused = PosValidation.Operator(write, TenantRules.Defaults);
        Assert.False(refused.IsSuccess);
        var rule = Assert.Single(refused.Error.Rules!);
        Assert.Equal((RuleRegistry.PosOperatorRoleRequired.Key, true, "roleId"), (rule.Key, rule.Value, Assert.Single(rule.Fields)));

        var allowed = PosValidation.Operator(write, new TenantRules(new Dictionary<string, bool> { [RuleRegistry.PosOperatorRoleRequired.Key] = false }));
        Assert.True(allowed.IsSuccess);
        Assert.Null(allowed.Value.RoleId);
    }

    [Theory]
    [InlineData("GET", "/api/v1/pos/operators", true)]
    [InlineData("POST", "/pos/sessions", true)]
    [InlineData("POST", "/pos/sessions/{id:guid}/end", true)]
    [InlineData("POST", "/pos/operators", false)]
    [InlineData("PUT", "/pos/operators/{id:guid}/pin", false)]
    [InlineData("GET", "/articles/{id:guid}", true)]
    [InlineData("GET", "/articles/sales-data", true)]
    [InlineData("PUT", "/articles/{articleId:guid}/sales-data", false)]
    [InlineData("GET", "/api-keys", false)]
    [InlineData("PUT", "/rules/{key}", false)]
    [InlineData("GET", "/rules/{key}", true)]
    [InlineData("GET", "/warehouses/by-code/{code}", true)]
    public void Allow_list_of_routes(string method, string route, bool allowed) =>
        Assert.Equal(allowed, PosAllowList.AllowsRoute(method, route.StartsWith("/api/v1") ? route["/api/v1".Length..] : route));

    [Theory]
    [InlineData("pos_session_start", true)]
    [InlineData("pos_operator_list", true)]
    [InlineData("pos_operator_create", false)]
    [InlineData("pos_role_update", false)]
    [InlineData("article_sales_data_list", true)]
    [InlineData("article_sales_data_set", false)]
    [InlineData("rule_set", false)]
    [InlineData("api_key_list", false)]
    public void Allow_list_of_tools(string tool, bool allowed) => Assert.Equal(allowed, PosAllowList.AllowsTool(tool));
}
