namespace Xerp.Domain.Pos;

/// <summary>The seven sensitive acts a POS role may allow (spec P02, 4.A; ADR-0022/7): role data, not rules.</summary>
public enum PosRight
{
    ChangePrice,
    Discount,
    VoidLine,
    VoidBill,
    Refund,
    CashMovement,
    CloseShift,
}

/// <summary>The contract names of <see cref="PosRight"/>: the members of <c>rights</c> and the values of <c>right</c>.</summary>
public static class PosRightNames
{
    public const string ChangePrice = "changePrice";
    public const string Discount = "discount";
    public const string VoidLine = "voidLine";
    public const string VoidBill = "voidBill";
    public const string Refund = "refund";
    public const string CashMovement = "cashMovement";
    public const string CloseShift = "closeShift";

    /// <summary>In the order of <see cref="PosRight"/>.</summary>
    public static IReadOnlyList<string> All { get; } = [ChangePrice, Discount, VoidLine, VoidBill, Refund, CashMovement, CloseShift];

    public static string ToName(this PosRight right) => All[(int)right];

    /// <summary>Exact, case-sensitive match: <c>Refund</c> is not a right.</summary>
    public static bool TryParse(string? name, out PosRight right)
    {
        for (var i = 0; i < All.Count; i++)
        {
            if (string.Equals(All[i], name, StringComparison.Ordinal))
            {
                right = (PosRight)i;
                return true;
            }
        }
        right = default;
        return false;
    }
}

/// <summary>What a role allows: one flag per <see cref="PosRight"/>. A new role allows nothing.</summary>
public sealed record PosRights(
    bool ChangePrice, bool Discount, bool VoidLine, bool VoidBill, bool Refund, bool CashMovement, bool CloseShift)
{
    public static readonly PosRights None = new(false, false, false, false, false, false, false);

    public bool Has(PosRight right) => right switch
    {
        PosRight.ChangePrice => ChangePrice,
        PosRight.Discount => Discount,
        PosRight.VoidLine => VoidLine,
        PosRight.VoidBill => VoidBill,
        PosRight.Refund => Refund,
        PosRight.CashMovement => CashMovement,
        PosRight.CloseShift => CloseShift,
        _ => throw new ArgumentOutOfRangeException(nameof(right)),
    };
}

/// <summary>The state of an operator session (spec P02, R14).</summary>
public enum PosSessionStatus
{
    Active,
    Ended,
}

/// <summary>Why an authorize is refused, or that it is not (spec P02, R20 steps 4 and 5).</summary>
public enum AuthorizeOutcome
{
    /// <summary>The session's operator has the right itself.</summary>
    Allowed,

    /// <summary>The approver signed in and has the right.</summary>
    Approved,

    /// <summary>No approver, and the operator lacks the right: key <c>right</c>.</summary>
    OperatorLacksRight,

    /// <summary><c>pos.secondOperatorApprovalAllowed</c> is false: key <c>approverCode</c>, with <c>rules</c>.</summary>
    ApprovalNotAllowed,

    /// <summary>The approver is the session's operator: key <c>approverCode</c>, no <c>rules</c>.</summary>
    ApproverIsOperator,

    /// <summary>The approver's sign-in failed: <c>OPERATOR_SIGN_IN_FAILED</c>, key <c>approverPin</c>.</summary>
    ApproverSignInFailed,

    /// <summary>The approver signed in but lacks the right: key <c>approverCode</c>.</summary>
    ApproverLacksRight,
}

/// <summary>
/// The invariants of operators and sessions (spec P02, 6.2) and the decisions of R11, R14 and R20, as pure
/// functions of their values and of the clock's now. The numbers are bounds of the platform, not rules.
/// </summary>
public static class PosRules
{
    public const int PinMinLength = 4;
    public const int PinMaxLength = 8;
    public const int MaxFailedPinAttempts = 5;
    public static readonly TimeSpan LockDuration = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan SessionLifetime = TimeSpan.FromHours(12);

    /// <summary>R4, E1: 4 to 8 ASCII digits, exactly as sent - not trimmed, no other digits of Unicode.</summary>
    public static bool IsValidPin(string? pin) =>
        pin is { Length: >= PinMinLength and <= PinMaxLength } && pin.All(c => c is >= '0' and <= '9');

    /// <summary>R10: locked while <paramref name="lockedUntil"/> is in the future.</summary>
    public static bool IsLocked(DateTime? lockedUntil, DateTime now) => lockedUntil is { } until && until > now;

    /// <summary>
    /// R11: a wrong PIN of an active, unlocked operator. At the fifth consecutive failure the operator is locked
    /// for 15 minutes and the count starts again at 0.
    /// </summary>
    public static (int FailedPinAttempts, DateTime? LockedUntil) AfterFailedPin(int failedPinAttempts, DateTime now)
    {
        var failed = failedPinAttempts + 1;
        return failed >= MaxFailedPinAttempts ? (0, now + LockDuration) : (failed, null);
    }

    public static DateTime SessionExpiresAt(DateTime startedAt) => startedAt + SessionLifetime;

    /// <summary>R14: active exactly when not ended and not yet expired.</summary>
    public static PosSessionStatus SessionStatus(DateTime? endedAt, DateTime expiresAt, DateTime now) =>
        endedAt is null && now < expiresAt ? PosSessionStatus.Active : PosSessionStatus.Ended;

    /// <summary>R14: an expired session reads as ended at its expiry; nothing is written.</summary>
    public static DateTime? SessionEndedAt(DateTime? endedAt, DateTime expiresAt, DateTime now) =>
        endedAt ?? (now < expiresAt ? null : expiresAt);

    /// <summary>
    /// R20 step 5, before the approver signs in: whether the approval is refused without counting anything.
    /// Null when the approver is to be signed in.
    /// </summary>
    public static AuthorizeOutcome? BeforeApproverSignIn(bool secondOperatorApprovalAllowed, bool approverIsOperator) =>
        !secondOperatorApprovalAllowed ? AuthorizeOutcome.ApprovalNotAllowed
        : approverIsOperator ? AuthorizeOutcome.ApproverIsOperator
        : null;

    /// <summary>
    /// R20 steps 4 and 5. Without an approver only the operator's right counts; with one, the approver is always
    /// checked, whether or not the operator has the right (E8).
    /// </summary>
    /// <param name="operatorHasRight">The operator's role is present, active and has the right.</param>
    /// <param name="approverGiven">An approver code and PIN came with the request.</param>
    /// <param name="approverSignedIn">The approver's code and PIN verified (R10); ignored without an approver.</param>
    /// <param name="approverHasRight">The approver's role is present, active and has the right; ignored without an approver.</param>
    public static AuthorizeOutcome Decide(
        bool operatorHasRight, bool approverGiven, bool secondOperatorApprovalAllowed, bool approverIsOperator,
        bool approverSignedIn, bool approverHasRight)
    {
        if (!approverGiven)
            return operatorHasRight ? AuthorizeOutcome.Allowed : AuthorizeOutcome.OperatorLacksRight;
        if (BeforeApproverSignIn(secondOperatorApprovalAllowed, approverIsOperator) is { } refused)
            return refused;
        if (!approverSignedIn)
            return AuthorizeOutcome.ApproverSignInFailed;
        return approverHasRight ? AuthorizeOutcome.Approved : AuthorizeOutcome.ApproverLacksRight;
    }
}
