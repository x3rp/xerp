using System.Globalization;
using Xerp.Application.Common;
using Xerp.Application.Ports;
using Xerp.Domain.Inventory;
using Xerp.Domain.Rules;

namespace Xerp.Application.Stock;

/// <summary>
/// The rules of spec 014 that judge saving a stock document or an order, read once per create or replace
/// (R4). A check never fetches a rule: it receives these booleans.
/// </summary>
public sealed record DocumentSaveRules(
    bool ReceiptPartnerRequired, bool IssuePartnerRequired, bool PartnerRoleRequired, bool ReferenceRequired,
    bool RepeatedArticleAllowed, bool WarehouseRequired, bool InactiveAssignable)
{
    /// <summary>A tenant that has set nothing: specs 001-013 exactly.</summary>
    public static readonly DocumentSaveRules Defaults = From(TenantRules.Defaults);

    public static DocumentSaveRules From(TenantRules rules) => new(
        rules[RuleRegistry.StockReceiptPartnerRequired], rules[RuleRegistry.StockIssuePartnerRequired],
        rules[RuleRegistry.StockPartnerRoleRequired], rules[RuleRegistry.DocumentReferenceRequired],
        rules[RuleRegistry.DocumentRepeatedArticleAllowed], rules[RuleRegistry.DocumentWarehouseRequired],
        rules[RuleRegistry.ReferencesInactiveAssignable]);

    /// <summary>R6: the rule that requires a partner on an unlinked document of <paramref name="type"/>; null for a type without a partner.</summary>
    public (RuleDefinition Rule, bool Value)? PartnerRequired(StockDocumentType type) => type switch
    {
        StockDocumentType.Receipt => (RuleRegistry.StockReceiptPartnerRequired, ReceiptPartnerRequired),
        StockDocumentType.Issue => (RuleRegistry.StockIssuePartnerRequired, IssuePartnerRequired),
        _ => null,
    };
}

/// <summary>The checks of spec 014 that judge the moment a date takes effect and the rules that name themselves in a refusal.</summary>
public static class DocumentRuleChecks
{
    public const string DocumentDateField = "documentDate";
    private const string DateFormat = "yyyy-MM-dd";

    /// <summary>
    /// R14: posting a document or confirming an order. Null when the date may take effect now, otherwise
    /// <c>409 DATE_NOT_ALLOWED</c> with key <paramref name="field"/>, naming the rule.
    /// </summary>
    public static AppError? Date(DateOnly date, DateTime nowUtc, TenantRules rules, string field, string what)
    {
        var (past, future) = (rules[RuleRegistry.DocumentPastDateAllowed], rules[RuleRegistry.DocumentFutureDateAllowed]);
        return DocumentDateRules.Judge(date, nowUtc, past, future) switch
        {
            DateJudgement.BeforeToday => new AppError(ErrorCodes.DateNotAllowed,
                    $"The {what} is dated {Text(date)}, before today, and this company's rule document.pastDateAllowed is false. Nothing was changed; change {field} to today's date and try again.",
                    new Dictionary<string, string[]> { [field] = [$"{field} must not be before today."] })
                .RefusedBy(RuleRegistry.DocumentPastDateAllowed, false),
            DateJudgement.AfterToday => new AppError(ErrorCodes.DateNotAllowed,
                    $"The {what} is dated {Text(date)}, after today, and this company's rule document.futureDateAllowed is false. Nothing was changed; change {field} to today's date and try again.",
                    new Dictionary<string, string[]> { [field] = [$"{field} must not be after today."] })
                .RefusedBy(RuleRegistry.DocumentFutureDateAllowed, false),
            _ => null,
        };
    }

    /// <summary>
    /// R15, R17: the date stage of a reversal. It answers once - <c>400 VALIDATION_FAILED</c> with key
    /// <c>documentDate</c> - with one <c>rules</c> element for every date rule that refused.
    /// </summary>
    /// <param name="clientCall">True for a reversal a client asks for. False for a reversal the system makes for a work order or an invoice: only its order to the original is judged.</param>
    public static AppError? ReversalDate(DateOnly reversalDate, DateOnly originalDate, DateTime nowUtc, TenantRules rules, bool clientCall = true)
    {
        var errors = new ValidationErrors();
        var beforeOriginalAllowed = rules[RuleRegistry.StockReversalBeforeOriginalAllowed];
        if (!beforeOriginalAllowed && reversalDate < originalDate)
            errors.Add(DocumentDateField,
                $"documentDate of a reversal must not be earlier than the date of the document it reverses, {Text(originalDate)}.",
                RuleRegistry.StockReversalBeforeOriginalAllowed, false);
        if (clientCall)
        {
            var (past, future) = (rules[RuleRegistry.DocumentPastDateAllowed], rules[RuleRegistry.DocumentFutureDateAllowed]);
            switch (DocumentDateRules.Judge(reversalDate, nowUtc, past, future))
            {
                case DateJudgement.BeforeToday:
                    errors.Add(DocumentDateField, "documentDate of a reversal must not be before today.", RuleRegistry.DocumentPastDateAllowed, false);
                    break;
                case DateJudgement.AfterToday:
                    errors.Add(DocumentDateField, "documentDate of a reversal must not be after today.", RuleRegistry.DocumentFutureDateAllowed, false);
                    break;
            }
        }
        return errors.Any ? errors.ToError() : null;
    }

    private static string Text(DateOnly date) => date.ToString(DateFormat, CultureInfo.InvariantCulture);
}
