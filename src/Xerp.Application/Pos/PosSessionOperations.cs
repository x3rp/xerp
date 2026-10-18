using Microsoft.EntityFrameworkCore;
using Xerp.Application.Common;
using Xerp.Application.Ports;
using Xerp.Domain.Pos;
using Xerp.Domain.Rules;

namespace Xerp.Application.Pos;

/// <summary>Queries over sessions that several operations share.</summary>
internal static class PosSessions
{
    /// <summary>Ends the active ones of <paramref name="sessions"/> at <paramref name="now"/>; expired and ended ones are not written (R14).</summary>
    public static Task<int> EndAsync(IQueryable<PosSession> sessions, DateTime now, CancellationToken cancellationToken) =>
        sessions
            .Where(s => s.EndedAt == null && s.ExpiresAt > now)
            .ExecuteUpdateAsync(u => u.SetProperty(s => s.EndedAt, (DateTime?)now), cancellationToken);
}

/// <summary>
/// The sign-in of an operator with code and PIN (spec P02, R10, R11), for a session and for an approver. It
/// runs in the caller's transaction, which holds the tenant's lock, and locks the operator's row: a wrong
/// PIN is counted and saved before the refusal is returned, and the caller commits it either way.
/// </summary>
public sealed class PosSignIn(IXerpDb db, IClock clock, IPinHasher pinHasher)
{
    private static string? _dummyHash;

    /// <returns>The operator, tracked, when code and PIN sign it in; null for every cause of failure alike.</returns>
    public async Task<PosOperator?> VerifyAsync(string code, string pin, CancellationToken cancellationToken)
    {
        var id = await db.PosOperators
            .Where(o => EF.Property<string>(o, DbNames.CodeLower) == DbText.Lower(code))
            .Select(o => (Guid?)o.Id)
            .SingleOrDefaultAsync(cancellationToken);
        var @operator = id is { } found ? await db.LockPosOperatorAsync(found, cancellationToken) : null;
        var now = clock.UtcNow;

        // S2: the same hash verification is run for a cause that never reaches the PIN.
        if (@operator is null || !@operator.IsActive || @operator.IsLocked(now))
        {
            pinHasher.Verify(_dummyHash ??= pinHasher.Hash("0000"), pin);
            return null;
        }
        if (!pinHasher.Verify(@operator.PinHash, pin))
        {
            @operator.RecordFailedPin(now);
            await db.SaveChangesAsync(cancellationToken);
            return null;
        }
        @operator.RecordSignIn();
        return @operator;
    }
}

/// <summary>
/// Operator sessions (spec P02, 4.C) and authorize (R20). A session belongs to the key that started it: get,
/// list and end see only the calling key's sessions (R13). <see cref="IXerpDb"/> is filtered to the tenant.
/// </summary>
public sealed class PosSessionOperations(IXerpDb db, ITenantContext context, IClock clock, IRules rules, PosSignIn signIn)
{
    private const string NotFoundDetail = "Operator session not found.";

    /// <summary>R10-R12, in one transaction under the tenant's lock: the counters of a failure are committed too.</summary>
    public async Task<Result<PosSessionDto>> StartAsync(StartPosSessionInput input, CancellationToken cancellationToken = default)
    {
        var validated = PosValidation.Start(input);
        if (!validated.IsSuccess)
            return validated.Error;
        var (code, pin) = validated.Value;
        var keyId = ActorKeyId();
        var tenantRules = await rules.ReadAsync(cancellationToken);

        try
        {
            var sessionId = await db.SerializedPerTenantAsync<Guid?>(async ct =>
            {
                var @operator = await signIn.VerifyAsync(code, pin, ct);
                if (@operator is null)
                    return null;
                var now = clock.UtcNow;
                if (!tenantRules[RuleRegistry.PosSeveralOperatorsPerDeviceAllowed])
                    await PosSessions.EndAsync(db.PosSessions.Where(s => s.ApiKeyId == keyId), now, ct);
                if (!tenantRules[RuleRegistry.PosOperatorOnSeveralDevicesAllowed])
                    await PosSessions.EndAsync(db.PosSessions.Where(s => s.OperatorId == @operator.Id && s.ApiKeyId != keyId), now, ct);
                var session = PosSession.Start(@operator.Id, keyId, now);
                db.PosSessions.Add(session);
                await db.SaveChangesAsync(ct);
                return session.Id;
            }, cancellationToken);
            if (sessionId is not { } id)
                return AppError.OperatorSignInFailed("pin");
            return (await ReadAsync(id, cancellationToken))!;
        }
        catch (ForeignKeyViolationException)
        {
            // S3: the operator was deleted between the lookup and the insert.
            return AppError.OperatorSignInFailed("pin");
        }
    }

    public async Task<Result<PosSessionDto>> GetAsync(Guid id, CancellationToken cancellationToken = default) =>
        await ReadAsync(id, cancellationToken) is { } session ? session : NotFound();

    /// <summary>As the overload with a <see cref="Guid"/>; an id that is not a UUID names no session.</summary>
    public Task<Result<PosSessionDto>> GetAsync(RecordIdInput input, CancellationToken cancellationToken = default) =>
        RecordAddress.Id(input.Id, NotFoundDetail, out var parsed) is { } error
            ? Task.FromResult<Result<PosSessionDto>>(error)
            : GetAsync(parsed, cancellationToken);

    /// <summary>R13: the calling key's sessions, newest first.</summary>
    public async Task<Result<PagedResult<PosSessionDto>>> ListAsync(ListPosSessionsInput input, CancellationToken cancellationToken = default)
    {
        var validated = PosValidation.SessionList(input);
        if (!validated.IsSuccess)
            return validated.Error;
        var (status, limit, offset) = validated.Value;
        var now = clock.UtcNow;

        var sessions = OwnSessions().AsNoTracking();
        sessions = status switch
        {
            PosSessionStatus.Active => sessions.Where(s => s.EndedAt == null && s.ExpiresAt > now),
            PosSessionStatus.Ended => sessions.Where(s => s.EndedAt != null || s.ExpiresAt <= now),
            _ => sessions,
        };
        var total = await sessions.CountAsync(cancellationToken);
        var items = await Read(sessions.OrderByDescending(s => s.StartedAt).ThenByDescending(s => s.Id).Skip(offset).Take(limit))
            .ToListAsync(cancellationToken);
        return new PagedResult<PosSessionDto>(items.Select(r => ToDto(r, now)).ToList(), total, limit, offset);
    }

    /// <summary>R15: ending an ended or expired session answers it unchanged.</summary>
    public async Task<Result<PosSessionDto>> EndAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var session = await OwnSessions().SingleOrDefaultAsync(s => s.Id == id, cancellationToken);
        if (session is null)
            return NotFound();
        session.End(clock.UtcNow);
        await db.SaveChangesAsync(cancellationToken);
        return await ReadAsync(id, cancellationToken) is { } read ? read : NotFound();
    }

    /// <summary>As the overload with a <see cref="Guid"/>; an id that is not a UUID names no session.</summary>
    public Task<Result<PosSessionDto>> EndAsync(RecordIdInput input, CancellationToken cancellationToken = default) =>
        RecordAddress.Id(input.Id, NotFoundDetail, out var parsed) is { } error
            ? Task.FromResult<Result<PosSessionDto>>(error)
            : EndAsync(parsed, cancellationToken);

    public Task<Result<PosAuthorizationDto>> AuthorizeAsync(AuthorizeInput input, CancellationToken cancellationToken = default)
    {
        var validated = PosValidation.Authorize(input);
        return validated.IsSuccess ? AuthorizeAsync(validated.Value, cancellationToken) : Task.FromResult<Result<PosAuthorizationDto>>(validated.Error);
    }

    /// <summary>
    /// R20: the check every sensitive act calls - the session's operator has the right, or a second operator
    /// approves with code and PIN. Writes nothing but the approver's PIN counters (R21).
    /// </summary>
    public async Task<Result<PosAuthorizationDto>> AuthorizeAsync(AuthorizeValues values, CancellationToken cancellationToken = default)
    {
        var right = values.Right.ToName();
        var session = await ReadRowAsync(values.SessionId, cancellationToken);
        if (session is null)
            return AppError.ReferenceNotFound("sessionId", values.SessionId);
        var now = clock.UtcNow;
        if (PosRules.SessionStatus(session.Session.EndedAt, session.Session.ExpiresAt, now) != PosSessionStatus.Active)
            return AppError.SessionNotActive();

        var operatorHasRight = RightsOf(session.Role).Has(values.Right);
        var @operator = new ReferenceSummary(session.OperatorId, session.OperatorCode, session.OperatorName);
        if (values.ApproverCode is not { } approverCode || values.ApproverPin is not { } approverPin)
        {
            return PosRules.Decide(operatorHasRight, false, true, false, false, false) == AuthorizeOutcome.Allowed
                ? new PosAuthorizationDto(right, @operator, null)
                : AppError.OperatorRightMissing(
                    $"The operator's role does not allow {right}. A second operator with the right may approve it (approverCode, approverPin).",
                    "right", $"The operator may not do {right}.");
        }

        var tenantRules = await rules.ReadAsync(cancellationToken);
        var approvalAllowed = tenantRules[RuleRegistry.PosSecondOperatorApprovalAllowed];
        var approverIsOperator = string.Equals(approverCode, session.OperatorCode, StringComparison.OrdinalIgnoreCase);
        switch (PosRules.BeforeApproverSignIn(approvalAllowed, approverIsOperator))
        {
            case AuthorizeOutcome.ApprovalNotAllowed:
                return AppError.OperatorRightMissing(
                        $"This company's rule {RuleRegistry.PosSecondOperatorApprovalAllowed.Key} is false: only the operator's own rights count.",
                        "approverCode", "A second operator's approval is not allowed.")
                    .RefusedBy(RuleRegistry.PosSecondOperatorApprovalAllowed, false);
            case AuthorizeOutcome.ApproverIsOperator:
                return AppError.OperatorRightMissing(
                    "The approver must be another operator than the session's.", "approverCode", "The approver is the session's operator.");
        }

        var approver = await db.SerializedPerTenantAsync(async ct =>
        {
            var signedIn = await signIn.VerifyAsync(approverCode, approverPin, ct);
            if (signedIn is null)
                return null;
            var role = signedIn.RoleId is { } roleId ? await db.PosRoles.AsNoTracking().SingleOrDefaultAsync(r => r.Id == roleId, ct) : null;
            // R21: a success writes nothing; the reset of the approver's count is a write of the counters.
            await db.SaveChangesAsync(ct);
            return new { Summary = new ReferenceSummary(signedIn.Id, signedIn.Code, signedIn.Name), Rights = RightsOf(role) };
        }, cancellationToken);

        return PosRules.Decide(operatorHasRight, true, approvalAllowed, approverIsOperator, approver is not null, approver?.Rights.Has(values.Right) == true) switch
        {
            AuthorizeOutcome.ApproverSignInFailed => AppError.OperatorSignInFailed("approverPin"),
            AuthorizeOutcome.Approved => new PosAuthorizationDto(right, @operator, approver!.Summary),
            _ => AppError.OperatorRightMissing(
                $"The approver's role does not allow {right}.", "approverCode", $"The approver may not do {right}."),
        };
    }

    private IQueryable<PosSession> OwnSessions()
    {
        var keyId = ActorKeyId();
        return db.PosSessions.Where(s => s.ApiKeyId == keyId);
    }

    private sealed record SessionRow(PosSession Session, Guid OperatorId, string OperatorCode, string OperatorName, PosRole? Role);

    private IQueryable<SessionRow> Read(IQueryable<PosSession> sessions) =>
        from s in sessions
        join o in db.PosOperators on s.OperatorId equals o.Id
        join r in db.PosRoles on o.RoleId equals r.Id into roles
        from r in roles.DefaultIfEmpty()
        select new SessionRow(s, o.Id, o.Code, o.Name, r);

    private Task<SessionRow?> ReadRowAsync(Guid id, CancellationToken cancellationToken) =>
        Read(OwnSessions().AsNoTracking().Where(s => s.Id == id)).SingleOrDefaultAsync(cancellationToken);

    private async Task<PosSessionDto?> ReadAsync(Guid id, CancellationToken cancellationToken) =>
        await ReadRowAsync(id, cancellationToken) is { } row ? ToDto(row, clock.UtcNow) : null;

    /// <summary>R14: no role, or an inactive one, gives no rights.</summary>
    private static PosRights RightsOf(PosRole? role) => role is { IsActive: true } ? role.Rights : PosRights.None;

    private static PosSessionDto ToDto(SessionRow row, DateTime now)
    {
        var s = row.Session;
        var role = row.Role is { IsActive: true } r ? new ReferenceSummary(r.Id, r.Code, r.Name) : null;
        return new PosSessionDto(
            s.Id, PosRules.SessionStatus(s.EndedAt, s.ExpiresAt, now).ToName(),
            new ReferenceSummary(row.OperatorId, row.OperatorCode, row.OperatorName), role, RightsOf(row.Role), s.ApiKeyId,
            s.StartedAt, s.ExpiresAt, PosRules.SessionEndedAt(s.EndedAt, s.ExpiresAt, now));
    }

    private Guid ActorKeyId() =>
        context.ApiKeyId ?? throw new InvalidOperationException("An operator session can only be used by a tenant API key.");

    private static AppError NotFound() => AppError.NotFound(NotFoundDetail);
}
