using Xerp.Application.Common;
using Xerp.Application.Ports;
using Xerp.Domain.Pos;
using Xerp.Domain.Rules;

namespace Xerp.Application.Pos;

/// <summary>
/// Input rules of spec P02. No I/O. Everything here is an invariant (6.2) except the role of an operator, which
/// the tenant's rule <c>posOperator.roleRequired</c> decides (R3).
/// </summary>
public static class PosValidation
{
    private static readonly string PinForm =
        $"must be a string of {PosRules.PinMinLength} to {PosRules.PinMaxLength} digits 0-9, without spaces.";

    public static Result<PosRoleValues> Role(PosRoleInput input, bool replace)
    {
        var errors = new ValidationErrors();
        var code = errors.Code(input.Code);
        var name = errors.Name(input.Name);
        var rights = Rights(errors, input.Rights);
        if (replace && input.IsActive is null)
            errors.Add("isActive", "isActive is required.");
        return errors.Any ? errors.ToError() : new PosRoleValues(code, name, rights!, input.IsActive ?? true);
    }

    /// <summary>R2: an object with all seven members, each a JSON boolean (unknown members and wrong types are refused by the binding).</summary>
    private static PosRights? Rights(ValidationErrors errors, PosRightsInput? input)
    {
        if (input is null)
        {
            errors.Add("rights", $"rights is required: an object with the boolean members {string.Join(", ", PosRightNames.All)}.");
            return null;
        }
        bool?[] values = [input.ChangePrice, input.Discount, input.VoidLine, input.VoidBill, input.Refund, input.CashMovement, input.CloseShift];
        for (var i = 0; i < values.Length; i++)
            if (values[i] is null)
                errors.Add($"rights.{PosRightNames.All[i]}", $"rights.{PosRightNames.All[i]} is required: true or false.");
        return values.All(v => v is not null)
            ? new PosRights(values[0]!.Value, values[1]!.Value, values[2]!.Value, values[3]!.Value, values[4]!.Value, values[5]!.Value, values[6]!.Value)
            : null;
    }

    /// <summary>R1, R3, R4: on a replace there is no PIN and every member is required.</summary>
    public static Result<PosOperatorValues> Operator(PosOperatorWrite input, TenantRules rules)
    {
        var errors = new ValidationErrors();
        var code = errors.Code(input.Code);
        var name = errors.Name(input.Name);

        Guid? roleId = null;
        if (input.Replace && !input.RoleIdGiven)
            errors.Add("roleId", "roleId is required: the id of a POS role, or null for none.");
        else if (input.RoleId is not null)
        {
            if (Guid.TryParse(input.RoleId, out var parsed)) roleId = parsed;
            else errors.Add("roleId", "roleId must be the id (UUID) of a POS role, or null.");
        }
        new RequiredFields(rules, RuleRegistry.PosOperatorRequiredFields).CheckValue(errors, "roleId", input.RoleId is null);

        if (!input.Replace)
            Pin(errors, "pin", input.Pin);
        if (input.Replace && input.IsActive is null)
            errors.Add("isActive", "isActive is required.");
        return errors.Any ? errors.ToError() : new PosOperatorValues(code, name, roleId, input.Pin, input.IsActive ?? true);
    }

    public static Result<string> SetPin(SetPosOperatorPinInput input)
    {
        var errors = new ValidationErrors();
        Pin(errors, "pin", input.Pin);
        return errors.Any ? errors.ToError() : input.Pin!;
    }

    /// <summary>R10: both required; the code by I9, the PIN by R4.</summary>
    public static Result<(string OperatorCode, string Pin)> Start(StartPosSessionInput input)
    {
        var errors = new ValidationErrors();
        var code = errors.Code(input.OperatorCode, "operatorCode");
        Pin(errors, "pin", input.Pin);
        return errors.Any ? errors.ToError() : (code, input.Pin!);
    }

    /// <summary>R13: <c>status</c> is <c>active</c>, <c>ended</c> or absent.</summary>
    public static Result<(PosSessionStatus? Status, int Limit, int Offset)> SessionList(ListPosSessionsInput input)
    {
        var errors = new ValidationErrors();
        PosSessionStatus? status = input.Status switch
        {
            null or "" => null,
            PosSessionStatusNames.Active => PosSessionStatus.Active,
            PosSessionStatusNames.Ended => PosSessionStatus.Ended,
            _ => Invalid(errors, "status", $"status must be \"{PosSessionStatusNames.Active}\" or \"{PosSessionStatusNames.Ended}\"."),
        };
        var limit = ListRules.Limit(errors, input.Limit);
        var offset = ListRules.Offset(errors, input.Offset);
        return errors.Any ? errors.ToError() : (status, limit, offset);
    }

    /// <summary>R20 step 1: the approver's code and PIN come together or not at all.</summary>
    public static Result<AuthorizeValues> Authorize(AuthorizeInput input)
    {
        var errors = new ValidationErrors();
        var sessionId = Guid.Empty;
        if (string.IsNullOrEmpty(input.SessionId))
            errors.Add("sessionId", "sessionId is required: the id of an operator session.");
        else if (!Guid.TryParse(input.SessionId, out sessionId))
            errors.Add("sessionId", "sessionId must be the id (UUID) of an operator session.");

        if (!PosRightNames.TryParse(input.Right, out var right))
            errors.Add("right", $"right must be one of: {string.Join(", ", PosRightNames.All)}.");

        string? approverCode = null;
        if (input.ApproverCode is not null)
            approverCode = errors.Code(input.ApproverCode, "approverCode");
        if (input.ApproverPin is not null)
            Pin(errors, "approverPin", input.ApproverPin);
        if (input.ApproverCode is not null && input.ApproverPin is null)
            errors.Add("approverPin", "approverPin is required with approverCode: the approver's PIN.");
        if (input.ApproverPin is not null && input.ApproverCode is null)
            errors.Add("approverCode", "approverCode is required with approverPin: the approver's operator code.");

        return errors.Any
            ? errors.ToError()
            : new AuthorizeValues(sessionId, right, approverCode, input.ApproverCode is null ? null : input.ApproverPin);
    }

    public static Guid? IdFilter(ValidationErrors errors, string field, string? input)
    {
        if (string.IsNullOrEmpty(input))
            return null;
        if (Guid.TryParse(input, out var id))
            return id;
        errors.Add(field, $"{field} must be a UUID.");
        return null;
    }

    /// <summary>R4: never echoes the value.</summary>
    private static void Pin(ValidationErrors errors, string field, string? pin)
    {
        if (pin is null)
            errors.Add(field, $"{field} is required: it {PinForm}");
        else if (!PosRules.IsValidPin(pin))
            errors.Add(field, $"{field} {PinForm}");
    }

    private static PosSessionStatus? Invalid(ValidationErrors errors, string field, string message)
    {
        errors.Add(field, message);
        return null;
    }
}
