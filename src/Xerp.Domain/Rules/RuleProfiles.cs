namespace Xerp.Domain.Rules;

/// <summary>One rule and the value a profile gives it.</summary>
public sealed record RuleSetting(string Key, bool Value);

/// <summary>
/// A ready-made combination of rule values an agent can propose to its user (spec A01, 2.4). Data, not behaviour:
/// applying a profile is <c>rule_set_many</c> with <see cref="Changes"/>, after the user has seen the difference.
/// </summary>
public sealed record RuleProfile(string Key, string Name, string Description, IReadOnlyList<RuleSetting> Changes);

/// <summary>
/// The profiles of the system, in code and reviewed like rules. A unit test checks that every key is a rule of
/// <see cref="RuleRegistry"/> and that no key repeats. <c>defaults</c> lists every rule with its shipped value.
/// </summary>
public static class RuleProfiles
{
    public const string Defaults = "defaults";

    public static IReadOnlyList<RuleProfile> All { get; } =
    [
        new(Defaults, "Shipped defaults",
            "Every rule at the value Xerp ships with. Apply it to undo a policy and return to the standard behaviour.",
            [.. RuleRegistry.All.Select(r => new RuleSetting(r.Key, r.Default))]),

        new("strict", "Strict wholesale / accounting-heavy",
            "For a wholesaler or a company with an accountant who wants clean books: a partner and a reference on every document, "
            + "no back-dating or future-dating, no negative stock, no stock reserved for others taken, no over-receipt or over-delivery, "
            + "unique tax ids and names, a customer on every invoice and no overpayment.",
            [
                new(RuleRegistry.StockReceiptPartnerRequired.Key, true),
                new(RuleRegistry.StockIssuePartnerRequired.Key, true),
                new(RuleRegistry.DocumentReferenceRequired.Key, true),
                new(RuleRegistry.DocumentPastDateAllowed.Key, false),
                new(RuleRegistry.DocumentFutureDateAllowed.Key, false),
                new(RuleRegistry.StockReversalBeforeOriginalAllowed.Key, false),
                new(RuleRegistry.DocumentRepeatedArticleAllowed.Key, false),
                new(RuleRegistry.DocumentWarehouseRequired.Key, true),
                new(RuleRegistry.NegativeStockAllowed.Key, false),
                new(RuleRegistry.ReservedStockProtected.Key, true),
                new(RuleRegistry.OverReceiptAllowed.Key, false),
                new(RuleRegistry.OverDeliveryAllowed.Key, false),
                new(RuleRegistry.PurchasePartnerRequired.Key, true),
                new(RuleRegistry.SalesPartnerRequired.Key, true),
                new(RuleRegistry.PartnerTaxIdUnique.Key, true),
                new(RuleRegistry.ReferencesInactiveAssignable.Key, false),
                new(RuleRegistry.PostingActiveMastersRequired.Key, true),
                new(RuleRegistry.StockReversalActiveMastersRequired.Key, true),
                new(RuleRegistry.InvoiceCustomerRequired.Key, true),
                new(RuleRegistry.InvoiceBackdatingAllowed.Key, false),
                new(RuleRegistry.InvoiceOverpaymentAllowed.Key, false),
                new(RuleRegistry.UsersShortPasswordsAllowed.Key, false),
            ]),

        new("retail", "Retail and counter sales",
            "For a shop that sells over the counter: an invoice may be issued without a named customer, issuing an invoice moves the stock, "
            + "the same article may repeat on a document, no reference or partner is needed on stock documents, and a sale is never held up by a missing date check.",
            [
                new(RuleRegistry.InvoiceCustomerRequired.Key, false),
                new(RuleRegistry.InvoiceIssueMovesStock.Key, true),
                new(RuleRegistry.DocumentRepeatedArticleAllowed.Key, true),
                new(RuleRegistry.DocumentReferenceRequired.Key, false),
                new(RuleRegistry.StockReceiptPartnerRequired.Key, false),
                new(RuleRegistry.StockIssuePartnerRequired.Key, false),
                new(RuleRegistry.SalesPartnerRequired.Key, false),
                new(RuleRegistry.DocumentPastDateAllowed.Key, true),
                new(RuleRegistry.NegativeStockAllowed.Key, false),
            ]),

        new("production", "Manufacturer",
            "For a company that makes what it sells: a work order must follow its bill of materials and its plan - the components, the consumed "
            + "quantities and the produced quantity may not differ from it - and goods are not issued below zero stock.",
            [
                new(RuleRegistry.ProductionComponentsMayDifferFromBom.Key, false),
                new(RuleRegistry.ProductionConsumptionMayDifferFromPlan.Key, false),
                new(RuleRegistry.ProductionProducedQuantityMayDifferFromPlan.Key, false),
                new(RuleRegistry.NegativeStockAllowed.Key, false),
                new(RuleRegistry.ReservedStockProtected.Key, true),
            ]),
    ];

    public static RuleProfile? Find(string? key) => All.FirstOrDefault(p => string.Equals(p.Key, key, StringComparison.Ordinal));
}

/// <summary>One sentence for each group of rules (the first part of a key), in plain language (spec A01, 2.4).</summary>
public static class RuleGroups
{
    private static readonly Dictionary<string, string> Descriptions = new(StringComparer.Ordinal)
    {
        ["stock"] = "How stock may move: negative stock, partners on receipts and issues, reversals and stock counts.",
        ["document"] = "What every stock document and order must have: dates, a reference, a warehouse, whether an article may repeat.",
        ["purchase"] = "How buying works: whether a supplier is required and whether more than ordered may be received.",
        ["sales"] = "How selling works: whether a customer is required, over-delivery and protection of reserved stock.",
        ["invoice"] = "How invoices are issued and paid: customer required, back-dating, stock movement on issue and overpayment.",
        ["partner"] = "What a customer or supplier record must have: role, tax id, address fields, unique names and codes.",
        ["article"] = "What an article must have: category, tax rate, sales price, unique barcodes, and what may be changed later.",
        ["articleCategory"] = "Rules for article categories: whether the code may change and whether names are unique.",
        ["articleUnit"] = "Rules for the units of measure of an article.",
        ["uom"] = "Rules for units of measure: whether the code may change and whether names are unique.",
        ["warehouse"] = "What a warehouse record must have: address fields, unique names and codes.",
        ["taxRate"] = "Rules for tax rates: whether the code may change and whether names are unique.",
        ["paymentMethod"] = "Rules for payment methods: whether the code may change and whether names are unique.",
        ["production"] = "How far a work order may depart from its bill of materials and its plan.",
        ["posting"] = "Whether inactive masters (articles, partners, warehouses) may still be posted on.",
        ["references"] = "Whether an inactive record may be newly named on a document, order or article.",
        ["pos"] = "How tills and operators work: several operators or devices at once, approval by a second operator.",
        ["posOperator"] = "What a POS operator record must have, and unique names and codes.",
        ["posRole"] = "Rules for POS roles: whether the code may change and whether names are unique.",
        ["users"] = "How people sign in: lock after failed logins and the minimum password length.",
    };

    public static string Describe(string group) =>
        Descriptions.TryGetValue(group, out var text) ? text : $"Business rules of the area '{group}'.";
}
