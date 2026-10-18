using Microsoft.EntityFrameworkCore;
using Xerp.Application.Common;
using Xerp.Application.Ports;
using Xerp.Application.Sales;
using Xerp.Domain.Pos;
using Xerp.Domain.Rules;

namespace Xerp.Application.Pos;

/// <summary>The six operations of a POS role (spec P02, 4.A): a master as in P01 with seven rights.</summary>
public sealed class PosRoleOperations(IXerpDb db, ITenantContext context, IClock clock, IRules rules)
    : CodedMasterOperations<PosRole, PosRoleDto, PosRoleInput, PosRoleValues>(db, context, clock, rules)
{
    protected override DbSet<PosRole> Records => Db.PosRoles;
    protected override MasterRuleSet RuleSet => RuleRegistry.PosRole;
    protected override string Word => "POS role";
    protected override string AWord => "a POS role";

    protected override Result<PosRoleValues> Validate(PosRoleInput input, bool replace) => PosValidation.Role(input, replace);

    protected override PosRole Create(PosRoleValues v, DateTime now, Guid actorKeyId) =>
        PosRole.Create(v.Code, v.Name, v.Rights, v.IsActive, now, actorKeyId);

    protected override void Replace(PosRole record, PosRoleValues v, DateTime now, Guid actorKeyId) =>
        record.Replace(v.Code, v.Name, v.Rights, v.IsActive, now, actorKeyId);

    protected override PosRoleDto ToDto(PosRole r) =>
        new(r.Id, r.Code, r.Name, r.IsActive, r.Rights, r.CreatedAt, r.UpdatedAt, r.CreatedBy, r.UpdatedBy);

    /// <summary>R7: any operator, also an inactive one, keeps its role.</summary>
    protected override async Task<string?> UsedByAsync(Guid id, CancellationToken cancellationToken)
    {
        var operators = await Db.PosOperators.CountAsync(o => o.RoleId == id, cancellationToken);
        return operators > 0 ? $"The POS role is the role of {operators} operator(s) (see pos_operator_list with roleId)." : null;
    }
}

/// <summary>
/// The operations of a POS operator (spec P02, 4.B): the six of a master, a create with a PIN and a replace
/// without one, and set PIN. Deactivation and a new PIN end the operator's sessions (R5, R6).
/// </summary>
public sealed class PosOperatorOperations(IXerpDb db, ITenantContext context, IClock clock, IRules rules, IPinHasher pinHasher)
    : CodedMasterOperations<PosOperator, PosOperatorDto, PosOperatorWrite, PosOperatorValues>(db, context, clock, rules)
{
    private Dictionary<Guid, ReferenceSummary> _roles = [];

    protected override DbSet<PosOperator> Records => Db.PosOperators;
    protected override MasterRuleSet RuleSet => RuleRegistry.PosOperator;
    protected override string Word => "operator";
    protected override string AWord => "an operator";

    public Task<Result<PosOperatorDto>> CreateAsync(CreatePosOperatorInput input, CancellationToken cancellationToken = default) =>
        CreateAsync(new PosOperatorWrite(input.Code, input.Name, input.Has(nameof(input.RoleId)), input.RoleId, input.Pin, input.IsActive, Replace: false), cancellationToken);

    public Task<Result<PosOperatorDto>> ReplaceAsync(Guid id, ReplacePosOperatorInput input, CancellationToken cancellationToken = default) =>
        ReplaceAsync(id, Write(input), cancellationToken);

    /// <summary>As the overload with a <see cref="Guid"/>; an id that is not a UUID names no operator.</summary>
    public Task<Result<PosOperatorDto>> ReplaceAsync(string? id, ReplacePosOperatorInput input, CancellationToken cancellationToken = default) =>
        ReplaceAsync(id, Write(input), cancellationToken);

    public Task<Result<PagedResult<PosOperatorDto>>> ListAsync(ListPosOperatorsInput input, CancellationToken cancellationToken = default)
    {
        var errors = new ValidationErrors();
        var roleId = PosValidation.IdFilter(errors, "roleId", input.RoleId);
        return ListAsync(
            new ListCodedMastersInput(input.Search, input.IsActive, input.Limit, input.Offset), errors,
            roleId is { } r ? operators => operators.Where(o => o.RoleId == r) : null, cancellationToken);
    }

    /// <summary>R5: validation -> exists -> new hash, unlocked, every active session of the operator ended.</summary>
    public async Task<Result<PosOperatorDto>> SetPinAsync(Guid id, SetPosOperatorPinInput input, CancellationToken cancellationToken = default)
    {
        var validated = PosValidation.SetPin(input);
        if (!validated.IsSuccess)
            return validated.Error;
        // The slow hash is computed before the lock is taken.
        var hash = pinHasher.Hash(validated.Value);

        try
        {
            return await Db.SerializedPerTenantAsync<Result<PosOperatorDto>>(async ct =>
            {
                var @operator = await Db.LockPosOperatorAsync(id, ct);
                if (@operator is null)
                    return NotFound();
                var now = clock.UtcNow;
                @operator.SetPin(hash, now, ActorKeyId());
                await PosSessions.EndAsync(Db.PosSessions.Where(s => s.OperatorId == id), now, ct);
                await Db.SaveChangesAsync(ct);
                await PrepareAsync([@operator], ct);
                return ToDto(@operator);
            }, cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return NotFound();
        }
    }

    /// <summary>As the overload with a <see cref="Guid"/>; an id that is not a UUID names no operator.</summary>
    public Task<Result<PosOperatorDto>> SetPinAsync(SetPosOperatorPinArguments input, CancellationToken cancellationToken = default) =>
        RecordAddress.Id(input.Id, NotFoundDetail, out var parsed) is { } error
            ? Task.FromResult<Result<PosOperatorDto>>(error)
            : SetPinAsync(parsed, new SetPosOperatorPinInput(input.Pin), cancellationToken);

    protected override Result<PosOperatorValues> Validate(PosOperatorWrite input, bool replace) =>
        throw new InvalidOperationException("An operator is validated with the tenant's rules.");

    protected override Result<PosOperatorValues> Validate(PosOperatorWrite input, bool replace, TenantRules rules) =>
        PosValidation.Operator(input with { Replace = replace }, rules);

    protected override PosOperator Create(PosOperatorValues v, DateTime now, Guid actorKeyId) =>
        PosOperator.Create(v.Code, v.Name, v.RoleId, pinHasher.Hash(v.Pin!), v.IsActive, now, actorKeyId);

    protected override void Replace(PosOperator record, PosOperatorValues v, DateTime now, Guid actorKeyId) =>
        record.Replace(v.Code, v.Name, v.RoleId, v.IsActive, now, actorKeyId);

    /// <summary>R3: a role the operator already has may be kept although it is inactive.</summary>
    protected override async Task<AppError?> ReferencesAsync(PosOperatorValues values, PosOperator? stored, CancellationToken cancellationToken) =>
        values.RoleId is { } roleId
            ? await ReferenceCheck.ValidateAsync(
                Db.PosRoles.Where(r => r.Id == roleId).Select(r => r.IsActive), "roleId", roleId, stored?.RoleId == roleId, cancellationToken)
            : null;

    /// <summary>R6: deactivation ends every active session of the operator in the same transaction.</summary>
    protected override async Task ReplacingAsync(PosOperator record, bool wasActive, DateTime now, CancellationToken cancellationToken)
    {
        if (!record.IsActive)
            await PosSessions.EndAsync(Db.PosSessions.Where(s => s.OperatorId == record.Id), now, cancellationToken);
    }

    protected override async Task PrepareAsync(IReadOnlyList<PosOperator> records, CancellationToken cancellationToken)
    {
        var ids = records.Where(o => o.RoleId is not null).Select(o => o.RoleId!.Value).Distinct().ToList();
        _roles = ids.Count == 0
            ? []
            : await Db.PosRoles.AsNoTracking().Where(r => ids.Contains(r.Id))
                .ToDictionaryAsync(r => r.Id, r => new ReferenceSummary(r.Id, r.Code, r.Name), cancellationToken);
    }

    protected override PosOperatorDto ToDto(PosOperator o) =>
        new(o.Id, o.Code, o.Name, o.IsActive, o.RoleId is { } roleId ? _roles.GetValueOrDefault(roleId) : null, o.PinChangedAt, o.LockedUntil,
            o.CreatedAt, o.UpdatedAt, o.CreatedBy, o.UpdatedBy);

    /// <summary>R7: sessions name the operator - and later bills; retire it by deactivation.</summary>
    protected override async Task<string?> UsedByAsync(Guid id, CancellationToken cancellationToken) =>
        await Db.PosSessions.AnyAsync(s => s.OperatorId == id, cancellationToken)
            ? "The operator has signed in (it has sessions)."
            : null;

    private static PosOperatorWrite Write(ReplacePosOperatorInput input) =>
        new(input.Code, input.Name, input.Has(nameof(input.RoleId)), input.RoleId, null, input.IsActive, Replace: true);
}
