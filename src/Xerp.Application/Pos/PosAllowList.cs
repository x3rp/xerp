using System.Text.RegularExpressions;

namespace Xerp.Application.Pos;

/// <summary>One operation as both transports name it: HTTP method and route under <c>/api/v1</c>, and its MCP tool.</summary>
/// <param name="Route">The route template, its parameters without constraints: <c>/articles/{id}</c>.</param>
/// <param name="Tool">The tool of the operation; null when another route of the table already maps to it.</param>
public sealed record PosOperation(string Method, string Route, string? Tool);

/// <summary>
/// What a key with scope <c>pos</c> may do (spec P02, R31-R33): one table, read by the HTTP middleware and by
/// the MCP tool filter alike. Everything under <c>/pos/</c> except managing roles and operators, and the reads
/// of the master data a till needs. A new route under <c>/pos/</c> is allowed unless it is added to
/// <see cref="PosExceptions"/>; a new route elsewhere is not, unless it is added to <see cref="Reads"/>.
/// </summary>
public static partial class PosAllowList
{
    public const string PosRoutePrefix = "/pos/";
    public const string PosToolPrefix = "pos_";

    /// <summary>The operations under <c>/pos/</c> a <c>pos</c> key may not do: it may read roles and operators, not manage them.</summary>
    public static IReadOnlyList<PosOperation> PosExceptions { get; } =
    [
        new("POST", "/pos/roles", "pos_role_create"),
        new("PUT", "/pos/roles/{id}", "pos_role_update"),
        new("DELETE", "/pos/roles/{id}", "pos_role_delete"),
        new("POST", "/pos/operators", "pos_operator_create"),
        new("PUT", "/pos/operators/{id}", "pos_operator_update"),
        new("DELETE", "/pos/operators/{id}", "pos_operator_delete"),
        new("PUT", "/pos/operators/{id}/pin", "pos_operator_set_pin"),
    ];

    /// <summary>The operations outside <c>/pos/</c> a <c>pos</c> key may do: reads only (R32).</summary>
    public static IReadOnlyList<PosOperation> Reads { get; } =
    [
        new("GET", "/whoami", "whoami"),
        new("GET", "/articles", "article_list"),
        new("GET", "/articles/{id}", "article_get"),
        new("GET", "/articles/by-code/{code}", null),
        new("GET", "/articles/{articleId}/sales-data", "article_sales_data_get"),
        new("GET", "/articles/sales-data", "article_sales_data_list"),
        new("GET", "/article-barcodes", "article_barcode_list"),
        new("GET", "/article-barcodes/{id}", "article_barcode_get"),
        .. Master("/article-categories", "article_category"),
        .. Master("/tax-rates", "tax_rate"),
        .. Master("/payment-methods", "payment_method"),
        .. Master("/units-of-measure", "uom"),
        .. Master("/warehouses", "warehouse"),
        .. Master("/partners", "partner"),
        new("GET", "/rules", "rule_list"),
        new("GET", "/rules/{key}", "rule_get"),
    ];

    /// <param name="method">The HTTP method, any case.</param>
    /// <param name="route">The route template under <c>/api/v1</c>, with or without parameter constraints.</param>
    public static bool AllowsRoute(string method, string route)
    {
        var normalized = Normalize(route);
        bool Matches(PosOperation o) =>
            string.Equals(o.Method, method, StringComparison.OrdinalIgnoreCase) && Normalize(o.Route) == normalized;
        return normalized.StartsWith(PosRoutePrefix, StringComparison.Ordinal)
            ? !PosExceptions.Any(Matches)
            : Reads.Any(Matches);
    }

    public static bool AllowsTool(string name) =>
        name.StartsWith(PosToolPrefix, StringComparison.Ordinal)
            ? !PosExceptions.Any(o => o.Tool == name)
            : Reads.Any(o => o.Tool == name);

    private static PosOperation[] Master(string route, string tool) =>
    [
        new("GET", route, $"{tool}_list"),
        new("GET", route + "/{id}", $"{tool}_get"),
        new("GET", route + "/by-code/{code}", null),
    ];

    /// <summary><c>{id:guid}</c> and <c>{articleId}</c> both become <c>{}</c>: a parameter's name and constraint do not matter.</summary>
    private static string Normalize(string route) => Parameter().Replace(route, "{}");

    [GeneratedRegex(@"\{[^}]*\}")]
    private static partial Regex Parameter();
}
