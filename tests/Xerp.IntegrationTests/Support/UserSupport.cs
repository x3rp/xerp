using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Xerp.IntegrationTests.Support;

/// <summary>Spec U01: users and sign-in.</summary>
public static class Users
{
    public const string Path = "/api/v1/users";
    public const string LockRule = "users.lockAfterFailedLogins";
    public const string ShortPasswordsRule = "users.shortPasswordsAllowed";

    public static readonly string[] RuleKeys = [LockRule, ShortPasswordsRule];

    public static string LoginPath(string tenantCode) => $"/api/v1/login/{tenantCode}";

    public static async Task<JsonElement> CreateAsync(HttpClient client, string username, string password = "correct-horse", string name = "Ana Anić")
    {
        using var response = await client.PostAsJsonAsync(Path, new { username, name, password });
        return await HttpAssert.JsonAsync(response, HttpStatusCode.Created);
    }

    public static Task<HttpResponseMessage> SendLoginAsync(XerpFixture app, string tenantCode, string username, string password) =>
        app.Anonymous().PostAsJsonAsync(LoginPath(tenantCode), new { username, password });

    public static async Task<JsonElement> LoginAsync(XerpFixture app, string tenantCode, string username, string password = "correct-horse")
    {
        using var response = await SendLoginAsync(app, tenantCode, username, password);
        return await HttpAssert.JsonAsync(response, HttpStatusCode.OK);
    }
}
