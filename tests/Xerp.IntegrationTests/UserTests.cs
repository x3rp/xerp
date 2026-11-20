using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xerp.IntegrationTests.Support;

namespace Xerp.IntegrationTests;

/// <summary>Spec U01: users sign in to their tenant with username and password and get a session key.</summary>
[Collection(XerpCollection.Name)]
public class UserTests(XerpFixture app)
{
    [Fact]
    public async Task Login_issues_an_expiring_session_key_that_acts_as_the_user()
    {
        var tenant = await app.NewTenantAsync();
        var user = await Users.CreateAsync(tenant.Client, "  Ana  ");
        Assert.Equal("ana", user.Str("username"));
        Assert.False(user.TryGetProperty("passwordHash", out _));

        var login = await Users.LoginAsync(app, tenant.Code.ToUpperInvariant(), "ANA");

        Assert.Equal(tenant.Id, login.GetProperty("tenant").GetProperty("id").GetGuid());
        Assert.Equal("ana", login.GetProperty("user").Str("username"));
        Assert.InRange(login.GetProperty("expiresAt").GetDateTime().ToUniversalTime(), DateTime.UtcNow.AddHours(11), DateTime.UtcNow.AddHours(13));
        using var session = app.WithBearer(login.Str("key"));
        using var whoami = await session.GetAsync("/api/v1/whoami");
        var me = await HttpAssert.JsonAsync(whoami, HttpStatusCode.OK);
        Assert.Equal("ana", me.GetProperty("user").Str("username"));
        Assert.Equal("human", me.GetProperty("actor").Str("actorType"));
        Assert.Equal(1, await app.ScalarAsync<long>("""SELECT count(*) FROM "ApiKeys" WHERE "UserId" = @id""", ("id", user.GetProperty("id").GetGuid())));
    }

    [Fact]
    public async Task Every_cause_of_failure_answers_LOGIN_FAILED()
    {
        var tenant = await app.NewTenantAsync();
        var user = await Users.CreateAsync(tenant.Client, "ana");

        foreach (var (code, username, password) in new[]
        {
            ("no-such-tenant", "ana", "correct-horse"), (tenant.Code, "nobody", "correct-horse"), (tenant.Code, "ana", "wrong-password"),
        })
        {
            using var response = await Users.SendLoginAsync(app, code, username, password);
            await HttpAssert.ProblemAsync(response, HttpStatusCode.Unauthorized, "LOGIN_FAILED");
        }

        using var deactivate = await tenant.Client.PostAsync($"{Users.Path}/{user.GetProperty("id").GetGuid()}/deactivate", null);
        await HttpAssert.JsonAsync(deactivate, HttpStatusCode.OK);
        using var inactive = await Users.SendLoginAsync(app, tenant.Code, "ana", "correct-horse");
        await HttpAssert.ProblemAsync(inactive, HttpStatusCode.Unauthorized, "LOGIN_FAILED");
    }

    [Fact]
    public async Task Five_wrong_passwords_lock_the_user_and_a_new_password_unlocks()
    {
        var tenant = await app.NewTenantAsync();
        var id = (await Users.CreateAsync(tenant.Client, "ana")).GetProperty("id").GetGuid();
        for (var i = 0; i < 5; i++)
        {
            using var wrong = await Users.SendLoginAsync(app, tenant.Code, "ana", "wrong-password");
            await HttpAssert.ProblemAsync(wrong, HttpStatusCode.Unauthorized, "LOGIN_FAILED");
        }

        using var locked = await Users.SendLoginAsync(app, tenant.Code, "ana", "correct-horse");
        await HttpAssert.ProblemAsync(locked, HttpStatusCode.Unauthorized, "LOGIN_FAILED");
        using var get = await tenant.Client.GetAsync($"{Users.Path}/{id}");
        Assert.True((await HttpAssert.JsonAsync(get, HttpStatusCode.OK)).GetProperty("isLocked").GetBoolean());

        using var reset = await tenant.Client.PostAsJsonAsync($"{Users.Path}/{id}/password", new { password = "battery-staple" });
        await HttpAssert.JsonAsync(reset, HttpStatusCode.OK);
        await Users.LoginAsync(app, tenant.Code, "ana", "battery-staple");
    }

    [Fact]
    public async Task Without_the_lock_rule_wrong_passwords_never_lock()
    {
        var tenant = await app.NewTenantAsync();
        await Rules.SetAsync(tenant.Client, Users.LockRule, false);
        await Users.CreateAsync(tenant.Client, "ana");
        for (var i = 0; i < 6; i++)
        {
            using var wrong = await Users.SendLoginAsync(app, tenant.Code, "ana", "wrong-password");
            await HttpAssert.ProblemAsync(wrong, HttpStatusCode.Unauthorized, "LOGIN_FAILED");
        }
        await Users.LoginAsync(app, tenant.Code, "ana");
    }

    [Fact]
    public async Task Short_passwords_are_refused_unless_the_rule_allows_them()
    {
        var tenant = await app.NewTenantAsync();

        using var refused = await tenant.Client.PostAsJsonAsync(Users.Path, new { username = "ana", name = "Ana", password = "short" });
        var problem = await HttpAssert.ProblemAsync(refused, HttpStatusCode.BadRequest, "VALIDATION_FAILED");
        Assert.True(problem.GetProperty("errors").TryGetProperty("password", out _));
        Assert.Equal(Users.ShortPasswordsRule, problem.GetProperty("rules")[0].Str("key"));

        await Rules.SetAsync(tenant.Client, Users.ShortPasswordsRule, true);
        await Users.CreateAsync(tenant.Client, "ana", "short");
        await Users.LoginAsync(app, tenant.Code, "ana", "short");
    }

    [Fact]
    public async Task Usernames_are_unique_per_tenant_only()
    {
        var a = await app.NewTenantAsync();
        var b = await app.NewTenantAsync();
        await Users.CreateAsync(a.Client, "ana");
        await Users.CreateAsync(b.Client, "ana");

        using var taken = await a.Client.PostAsJsonAsync(Users.Path, new { username = "ANA", name = "Ana", password = "correct-horse" });
        await HttpAssert.ProblemAsync(taken, HttpStatusCode.Conflict, "USERNAME_TAKEN");

        using var listB = await b.Client.GetAsync(Users.Path);
        Assert.Equal(1, (await HttpAssert.JsonAsync(listB, HttpStatusCode.OK)).GetProperty("total").GetInt32());
        // A user of tenant b cannot sign in to tenant a with its password, and the other way round.
        var loginB = await Users.LoginAsync(app, b.Code, "ana");
        Assert.Equal(b.Id, loginB.GetProperty("tenant").GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task Logout_revokes_the_session_and_only_a_session_can_log_out()
    {
        var tenant = await app.NewTenantAsync();
        await Users.CreateAsync(tenant.Client, "ana");
        var login = await Users.LoginAsync(app, tenant.Code, "ana");
        using var session = app.WithBearer(login.Str("key"));

        using var logout = await session.PostAsync("/api/v1/logout", null);
        await HttpAssert.JsonAsync(logout, HttpStatusCode.OK);
        using var after = await session.GetAsync("/api/v1/whoami");
        Assert.Equal(HttpStatusCode.Unauthorized, after.StatusCode);

        using var notSession = await tenant.Client.PostAsync("/api/v1/logout", null);
        await HttpAssert.ProblemAsync(notSession, HttpStatusCode.Conflict, "INVALID_STATE");
    }

    [Fact]
    public async Task An_expired_session_no_longer_authenticates()
    {
        var tenant = await app.NewTenantAsync();
        await Users.CreateAsync(tenant.Client, "ana");
        var login = await Users.LoginAsync(app, tenant.Code, "ana");
        await app.ExecuteSqlAsync("""UPDATE "ApiKeys" SET "ExpiresAt" = now() - interval '1 minute' WHERE "Id" = @id""",
            ("id", login.GetProperty("apiKeyId").GetGuid()));

        using var session = app.WithBearer(login.Str("key"));
        using var response = await session.GetAsync("/api/v1/whoami");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Deactivating_a_user_ends_their_sessions()
    {
        var tenant = await app.NewTenantAsync();
        var id = (await Users.CreateAsync(tenant.Client, "ana")).GetProperty("id").GetGuid();
        var login = await Users.LoginAsync(app, tenant.Code, "ana");

        using var deactivate = await tenant.Client.PostAsync($"{Users.Path}/{id}/deactivate", null);
        await HttpAssert.JsonAsync(deactivate, HttpStatusCode.OK);

        using var session = app.WithBearer(login.Str("key"));
        using var response = await session.GetAsync("/api/v1/whoami");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Users_and_login_need_no_admin_and_the_admin_cannot_sign_in()
    {
        var tenant = await app.NewTenantAsync();
        using var admin = app.Admin();
        using var response = await admin.GetAsync(Users.Path);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        using var anonymous = await app.Anonymous().GetAsync(Users.Path);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        using var getLogin = await app.Anonymous().GetAsync(Users.LoginPath(tenant.Code));
        Assert.Equal(HttpStatusCode.Unauthorized, getLogin.StatusCode);
    }
}
