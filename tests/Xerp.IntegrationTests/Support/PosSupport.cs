using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Xerp.IntegrationTests.Support;

/// <summary>
/// The eight rules of spec P02 (section 6.1): <c>codeChangeable</c> and <c>nameUnique</c> of POS roles and operators,
/// <c>posOperator.roleRequired</c> and the three <c>pos.*</c> decisions.
/// </summary>
public static class PosRules
{
    public const string RoleRequired = "posOperator.roleRequired";
    public const string ApprovalAllowed = "pos.secondOperatorApprovalAllowed";
    public const string SeveralOperatorsPerDevice = "pos.severalOperatorsPerDeviceAllowed";
    public const string OperatorOnSeveralDevices = "pos.operatorOnSeveralDevicesAllowed";

    /// <summary>The first segment of the keys of the two masters.</summary>
    public static readonly string[] Groups = ["posRole", "posOperator"];

    /// <summary>The eight keys of spec P02.</summary>
    public static readonly string[] Keys =
    [
        .. Groups.Select(g => $"{g}.codeChangeable"),
        .. Groups.Select(g => $"{g}.nameUnique"),
        RoleRequired,
        ApprovalAllowed,
        SeveralOperatorsPerDevice,
        OperatorOnSeveralDevices,
    ];

    public static IEnumerable<object[]> GroupData() => Groups.Select(g => new object[] { g });

    public static IEnumerable<object[]> KeyData() => Keys.Select(k => new object[] { k });

    /// <summary>Sets every one of the eight rules to the value that is not its default.</summary>
    public static async Task SetAllToOtherAsync(HttpClient client)
    {
        foreach (var key in Keys)
            await Rules.SetAsync(client, key, !Rules.Default(key));
    }
}

/// <summary>
/// Roles, operators, sessions and authorize of spec P02 (section 4) through HTTP, and the database shortcuts for the
/// time-dependent criteria (section 10: <c>lockedUntil</c> / <c>expiresAt</c> moved into the past directly).
/// </summary>
public static class Pos
{
    public const string Roles = "/api/v1/pos/roles";
    public const string Operators = "/api/v1/pos/operators";
    public const string Sessions = "/api/v1/pos/sessions";
    public const string Authorize = "/api/v1/pos/authorize";

    /// <summary>The seven member names of <c>rights</c>, in the order of section 4.A.</summary>
    public static readonly string[] RightNames = ["changePrice", "discount", "voidLine", "voidBill", "refund", "cashMovement", "closeShift"];

    /// <summary>A <c>rights</c> object with the named members <c>true</c> and every other <c>false</c>.</summary>
    public static JsonObject Rights(params string[] granted)
    {
        var rights = new JsonObject();
        foreach (var name in RightNames)
            rights[name] = granted.Contains(name);
        return rights;
    }

    public static JsonObject AllRights() => Rights(RightNames);

    /// <summary>Asserts that a <c>rights</c> object has exactly the seven members and exactly the named ones <c>true</c>.</summary>
    public static void AssertRights(JsonElement rights, params string[] granted)
    {
        Assert.Equal(RightNames.Order(StringComparer.Ordinal), rights.PropertyNames());
        foreach (var name in RightNames)
            Assert.True(rights.GetProperty(name).ValueKind == (granted.Contains(name) ? JsonValueKind.True : JsonValueKind.False),
                $"rights.{name} is {rights.GetProperty(name)}, expected {granted.Contains(name)}: {rights}");
    }

    // ---- roles ----

    public static JsonObject RoleBody(string code, string name, JsonObject? rights = null) =>
        new() { ["code"] = code, ["name"] = name, ["rights"] = rights ?? Rights() };

    public static JsonObject RoleBodyOf(JsonElement role) => new()
    {
        ["code"] = role.Str("code"),
        ["name"] = role.Str("name"),
        ["rights"] = JsonNode.Parse(role.GetProperty("rights").GetRawText()),
        ["isActive"] = role.Bool("isActive"),
    };

    public static Task<HttpResponseMessage> PostRoleAsync(HttpClient client, JsonObject body) => client.PostAsJsonAsync(Roles, body);

    public static async Task<JsonElement> RoleAsync(HttpClient client, string code, params string[] granted)
    {
        using var response = await PostRoleAsync(client, RoleBody(code, $"Role {code}", Rights(granted)));
        return await HttpAssert.JsonAsync(response, HttpStatusCode.Created);
    }

    public static async Task<JsonElement> ReplaceRoleAsync(HttpClient client, Guid id, JsonObject body)
    {
        using var response = await client.PutAsJsonAsync($"{Roles}/{id}", body);
        return await HttpAssert.JsonAsync(response, HttpStatusCode.OK);
    }

    // ---- operators ----

    public static JsonObject OperatorBody(string code, string name, Guid? roleId, string pin = "1234") => new()
    {
        ["code"] = code,
        ["name"] = name,
        ["roleId"] = roleId?.ToString(),
        ["pin"] = pin,
    };

    /// <summary>The replace body that keeps every value of the operator ("its own body"; no <c>pin</c>).</summary>
    public static JsonObject OperatorBodyOf(JsonElement op) => new()
    {
        ["code"] = op.Str("code"),
        ["name"] = op.Str("name"),
        ["roleId"] = op.GetProperty("role").ValueKind == JsonValueKind.Null ? null : op.GetProperty("role").Id().ToString(),
        ["isActive"] = op.Bool("isActive"),
    };

    public static Task<HttpResponseMessage> PostOperatorAsync(HttpClient client, JsonObject body) => client.PostAsJsonAsync(Operators, body);

    public static Task<HttpResponseMessage> PutOperatorAsync(HttpClient client, Guid id, JsonObject body) =>
        client.PutAsJsonAsync($"{Operators}/{id}", body);

    public static async Task<JsonElement> OperatorAsync(HttpClient client, string code, Guid? roleId, string pin = "1234")
    {
        using var response = await PostOperatorAsync(client, OperatorBody(code, $"Operator {code}", roleId, pin));
        return await HttpAssert.JsonAsync(response, HttpStatusCode.Created);
    }

    public static async Task<JsonElement> ReplaceOperatorAsync(HttpClient client, Guid id, JsonObject body)
    {
        using var response = await PutOperatorAsync(client, id, body);
        return await HttpAssert.JsonAsync(response, HttpStatusCode.OK);
    }

    public static async Task<JsonElement> GetOperatorAsync(HttpClient client, Guid id)
    {
        using var response = await client.GetAsync($"{Operators}/{id}");
        return await HttpAssert.JsonAsync(response, HttpStatusCode.OK);
    }

    public static Task<HttpResponseMessage> SendSetPinAsync(HttpClient client, Guid id, string pin) =>
        client.PutAsJsonAsync($"{Operators}/{id}/pin", new JsonObject { ["pin"] = pin });

    public static async Task<JsonElement> SetPinAsync(HttpClient client, Guid id, string pin)
    {
        using var response = await SendSetPinAsync(client, id, pin);
        return await HttpAssert.JsonAsync(response, HttpStatusCode.OK);
    }

    // ---- sessions ----

    public static Task<HttpResponseMessage> SendStartAsync(HttpClient client, string operatorCode, string pin) =>
        client.PostAsJsonAsync(Sessions, new JsonObject { ["operatorCode"] = operatorCode, ["pin"] = pin });

    public static async Task<JsonElement> StartAsync(HttpClient client, string operatorCode, string pin = "1234")
    {
        using var response = await SendStartAsync(client, operatorCode, pin);
        return await HttpAssert.JsonAsync(response, HttpStatusCode.Created);
    }

    /// <summary>A start that must fail: <c>409 OPERATOR_SIGN_IN_FAILED</c>, exactly the key <c>pin</c>, no rule (R10).</summary>
    public static async Task<JsonElement> SignInFailsAsync(HttpClient client, string operatorCode, string pin)
    {
        using var response = await SendStartAsync(client, operatorCode, pin);
        var text = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(pin, text, StringComparison.Ordinal);
        return await SignInFailedAsync(response, "pin");
    }

    public static async Task<JsonElement> SignInFailedAsync(HttpResponseMessage response, string key)
    {
        var problem = await HttpAssert.ProblemAsync(response, HttpStatusCode.Conflict, "OPERATOR_SIGN_IN_FAILED");
        Assert.Equal(new[] { key }, McpAssert.ErrorKeys(problem));
        return Rules.AssertNamesNoRule(problem);
    }

    public static Task<HttpResponseMessage> SendGetSessionAsync(HttpClient client, Guid id) => client.GetAsync($"{Sessions}/{id}");

    public static async Task<JsonElement> GetSessionAsync(HttpClient client, Guid id)
    {
        using var response = await SendGetSessionAsync(client, id);
        return await HttpAssert.JsonAsync(response, HttpStatusCode.OK);
    }

    public static Task<HttpResponseMessage> SendEndAsync(HttpClient client, Guid id) => client.PostAsync($"{Sessions}/{id}/end", null);

    public static async Task<JsonElement> EndAsync(HttpClient client, Guid id)
    {
        using var response = await SendEndAsync(client, id);
        return await HttpAssert.JsonAsync(response, HttpStatusCode.OK);
    }

    public static Task<JsonElement> SessionsAsync(HttpClient client, string query = "") => Stock.ListAsync(client, Sessions + query);

    public static async Task<string> StatusAsync(HttpClient client, Guid session) => (await GetSessionAsync(client, session)).Str("status");

    // ---- authorize ----

    public static JsonObject AuthorizeBody(Guid session, string right, string? approverCode = null, string? approverPin = null)
    {
        var body = new JsonObject { ["sessionId"] = session.ToString(), ["right"] = right };
        if (approverCode is not null)
            body["approverCode"] = approverCode;
        if (approverPin is not null)
            body["approverPin"] = approverPin;
        return body;
    }

    public static Task<HttpResponseMessage> SendAuthorizeAsync(HttpClient client, JsonObject body) => client.PostAsJsonAsync(Authorize, body);

    public static Task<HttpResponseMessage> SendAuthorizeAsync(HttpClient client, Guid session, string right, string? approverCode = null, string? approverPin = null) =>
        SendAuthorizeAsync(client, AuthorizeBody(session, right, approverCode, approverPin));

    public static async Task<JsonElement> AuthorizeAsync(HttpClient client, Guid session, string right, string? approverCode = null, string? approverPin = null)
    {
        using var response = await SendAuthorizeAsync(client, session, right, approverCode, approverPin);
        return await HttpAssert.JsonAsync(response, HttpStatusCode.OK);
    }

    /// <summary><c>403 OPERATOR_RIGHT_MISSING</c> with exactly the given key and no rule (R20, steps 4 and 5).</summary>
    public static async Task<JsonElement> RightMissingAsync(HttpResponseMessage response, string key)
    {
        var problem = await HttpAssert.ProblemAsync(response, HttpStatusCode.Forbidden, "OPERATOR_RIGHT_MISSING");
        Assert.Equal(new[] { key }, McpAssert.ErrorKeys(problem));
        return Rules.AssertNamesNoRule(problem);
    }

    /// <summary><c>409 SESSION_NOT_ACTIVE</c>, key <c>sessionId</c>.</summary>
    public static async Task<JsonElement> NotActiveAsync(HttpResponseMessage response)
    {
        var problem = await HttpAssert.ProblemAsync(response, HttpStatusCode.Conflict, "SESSION_NOT_ACTIVE");
        Assert.Equal(new[] { "sessionId" }, McpAssert.ErrorKeys(problem));
        return problem;
    }

    // ---- keys ----

    /// <summary>A second key of the tenant with the given scope (<c>null</c> = omitted).</summary>
    public static async Task<TestKey> KeyAsync(XerpFixture app, HttpClient client, string? scope, string name = "till", string actorType = "human")
    {
        var body = new JsonObject { ["name"] = name, ["actorType"] = actorType };
        if (scope is not null)
            body["scope"] = scope;
        using var response = await client.PostAsJsonAsync(Keys.Path, body);
        var created = await HttpAssert.JsonAsync(response, HttpStatusCode.Created);
        var key = created.Str("key");
        return new TestKey(created.Id(), key, created, app.WithBearer(key));
    }

    // ---- the database (section 10) ----

    public static Task<int> UnlockInThePastAsync(XerpFixture app, Guid operatorId) =>
        app.ExecuteSqlAsync("""UPDATE "PosOperators" SET "LockedUntil" = now() - interval '1 minute' WHERE "Id" = @id""", ("id", operatorId));

    public static Task<int> ExpireAsync(XerpFixture app, Guid sessionId) =>
        app.ExecuteSqlAsync(
            """UPDATE "PosSessions" SET "StartedAt" = now() - interval '13 hours', "ExpiresAt" = now() - interval '1 hour' WHERE "Id" = @id""",
            ("id", sessionId));

    public static Task<int?> FailedAttemptsAsync(XerpFixture app, Guid operatorId) =>
        app.ScalarAsync<int?>("""SELECT "FailedPinAttempts" FROM "PosOperators" WHERE "Id" = @id""", ("id", operatorId));
}

/// <summary>A tenant with a waiter (void line only), a manager (every right) and their operators ANA / MARKO, PIN 1234 / 9876.</summary>
public sealed record PosSetup(TestTenant Tenant, JsonElement Waiter, JsonElement Manager, JsonElement Ana, JsonElement Marko)
{
    public const string AnaPin = "1234";
    public const string MarkoPin = "9876";

    public HttpClient Http => Tenant.Client;

    public static async Task<PosSetup> NewAsync(XerpFixture app)
    {
        var tenant = await app.NewTenantAsync();
        var waiter = await Pos.RoleAsync(tenant.Client, "WAITER", "voidLine", "discount");
        var manager = await Pos.RoleAsync(tenant.Client, "MANAGER", Pos.RightNames);
        var ana = await Pos.OperatorAsync(tenant.Client, "ANA", waiter.Id(), AnaPin);
        var marko = await Pos.OperatorAsync(tenant.Client, "MARKO", manager.Id(), MarkoPin);
        return new PosSetup(tenant, waiter, manager, ana, marko);
    }
}
