using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Xerp.IntegrationTests.Support;

/// <summary>
/// The conventions of spec 014, section 10: the 13 rules of documents, "Today / Past / Future" computed from the
/// UTC clock (never yesterday or tomorrow, R13), "Deactivate X" and shortcuts for refusals of this spec.
/// </summary>
public static class DocRules
{
    public const string ReceiptPartner = "stock.receiptPartnerRequired";
    public const string IssuePartner = "stock.issuePartnerRequired";
    public const string PartnerRole = "stock.partnerRoleRequired";
    public const string PastDate = "document.pastDateAllowed";
    public const string FutureDate = "document.futureDateAllowed";
    public const string ReversalBefore = "stock.reversalBeforeOriginalAllowed";
    public const string ReferenceRequired = "document.referenceRequired";
    public const string RepeatedArticle = "document.repeatedArticleAllowed";
    public const string WarehouseRequired = "document.warehouseRequired";
    public const string InactiveAssignable = "references.inactiveAssignable";
    public const string ActiveMasters = "posting.activeMastersRequired";
    public const string ReversalMasters = "stock.reversalActiveMastersRequired";
    public const string OutdatedCount = "stock.outdatedCountAllowed";

    /// <summary>The 13 keys of spec 014 with their defaults (section 6.1), in ordinal order.</summary>
    public static readonly (string Key, bool Default)[] Inventory =
    [
        (FutureDate, true), (PastDate, true), (ReferenceRequired, false), (RepeatedArticle, true), (WarehouseRequired, false),
        (ActiveMasters, true), (InactiveAssignable, false), (IssuePartner, false), (OutdatedCount, false), (PartnerRole, true),
        (ReceiptPartner, false), (ReversalMasters, false), (ReversalBefore, false),
    ];

    public static readonly string[] Keys = Inventory.Select(i => i.Key).ToArray();

    public static IEnumerable<object[]> KeyData() => Keys.Select(k => new object[] { k });

    public static bool IsDefaultTrue(string key) => Inventory.Any(i => i.Key == key && i.Default);

    // ---- dates (R13): the date at UTC now, +-3 days ----

    public static string Today => Format(DateTime.UtcNow.Date);
    public static string Past => Format(DateTime.UtcNow.Date.AddDays(-3));
    public static string Future => Format(DateTime.UtcNow.Date.AddDays(3));

    private static string Format(DateTime d) => d.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    public static JsonObject Dated(this JsonObject body, string date) => body.With("documentDate", date);

    public static JsonObject OrderDated(this JsonObject body, string date) => body.With("orderDate", date);

    // ---- bodies ----

    /// <summary>A replace body of a one-line receipt or issue for W1, dated as given, with an explicit partner (null allowed).</summary>
    public static JsonObject Replace(Guid warehouse, Guid article, string date, JsonNode? partner = null, decimal quantity = 1) =>
        Stock.Replacement(warehouse, (article, quantity)).With("documentDate", date).With("partnerId", partner);

    // ---- "Deactivate X" ----

    public static Task DeactivateWarehouse(HttpClient c, Guid id) => Stock.SetWarehouseActiveAsync(c, id, false);
    public static Task ActivateWarehouse(HttpClient c, Guid id) => Stock.SetWarehouseActiveAsync(c, id, true);
    public static Task DeactivateArticle(HttpClient c, Guid id) => Stock.SetArticleActiveAsync(c, id, false);
    public static Task ActivateArticle(HttpClient c, Guid id) => Stock.SetArticleActiveAsync(c, id, true);
    public static Task DeactivatePartner(HttpClient c, Guid id) => Orders.SetPartnerActiveAsync(c, id, false);
    public static Task ActivatePartner(HttpClient c, Guid id) => Orders.SetPartnerActiveAsync(c, id, true);

    // ---- refusals ----

    /// <summary>Posts a draft and asserts <c>409 DATE_NOT_ALLOWED</c> refused by the rule (value false) with the one key.</summary>
    public static async Task<JsonElement> DateRefusedAsync(HttpClient http, Guid id, string rule, string key = "documentDate")
    {
        using var response = await Stock.SendPostAsync(http, id);
        return await Rules.ConflictAsync(response, "DATE_NOT_ALLOWED", rule, false, key);
    }

    /// <summary><c>400 VALIDATION_FAILED</c> refused by (rule, value) with exactly these error keys.</summary>
    public static Task<JsonElement> ValidationRefusedAsync(HttpResponseMessage response, string rule, bool value, params string[] keys) =>
        Rules.RefusedAsync(response, HttpStatusCode.BadRequest, "VALIDATION_FAILED", rule, value, keys);

    /// <summary>A refusal that no rule caused: status, code, exact keys, no <c>rules</c>.</summary>
    public static async Task<JsonElement> PlainAsync(HttpResponseMessage response, HttpStatusCode status, string code, params string[] keys)
    {
        var problem = await HttpAssert.ProblemAsync(response, status, code);
        if (keys.Length > 0)
            Assert.Equal(keys.Order(StringComparer.Ordinal), McpAssert.ErrorKeys(problem));
        return Rules.AssertNamesNoRule(problem);
    }

    /// <summary>The unlinked draft receipt of one line of A in W1 (partner and reference omitted), dated as given.</summary>
    public static Task<JsonElement> DraftReceiptAsync(OrderSetup s, string? date = null) =>
        Stock.CreateAsync(s.Http, Stock.Draft("receipt", s.W1, (s.A, 1)).Dated(date ?? Today));

    /// <summary>Sets a rule to the value that is not its default.</summary>
    public static Task<JsonElement> FlipAsync(HttpClient http, string key) => Rules.SetAsync(http, key, !IsDefaultTrue(key));
}
