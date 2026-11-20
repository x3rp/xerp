using Xerp.Application.Ports;
using Xerp.Domain.Tenancy;

namespace Xerp.Api.Http;

/// <summary>
/// The tenant and API key of the current request. Set once, by <see cref="ApiV1Middleware"/>, from the
/// authenticated credential and from nothing else (spec 001, T1).
/// </summary>
public sealed class RequestActor : ITenantContext
{
    public Guid? TenantId { get; private set; }
    public Guid? ApiKeyId { get; private set; }

    /// <summary>What the key may do (spec P02, R31); <c>full</c> when there is no tenant key.</summary>
    public ApiKeyScope Scope { get; private set; }

    /// <summary>Spec U01: a sign-in names its tenant in the route and has no key yet; nothing but the sign-in runs with it.</summary>
    public void SetTenantOnly(Guid tenantId)
    {
        if (TenantId is not null)
            throw new InvalidOperationException("The actor of a request cannot change.");
        TenantId = tenantId;
    }

    public void Set(Guid tenantId, Guid apiKeyId, ApiKeyScope scope = ApiKeyScope.Full)
    {
        Scope = scope;
        if (TenantId is not null)
            throw new InvalidOperationException("The actor of a request cannot change.");
        TenantId = tenantId;
        ApiKeyId = apiKeyId;
    }
}
