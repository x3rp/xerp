using Xerp.Application.Common;
using Xerp.Domain.Tenancy;

namespace Xerp.Application.ApiKeys;

/// <summary>Input rules for API keys (spec 003, R1, R2, R10). No I/O.</summary>
public static class ApiKeyValidation
{
    private const string ScopeMessage = $"scope must be \"{ApiKeyScopeNames.Full}\" or \"{ApiKeyScopeNames.Pos}\"; omit it for \"{ApiKeyScopeNames.Full}\".";

    private const string ActorTypeMessage = $"actorType must be \"{ActorTypeNames.Human}\" or \"{ActorTypeNames.Agent}\".";

    public static Result<ApiKeyValues> Create(CreateApiKeyInput input)
    {
        var errors = new ValidationErrors();
        var name = errors.Name(input.Name, maxLength: ApiKey.NameMaxLength);
        if (!ActorTypeNames.TryParse(input.ActorType, out var actorType))
            errors.Add("actorType", string.IsNullOrEmpty(input.ActorType) ? "actorType is required. " + ActorTypeMessage : ActorTypeMessage);
        // Spec P02, R30: omitted is full; null, another text or another JSON type is refused.
        var scope = ApiKeyScope.Full;
        if (input.Has(nameof(input.Scope)) && !ApiKeyScopeNames.TryParse(input.Scope, out scope))
            errors.Add("scope", ScopeMessage);
        if (errors.Any)
            return errors.ToError();
        return new ApiKeyValues(name, actorType, scope);
    }

    public static Result<ApiKeyListQuery> List(ListApiKeysInput input)
    {
        var errors = new ValidationErrors();
        var search = ListRules.Search(errors, input.Search);
        var limit = ListRules.Limit(errors, input.Limit);
        var offset = ListRules.Offset(errors, input.Offset);
        ActorType? actorType = null;
        if (!string.IsNullOrEmpty(input.ActorType))
        {
            if (ActorTypeNames.TryParse(input.ActorType, out var parsed)) actorType = parsed;
            else errors.Add("actorType", ActorTypeMessage);
        }
        ApiKeyScope? scope = null;
        if (!string.IsNullOrEmpty(input.Scope))
        {
            if (ApiKeyScopeNames.TryParse(input.Scope, out var parsed)) scope = parsed;
            else errors.Add("scope", $"scope must be \"{ApiKeyScopeNames.Full}\" or \"{ApiKeyScopeNames.Pos}\".");
        }
        if (errors.Any)
            return errors.ToError();
        return new ApiKeyListQuery(search, actorType, input.IsActive, limit, offset, scope);
    }
}
