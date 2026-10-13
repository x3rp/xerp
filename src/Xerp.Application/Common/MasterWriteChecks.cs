using Microsoft.EntityFrameworkCore;
using Xerp.Application.Ports;
using Xerp.Domain.Common;
using Xerp.Domain.Rules;

namespace Xerp.Application.Common;

/// <summary>
/// The stages of a create or a replace of a master that the rules of spec 013 added, written once for the
/// four masters: the code change (R9) and the uniqueness of the name (R12) and of a partner's tax id (R15).
/// The decisions are <see cref="MasterDataRules"/>; here are the lookup and the error that names the rule.
/// The caller holds the tenant's lock, so the lookup and the write that follows cannot be overtaken (R13).
/// </summary>
public static class MasterWriteChecks
{
    /// <summary>R9, R10: stands immediately before the <c>CODE_TAKEN</c> check of a replace.</summary>
    /// <param name="what">The record, with its article: <c>a partner</c>.</param>
    public static AppError? CodeChange(TenantRules rules, MasterRuleSet master, string what, string storedCode, string code) =>
        MasterDataRules.CodeChangeRefused(rules[master.CodeChangeable], storedCode, code)
            ? AppError.CodeChangeNotAllowed(what, storedCode, master.CodeChangeable)
            : null;

    /// <summary>R12, R14: compared like codes - trimmed (the name is normalised) and case-insensitively; inactive records count.</summary>
    /// <param name="others">Every record of the resource in the tenant except the one being written.</param>
    /// <param name="what">The resource in the singular: <c>partner</c>.</param>
    public static async Task<AppError?> NameAsync<T>(
        TenantRules rules, MasterRuleSet master, IQueryable<T> others, string what, string name, CancellationToken cancellationToken)
        where T : class
    {
        // With the rule false nothing is looked up: duplicates are allowed, as before spec 013.
        if (!rules[master.NameUnique])
            return null;
        var taken = await others.AnyAsync(e => DbText.Lower(EF.Property<string>(e, DbNames.Name)) == DbText.Lower(name), cancellationToken);
        return MasterDataRules.Taken(rules[master.NameUnique], taken) ? AppError.NameTaken(what, name, master.NameUnique) : null;
    }
}

/// <summary>
/// The required-field rules of one master with the tenant's values (spec 013, R5-R7): a table from field to
/// rule, asked once per optional field. A field that already has an error - absent from a replace, or given
/// in a wrong form - is not judged: that refusal is an invariant's and names no rule (R6).
/// </summary>
public readonly record struct RequiredFields(TenantRules Rules, IReadOnlyDictionary<string, RuleDefinition> Table)
{
    public static RequiredFields Of(TenantRules rules, MasterRuleSet master) => new(rules, master.RequiredFields);

    /// <param name="value">The normalised value of the field: null when omitted, null, empty or whitespace only.</param>
    public void Check(ValidationErrors errors, string field, string? value)
    {
        if (Table.TryGetValue(field, out var rule) && !errors.Has(field) && MasterDataRules.RequiredFieldMissing(Rules[rule], value))
            errors.Add(field, $"{field} is required: this company's rule {rule.Key} is true.", rule, true);
    }
}
