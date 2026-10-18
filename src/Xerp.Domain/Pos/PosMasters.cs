using Xerp.Domain.Common;
using Xerp.Domain.Sales;

namespace Xerp.Domain.Pos;

/// <summary>A role of POS operators (spec P02, 4.A): a master with the seven rights its operators have.</summary>
public sealed class PosRole : CodedMaster
{
    private PosRole() { }

    public bool CanChangePrice { get; private set; }
    public bool CanDiscount { get; private set; }
    public bool CanVoidLine { get; private set; }
    public bool CanVoidBill { get; private set; }
    public bool CanRefund { get; private set; }
    public bool CanCashMovement { get; private set; }
    public bool CanCloseShift { get; private set; }

    public PosRights Rights => new(CanChangePrice, CanDiscount, CanVoidLine, CanVoidBill, CanRefund, CanCashMovement, CanCloseShift);

    public static PosRole Create(string code, string name, PosRights rights, bool isActive, DateTime now, Guid actorKeyId)
    {
        var role = new PosRole();
        role.Init(code, name, isActive, now, actorKeyId);
        role.SetRights(rights);
        return role;
    }

    /// <summary>R6: a change of a role ends no session; sessions read the rights live (R14).</summary>
    public void Replace(string code, string name, PosRights rights, bool isActive, DateTime now, Guid actorKeyId)
    {
        Change(code, name, isActive, now, actorKeyId);
        SetRights(rights);
    }

    private void SetRights(PosRights rights)
    {
        CanChangePrice = rights.ChangePrice;
        CanDiscount = rights.Discount;
        CanVoidLine = rights.VoidLine;
        CanVoidBill = rights.VoidBill;
        CanRefund = rights.Refund;
        CanCashMovement = rights.CashMovement;
        CanCloseShift = rights.CloseShift;
    }
}

/// <summary>
/// A member of a venue's staff who signs in on a POS device with code and PIN (spec P02, 4.B). Only a salted,
/// slow hash of the PIN is kept (6.2); it is never part of any representation.
/// </summary>
public sealed class PosOperator : CodedMaster
{
    private PosOperator() { }

    public Guid? RoleId { get; private set; }
    public string PinHash { get; private set; } = "";
    public DateTime PinChangedAt { get; private set; }
    public int FailedPinAttempts { get; private set; }
    public DateTime? LockedUntil { get; private set; }

    public static PosOperator Create(string code, string name, Guid? roleId, string pinHash, bool isActive, DateTime now, Guid actorKeyId)
    {
        var @operator = new PosOperator();
        @operator.Init(code, name, isActive, now, actorKeyId);
        @operator.RoleId = roleId;
        @operator.PinHash = pinHash;
        @operator.PinChangedAt = now;
        return @operator;
    }

    public void Replace(string code, string name, Guid? roleId, bool isActive, DateTime now, Guid actorKeyId)
    {
        Change(code, name, isActive, now, actorKeyId);
        RoleId = roleId;
    }

    public bool IsLocked(DateTime now) => PosRules.IsLocked(LockedUntil, now);

    /// <summary>R5: a new PIN also unlocks the operator and clears its failures; the caller ends its sessions.</summary>
    public void SetPin(string pinHash, DateTime now, Guid actorKeyId)
    {
        PinHash = pinHash;
        PinChangedAt = now;
        FailedPinAttempts = 0;
        LockedUntil = null;
        Touch(now, actorKeyId);
    }

    /// <summary>R11: a wrong PIN of an active, unlocked operator. Not attributed: it is no change by the caller.</summary>
    public void RecordFailedPin(DateTime now) => (FailedPinAttempts, LockedUntil) = PosRules.AfterFailedPin(FailedPinAttempts, now);

    /// <summary>R12: a success resets the count.</summary>
    public void RecordSignIn() => FailedPinAttempts = 0;
}

/// <summary>
/// An operator signed in on the device of one API key (spec P02, 4.C). It lasts at most 12 hours; it is ended
/// by end, by a new PIN and by deactivation of its operator. Created, then at most ended: never otherwise changed.
/// </summary>
public sealed class PosSession : ITenantOwned
{
    private PosSession() { }

    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid OperatorId { get; private set; }
    public Guid ApiKeyId { get; private set; }
    public DateTime StartedAt { get; private set; }
    public DateTime ExpiresAt { get; private set; }
    public DateTime? EndedAt { get; private set; }
    public Guid CreatedBy { get; private set; }

    public static PosSession Start(Guid operatorId, Guid apiKeyId, DateTime now) => new()
    {
        Id = Guid.CreateVersion7(),
        OperatorId = operatorId,
        ApiKeyId = apiKeyId,
        StartedAt = now,
        ExpiresAt = PosRules.SessionExpiresAt(now),
        CreatedBy = apiKeyId,
    };

    public PosSessionStatus Status(DateTime now) => PosRules.SessionStatus(EndedAt, ExpiresAt, now);

    /// <summary>R15: ending an ended or expired session changes nothing.</summary>
    public void End(DateTime now)
    {
        if (Status(now) == PosSessionStatus.Active)
            EndedAt = now;
    }
}
