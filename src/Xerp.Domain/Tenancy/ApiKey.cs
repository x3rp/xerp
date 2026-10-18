using Xerp.Domain.Common;

namespace Xerp.Domain.Tenancy;

public enum ActorType
{
    Human,
    Agent,
}

/// <summary>The contract names of <see cref="ActorType"/> (<c>human</c>, <c>agent</c>), as stored and as sent to clients.</summary>
public static class ActorTypeNames
{
    public const string Human = "human";
    public const string Agent = "agent";

    public static string ToName(this ActorType actorType) => actorType switch
    {
        ActorType.Human => Human,
        ActorType.Agent => Agent,
        _ => throw new ArgumentOutOfRangeException(nameof(actorType)),
    };

    /// <summary>True only for exactly <c>human</c> or <c>agent</c> (spec 003, R2).</summary>
    public static bool TryParse(string? name, out ActorType actorType)
    {
        switch (name)
        {
            case Human: actorType = ActorType.Human; return true;
            case Agent: actorType = ActorType.Agent; return true;
            default: actorType = default; return false;
        }
    }

    public static ActorType Parse(string name) => name switch
    {
        Human => ActorType.Human,
        Agent => ActorType.Agent,
        _ => throw new ArgumentException($"Unknown actor type '{name}'.", nameof(name)),
    };
}

/// <summary>
/// What a key may do (spec P02, 4.D): <see cref="Full"/> everything, <see cref="Pos"/> only the operations of
/// the POS allow-list. Chosen at creation and never changed.
/// </summary>
public enum ApiKeyScope
{
    Full,
    Pos,
}

/// <summary>The contract names of <see cref="ApiKeyScope"/> (<c>full</c>, <c>pos</c>), as stored and as sent to clients.</summary>
public static class ApiKeyScopeNames
{
    public const string Full = "full";
    public const string Pos = "pos";

    public static string ToName(this ApiKeyScope scope) => scope switch
    {
        ApiKeyScope.Full => Full,
        ApiKeyScope.Pos => Pos,
        _ => throw new ArgumentOutOfRangeException(nameof(scope)),
    };

    /// <summary>True only for exactly <c>full</c> or <c>pos</c> (spec P02, R30).</summary>
    public static bool TryParse(string? name, out ApiKeyScope scope)
    {
        switch (name)
        {
            case Full: scope = ApiKeyScope.Full; return true;
            case Pos: scope = ApiKeyScope.Pos; return true;
            default: scope = default; return false;
        }
    }

    public static ApiKeyScope Parse(string name) =>
        TryParse(name, out var scope) ? scope : throw new ArgumentException($"Unknown API key scope '{name}'.", nameof(name));
}

/// <summary>
/// A credential of one tenant. Only the hash of the secret is kept. A key is created and revoked,
/// never edited and never deleted (ADR-0010): records keep pointing at it as their actor.
/// </summary>
public sealed class ApiKey : ITenantOwned
{
    public const int NameMaxLength = 100;

    private ApiKey() { }

    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public string Name { get; private set; } = "";
    public ActorType ActorType { get; private set; }

    /// <summary>Spec P02, R30: <c>full</c> for every key created before it and for a tenant's first key.</summary>
    public ApiKeyScope Scope { get; private set; }

    public string KeyHash { get; private set; } = "";
    public bool IsActive { get; private set; } = true;
    public DateTime CreatedAt { get; private set; }

    /// <summary>The key that created this one; null for a tenant's first key.</summary>
    public Guid? CreatedBy { get; private set; }

    /// <summary>Null for an active key, and for a key that was deactivated outside the application.</summary>
    public DateTime? RevokedAt { get; private set; }
    public Guid? RevokedBy { get; private set; }

    public static ApiKey Create(Guid tenantId, string name, ActorType actorType, string keyHash, DateTime now, Guid? createdBy = null,
        ApiKeyScope scope = ApiKeyScope.Full) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            Name = NameRules.Normalize(name, nameof(name), NameMaxLength),
            ActorType = actorType,
            Scope = scope,
            KeyHash = keyHash,
            IsActive = true,
            CreatedAt = now,
            CreatedBy = createdBy,
        };

    /// <summary>
    /// Revocation is permanent (spec 003, R5). Revoking an inactive key changes nothing, so the first
    /// revocation's time and actor are kept (R6).
    /// </summary>
    public void Revoke(DateTime now, Guid revokedBy)
    {
        if (!IsActive)
            return;
        IsActive = false;
        RevokedAt = now;
        RevokedBy = revokedBy;
    }
}
