using Xerp.Application.Common;
using Xerp.Domain.Rules;

namespace Xerp.Application.Rules;

/// <summary>Spec A01, 2.4: the ready-made rule profiles an agent may propose. Data in code, the same for every tenant; applying one is <c>rule_set_many</c>.</summary>
public sealed class RuleProfileOperations
{
    public Result<ItemsResult<RuleProfileSummaryDto>> List() =>
        new ItemsResult<RuleProfileSummaryDto>(RuleProfiles.All.Select(p => new RuleProfileSummaryDto(p.Key, p.Name, p.Description, p.Changes.Count)).ToList());

    public Result<RuleProfileDto> Get(RuleKeyInput input)
    {
        if (input.Key is null)
            return AppError.Validation("key", "key is required: one of the keys of rule_profile_list.");
        return RuleProfiles.Find(input.Key) is { } p
            ? new RuleProfileDto(p.Key, p.Name, p.Description, p.Changes.Select(c => new RuleSettingDto(c.Key, c.Value)).ToList())
            : AppError.NotFound($"No rule profile '{input.Key}'. See rule_profile_list.");
    }
}
