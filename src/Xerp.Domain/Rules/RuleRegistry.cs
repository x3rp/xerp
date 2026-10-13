namespace Xerp.Domain.Rules;

/// <summary>
/// A configurable business rule (ADR-0020, decision 2): a yes/no switch every tenant has a value for. The
/// <see cref="Name"/> is a statement in plain language and <c>true</c> always means that the statement holds;
/// <see cref="Default"/> is the value of a tenant that has set nothing. A check never fetches a rule: it
/// receives the boolean as an argument.
/// </summary>
/// <param name="Key">Stable identifier, <c>area.statement</c>. Part of the contract like an error code: never renamed, never reused.</param>
/// <param name="Description">Written for an agent: which operations the rule affects, what <c>true</c> does and what <c>false</c>.</param>
public sealed record RuleDefinition(string Key, string Name, string Description, bool Default)
{
    public const int KeyMaxLength = 100;

    /// <summary>The first segment of the key, for listing: <c>stock</c>, <c>purchase</c>, <c>sales</c>.</summary>
    public string Group => Key[..Key.IndexOf('.')];
}

/// <summary>
/// The registry (ADR-0020, decision 2; spec 012, R1): every rule of the system, defined once, in code, the
/// same for all tenants. <c>docs/rules.md</c> is its catalogue and <c>GET /rules</c> its live form; a test
/// pins the keys with their defaults.
/// </summary>
public static class RuleRegistry
{
    public static readonly RuleDefinition NegativeStockAllowed = new(
        "stock.negativeStockAllowed",
        "Stock may go below zero",
        "Judges the posting and the reversal of stock documents (stock_document_post, stock_document_reverse). "
        + "When false, a posting or a reversal that would lower the stock of an article in a warehouse to below zero is refused with INSUFFICIENT_STOCK. "
        + "When true, there is no such check: an issue, a transfer, a delivery or a reversal is posted although the goods are not on hand, and stock on hand and the available quantity may be negative. "
        + "A stock count is never judged. After a change back to false, negative stock stays as it is; movements that raise it are accepted, movements that lower it are refused.",
        Default: false);

    public static readonly RuleDefinition OverReceiptAllowed = new(
        "purchase.overReceiptAllowed",
        "More than ordered may be received on a purchase order",
        "Judges the posting of a receipt linked to a purchase order (stock_document_post). "
        + "When false, a receipt that would take an order line above its ordered quantity, counted in base units, is refused with QUANTITY_EXCEEDS_ORDER. "
        + "When true, any quantity may be received against a confirmed order, without limit; the line's outstanding quantity is then 0 and the order is fully received. "
        + "What was received is never changed by this rule; a reversal is not judged by it.",
        Default: false);

    public static readonly RuleDefinition OverDeliveryAllowed = new(
        "sales.overDeliveryAllowed",
        "More than ordered may be delivered on a sales order",
        "Judges the posting of an issue linked to a sales order - a delivery (stock_document_post). "
        + "When false, a delivery that would take an order line above its ordered quantity, counted in base units, is refused with QUANTITY_EXCEEDS_ORDER. "
        + "When true, any quantity may be delivered against a confirmed order, without limit - but only what is in stock: the delivery is still judged by stock.negativeStockAllowed and sales.reservedStockProtected. "
        + "What was delivered is never changed by this rule; a reversal is not judged by it.",
        Default: false);

    public static readonly RuleDefinition ReservedStockProtected = new(
        "sales.reservedStockProtected",
        "Reserved stock is protected",
        "Judges the posting and the reversal of stock documents (stock_document_post, stock_document_reverse). "
        + "When false, the quantity reserved by confirmed sales orders only informs: any issue or transfer may take it. "
        + "When true, a posting or a reversal that takes goods out of a warehouse is refused with STOCK_RESERVED when afterwards something is still reserved there, stock no longer covers it, and the operation made that worse. "
        + "A delivery against a sales order, up to the outstanding quantity of its order lines, is never refused by it, and neither is a stock count. Confirming a sales order is not affected.",
        Default: false);

    public static readonly RuleDefinition PurchasePartnerRequired = new(
        "purchase.partnerRequired",
        "Partner (supplier) required on a purchase order",
        "Judges creating and replacing a purchase order (purchase_order_create, purchase_order_update). "
        + "When true, a purchase order cannot be saved without a supplier: supplierId omitted or null is refused with VALIDATION_FAILED. "
        + "When false, a purchase order may be saved without a supplier; its supplier is then null, and so is the partner of every receipt linked to it. A supplier that is named is checked as always. "
        + "Orders that already exist are not judged again: an order without a supplier is confirmed, received against, closed and reopened whatever the value.",
        Default: true);

    public static readonly RuleDefinition SalesPartnerRequired = new(
        "sales.partnerRequired",
        "Partner (customer) required on a sales order",
        "Judges creating and replacing a sales order (sales_order_create, sales_order_update). "
        + "When true, a sales order cannot be saved without a customer: customerId omitted or null is refused with VALIDATION_FAILED. "
        + "When false, a sales order may be saved without a customer; its customer is then null, and so is the partner of every delivery linked to it. A customer that is named is checked as always. "
        + "Orders that already exist are not judged again: an order without a customer is confirmed, delivered against, closed and reopened whatever the value.",
        Default: true);

    /// <summary>The master-data rules of units of measure (spec 013, 6.1).</summary>
    public static readonly MasterRuleSet Uom = Master("uom", "unit of measure", "units of measure", "uom");

    /// <summary>The master-data rules of articles (spec 013, 6.1).</summary>
    public static readonly MasterRuleSet Article = Master("article", "article", "articles", "article",
        ("description", "Description"));

    /// <summary>The master-data rules of partners (spec 013, 6.1), without the two only a partner has.</summary>
    public static readonly MasterRuleSet Partner = Master("partner", "partner", "partners", "partner",
        [("taxId", "Tax id"), .. AddressFields]);

    /// <summary>The master-data rules of warehouses (spec 013, 6.1).</summary>
    public static readonly MasterRuleSet Warehouse = Master("warehouse", "warehouse", "warehouses", "warehouse", AddressFields);

    public static readonly RuleDefinition PartnerRoleRequired = new(
        "partner.roleRequired",
        "A partner must be a customer or a supplier",
        "Judges creating and replacing a partner (partner_create, partner_update). "
        + "When true, a partner with isCustomer false and isSupplier false is refused with VALIDATION_FAILED: a partner is a customer, a supplier or both. "
        + "When false, a partner with neither role may be saved; it still cannot be named where a supplier or a customer is needed (PARTNER_ROLE_MISSING). "
        + "Partners that already exist are not judged again: after a change back to true a partner without a role stays and is read as before, and its next replace must give it a role.",
        Default: true);

    public static readonly RuleDefinition PartnerTaxIdUnique = new(
        "partner.taxIdUnique",
        "Tax ids of partners must be unique",
        "Judges creating and replacing a partner (partner_create, partner_update). "
        + "When false, any number of partners may have the same tax id. "
        + "When true, a create or a replace whose taxId another partner already has is refused with TAX_ID_TAKEN; tax ids are compared trimmed and case-insensitively, inactive partners count, and partners without a tax id never collide. "
        + "Duplicates that already exist stay; each is judged at its own next replace, which is refused until one of the two has another tax id.",
        Default: false);

    /// <summary>The four masters, each with its rules.</summary>
    public static IReadOnlyList<MasterRuleSet> Masters { get; } = [Uom, Article, Partner, Warehouse];

    /// <summary>Every rule, ordered by key (ordinal): the order of every listing (spec 012, R2).</summary>
    public static IReadOnlyList<RuleDefinition> All { get; } = ((RuleDefinition[])
        [
            NegativeStockAllowed, OverReceiptAllowed, OverDeliveryAllowed, ReservedStockProtected, PurchasePartnerRequired, SalesPartnerRequired,
            PartnerRoleRequired, PartnerTaxIdUnique, .. Masters.SelectMany(m => m.All),
        ])
        .OrderBy(r => r.Key, StringComparer.Ordinal)
        .ToList();

    /// <summary>The rule with exactly this key (case-sensitive), or null: anything else names no rule (spec 012, R4).</summary>
    public static RuleDefinition? Find(string? key) => All.FirstOrDefault(r => string.Equals(r.Key, key, StringComparison.Ordinal));

    /// <summary>The six address fields a partner and a warehouse share, as requests spell them, with the words of their rules' names.</summary>
    private static (string Field, string Label)[] AddressFields =>
    [
        ("addressLine1", "Address line 1"), ("addressLine2", "Address line 2"), ("postalCode", "Postal code"),
        ("city", "City"), ("region", "Region"), ("countryCode", "Country code"),
    ];

    /// <summary>
    /// The rules of one master (spec 013, 6.1): whether its code may change, whether its names are unique, and
    /// one rule per optional field that a tenant may require. Every default is the behaviour of specs 001-004.
    /// </summary>
    /// <param name="tool">The prefix of the master's tools: <c>partner</c> for <c>partner_create</c> and <c>partner_update</c>.</param>
    private static MasterRuleSet Master(string group, string singular, string plural, string tool, params (string Field, string Label)[] optionalFields)
    {
        var writes = $"Judges creating and replacing {WithArticle(singular)} ({tool}_create, {tool}_update). ";
        var codeChangeable = new RuleDefinition(
            $"{group}.codeChangeable",
            $"The code of {WithArticle(singular)} may be changed",
            $"Judges replacing {WithArticle(singular)} ({tool}_update); a create is never judged. "
            + $"When true, a replace may give the {singular} another code. "
            + "When false, a replace whose code differs from the stored code is refused with CODE_CHANGE_NOT_ALLOWED; a change of letter case only is a change, and a replace that sends the stored code passes. "
            + "Codes that were changed earlier stay as they are.",
            Default: true);
        var nameUnique = new RuleDefinition(
            $"{group}.nameUnique",
            $"Names of {plural} must be unique",
            writes
            + $"When false, any number of {plural} may have the same name. "
            + $"When true, a create or a replace whose name another {singular} already has is refused with NAME_TAKEN; names are compared trimmed and case-insensitively, and inactive {plural} count. "
            + "Duplicates that already exist stay; each is judged at its own next replace, which is refused until one of the two is renamed.",
            Default: false);
        var required = optionalFields.ToDictionary(
            f => f.Field,
            f => new RuleDefinition(
                $"{group}.{f.Field}Required",
                $"{f.Label} required on {WithArticle(singular)}",
                writes
                + $"When false, {f.Field} is optional. "
                + $"When true, {WithArticle(singular)} saved with {f.Field} omitted, null or empty is refused with VALIDATION_FAILED. "
                + $"Records without it that already exist stay valid and usable; the next replace of one must give it.",
                Default: false),
            StringComparer.Ordinal);
        return new MasterRuleSet(codeChangeable, nameUnique, required);
    }

    private static string WithArticle(string noun) => (noun[0] is 'a' or 'e' or 'i' or 'o' ? "an " : "a ") + noun;
}

/// <summary>The rules every master has (spec 013, 6.1), so that the four masters are judged by the same code.</summary>
/// <param name="CodeChangeable">Whether a replace may change the record's code (R9).</param>
/// <param name="NameUnique">Whether no two records of the resource may have the same name (R12).</param>
/// <param name="RequiredFields">By request field, the rule that makes that optional field required (R5): a table, not a branch per field.</param>
public sealed record MasterRuleSet(
    RuleDefinition CodeChangeable, RuleDefinition NameUnique, IReadOnlyDictionary<string, RuleDefinition> RequiredFields)
{
    public IEnumerable<RuleDefinition> All => [CodeChangeable, NameUnique, .. RequiredFields.Values];
}
