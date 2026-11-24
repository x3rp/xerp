using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;

namespace Xerp.IntegrationTests.Support;

/// <summary>
/// Spec RP01 (reports): every route, tool shape and column key the spec leaves open is chosen HERE, once, so the
/// builder can align the implementation (or this file) in one place.
///
/// CHOSEN SHAPES (camelCase):
///   GET /reports                    -> { items: [ { key, name, description, parameters: [ { name, type, required, default, allowedValues } ],
///                                                   columns: [ { key, label, type } ] } ] }      (13 items, ordered as <see cref="Keys"/>)
///   GET /reports/{key}?from=&amp;to=...   -> { report, parameters, generatedAt, columns, rows, totals, rowCount, truncated, notes }
///   GET /reports/{key}?format=csv   -> 200 text/csv; charset=utf-8, UTF-8 BOM, ';' separator, RFC 4180 quoting
///                                      (also Accept: text/csv); first line = column labels (or keys), then one line per row
///                                      (a final totals line is tolerated); decimal point or comma and ISO or dd.MM.yyyy dates are tolerated
///   Tools:  report_list {}  (answers the same object as GET /reports)
///           report_run { key, parameters?: { from, to, groupBy, ... }, format? }  (answers the same object as the HTTP JSON; "csv" is not tested over MCP)
///   Errors: 400 VALIDATION_FAILED with errors keyed by the parameter name ("from"/"to", "groupBy", "articleId", "days");
///           unknown report key -> 404 NOT_FOUND (tool error NOT_FOUND).
///   Dates are "yyyy-MM-dd"; a "period" cell is the FIRST day of its period (Monday for week, 1 Jan/Apr/Jul/Oct for quarter);
///   `from` and `to` are both inclusive; a refund (cancelling invoice) counts as -1 in an invoice count.
///   Totals: an object keyed by the summable column keys (not percent, not dates, not "daysOverdue"); every report except
///   slow-movers has totals when it has rows.
/// </summary>
public static class ReportApi
{
    public const string Path = "/api/v1/reports";
    public const int ToolCount = 134;

    public static readonly string[] ToolNames = ["report_list", "report_run"];

    public static readonly string[] Keys =
    [
        "sales-by-period", "sales-by-customer", "sales-by-article", "vat-output", "payments-received", "unpaid-invoices",
        "stock-on-hand", "stock-movements", "slow-movers", "open-sales-orders", "open-purchase-orders",
        "purchases-by-supplier", "production-output",
    ];

    public static readonly string[] ColumnTypes = ["date", "text", "number", "money", "quantity", "percent"];

    public static readonly string[] GroupBys = ["day", "week", "month", "quarter", "year"];

    /// <summary>The reports that group time: they take <c>groupBy</c>.</summary>
    public static readonly string[] GroupedKeys = ["sales-by-period", "vat-output", "payments-received", "production-output"];

    /// <summary>The reports that take <c>from</c> and <c>to</c> (a period).</summary>
    public static readonly string[] PeriodKeys =
    [
        "sales-by-period", "sales-by-customer", "sales-by-article", "vat-output", "payments-received", "stock-movements",
        "purchases-by-supplier", "production-output",
    ];

    /// <summary>The reports that are "as of now" and take no period: stock-on-hand, slow-movers, unpaid-invoices, open-*.</summary>
    public static readonly string[] AsOfNowKeys = ["unpaid-invoices", "stock-on-hand", "slow-movers", "open-sales-orders", "open-purchase-orders"];

    public static IEnumerable<object[]> KeyData() => Keys.Select(k => new object[] { k });

    /// <summary>The column keys of each report, in order (type in the comment).</summary>
    public static readonly Dictionary<string, string[]> Columns = new()
    {
        // period date, invoiceCount number, netAmount/taxAmount/totalAmount money
        ["sales-by-period"] = ["period", "invoiceCount", "netAmount", "taxAmount", "totalAmount"],
        // customerCode/customerName text, invoiceCount number, money x3
        ["sales-by-customer"] = ["customerCode", "customerName", "invoiceCount", "netAmount", "taxAmount", "totalAmount"],
        // articleCode/articleName/categoryCode/categoryName text, quantity quantity, netAmount money
        ["sales-by-article"] = ["articleCode", "articleName", "categoryCode", "categoryName", "quantity", "netAmount"],
        // period date, taxPercent percent, netAmount (base) money, taxAmount money
        ["vat-output"] = ["period", "taxPercent", "netAmount", "taxAmount"],
        // period date, paymentMethodCode/paymentMethodName text, paymentCount number, amount money
        ["payments-received"] = ["period", "paymentMethodCode", "paymentMethodName", "paymentCount", "amount"],
        // number text, issueDate/dueDate date, customerCode/customerName text, totalAmount/paidAmount/openAmount money, daysOverdue number
        ["unpaid-invoices"] = ["number", "issueDate", "dueDate", "customerCode", "customerName", "totalAmount", "paidAmount", "openAmount", "daysOverdue"],
        // articleCode/articleName/warehouseCode/warehouseName text, quantity quantity
        ["stock-on-hand"] = ["articleCode", "articleName", "warehouseCode", "warehouseName", "quantity"],
        // articleCode/articleName text, openingQuantity/inQuantity/outQuantity/closingQuantity quantity
        ["stock-movements"] = ["articleCode", "articleName", "openingQuantity", "inQuantity", "outQuantity", "closingQuantity"],
        // articleCode/articleName text, quantity quantity, lastOutDate date (null: never), daysSinceLastOut number (null: never)
        ["slow-movers"] = ["articleCode", "articleName", "quantity", "lastOutDate", "daysSinceLastOut"],
        // number text, orderDate date, customerCode/customerName/articleCode/articleName text, ordered/delivered/outstandingQuantity quantity
        ["open-sales-orders"] = ["number", "orderDate", "customerCode", "customerName", "articleCode", "articleName", "orderedQuantity", "deliveredQuantity", "outstandingQuantity"],
        // number text, orderDate date, supplierCode/supplierName/articleCode/articleName text, ordered/received/outstandingQuantity quantity
        ["open-purchase-orders"] = ["number", "orderDate", "supplierCode", "supplierName", "articleCode", "articleName", "orderedQuantity", "receivedQuantity", "outstandingQuantity"],
        // supplierCode/supplierName text, receiptCount number, quantity quantity
        ["purchases-by-supplier"] = ["supplierCode", "supplierName", "receiptCount", "quantity"],
        // period date, articleCode/articleName text, workOrderCount number, quantity quantity
        ["production-output"] = ["period", "articleCode", "articleName", "workOrderCount", "quantity"],
    };

    // ---- HTTP ----

    public static string Url(string key, params (string Name, string? Value)[] query)
    {
        var parts = query.Where(q => q.Value is not null).Select(q => $"{q.Name}={Uri.EscapeDataString(q.Value!)}").ToArray();
        return $"{Path}/{Uri.EscapeDataString(key)}" + (parts.Length == 0 ? "" : "?" + string.Join("&", parts));
    }

    public static Task<HttpResponseMessage> SendAsync(HttpClient http, string key, params (string Name, string? Value)[] query) =>
        http.GetAsync(Url(key, query));

    public static async Task<JsonElement> RunAsync(HttpClient http, string key, params (string Name, string? Value)[] query)
    {
        using var response = await SendAsync(http, key, query);
        return await HttpAssert.JsonAsync(response, HttpStatusCode.OK);
    }

    public static async Task<JsonElement> ListAsync(HttpClient http)
    {
        using var response = await http.GetAsync(Path);
        return await HttpAssert.JsonAsync(response, HttpStatusCode.OK);
    }

    /// <summary>The query of a seeded period: <c>from</c>, <c>to</c> and optionally <c>groupBy</c> / filters.</summary>
    public static (string, string?)[] Period(string from, string to, string? groupBy = null, params (string Name, string? Value)[] more) =>
    [
        ("from", from), ("to", to), ("groupBy", groupBy), .. more,
    ];

    /// <summary>The raw CSV response of a report: the status, the content type and the bytes.</summary>
    public static async Task<(HttpResponseMessage Response, byte[] Bytes)> CsvAsync(HttpClient http, string key, params (string Name, string? Value)[] query)
    {
        var response = await http.GetAsync(Url(key, [.. query, ("format", "csv")]));
        return (response, await response.Content.ReadAsByteArrayAsync());
    }

    // ---- MCP ----

    public static Task<JsonElement> ListAsync(McpConnection mcp) => mcp.OkAsync("report_list");

    public static Task<JsonElement> RunAsync(McpConnection mcp, string key, object? parameters = null) =>
        mcp.OkAsync("report_run", parameters is null ? new { key } : new { key, parameters });

    // ---- reading ----

    public static JsonElement[] Rows(this JsonElement body) => body.GetProperty("rows").EnumerateArray().ToArray();

    public static string[] ColumnKeys(this JsonElement body) => body.GetProperty("columns").EnumerateArray().Select(c => c.Str("key")).ToArray();

    public static bool HasTotals(this JsonElement body) => body.GetProperty("totals").ValueKind == JsonValueKind.Object;

    public static decimal Total(this JsonElement body, string column) => body.GetProperty("totals").GetProperty(column).GetDecimal();

    /// <summary>The single row whose given columns have the given text values.</summary>
    public static JsonElement Row(this JsonElement body, params (string Column, string Value)[] match)
    {
        var found = body.Rows().Where(r => match.All(m => Text(r, m.Column) == m.Value)).ToArray();
        Assert.True(found.Length == 1, $"Expected one row with {string.Join(", ", match.Select(m => $"{m.Column}={m.Value}"))}, found {found.Length}: {body.GetProperty("rows")}");
        return found[0];
    }

    public static JsonElement? RowOrNull(this JsonElement body, params (string Column, string Value)[] match) =>
        body.Rows().Where(r => match.All(m => Text(r, m.Column) == m.Value)).Cast<JsonElement?>().FirstOrDefault();

    public static string? Text(this JsonElement row, string column) =>
        row.TryGetProperty(column, out var v) ? v.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.String => v.GetString(),
            _ => v.GetRawText(),
        } : null;

    public static decimal Num(this JsonElement row, string column) => row.GetProperty(column).GetDecimal();

    /// <summary>Asserts the numbers of a row (column, value) — money and quantities compared as decimals.</summary>
    public static void AssertNums(this JsonElement row, params (string Column, decimal Value)[] expected)
    {
        foreach (var (column, value) in expected)
        {
            Assert.True(row.TryGetProperty(column, out var actual) && actual.ValueKind == JsonValueKind.Number,
                $"Column '{column}' is not a number in row {row}");
            Assert.True(actual.GetDecimal() == value, $"Column '{column}': expected {value}, got {actual.GetDecimal()} in row {row}");
        }
    }

    /// <summary>The body without <c>generatedAt</c>, as text, for comparisons between two runs of a report.</summary>
    public static string WithoutTimestamp(this JsonElement body) =>
        JsonSerializer.Serialize(body.EnumerateObject().Where(p => p.Name != "generatedAt").ToDictionary(p => p.Name, p => p.Value));

    // ---- shape of every run ----

    /// <summary>
    /// What every answer of every report has (spec section 1): the envelope, the columns of the catalogue, rows with
    /// every column, <c>rowCount</c>, <c>truncated</c>, <c>notes</c>, and totals equal to the sum of the rows.
    /// </summary>
    public static void AssertShape(JsonElement body, string key, JsonElement definition)
    {
        Assert.Equal(key, body.Str("report"));
        Assert.Equal(JsonValueKind.Object, body.GetProperty("parameters").ValueKind);
        var generated = DateTimeOffset.Parse(body.Str("generatedAt"), CultureInfo.InvariantCulture);
        Assert.True(body.Str("generatedAt").EndsWith('Z'), "generatedAt must be UTC ('Z').");
        Assert.InRange((DateTimeOffset.UtcNow - generated).Duration(), TimeSpan.Zero, TimeSpan.FromDays(1));

        var columns = body.GetProperty("columns").EnumerateArray().ToArray();
        Assert.Equal(Columns[key], columns.Select(c => c.Str("key")).ToArray());
        Assert.Equal(definition.GetProperty("columns").GetRawText(), body.GetProperty("columns").GetRawText());
        foreach (var column in columns)
        {
            Assert.False(string.IsNullOrWhiteSpace(column.Str("label")));
            Assert.Contains(column.Str("type"), ColumnTypes);
        }

        var rows = body.Rows();
        Assert.Equal(rows.Length, body.GetProperty("rowCount").GetInt32());
        Assert.False(body.GetProperty("truncated").GetBoolean());
        Assert.Equal(JsonValueKind.Array, body.GetProperty("notes").ValueKind);
        foreach (var row in rows)
            foreach (var column in columns)
                Assert.True(row.TryGetProperty(column.Str("key"), out _), $"Row without '{column.Str("key")}': {row}");

        if (!body.HasTotals())
        {
            Assert.Equal(JsonValueKind.Null, body.GetProperty("totals").ValueKind);
            return;
        }
        foreach (var total in body.GetProperty("totals").EnumerateObject())
        {
            var column = columns.SingleOrDefault(c => c.Str("key") == total.Name);
            Assert.True(column.ValueKind == JsonValueKind.Object, $"Totals has a key that is not a column: {total.Name}");
            Assert.Contains(column.Str("type"), new[] { "number", "money", "quantity" });
            var sum = rows.Sum(r => r.GetProperty(total.Name).ValueKind == JsonValueKind.Number ? r.GetProperty(total.Name).GetDecimal() : 0m);
            Assert.True(sum == total.Value.GetDecimal(), $"{key}: totals.{total.Name} = {total.Value} but the rows add up to {sum}");
        }
    }
}

/// <summary>A tolerant reader of the CSV of a report: UTF-8 with BOM, ';' separator, RFC 4180 quoting.</summary>
public static class ReportCsv
{
    public static readonly byte[] Bom = [0xEF, 0xBB, 0xBF];

    public static string Decode(byte[] bytes)
    {
        Assert.True(bytes.Length >= 3 && bytes.AsSpan(0, 3).SequenceEqual(Bom), "The CSV must start with a UTF-8 BOM (EF BB BF).");
        return new UTF8Encoding(false, true).GetString(bytes, 3, bytes.Length - 3);
    }

    /// <summary>Splits text into records of cells; a quoted cell may hold the separator, quotes ("") and line breaks.</summary>
    public static List<string[]> Parse(string text, char separator = ';')
    {
        var records = new List<string[]>();
        var cells = new List<string>();
        var cell = new StringBuilder();
        var quoted = false;
        var any = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else cell.Append(c);
                continue;
            }
            if (c == '"') { quoted = true; any = true; }
            else if (c == separator) { cells.Add(cell.ToString()); cell.Clear(); any = true; }
            else if (c == '\r' || c == '\n')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                if (any || cell.Length > 0) { cells.Add(cell.ToString()); records.Add([.. cells]); }
                cells.Clear(); cell.Clear(); any = false;
            }
            else { cell.Append(c); any = true; }
        }
        if (any || cell.Length > 0) { cells.Add(cell.ToString()); records.Add([.. cells]); }
        Assert.False(quoted, "The CSV ends inside a quoted cell.");
        return records;
    }

    /// <summary>Asserts that CSV text holds exactly the columns and rows of the JSON body (see <see cref="ReportApi"/> for the tolerances).</summary>
    public static void AssertEqualsJson(string csv, JsonElement body)
    {
        var records = Parse(csv);
        var columns = body.GetProperty("columns").EnumerateArray().ToArray();
        var rows = body.Rows();
        Assert.NotEmpty(records);

        var header = records[0];
        var labels = columns.Select(c => c.Str("label")).ToArray();
        var keys = columns.Select(c => c.Str("key")).ToArray();
        Assert.True(header.SequenceEqual(labels) || header.SequenceEqual(keys),
            $"The first line must be the column labels or keys: [{string.Join("|", header)}]");

        var data = records.Skip(1).ToList();
        Assert.True(data.Count == rows.Length || data.Count == rows.Length + 1,
            $"The CSV has {data.Count} data lines for {rows.Length} rows.");
        for (var r = 0; r < rows.Length; r++)
        {
            Assert.Equal(columns.Length, data[r].Length);
            for (var c = 0; c < columns.Length; c++)
                AssertCell(data[r][c], rows[r].GetProperty(keys[c]), columns[c].Str("type"), $"row {r}, column {keys[c]}");
        }
    }

    private static void AssertCell(string cell, JsonElement value, string type, string where)
    {
        if (value.ValueKind == JsonValueKind.Null)
        {
            Assert.True(cell == "", $"{where}: a null must be an empty cell, got '{cell}'");
            return;
        }
        if (value.ValueKind == JsonValueKind.Number)
        {
            Assert.True(decimal.TryParse(cell.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var number),
                $"{where}: '{cell}' is not a number");
            Assert.True(number == value.GetDecimal(), $"{where}: CSV {cell} != JSON {value}");
            return;
        }
        var text = value.GetString()!;
        if (type == "date" && DateOnly.TryParseExact(text, "yyyy-MM-dd", out var date))
        {
            Assert.True(cell == text || cell.TrimEnd('.') == date.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture), $"{where}: CSV '{cell}' != JSON '{text}'");
            return;
        }
        Assert.True(cell == text, $"{where}: CSV '{cell}' != JSON '{text}'");
    }
}
