using System.Net;
using Xerp.IntegrationTests.Support;

namespace Xerp.IntegrationTests;

/// <summary>
/// Production hardening: an optional per-client-address request limit on /api/v1 and /mcp
/// (Xerp:RateLimit:PermitsPerMinute), and forwarded headers from a trusted proxy (Xerp:ForwardedHeaders).
/// Each test runs its own host, so the window of one test never touches another.
/// </summary>
[Collection(XerpCollection.Name)]
public class RateLimitTests(XerpFixture app)
{
    [Fact]
    public async Task Request_over_the_limit_is_429_in_the_error_model_with_retry_after()
    {
        var tenant = await app.NewTenantAsync();
        var host = app.CreateHost(XerpFixture.AdminKey, (RateLimitSetting, "3"));
        using var client = XerpFixture.Bearer(host.CreateClient(), tenant.Key);

        for (var i = 0; i < 3; i++)
        {
            using var ok = await client.GetAsync(Uom.Path);
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        }

        using var refused = await client.GetAsync(Uom.Path);
        var problem = await HttpAssert.ProblemAsync(refused, HttpStatusCode.TooManyRequests, "RATE_LIMITED");
        Assert.Equal("RATE_LIMITED", problem.GetProperty("title").GetString());
        Assert.True(int.Parse(Assert.Single(refused.Headers.GetValues("Retry-After"))) is > 0 and <= 60);

        // An unauthenticated caller counts against the same address: the limit runs before the key is looked up.
        using var anonymous = await host.CreateClient().GetAsync(Uom.Path);
        await HttpAssert.ProblemAsync(anonymous, HttpStatusCode.TooManyRequests, "RATE_LIMITED");

        using var mcp = await client.PostAsync(Mcp.Path, new StringContent("{}", System.Text.Encoding.UTF8, "application/json"));
        await HttpAssert.ProblemAsync(mcp, HttpStatusCode.TooManyRequests, "RATE_LIMITED");

        // The health check is outside the guarded paths.
        using var health = await client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
    }

    [Fact]
    public async Task Without_the_setting_there_is_no_limit()
    {
        var tenant = await app.NewTenantAsync();
        using var client = app.WithBearer(tenant.Key);
        for (var i = 0; i < 20; i++)
        {
            using var ok = await client.GetAsync(Uom.Path);
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        }
    }

    [Fact]
    public async Task With_forwarded_headers_each_forwarded_client_has_its_own_window()
    {
        var tenant = await app.NewTenantAsync();
        var host = app.CreateHost(XerpFixture.AdminKey, (RateLimitSetting, "2"), ("Xerp:ForwardedHeaders", "true"));
        using var client = XerpFixture.Bearer(host.CreateClient(), tenant.Key);

        async Task<HttpStatusCode> From(string address)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, Uom.Path);
            request.Headers.Add("X-Forwarded-For", address);
            using var response = await client.SendAsync(request);
            return response.StatusCode;
        }

        Assert.Equal(HttpStatusCode.OK, await From("203.0.113.1"));
        Assert.Equal(HttpStatusCode.OK, await From("203.0.113.1"));
        Assert.Equal(HttpStatusCode.TooManyRequests, await From("203.0.113.1"));
        Assert.Equal(HttpStatusCode.OK, await From("203.0.113.2"));
    }

    private const string RateLimitSetting = "Xerp:RateLimit:PermitsPerMinute";
}
