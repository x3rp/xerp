using Xerp.Application.Common;
using Xerp.Application.Sales;
using Xerp.Domain.Pos;

namespace Xerp.Application.Pos;

public sealed record PosRoleDto(
    Guid Id, string Code, string Name, bool IsActive, PosRights Rights,
    DateTime CreatedAt, DateTime UpdatedAt, Guid CreatedBy, Guid UpdatedBy);

/// <summary>An operator as clients see it: never its PIN, never the hash (spec P02, 4.B).</summary>
public sealed record PosOperatorDto(
    Guid Id, string Code, string Name, bool IsActive, ReferenceSummary? Role, DateTime PinChangedAt, DateTime? LockedUntil,
    DateTime CreatedAt, DateTime UpdatedAt, Guid CreatedBy, Guid UpdatedBy);

/// <summary>
/// An operator session (spec P02, 4.C). <c>Role</c> is null and every right false when the operator has no role
/// or its role is inactive; both are read at each read (R14).
/// </summary>
public sealed record PosSessionDto(
    Guid Id, string Status, ReferenceSummary Operator, ReferenceSummary? Role, PosRights Rights, Guid ApiKeyId,
    DateTime StartedAt, DateTime ExpiresAt, DateTime? EndedAt);

/// <summary>The answer of authorize (R20): the right, who acts, and who approved - null when the operator's own right sufficed.</summary>
public sealed record PosAuthorizationDto(string Right, ReferenceSummary Operator, ReferenceSummary? ApprovedBy);

/// <summary>The seven members of <c>rights</c>; each is required, so each is nullable here and judged by the validation (R2).</summary>
public sealed record PosRightsInput(
    bool? ChangePrice = null, bool? Discount = null, bool? VoidLine = null, bool? VoidBill = null,
    bool? Refund = null, bool? CashMovement = null, bool? CloseShift = null);

/// <summary>The body of a create and of a replace of a role. On a create <c>IsActive</c> is optional; a replace requires it.</summary>
public sealed record PosRoleInput(string? Code = null, string? Name = null, PosRightsInput? Rights = null, bool? IsActive = null);

public sealed record PosRoleValues(string Code, string Name, PosRights Rights, bool IsActive) : CodedMasterValues(Code, Name, IsActive);

/// <summary>The body of an operator's create (spec P02, 4.B): with the PIN. <c>RoleId</c> may be null (R3).</summary>
public sealed record CreatePosOperatorInput : TrackedInput
{
    private readonly string? _roleId;

    public string? Code { get; init; }
    public string? Name { get; init; }
    public string? RoleId { get => _roleId; init => _roleId = Given(value); }
    public string? Pin { get; init; }
    public bool? IsActive { get; init; }
}

/// <summary>The body of an operator's replace: no PIN - a <c>pin</c> is an unknown property (4.B). Every member is required.</summary>
public record ReplacePosOperatorInput : TrackedInput
{
    private readonly string? _roleId;

    public string? Code { get; init; }
    public string? Name { get; init; }
    public string? RoleId { get => _roleId; init => _roleId = Given(value); }
    public bool? IsActive { get; init; }
}

/// <summary>What a create and a replace of an operator share once bound; <c>Replace</c> says which it is.</summary>
public sealed record PosOperatorWrite(string? Code, string? Name, bool RoleIdGiven, string? RoleId, string? Pin, bool? IsActive, bool Replace);

public sealed record PosOperatorValues(string Code, string Name, Guid? RoleId, string? Pin, bool IsActive) : CodedMasterValues(Code, Name, IsActive);

public sealed record ListPosOperatorsInput(
    string? Search = null, bool? IsActive = null, string? RoleId = null, int? Limit = null, int? Offset = null);

/// <summary>The body of set PIN.</summary>
public sealed record SetPosOperatorPinInput(string? Pin = null);

/// <summary>The arguments of set PIN without a URL path.</summary>
public sealed record SetPosOperatorPinArguments(string? Id = null, string? Pin = null);

public sealed record StartPosSessionInput(string? OperatorCode = null, string? Pin = null);

public sealed record ListPosSessionsInput(string? Status = null, int? Limit = null, int? Offset = null);

public sealed record AuthorizeInput(string? SessionId = null, string? Right = null, string? ApproverCode = null, string? ApproverPin = null);

/// <summary>Validated values of authorize; the approver is null when none was given.</summary>
public sealed record AuthorizeValues(Guid SessionId, PosRight Right, string? ApproverCode, string? ApproverPin);

public static class PosSessionStatusNames
{
    public const string Active = "active";
    public const string Ended = "ended";

    public static string ToName(this PosSessionStatus status) => status == PosSessionStatus.Active ? Active : Ended;
}
