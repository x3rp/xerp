using System.Globalization;
using System.Net;
using System.Threading.RateLimiting;
using Xerp.Application.Common;

namespace Xerp.Api.Http;

/// <summary>
/// A per-client-address request limit on <c>/api/v1</c> and <c>/mcp</c>, applied before the credential is
/// resolved so that a flood never reaches the database. It is a deployment setting, not a business rule:
/// <c>Xerp:RateLimit:PermitsPerMinute</c> (0 or unset = off). The address is the connection's remote address,
/// which is the real client's only when forwarded headers from a trusted proxy are applied (<c>Xerp:ForwardedHeaders</c>).
/// A refused request gets <c>429 RATE_LIMITED</c> in the error model and a <c>Retry-After</c> header.
/// </summary>
public sealed class RateLimitMiddleware : IDisposable
{
    public const string PermitsSetting = "Xerp:RateLimit:PermitsPerMinute";

    private readonly RequestDelegate _next;
    private readonly int _permits;
    private readonly PartitionedRateLimiter<IPAddress>? _limiter;

    public RateLimitMiddleware(RequestDelegate next, IConfiguration configuration)
    {
        _next = next;
        _permits = configuration.GetValue<int?>(PermitsSetting) ?? 0;
        if (_permits <= 0)
            return;
        _limiter = PartitionedRateLimiter.Create<IPAddress, IPAddress>(address =>
            RateLimitPartition.GetFixedWindowLimiter(address, _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = _permits,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true,
            }));
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (_limiter is null || !IsGuarded(context.Request.Path))
        {
            await _next(context);
            return;
        }

        var address = context.Connection.RemoteIpAddress ?? IPAddress.None;
        using var lease = _limiter.AttemptAcquire(address);
        if (!lease.IsAcquired)
        {
            var retryAfter = lease.TryGetMetadata(MetadataName.RetryAfter, out var after) ? after : TimeSpan.FromMinutes(1);
            context.Response.Headers.RetryAfter = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
            await Problems.WriteAsync(context, AppError.RateLimited(_permits));
            return;
        }

        await _next(context);
    }

    private static bool IsGuarded(PathString path) =>
        path.StartsWithSegments("/api/v1") || path.StartsWithSegments(ApiV1Middleware.McpPath);

    public void Dispose() => _limiter?.Dispose();
}
