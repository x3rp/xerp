using System.Net;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using Xerp.IntegrationTests.Support;

namespace Xerp.IntegrationTests;

/// <summary>
/// Spec P02, AC-10 to AC-12 (the registry has 48 rules, the tool list 100 names; HTTP and tool parity), AC-60 and AC-61
/// (tenant isolation, T1–T3) and AC-62 (T4): roles, operators, sessions and authorize through the tools, with the same
/// data, <c>code</c>, <c>errors</c> keys and <c>rules</c> as HTTP, and never a PIN in a tool result.
/// </summary>
[Collection(XerpCollection.Name)]
public class PosRuleIsolationAndMcpTests(XerpFixture app)
{
    private sealed record Session(PosSetup S, McpConnection Mcp) : IAsyncDisposable
    {
        public HttpClient Http => S.Http;

        public ValueTask DisposeAsync() => Mcp.DisposeAsync();
    }

    /// <summary>The MCP connection uses the tenant's initial key, so HTTP and the tools see the same sessions (R13).</summary>
    private async Task<Session> OpenAsync()
    {
        var setup = await PosSetup.NewAsync(app);
        return new Session(setup, await app.McpAsync(setup.Tenant.Key));
    }

    private static void AssertSameRefusal(JsonElement problem, JsonElement toolError)
    {
        Assert.Equal(problem.Str("code"), toolError.Str("code"));
        Assert.Equal(Rules.ErrorKeysInOrder(problem), Rules.ErrorKeysInOrder(toolError));
        Assert.Equal(problem.TryGetProperty("rules", out var rules), toolError.TryGetProperty("rules", out var toolRules));
        if (rules.ValueKind != JsonValueKind.Undefined)
            McpAssert.JsonEqual(rules, toolRules, "'rules' of the tool error differs from HTTP");
    }

    private static string Text(CallToolResult result) =>
        string.Join("\n", result.Content.OfType<TextContentBlock>().Select(t => t.Text)) + result.StructuredContent;

    // ---- AC-10 ----

    [Fact]
    public async Task AC10_A_new_tenant_lists_48_rules_with_the_eight_of_POS_at_their_defaults()
    {
        var tenant = await app.NewTenantAsync();

        var list = await Rules.ListAsync(tenant.Client, "?limit=500");

        Assert.Equal(48, list.Total());
        Assert.Equal(Rules.All, list.RuleKeys());
        var expected = new (string Key, bool Default)[]
        {
            ("pos.operatorOnSeveralDevicesAllowed", true),
            ("pos.secondOperatorApprovalAllowed", true),
            ("pos.severalOperatorsPerDeviceAllowed", true),
            ("posOperator.codeChangeable", true),
            ("posOperator.nameUnique", false),
            ("posOperator.roleRequired", true),
            ("posRole.codeChangeable", true),
            ("posRole.nameUnique", false),
        };
        Assert.Equal(expected.Select(e => e.Key), PosRules.Keys.Order(StringComparer.Ordinal));
        var rules = list.Items().ToDictionary(r => r.Str("key"));
        foreach (var (key, value) in expected)
        {
            Assert.True(rules.TryGetValue(key, out var rule), $"The registry has no rule '{key}'.");
            Rules.AssertAtDefault(rule, key);
            Rules.AssertBool(rule, "default", value);
            Assert.Equal(key.Split('.')[0], rule.Str("group"));
        }
        Assert.Equal(3, (await Rules.ListAsync(tenant.Client, "?group=pos")).Total());
        Assert.Equal(3, (await Rules.ListAsync(tenant.Client, "?group=posOperator")).Total());
        Assert.Equal(2, (await Rules.ListAsync(tenant.Client, "?group=posRole")).Total());
    }

    // ---- AC-11 ----

    [Theory]
    [MemberData(nameof(PosRules.KeyData), MemberType = typeof(PosRules))]
    public async Task AC11_Each_of_the_eight_accepts_both_values_and_resets(string key)
    {
        var tenant = await app.NewTenantAsync();
        var initial = Rules.Default(key);

        Rules.AssertTenantValue(await Rules.SetAsync(tenant.Client, key, !initial), key, !initial, tenant.ApiKeyId);
        Rules.AssertTenantValue(await Rules.SetAsync(tenant.Client, key, initial), key, initial, tenant.ApiKeyId);
        Rules.AssertAtDefault(await Rules.ResetAsync(tenant.Client, key), key);
    }

    // ---- AC-12 ----

    [Fact]
    public async Task AC12_One_operation_of_roles_operators_sessions_and_authorize_through_a_tool_equals_HTTP()
    {
        await using var s = await OpenAsync();
        var session = await Pos.StartAsync(s.Http, "ANA", PosSetup.AnaPin);

        Assert.Equal(100, (await s.Mcp.Client.ListToolsAsync()).Select(t => t.ProtocolTool.Name).Distinct().Count());
        McpAssert.JsonEqual(await Stock.ListAsync(s.Http, Pos.Roles), await s.Mcp.OkAsync("pos_role_list", new { }));
        using (var byCode = await s.Http.GetAsync($"{Pos.Operators}/by-code/ana"))
            McpAssert.JsonEqual(await HttpAssert.JsonAsync(byCode, HttpStatusCode.OK), await s.Mcp.OkAsync("pos_operator_get", new { code = "ana" }));
        McpAssert.JsonEqual(await Pos.GetSessionAsync(s.Http, session.Id()), await s.Mcp.OkAsync("pos_session_get", new { id = session.Id() }));
        McpAssert.JsonEqual(await Pos.SessionsAsync(s.Http, "?status=active"), await s.Mcp.OkAsync("pos_session_list", new { status = "active" }));
        McpAssert.JsonEqual(await Pos.AuthorizeAsync(s.Http, session.Id(), "discount"),
            await s.Mcp.OkAsync("pos_authorize", new { sessionId = session.Id(), right = "discount" }));
    }

    // ---- AC-60 ----

    [Fact]
    public async Task AC60_T1_Tenant_Y_cannot_sign_in_with_X_s_operator_and_X_s_counter_is_unchanged()
    {
        var x = await PosSetup.NewAsync(app);
        var y = await app.NewTenantAsync();
        await Pos.SignInFailsAsync(x.Http, "ANA", "0000");

        await Pos.SignInFailsAsync(y.Client, "ANA", PosSetup.AnaPin);
        await Pos.SignInFailsAsync(y.Client, "ANA", "0000");

        Assert.Equal(1, await Pos.FailedAttemptsAsync(app, x.Ana.Id()));
        Assert.Equal(0, (await Pos.SessionsAsync(y.Client)).Total());
    }

    [Fact]
    public async Task AC60_Y_cannot_read_replace_delete_or_reference_X_s_role_operator_or_session_and_creates_the_same_codes()
    {
        var x = await PosSetup.NewAsync(app);
        var y = await app.NewTenantAsync();
        var session = await Pos.StartAsync(x.Http, "ANA", PosSetup.AnaPin);
        var role = x.Waiter.Id();
        var op = x.Ana.Id();

        foreach (var path in new[] { $"{Pos.Roles}/{role}", $"{Pos.Roles}/by-code/WAITER", $"{Pos.Operators}/{op}", $"{Pos.Operators}/by-code/ANA", $"{Pos.Sessions}/{session.Id()}" })
        {
            using var read = await y.Client.GetAsync(path);
            await HttpAssert.NotFoundAsync(read);
        }
        using (var replaceRole = await y.Client.PutAsync($"{Pos.Roles}/{role}", HttpAssert.Raw(Pos.RoleBodyOf(x.Waiter).ToJsonString())))
            await HttpAssert.NotFoundAsync(replaceRole);
        using (var replaceOp = await Pos.PutOperatorAsync(y.Client, op, Pos.OperatorBodyOf(x.Ana)))
            await HttpAssert.NotFoundAsync(replaceOp);
        using (var setPin = await Pos.SendSetPinAsync(y.Client, op, "1111"))
            await HttpAssert.NotFoundAsync(setPin);
        using (var deleteRole = await y.Client.DeleteAsync($"{Pos.Roles}/{role}"))
            await HttpAssert.NotFoundAsync(deleteRole);
        using (var deleteOp = await y.Client.DeleteAsync($"{Pos.Operators}/{op}"))
            await HttpAssert.NotFoundAsync(deleteOp);
        using (var end = await Pos.SendEndAsync(y.Client, session.Id()))
            await HttpAssert.NotFoundAsync(end);
        using (var authorize = await Pos.SendAuthorizeAsync(y.Client, session.Id(), "discount"))
            await HttpAssert.ReferenceNotFoundAsync(authorize, "sessionId");
        using (var reference = await Pos.PostOperatorAsync(y.Client, Pos.OperatorBody("IVA", "Iva", role)))
            await HttpAssert.ReferenceNotFoundAsync(reference, "roleId");

        Assert.Equal(0, (await Stock.ListAsync(y.Client, Pos.Roles)).Total());
        Assert.Equal(0, (await Stock.ListAsync(y.Client, Pos.Operators)).Total());
        var ownRole = await Pos.RoleAsync(y.Client, "WAITER");
        await Pos.OperatorAsync(y.Client, "ANA", ownRole.Id(), "5555");
        Assert.Equal("active", (await Pos.StartAsync(y.Client, "ANA", "5555")).Str("status"));
        // X is untouched.
        McpAssert.JsonEqual(x.Ana, await Pos.GetOperatorAsync(x.Http, op));
        Assert.Equal("active", await Pos.StatusAsync(x.Http, session.Id()));
        await Pos.SignInFailsAsync(x.Http, "ANA", "5555");
    }

    // ---- AC-61 ----

    [Fact]
    public async Task AC61_T3_Rules_set_in_X_change_nothing_in_Y()
    {
        var x = await app.NewTenantAsync();
        await PosRules.SetAllToOtherAsync(x.Client);
        var y = await PosSetup.NewAsync(app);
        var b = await Pos.KeyAsync(app, y.Http, null, "tablet-b");

        foreach (var key in PosRules.Keys)
            Rules.AssertAtDefault(await Rules.GetAsync(y.Http, key), key);
        // AC-23: a role is required.
        await MasterRules.RequiredAsync(Pos.PostOperatorAsync(y.Http, Pos.OperatorBody("NOROLE", "No role", null)), PosRules.RoleRequired, "roleId");
        // AC-38: two operators on one key, one operator on two keys.
        var anaOnA = await Pos.StartAsync(y.Http, "ANA", PosSetup.AnaPin);
        var markoOnA = await Pos.StartAsync(y.Http, "MARKO", PosSetup.MarkoPin);
        await Pos.StartAsync(b.Client, "ANA", PosSetup.AnaPin);
        Assert.Equal("active", await Pos.StatusAsync(y.Http, anaOnA.Id()));
        Assert.Equal("active", await Pos.StatusAsync(y.Http, markoOnA.Id()));
        // AC-41: an approval is allowed.
        await Pos.AuthorizeAsync(y.Http, anaOnA.Id(), "refund", "MARKO", PosSetup.MarkoPin);
        // codeChangeable and nameUnique at their defaults.
        await Pos.ReplaceRoleAsync(y.Http, y.Waiter.Id(), Pos.RoleBodyOf(y.Waiter).With("code", "SERVER").With("name", y.Manager.Str("name")));
        await Pos.ReplaceOperatorAsync(y.Http, y.Ana.Id(), Pos.OperatorBodyOf(y.Ana).With("code", "ANA2").With("name", y.Marko.Str("name")));
    }

    // ---- AC-62 ----

    [Fact]
    public async Task AC62_T4_Through_tools_only_create_sign_in_and_approve_with_the_same_data_as_HTTP_and_no_pin()
    {
        await using var s = await OpenAsync();
        var rights = Pos.Rights("refund");

        var role = await s.Mcp.OkAsync("pos_role_create", new { code = "CASHIER", name = "Cashier", rights });
        var createOp = await s.Mcp.CallAsync("pos_operator_create", new { code = "IVA", name = "Iva", roleId = role.Id(), pin = "4826" });
        var op = McpAssert.Success(createOp);
        Assert.DoesNotContain("4826", Text(createOp), StringComparison.Ordinal);
        var start = await s.Mcp.CallAsync("pos_session_start", new { operatorCode = "iva", pin = "4826" });
        var session = McpAssert.Success(start);
        Assert.DoesNotContain("4826", Text(start), StringComparison.Ordinal);
        var authorize = await s.Mcp.CallAsync("pos_authorize", new { sessionId = session.Id(), right = "voidBill", approverCode = "MARKO", approverPin = PosSetup.MarkoPin });
        var approved = McpAssert.Success(authorize);
        Assert.DoesNotContain(PosSetup.MarkoPin, Text(authorize), StringComparison.Ordinal);
        var setPin = await s.Mcp.CallAsync("pos_operator_set_pin", new { id = op.Id(), pin = "9182" });
        Assert.DoesNotContain("9182", Text(setPin), StringComparison.Ordinal);

        using (var readRole = await s.Http.GetAsync($"{Pos.Roles}/{role.Id()}"))
            McpAssert.JsonEqual(await HttpAssert.JsonAsync(readRole, HttpStatusCode.OK), role);
        Pos.AssertRights(role.GetProperty("rights"), "refund");
        Assert.Equal(role.Id(), op.GetProperty("role").Id());
        Assert.Equal("ended", (await Pos.GetSessionAsync(s.Http, session.Id())).Str("status"));
        Assert.Equal(new[] { "approvedBy", "operator", "right" }, approved.PropertyNames());
        Assert.Equal((op.Id(), s.S.Marko.Id()), (approved.GetProperty("operator").Id(), approved.GetProperty("approvedBy").Id()));
        McpAssert.JsonEqual(await Pos.GetOperatorAsync(s.Http, op.Id()), McpAssert.Success(setPin));
    }

    [Fact]
    public async Task AC62_T4_The_three_new_codes_carry_the_same_code_errors_and_rules_as_HTTP()
    {
        await using var s = await OpenAsync();
        var session = await Pos.StartAsync(s.Http, "ANA", PosSetup.AnaPin);
        var ended = await Pos.StartAsync(s.Http, "MARKO", PosSetup.MarkoPin);
        await Pos.EndAsync(s.Http, ended.Id());

        // OPERATOR_SIGN_IN_FAILED (start and authorize).
        var httpSignIn = await Pos.SignInFailsAsync(s.Http, "ANA", "0000");
        var toolSignIn = await s.Mcp.CallAsync("pos_session_start", new { operatorCode = "ANA", pin = "0000" });
        AssertSameRefusal(httpSignIn, McpAssert.Error(toolSignIn, "OPERATOR_SIGN_IN_FAILED", "pin"));
        Assert.DoesNotContain("0000", Text(toolSignIn), StringComparison.Ordinal);
        JsonElement httpApprover;
        using (var r = await Pos.SendAuthorizeAsync(s.Http, session.Id(), "refund", "MARKO", "1111"))
            httpApprover = await Pos.SignInFailedAsync(r, "approverPin");
        AssertSameRefusal(httpApprover, await s.Mcp.ErrorAsync("pos_authorize",
            new { sessionId = session.Id(), right = "refund", approverCode = "MARKO", approverPin = "1111" }, "OPERATOR_SIGN_IN_FAILED", "approverPin"));

        // OPERATOR_RIGHT_MISSING without and with rules.
        JsonElement httpMissing;
        using (var r = await Pos.SendAuthorizeAsync(s.Http, session.Id(), "refund"))
            httpMissing = await Pos.RightMissingAsync(r, "right");
        AssertSameRefusal(httpMissing, await s.Mcp.ErrorAsync("pos_authorize", new { sessionId = session.Id(), right = "refund" }, "OPERATOR_RIGHT_MISSING", "right"));
        await Rules.SetAsync(s.Http, PosRules.ApprovalAllowed, false);
        JsonElement httpByRule;
        using (var r = await Pos.SendAuthorizeAsync(s.Http, session.Id(), "refund", "MARKO", PosSetup.MarkoPin))
            httpByRule = await Rules.RefusedAsync(r, HttpStatusCode.Forbidden, "OPERATOR_RIGHT_MISSING", PosRules.ApprovalAllowed, false, "approverCode");
        var toolByRule = await s.Mcp.CallAsync("pos_authorize", new { sessionId = session.Id(), right = "refund", approverCode = "MARKO", approverPin = PosSetup.MarkoPin });
        AssertSameRefusal(httpByRule, Rules.AssertNames(McpAssert.Error(toolByRule, "OPERATOR_RIGHT_MISSING", "approverCode"), PosRules.ApprovalAllowed, false, "approverCode"));
        Assert.DoesNotContain(PosSetup.MarkoPin, Text(toolByRule), StringComparison.Ordinal);

        // SESSION_NOT_ACTIVE.
        JsonElement httpEnded;
        using (var r = await Pos.SendAuthorizeAsync(s.Http, ended.Id(), "refund"))
            httpEnded = await Pos.NotActiveAsync(r);
        AssertSameRefusal(httpEnded, await s.Mcp.ErrorAsync("pos_authorize", new { sessionId = ended.Id(), right = "refund" }, "SESSION_NOT_ACTIVE", "sessionId"));
    }

    // ---- Section 5 annotations of the six non-master tools (P02-q Q2) ----

    [Fact]
    public async Task S5_The_six_non_master_tools_carry_the_annotations_of_section_5()
    {
        var tenant = await app.NewTenantAsync();
        await using var mcp = await app.McpAsync(tenant.Key);
        var tools = (await mcp.Client.ListToolsAsync()).ToDictionary(tool => tool.ProtocolTool.Name, tool => tool.ProtocolTool.Annotations);

        // name -> (readOnlyHint, destructiveHint, idempotentHint); a read-only tool is not judged on the other two.
        var expected = new (string Name, bool ReadOnly, bool? Destructive, bool? Idempotent)[]
        {
            ("pos_operator_set_pin", false, true, true),
            ("pos_session_start", false, false, false),
            ("pos_session_end", false, false, true),
            ("pos_session_get", true, null, null),
            ("pos_session_list", true, null, null),
            ("pos_authorize", false, false, false),
        };
        foreach (var (name, readOnly, destructive, idempotent) in expected)
        {
            Assert.True(tools.TryGetValue(name, out var a) && a is not null, $"{name} is not listed with annotations.");
            Assert.True(a!.ReadOnlyHint == readOnly, $"{name}: readOnlyHint {a.ReadOnlyHint}, expected {readOnly}");
            if (destructive is not null) Assert.True(a.DestructiveHint == destructive, $"{name}: destructiveHint {a.DestructiveHint}, expected {destructive}");
            if (idempotent is not null) Assert.True(a.IdempotentHint == idempotent, $"{name}: idempotentHint {a.IdempotentHint}, expected {idempotent}");
        }
    }
}
