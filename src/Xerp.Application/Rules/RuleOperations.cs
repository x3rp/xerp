using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Xerp.Application.Common;
using Xerp.Application.Ports;
using Xerp.Domain.Inventory;
using Xerp.Domain.Orders;
using Xerp.Domain.Common;
using Xerp.Domain.Rules;
using Xerp.Domain.Tenancy;

namespace Xerp.Application.Rules;

/// <summary>
/// The rule operations of spec 012, sections 4 and 6.2: list, get, set, reset and the history of changes. One
/// method = one HTTP endpoint = one MCP tool. The definitions come from <see cref="RuleRegistry"/> and are the
/// same for every tenant; the values and the history are the tenant's - <see cref="IXerpDb"/> is already
/// filtered to it.
/// <para>
/// Set and reset run inside <see cref="IXerpDb.SerializedPerTenantAsync{T}"/>, the lock every posting,
/// reversal and save of a document takes (ADR-0020, decision 10): a change of a rule is ordered against them,
/// so each of them is judged entirely by the values before it or entirely by the values after it (R14), and
/// two changes of one rule never overlap. A change writes the value and its history in one save, touches
/// nothing else (R15), and applies to whatever starts after it (R13). Reading takes no lock.
/// </para>
/// </summary>
public sealed class RuleOperations(IXerpDb db, ITenantContext context, ICurrentTenantReader currentTenant, IClock clock)
{
    public const string SourceDefault = "default";
    public const string SourceTenant = "tenant";

    private const string KeyField = "key";
    private const string ValueField = "value";
    private const string NotFoundDetail = "Rule not found: the key is not one of the rules of this system. List them with rule_list (GET /rules); keys are case-sensitive.";

    /// <summary>
    /// R1, R2: every rule of the registry, set or not, ordered by key. The filters combine with AND:
    /// <c>group</c> is compared exactly, <c>source</c> is <c>default</c> or <c>tenant</c>, <c>search</c> is a
    /// case-insensitive text the key or the name contains.
    /// </summary>
    public async Task<Result<PagedResult<RuleDto>>> ListAsync(ListRulesInput input, CancellationToken cancellationToken = default)
    {
        var errors = new ValidationErrors();
        var search = ListRules.Search(errors, input.Search);
        var source = string.IsNullOrEmpty(input.Source) ? null : input.Source;
        if (source is not (null or SourceDefault or SourceTenant))
            errors.Add("source", $"source must be \"{SourceDefault}\" or \"{SourceTenant}\".");
        var limit = ListRules.Limit(errors, input.Limit);
        var offset = ListRules.Offset(errors, input.Offset);
        if (errors.Any)
            return errors.ToError();

        var rules = await RulesAsync(RuleRegistry.All, cancellationToken);
        var matching = rules
            .Where(r => string.IsNullOrEmpty(input.Group) || string.Equals(r.Group, input.Group, StringComparison.Ordinal))
            .Where(r => source is null || r.Source == source)
            .Where(r => search is null
                || r.Key.Contains(search, StringComparison.OrdinalIgnoreCase)
                || r.Name.Contains(search, StringComparison.OrdinalIgnoreCase))
            .ToList();
        return new PagedResult<RuleDto>(matching.Skip(offset).Take(limit).ToList(), matching.Count, limit, offset);
    }

    /// <summary>R4: the key addresses the rule and is compared exactly; anything that is no rule of the registry is not found.</summary>
    public async Task<Result<RuleDto>> GetAsync(string? key, CancellationToken cancellationToken = default)
    {
        if (Address(key, out var rule) is { } error)
            return error;
        return (await RulesAsync([rule], cancellationToken)).Single();
    }

    /// <summary>
    /// R5-R8, R10. Order of checks: the form of the request (a value was sent) -> the rule exists -> the value
    /// is the JSON literal <c>true</c> or <c>false</c>. Then the tenant has that value as its own - also when
    /// it is the default's (R7). Setting the value the tenant already has changes nothing: no change is
    /// recorded and the attribution stays (R8).
    /// </summary>
    public async Task<Result<RuleDto>> SetAsync(string? key, SetRuleInput input, CancellationToken cancellationToken = default)
    {
        var form = new ValidationErrors();
        if (key is null)
            form.Add(KeyField, "key is required.");
        if (input.Value.ValueKind == JsonValueKind.Undefined)
            form.Add(ValueField, "value is required: true or false.");
        if (form.Any)
            return form.ToError();
        if (Address(key, out var rule) is { } error)
            return error;
        if (input.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            return AppError.Validation(ValueField, "value must be the JSON boolean true or false: a rule is a yes/no switch. A string, a number and null are not values.");
        var value = input.Value.GetBoolean();

        return await db.SerializedPerTenantAsync<Result<RuleDto>>(async ct =>
        {
            var stored = await db.RuleValues.SingleOrDefaultAsync(v => v.Key == rule.Key, ct);
            if (stored is null || stored.Value != value)
            {
                var (now, actor) = (clock.UtcNow, ActorKeyId());
                db.RuleChanges.Add(RuleChange.Record(rule, RuleChangeAction.Set, oldValue: stored?.Value ?? rule.Default, newValue: value, now, actor));
                if (stored is null)
                    db.RuleValues.Add(RuleValue.Set(rule, value, now, actor));
                else
                    stored.Change(value, now, actor);
                await db.SaveChangesAsync(ct);
            }
            return (await RulesAsync([rule], ct)).Single();
        }, cancellationToken);
    }

    /// <summary>As <see cref="SetAsync(string?, SetRuleInput, CancellationToken)"/>, for a client without a URL path.</summary>
    public Task<Result<RuleDto>> SetAsync(SetRuleByKeyInput input, CancellationToken cancellationToken = default) =>
        SetAsync(input.Key, new SetRuleInput(input.Value), cancellationToken);

    /// <summary>
    /// R9, R10: removes the tenant's value, so the rule is at its default again; the change is recorded like a
    /// set and is what the rule shows as its last change. Reset of a rule the tenant has no value for changes
    /// nothing and records nothing.
    /// </summary>
    public async Task<Result<RuleDto>> ResetAsync(string? key, CancellationToken cancellationToken = default)
    {
        if (Address(key, out var rule) is { } error)
            return error;

        return await db.SerializedPerTenantAsync<Result<RuleDto>>(async ct =>
        {
            var stored = await db.RuleValues.SingleOrDefaultAsync(v => v.Key == rule.Key, ct);
            if (stored is not null)
            {
                db.RuleChanges.Add(RuleChange.Record(rule, RuleChangeAction.Reset, oldValue: stored.Value, newValue: rule.Default, clock.UtcNow, ActorKeyId()));
                db.RuleValues.Remove(stored);
                await db.SaveChangesAsync(ct);
            }
            return (await RulesAsync([rule], ct)).Single();
        }, cancellationToken);
    }

    /// <summary>R11: the changes of the tenant, newest first; <c>key</c> filters by exact key, and a text that is no rule finds nothing.</summary>
    public async Task<Result<PagedResult<RuleChangeDto>>> ListChangesAsync(ListRuleChangesInput input, CancellationToken cancellationToken = default)
    {
        var errors = new ValidationErrors();
        var limit = ListRules.Limit(errors, input.Limit);
        var offset = ListRules.Offset(errors, input.Offset);
        if (errors.Any)
            return errors.ToError();

        var changes = db.RuleChanges.AsNoTracking();
        if (!string.IsNullOrEmpty(input.Key))
            changes = changes.Where(c => c.Key == input.Key);

        var total = await changes.CountAsync(cancellationToken);
        var page = await changes
            .OrderByDescending(c => c.ChangedAt)
            .ThenByDescending(c => c.Id)
            .Skip(offset)
            .Take(limit)
            .ToListAsync(cancellationToken);
        var items = page
            .Select(c => new RuleChangeDto(c.Id, c.Key, c.Action.ToName(), c.OldValue, c.NewValue, c.ChangedAt, c.ChangedBy, c.Reason))
            .ToList();
        return new PagedResult<RuleChangeDto>(items, total, limit, offset);
    }


    // ---- spec A01, 2.4

    /// <summary>Every group of rules (the first part of the keys), described in a sentence, with how many of its rules the tenant has changed from the default.</summary>
    public async Task<Result<ItemsResult<RuleGroupDto>>> ListGroupsAsync(CancellationToken cancellationToken = default)
    {
        var rules = await RulesAsync(RuleRegistry.All, cancellationToken);
        var groups = rules.GroupBy(r => r.Group, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new RuleGroupDto(g.Key, RuleGroups.Describe(g.Key), g.Count(), g.Count(r => r.Value != r.Default)))
            .ToList();
        return new ItemsResult<RuleGroupDto>(groups);
    }

    /// <summary>
    /// Several rules in one call, all or none. Every item is checked before anything is written: an unknown key, a value that
    /// is not the JSON boolean true or false, a key twice - each is reported under <c>changes[i].key</c> / <c>changes[i].value</c>.
    /// A change writes one history row with the common <c>reason</c>; an item that leaves the rule as it is writes nothing, and one
    /// that gives a rule the default's value removes the tenant's own value (a reset). The company's <c>rulesReviewedAt</c> is set.
    /// Answers the rules that changed.
    /// </summary>
    public async Task<Result<ItemsResult<RuleDto>>> SetManyAsync(SetManyRulesInput input, CancellationToken cancellationToken = default)
    {
        var errors = new ValidationErrors();
        var changes = Changes(errors, input.Changes);
        string? reason = null;
        if (!NoteRules.TryNormalize(input.Reason, RuleChange.ReasonMaxLength, out reason))
            errors.Add("reason", $"reason must be at most {RuleChange.ReasonMaxLength} characters.");
        if (errors.Any)
            return errors.ToError();
        var actor = ActorKeyId();
        var tenant = await currentTenant.ReadAsync(cancellationToken);
        if (tenant is null)
            return AppError.Unauthenticated();

        return await db.SerializedPerTenantAsync<Result<ItemsResult<RuleDto>>>(async ct =>
        {
            var now = clock.UtcNow;
            var keys = changes.Select(c => c.Rule.Key).ToList();
            var stored = await db.RuleValues.Where(v => keys.Contains(v.Key)).ToDictionaryAsync(v => v.Key, ct);
            var changed = new List<RuleDefinition>();
            foreach (var (rule, value) in changes)
            {
                stored.TryGetValue(rule.Key, out var own);
                var current = own?.Value ?? rule.Default;
                if (value == current && (own is null || value != rule.Default))
                    continue;
                if (value == rule.Default)
                {
                    // Back to the default: the tenant's own value goes, as in a reset.
                    db.RuleChanges.Add(RuleChange.Record(rule, RuleChangeAction.Reset, oldValue: current, newValue: value, now, actor, reason));
                    db.RuleValues.Remove(own!);
                }
                else
                {
                    db.RuleChanges.Add(RuleChange.Record(rule, RuleChangeAction.Set, oldValue: current, newValue: value, now, actor, reason));
                    if (own is null)
                        db.RuleValues.Add(RuleValue.Set(rule, value, now, actor));
                    else
                        own.Change(value, now, actor);
                }
                changed.Add(rule);
            }
            var profile = await db.CompanyProfiles.SingleOrDefaultAsync(ct);
            if (profile is null)
            {
                profile = CompanyProfile.CreateForReview(tenant.Name, now, actor);
                db.CompanyProfiles.Add(profile);
            }
            profile.MarkRulesReviewed(now);
            await db.SaveChangesAsync(ct);
            return new ItemsResult<RuleDto>(await RulesAsync(changed, ct));
        }, cancellationToken);
    }

    /// <summary>What the changes would do, without doing it: the rule, old and new value, what it judges, and the drafts it would refuse at their next save or posting where that is cheap to count.</summary>
    public async Task<Result<ItemsResult<RulePreviewDto>>> PreviewAsync(RulePreviewInput input, CancellationToken cancellationToken = default)
    {
        var errors = new ValidationErrors();
        var changes = Changes(errors, input.Changes);
        if (errors.Any)
            return errors.ToError();

        var current = (await RulesAsync(changes.Select(c => c.Rule).ToList(), cancellationToken)).ToDictionary(r => r.Key);
        var items = new List<RulePreviewDto>();
        foreach (var (rule, value) in changes)
        {
            var old = current[rule.Key].Value;
            items.Add(new RulePreviewDto(rule.Key, rule.Name, old, value, Judges(rule), await AffectedDraftsAsync(rule, old, value, cancellationToken)));
        }
        return new ItemsResult<RulePreviewDto>(items);
    }

    /// <summary>The first sentence of the description: which operations the rule judges.</summary>
    private static string Judges(RuleDefinition rule)
    {
        var end = rule.Description.IndexOf(". ", StringComparison.Ordinal);
        return end < 0 ? rule.Description : rule.Description[..(end + 1)];
    }

    /// <summary>
    /// Drafts (stock documents and orders) that the new value would refuse at their next save or posting - only for the rules
    /// where a draft can be counted without running the checks: reference, the two dates, the partner of a receipt and an issue.
    /// A change that does not restrict (or changes nothing) affects none; every other rule is null.
    /// </summary>
    private async Task<int?> AffectedDraftsAsync(RuleDefinition rule, bool oldValue, bool newValue, CancellationToken ct)
    {
        var counted = rule.Key is "document.referenceRequired" or "document.pastDateAllowed" or "document.futureDateAllowed"
            or "stock.receiptPartnerRequired" or "stock.issuePartnerRequired";
        if (!counted)
            return null;
        // "required" restricts when it becomes true; "allowed" restricts when it becomes false.
        var restricts = rule.Key.EndsWith("Required", StringComparison.Ordinal) ? newValue && !oldValue : !newValue && oldValue;
        if (!restricts)
            return 0;

        var now = clock.UtcNow;
        var drafts = db.StockDocuments.AsNoTracking().Where(d => d.Status == StockDocumentStatus.Draft);
        var purchases = db.PurchaseOrders.AsNoTracking().Where(o => o.Status == OrderStatus.Draft);
        var sales = db.SalesOrders.AsNoTracking().Where(o => o.Status == OrderStatus.Draft);
        switch (rule.Key)
        {
            case "document.referenceRequired":
                return await drafts.CountAsync(d => d.Reference == null || d.Reference == "", ct)
                    + await purchases.CountAsync(o => o.Reference == null || o.Reference == "", ct)
                    + await sales.CountAsync(o => o.Reference == null || o.Reference == "", ct);
            case "stock.receiptPartnerRequired":
                return await drafts.CountAsync(d => d.Type == StockDocumentType.Receipt && d.PartnerId == null && d.PurchaseOrderId == null, ct);
            case "stock.issuePartnerRequired":
                return await drafts.CountAsync(d => d.Type == StockDocumentType.Issue && d.PartnerId == null && d.SalesOrderId == null, ct);
        }
        // The two date rules: the dates that DocumentDateRules would judge before / after today.
        var past = DocumentDateRules.Earliest(now);
        var future = DocumentDateRules.Latest(now);
        return rule.Key == "document.pastDateAllowed"
            ? await drafts.CountAsync(d => d.DocumentDate < past, ct) + await purchases.CountAsync(o => o.OrderDate < past, ct) + await sales.CountAsync(o => o.OrderDate < past, ct)
            : await drafts.CountAsync(d => d.DocumentDate > future, ct) + await purchases.CountAsync(o => o.OrderDate > future, ct) + await sales.CountAsync(o => o.OrderDate > future, ct);
    }

    /// <summary>The changes of a call checked as a whole: every fault is reported, keyed by the item's position.</summary>
    private static List<(RuleDefinition Rule, bool Value)> Changes(ValidationErrors errors, IReadOnlyList<RuleChangeInput>? input)
    {
        var result = new List<(RuleDefinition, bool)>();
        if (input is null || input.Count == 0)
        {
            errors.Add("changes", "changes must name at least one rule: [{ \"key\": \"stock.negativeStockAllowed\", \"value\": true }].");
            return result;
        }
        if (input.Count > RuleRegistry.All.Count)
            errors.Add("changes", $"changes may hold at most {RuleRegistry.All.Count} items, one per rule.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < input.Count; i++)
        {
            var (key, value) = (input[i].Key, input[i].Value);
            var rule = RuleRegistry.Find(key);
            if (key is null)
                errors.Add($"changes[{i}].key", "key is required.");
            else if (rule is null)
                errors.Add($"changes[{i}].key", $"'{key}' is not a rule of this system; list the keys with rule_list (keys are case-sensitive).");
            else if (!seen.Add(key))
                errors.Add($"changes[{i}].key", $"'{key}' is named twice; give each rule once.");
            if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                errors.Add($"changes[{i}].value", "value must be the JSON boolean true or false: a rule is a yes/no switch.");
            else if (rule is not null)
                result.Add((rule, value.GetBoolean()));
        }
        return result;
    }

    /// <summary>
    /// The given definitions with the tenant's values. The value in force is the tenant's own where it has
    /// one and the default otherwise (R3); the attribution is that of the value, or - once a reset removed it -
    /// of that reset, the rule's newest change (R9). A stored value whose key is no rule is never looked at (S4).
    /// </summary>
    private async Task<List<RuleDto>> RulesAsync(IReadOnlyList<RuleDefinition> definitions, CancellationToken cancellationToken)
    {
        var keys = definitions.Select(d => d.Key).ToList();
        var values = await db.RuleValues.AsNoTracking()
            .Where(v => keys.Contains(v.Key))
            .ToDictionaryAsync(v => v.Key, cancellationToken);
        var withoutValue = keys.Where(k => !values.ContainsKey(k)).ToList();
        var lastChanges = withoutValue.Count == 0
            ? []
            : (await db.RuleChanges.AsNoTracking()
                .Where(c => withoutValue.Contains(c.Key))
                .Where(c => !db.RuleChanges.Any(later => later.Key == c.Key && later.ChangedAt > c.ChangedAt))
                .Select(c => new { c.Key, c.Id, c.ChangedAt, c.ChangedBy })
                .ToListAsync(cancellationToken))
                .GroupBy(c => c.Key)
                .ToDictionary(g => g.Key, g => g.MaxBy(c => c.Id)!);

        return definitions.Select(d =>
        {
            if (values.TryGetValue(d.Key, out var own))
                return new RuleDto(d.Key, d.Group, d.Name, d.Description, d.Default, own.Value, SourceTenant, own.UpdatedAt, own.UpdatedBy);
            return lastChanges.TryGetValue(d.Key, out var last)
                ? new RuleDto(d.Key, d.Group, d.Name, d.Description, d.Default, d.Default, SourceDefault, last.ChangedAt, last.ChangedBy)
                : new RuleDto(d.Key, d.Group, d.Name, d.Description, d.Default, d.Default, SourceDefault, null, null);
        }).ToList();
    }

    /// <summary>
    /// Null when <paramref name="key"/> is a rule of the registry. Otherwise the error: a missing key is a
    /// validation error under the argument's name (only a client without a URL path can omit it); any text
    /// that is no rule is <c>NOT_FOUND</c>.
    /// </summary>
    private static AppError? Address(string? key, out RuleDefinition rule)
    {
        rule = RuleRegistry.Find(key)!;
        if (key is null)
            return AppError.Validation(KeyField, "key is required.");
        return rule is null ? AppError.NotFound(NotFoundDetail) : null;
    }

    private Guid ActorKeyId() =>
        context.ApiKeyId ?? throw new InvalidOperationException("A rule can only be changed by a tenant API key.");
}
