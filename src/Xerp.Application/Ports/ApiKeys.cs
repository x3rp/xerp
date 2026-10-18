using Xerp.Domain.Inventory;
using Xerp.Domain.Tenancy;

namespace Xerp.Application.Ports;

public interface IApiKeyGenerator
{
    /// <summary>A new secret: <c>xerp_</c> + 43 base64url characters (32 random bytes).</summary>
    string Generate();
}

/// <summary>
/// A salted, slow hash of a POS operator's PIN (spec P02, section 3): a PIN has only 10^4 to 10^8 values, so the
/// unsalted SHA-256 of API keys (ADR-0003) would not protect it.
/// </summary>
public interface IPinHasher
{
    string Hash(string pin);

    /// <summary>Takes about the same time whether or not the PIN matches.</summary>
    bool Verify(string hash, string pin);
}

public interface IApiKeyHasher
{
    /// <summary>Lower-case hex SHA-256 of the UTF-8 bytes of the full key string.</summary>
    string Hash(string key);
}

/// <summary>What the authentication lookup returns for a stored key hash.</summary>
public sealed record ApiKeyRecord(
    Guid ApiKeyId,
    string Name,
    ActorType ActorType,
    bool IsActive,
    Guid TenantId,
    bool TenantIsActive,
    ApiKeyScope Scope = ApiKeyScope.Full);

/// <summary>
/// The one place that reads API keys without a tenant filter: authentication, before a tenant is known.
/// </summary>
public interface IApiKeyLookup
{
    Task<ApiKeyRecord?> FindByHashAsync(string keyHash, CancellationToken cancellationToken = default);
}

/// <summary>Writes a new tenant together with its first API key and its default warehouse in one transaction.</summary>
public interface ITenantProvisioningStore
{
    /// <exception cref="UniqueConstraintViolationException">The tenant code is already used.</exception>
    Task AddAsync(Tenant tenant, ApiKey firstKey, Warehouse defaultWarehouse, CancellationToken cancellationToken = default);

    Task<bool> CodeExistsAsync(string code, CancellationToken cancellationToken = default);
}
