using Xerp.Application.Common;
using Xerp.Domain.Tenancy;

namespace Xerp.Application.ApiKeys;

/// <summary>An API key as clients see it: never the secret, never its hash (spec 003, section 4).</summary>
public sealed record ApiKeyDto(
    Guid Id,
    string Name,
    string ActorType,
    string Scope,
    bool IsActive,
    DateTime CreatedAt,
    Guid? CreatedBy,
    DateTime? RevokedAt,
    Guid? RevokedBy);

/// <summary>The only representation that carries the secret: the answer to a create (spec 003, R3).</summary>
public sealed record CreatedApiKeyDto(
    Guid Id,
    string Name,
    string ActorType,
    string Scope,
    bool IsActive,
    DateTime CreatedAt,
    Guid? CreatedBy,
    DateTime? RevokedAt,
    Guid? RevokedBy,
    string Key);

/// <summary><c>ActorType</c> is the raw string of the request, so that a wrong value is reported with the other fields.</summary>
/// <remarks><c>Scope</c> may be omitted (then <c>full</c>), but not null (spec P02, R30): the input remembers whether it was given.</remarks>
public sealed record CreateApiKeyInput : TrackedInput
{
    private readonly string? _scope;

    public CreateApiKeyInput(string? name, string? actorType)
    {
        Name = name;
        ActorType = actorType;
    }

    public string? Name { get; init; }
    public string? ActorType { get; init; }
    public string? Scope { get => _scope; init => _scope = Given(value); }
}

public sealed record ListApiKeysInput(
    string? Search = null, string? ActorType = null, bool? IsActive = null, int? Limit = null, int? Offset = null, string? Scope = null);

/// <summary>Validated field values of a new key.</summary>
public sealed record ApiKeyValues(string Name, ActorType ActorType, ApiKeyScope Scope = ApiKeyScope.Full);

/// <summary>Validated list query. <c>Search</c> is null when there is no text filter.</summary>
public sealed record ApiKeyListQuery(string? Search, ActorType? ActorType, bool? IsActive, int Limit, int Offset, ApiKeyScope? Scope = null);
