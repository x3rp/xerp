using Microsoft.EntityFrameworkCore;
using Xerp.Application.Common;
using Xerp.Application.Ports;
using Xerp.Domain.Tenancy;

namespace Xerp.Application.Identity;

public sealed record WhoAmITenant(Guid Id, string Code, string Name);

public sealed record WhoAmIActor(Guid ApiKeyId, string Name, string ActorType);

/// <summary>Spec U01: the user a session key belongs to.</summary>
public sealed record WhoAmIUser(Guid Id, string Username, string Name);

/// <summary><c>User</c> is null for a key that no sign-in issued; <c>ExpiresAt</c> null for a key that never expires.</summary>
public sealed record WhoAmIDto(WhoAmITenant Tenant, WhoAmIActor Actor, WhoAmIUser? User = null, DateTime? ExpiresAt = null);

/// <summary>Spec 001, 4.2: the tenant and actor behind the current credential.</summary>
public sealed class WhoAmIOperation(IXerpDb db, ICurrentTenantReader currentTenant, ITenantContext context)
{
    public async Task<Result<WhoAmIDto>> ExecuteAsync(CancellationToken cancellationToken = default)
    {
        if (context.ApiKeyId is not { } apiKeyId)
            return AppError.Unauthenticated();

        var tenant = await currentTenant.ReadAsync(cancellationToken);
        // ApiKeys is filtered to the current tenant, so a key id of another tenant is never found here.
        var key = await db.ApiKeys.AsNoTracking()
            .Where(k => k.Id == apiKeyId)
            .Select(k => new { k.Id, k.Name, k.ActorType, k.UserId, k.ExpiresAt })
            .SingleOrDefaultAsync(cancellationToken);
        if (tenant is null || key is null)
            return AppError.Unauthenticated();
        var user = key.UserId is { } userId
            ? await db.Users.AsNoTracking().Where(u => u.Id == userId).Select(u => new WhoAmIUser(u.Id, u.Username, u.Name)).SingleOrDefaultAsync(cancellationToken)
            : null;

        return new WhoAmIDto(new WhoAmITenant(tenant.Id, tenant.Code, tenant.Name), new WhoAmIActor(key.Id, key.Name, key.ActorType.ToName()),
            user, key.ExpiresAt);
    }
}
