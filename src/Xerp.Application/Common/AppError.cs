using Xerp.Domain.Rules;

namespace Xerp.Application.Common;

/// <summary>Stable, machine-readable error codes (docs/architecture.md section 6). Part of the contract.</summary>
public static class ErrorCodes
{
    public const string ValidationFailed = "VALIDATION_FAILED";
    public const string Unauthenticated = "UNAUTHENTICATED";
    public const string Forbidden = "FORBIDDEN";
    public const string NotFound = "NOT_FOUND";
    public const string CodeTaken = "CODE_TAKEN";
    public const string InUse = "IN_USE";
    public const string ReferenceNotFound = "REFERENCE_NOT_FOUND";
    public const string ReferenceInactive = "REFERENCE_INACTIVE";
    public const string CannotRevokeSelf = "CANNOT_REVOKE_SELF";
    public const string InvalidState = "INVALID_STATE";
    public const string InsufficientStock = "INSUFFICIENT_STOCK";
    public const string ArticleNotStocked = "ARTICLE_NOT_STOCKED";
    public const string UnitIsBaseUnit = "UNIT_IS_BASE_UNIT";
    public const string UnitNotOnArticle = "UNIT_NOT_ON_ARTICLE";
    public const string QuantityNotConvertible = "QUANTITY_NOT_CONVERTIBLE";
    public const string CountOutdated = "COUNT_OUTDATED";
    public const string PartnerRoleMissing = "PARTNER_ROLE_MISSING";
    public const string OrderNotOpen = "ORDER_NOT_OPEN";
    public const string OrderMismatch = "ORDER_MISMATCH";
    public const string QuantityExceedsOrder = "QUANTITY_EXCEEDS_ORDER";
    public const string DefaultWarehouse = "DEFAULT_WAREHOUSE";
    public const string StockReserved = "STOCK_RESERVED";
    public const string CodeChangeNotAllowed = "CODE_CHANGE_NOT_ALLOWED";
    public const string NameTaken = "NAME_TAKEN";
    public const string TaxIdTaken = "TAX_ID_TAKEN";
    public const string BarcodeTaken = "BARCODE_TAKEN";
    public const string OperatorSignInFailed = "OPERATOR_SIGN_IN_FAILED";
    public const string SessionNotActive = "SESSION_NOT_ACTIVE";
    public const string OperatorRightMissing = "OPERATOR_RIGHT_MISSING";
    public const string PayloadTooLarge = "PAYLOAD_TOO_LARGE";
    public const string InternalError = "INTERNAL_ERROR";
}

/// <summary>
/// A configurable rule that refused an operation (ADR-0020, decision 9; spec 012, R38): its key, the value it
/// had for the tenant, and the keys of <c>errors</c> it produced, in their order there.
/// </summary>
public sealed record RuleRefusal(string Key, bool Value, IReadOnlyList<string> Fields);

/// <summary>
/// An expected failure of an Application operation. Clients (HTTP, MCP, CLI) map it to their own
/// transport; they branch on <see cref="Code"/>, never on <see cref="Detail"/>.
/// </summary>
/// <param name="Rules">
/// The configurable rules that refused, one element per rule; null when none did - the refusal is then an
/// invariant's, and no switch lifts it.
/// </param>
public sealed record AppError(
    string Code, string Detail, IReadOnlyDictionary<string, string[]>? Errors = null, IReadOnlyList<RuleRefusal>? Rules = null)
{
    /// <summary>
    /// The same error, naming a rule that refused and the value it had. Its fields are
    /// <paramref name="fields"/>, or every key of <see cref="Errors"/> when the rule alone refused.
    /// </summary>
    public AppError RefusedBy(RuleDefinition rule, bool value, IReadOnlyCollection<string>? fields = null) =>
        this with { Rules = [.. Rules ?? [], new RuleRefusal(rule.Key, value, [.. fields ?? Errors?.Keys ?? []])] };

    public static AppError Unauthenticated(string detail = "A valid API key is required.") =>
        new(ErrorCodes.Unauthenticated, detail);

    public static AppError Forbidden(string detail = "This credential is not allowed to perform the operation.") =>
        new(ErrorCodes.Forbidden, detail);

    /// <summary>The request was refused unread because its body is larger than the transport accepts.</summary>
    public static AppError PayloadTooLarge(long maxBytes) =>
        new(ErrorCodes.PayloadTooLarge, $"The request body is larger than the limit of {maxBytes} bytes.");

    public static AppError Internal() => new(ErrorCodes.InternalError, "An unexpected error occurred.");

    public static AppError NotFound(string detail) => new(ErrorCodes.NotFound, detail);

    public static AppError CodeTaken(string detail) => new(ErrorCodes.CodeTaken, detail);

    /// <summary>Spec 013, R9: a replace would change the code and the master's <c>codeChangeable</c> rule is false.</summary>
    /// <param name="what">The record, with its article: <c>a partner</c>.</param>
    public static AppError CodeChangeNotAllowed(string what, string storedCode, RuleDefinition rule) =>
        new AppError(ErrorCodes.CodeChangeNotAllowed,
            $"The code of {what} cannot be changed: this company's rule {rule.Key} is false. Send the stored code '{storedCode}'.",
            new Dictionary<string, string[]> { ["code"] = [$"The code must stay '{storedCode}'."] })
            .RefusedBy(rule, false);

    /// <summary>
    /// Spec 013, R12: another record of the same resource has this name and the master's <c>nameUnique</c> rule
    /// is true. Only the value the caller sent is repeated, never the record that has it (S2).
    /// </summary>
    /// <param name="what">The other record, with its article: <c>partner</c> gives "Another partner".</param>
    public static AppError NameTaken(string what, string name, RuleDefinition rule) =>
        new AppError(ErrorCodes.NameTaken,
            $"Another {what} already has the name '{name}': this company's rule {rule.Key} is true. Choose another name.",
            new Dictionary<string, string[]> { ["name"] = [$"The name '{name}' is already used (names are compared case-insensitively)."] })
            .RefusedBy(rule, true);

    /// <summary>Spec 013, R15: as <see cref="NameTaken"/>, for the tax id of a partner.</summary>
    public static AppError TaxIdTaken(string taxId, RuleDefinition rule) =>
        new AppError(ErrorCodes.TaxIdTaken,
            $"Another partner already has the tax id '{taxId}': this company's rule {rule.Key} is true. Find that partner with partner_list instead of creating a duplicate.",
            new Dictionary<string, string[]> { ["taxId"] = [$"The tax id '{taxId}' is already used (tax ids are compared case-insensitively)."] })
            .RefusedBy(rule, true);

    /// <summary>
    /// Spec P01, R16, R21: the article already has the barcode (an invariant: <paramref name="rule"/> is null and
    /// no rule is named), or another article has it and <c>article.barcodeUnique</c> is true. Only the barcode
    /// the caller sent is repeated, never the record that has it.
    /// </summary>
    public static AppError BarcodeTaken(string barcode, RuleDefinition? rule)
    {
        var error = new AppError(ErrorCodes.BarcodeTaken,
            rule is null
                ? $"The article already has the barcode '{barcode}'."
                : $"Another article already has the barcode '{barcode}': this company's rule {rule.Key} is true. Find it with article_barcode_list.",
            new Dictionary<string, string[]> { ["barcode"] = [$"The barcode '{barcode}' is already used (barcodes are compared exactly)."] });
        return rule is null ? error : error.RefusedBy(rule, true);
    }

    public static AppError InUse(string detail) => new(ErrorCodes.InUse, detail);

    /// <summary>
    /// Spec P02, R10: one answer for an unknown code, a wrong PIN, an inactive and a locked operator, so that the
    /// answer tells a guesser nothing. Never repeats the code or the PIN.
    /// </summary>
    /// <param name="field"><c>pin</c> at sign-in, <c>approverPin</c> in authorize.</param>
    public static AppError OperatorSignInFailed(string field) =>
        new(ErrorCodes.OperatorSignInFailed, "The operator code and PIN were not accepted.",
            new Dictionary<string, string[]> { [field] = ["The code and PIN do not sign in an operator; check both and try again."] });

    /// <summary>Spec P02, R20 step 3: the session is ended or expired.</summary>
    public static AppError SessionNotActive() =>
        new(ErrorCodes.SessionNotActive, "The operator session is ended or expired. The operator must sign in again.",
            new Dictionary<string, string[]> { ["sessionId"] = ["The session is not active."] });

    /// <summary>Spec P02, R20: neither the session's operator nor the approver may do the act.</summary>
    /// <param name="field"><c>right</c> without an approver, <c>approverCode</c> with one.</param>
    public static AppError OperatorRightMissing(string detail, string field, string message) =>
        new(ErrorCodes.OperatorRightMissing, detail, new Dictionary<string, string[]> { [field] = [message] });

    /// <summary>The record is used, so the named fields cannot change (ADR-0012: <c>IN_USE</c> for frozen fields).</summary>
    public static AppError InUse(string detail, IReadOnlyDictionary<string, string[]> errors) => new(ErrorCodes.InUse, detail, errors);

    /// <summary>The operation is not allowed in the document's current status (ADR-0007).</summary>
    public static AppError InvalidState(string detail) => new(ErrorCodes.InvalidState, detail);

    /// <summary>ADR-0008: a well-formed id in request field <paramref name="field"/> names no record of this tenant.</summary>
    public static AppError ReferenceNotFound(string field, Guid id) =>
        new(ErrorCodes.ReferenceNotFound, $"The record referenced by {field} does not exist.",
            new Dictionary<string, string[]> { [field] = [$"No record with id '{id}' exists."] });

    /// <summary>ADR-0008: the record named by request field <paramref name="field"/> is inactive and would be newly assigned.</summary>
    public static AppError ReferenceInactive(string field, Guid id) =>
        new(ErrorCodes.ReferenceInactive, $"The record referenced by {field} is inactive and cannot be newly assigned.",
            new Dictionary<string, string[]> { [field] = [$"The record with id '{id}' is inactive."] });

    public static AppError Validation(IReadOnlyDictionary<string, string[]> errors) =>
        new(ErrorCodes.ValidationFailed, "One or more fields are invalid.", errors);

    public static AppError Validation(string field, string message) =>
        Validation(new Dictionary<string, string[]> { [field] = [message] });
}
