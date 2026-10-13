using Xerp.Domain.Common;
using Xerp.Domain.Rules;

namespace Xerp.Application.Common;

/// <summary>Collects field errors (camelCase field name -> messages) into one VALIDATION_FAILED error.</summary>
public sealed class ValidationErrors
{
    private readonly Dictionary<string, List<string>> _errors = new(StringComparer.Ordinal);
    private readonly List<(RuleDefinition Rule, bool Value, string Field)> _refusals = [];

    public bool Any => _errors.Count > 0;

    /// <summary>The number of fields that have an error so far.</summary>
    public int Count => _errors.Count;

    public bool Has(string field) => _errors.ContainsKey(field);

    public void Add(string field, string message)
    {
        if (!_errors.TryGetValue(field, out var list))
            _errors[field] = list = [];
        list.Add(message);
    }

    /// <summary>
    /// An error a configurable rule produced: with another value of <paramref name="rule"/> the field would
    /// have passed. <see cref="ToError"/> names the rule with exactly the fields it refused (spec 013, R17).
    /// </summary>
    /// <param name="value">The value of the rule that is in force for the tenant.</param>
    public void Add(string field, string message, RuleDefinition rule, bool value)
    {
        Add(field, message);
        _refusals.Add((rule, value, field));
    }

    /// <summary>Checks a <c>code</c> field (R1-R3); returns the normalised value, or records an error.</summary>
    public string Code(string? input, string field = "code")
    {
        if (CodeRules.TryNormalize(input, out var code))
            return code;
        Add(field, string.IsNullOrWhiteSpace(input)
            ? "Code is required."
            : $"Code must be 1-{CodeRules.MaxLength} characters: letters, digits, '.', '_' or '-'.");
        return "";
    }

    /// <summary>Checks a <c>name</c> field (R4); returns the normalised value, or records an error.</summary>
    public string Name(string? input, string field = "name", int maxLength = NameRules.MaxLength)
    {
        if (NameRules.TryNormalize(input, maxLength, out var name))
            return name;
        Add(field, string.IsNullOrWhiteSpace(input)
            ? "Name is required."
            : $"Name must be at most {maxLength} characters and contain no control characters.");
        return "";
    }

    /// <summary>
    /// All invalid fields together. One element of <c>rules</c> per rule that refused, ordered by the first of
    /// its fields in <c>errors</c>; a field an invariant refused is in no rule's fields (spec 013, R18).
    /// </summary>
    public AppError ToError()
    {
        var error = AppError.Validation(_errors.ToDictionary(e => e.Key, e => e.Value.ToArray(), StringComparer.Ordinal));
        var order = _errors.Keys.ToList();
        var byRule = _refusals
            .GroupBy(r => r.Rule)
            .Select(g => (Rule: g.Key, g.First().Value, Fields: g.Select(r => r.Field).Distinct().OrderBy(order.IndexOf).ToList()))
            .OrderBy(r => order.IndexOf(r.Fields[0]));
        foreach (var (rule, value, fields) in byRule)
            error = error.RefusedBy(rule, value, fields);
        return error;
    }
}
