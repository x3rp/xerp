using System.Globalization;
using System.Text;
using System.Text.Json;
using Xerp.Application.Common;
using Xerp.Application.Ports;

namespace Xerp.Application.Reports;

/// <summary>
/// Spec RP01: the report catalogue and the one way to run a report. A report is a class with its definition and its
/// query; this class checks the parameters against the report's own list (a refusal is <c>VALIDATION_FAILED</c> keyed
/// by the parameter), runs it, cuts at <see cref="MaxRows"/> and adds the totals. Reports are read-only.
/// </summary>
public sealed class ReportOperations(IXerpDb db, IClock clock)
{
    public const int MaxRows = 5000;

    public const string FormatJson = "json";
    public const string FormatCsv = "csv";

    /// <summary>Every report, in the order of the catalogue. A new spec adds its reports here.</summary>
    public static IReadOnlyList<ReportBase> Catalogue { get; } =
    [
        new SalesByPeriodReport(), new SalesByCustomerReport(), new SalesByArticleReport(), new VatOutputReport(),
        new PaymentsReceivedReport(), new UnpaidInvoicesReport(), new StockOnHandReport(), new StockMovementsReport(),
        new SlowMoversReport(), new OpenSalesOrdersReport(), new OpenPurchaseOrdersReport(), new PurchasesBySupplierReport(),
        new ProductionOutputReport(),
    ];

    /// <summary>Every parameter name some report takes: what an HTTP client may put in the query string besides <c>format</c>.</summary>
    public static IReadOnlyList<string> ParameterNames { get; } =
        Catalogue.SelectMany(r => r.Parameters).Select(p => p.Name).Distinct(StringComparer.Ordinal).ToList();

    public Result<ItemsResult<ReportDefinitionDto>> List() =>
        new ItemsResult<ReportDefinitionDto>(Catalogue.Select(r => r.ToDefinition()).ToList());

    /// <summary>Runs a report: the result (JSON, <see cref="ReportResultDto"/>) or, for <c>csv</c>, the same as <see cref="ReportCsvDto"/>.</summary>
    public async Task<Result<ReportResultDto>> RunAsync(RunReportInput input, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(input.Key))
            return AppError.Validation("key", "key is required: the key of a report from report_list.");
        var report = Catalogue.FirstOrDefault(r => string.Equals(r.Key, input.Key.Trim(), StringComparison.Ordinal));
        if (report is null)
            return AppError.NotFound($"No report '{input.Key}'. See report_list (GET /reports).");
        var format = string.IsNullOrWhiteSpace(input.Format) ? FormatJson : input.Format.Trim();
        var errors = new ValidationErrors();
        if (format is not (FormatJson or FormatCsv))
            errors.Add("format", "format must be \"json\" or \"csv\".");

        var now = clock.UtcNow;
        var args = Parse(report, input.Parameters ?? [], DateOnly.FromDateTime(now), errors, out var echo);
        if (errors.Any)
            return errors.ToError();

        var rows = await report.RowsAsync(db, args, ct);
        return Build(report, echo, now, rows);
    }

    /// <summary>The same as <see cref="RunAsync"/> for the MCP tool: the JSON result, or the CSV text when <c>format</c> is <c>csv</c>.</summary>
    public async Task<Result<object>> RunForToolAsync(RunReportInput input, CancellationToken ct = default)
    {
        var result = await RunAsync(input, ct);
        if (!result.IsSuccess)
            return result.Error;
        if (string.Equals(input.Format?.Trim(), FormatCsv, StringComparison.Ordinal))
            return (object)new ReportCsvDto(result.Value.Report, FormatCsv, ToCsv(result.Value), result.Value.RowCount, result.Value.Truncated);
        return (object)result.Value;
    }

    // ---- parameters

    private static ReportArgs Parse(ReportBase report, Dictionary<string, JsonElement> given, DateOnly today, ValidationErrors errors,
        out IReadOnlyDictionary<string, object?> echo)
    {
        var shown = new Dictionary<string, object?>(StringComparer.Ordinal);
        var declared = report.Parameters.ToDictionary(p => p.Name, StringComparer.Ordinal);
        foreach (var name in given.Keys.Where(k => !declared.ContainsKey(k)))
            errors.Add(name, $"{name} is not a parameter of the report '{report.Key}'; its parameters are: {string.Join(", ", declared.Keys)}.");

        string? Raw(string name)
        {
            if (!given.TryGetValue(name, out var value))
                return null;
            switch (value.ValueKind)
            {
                case JsonValueKind.String: return string.IsNullOrWhiteSpace(value.GetString()) ? null : value.GetString()!.Trim();
                case JsonValueKind.Number: return value.GetRawText();
                case JsonValueKind.Null or JsonValueKind.Undefined: return null;
                default:
                    errors.Add(name, $"{name} must be a text or a number.");
                    return null;
            }
        }

        DateOnly? Date(string name)
        {
            if (!declared.ContainsKey(name) || Raw(name) is not { } text)
                return null;
            if (DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                return date;
            errors.Add(name, $"{name} must be a date written YYYY-MM-DD.");
            return null;
        }

        Guid? Id(string name)
        {
            if (!declared.ContainsKey(name) || Raw(name) is not { } text)
                return null;
            if (Guid.TryParse(text, out var id))
            {
                shown[name] = id.ToString();
                return id;
            }
            errors.Add(name, $"{name} must be the id (a UUID) of a record.");
            return null;
        }

        var monthStart = new DateOnly(today.Year, today.Month, 1);
        var from = Date("from");
        var to = Date("to");
        if (declared.ContainsKey("from"))
        {
            // Only one of them given: the other is the edge of that day's month, so a default never contradicts what was asked.
            var f = from ?? (to is { } toDate ? new DateOnly(toDate.Year, toDate.Month, 1) : monthStart);
            var t = to ?? new DateOnly(f.Year, f.Month, DateTime.DaysInMonth(f.Year, f.Month));
            if (f > t && !errors.Has("from") && !errors.Has("to"))
                errors.Add("to", "to must not be before from.");
            (from, to) = (f, t);
            shown["from"] = f.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            shown["to"] = t.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        var groupBy = "month";
        if (declared.ContainsKey("groupBy"))
        {
            if (Raw("groupBy") is { } text)
            {
                if (ReportParameter.GroupBys.Contains(text, StringComparer.Ordinal))
                    groupBy = text;
                else
                    errors.Add("groupBy", $"groupBy must be one of: {string.Join(", ", ReportParameter.GroupBys)}.");
            }
            shown["groupBy"] = groupBy;
        }

        var days = 90;
        if (declared.ContainsKey("days"))
        {
            if (Raw("days") is { } text)
            {
                if (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n is >= 1 and <= MaxDays)
                    days = n;
                else
                    errors.Add("days", $"days must be a whole number from 1 to {MaxDays}.");
            }
            shown["days"] = days;
        }

        var args = new ReportArgs
        {
            Today = today,
            From = from ?? default,
            To = to ?? default,
            GroupBy = groupBy,
            ArticleId = Id("articleId"),
            PartnerId = Id("partnerId"),
            WarehouseId = Id("warehouseId"),
            CategoryId = Id("categoryId"),
            Days = days,
        };
        echo = shown;
        return args;
    }

    private const int MaxDays = 100000;

    // ---- the answer

    private static Result<ReportResultDto> Build(ReportBase report, IReadOnlyDictionary<string, object?> echo, DateTime now,
        IReadOnlyList<Dictionary<string, object?>> rows)
    {
        foreach (var row in rows)
            foreach (var column in report.Columns)
                row[column.Key] = Normalize(column, row.GetValueOrDefault(column.Key));

        Dictionary<string, object?>? totals = null;
        if (report.HasTotals && rows.Count > 0)
        {
            totals = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var column in report.Columns.Where(c => c.Summable))
                totals[column.Key] = Normalize(column, rows.Sum(r => r[column.Key] is decimal d ? d : r[column.Key] is null ? 0m : Convert.ToDecimal(r[column.Key], CultureInfo.InvariantCulture)));
        }

        var notes = new List<string>();
        var truncated = rows.Count > MaxRows;
        var shown = truncated ? rows.Take(MaxRows).ToList() : rows;
        if (truncated)
        {
            var narrow = report.Parameters.Select(p => p.Name).Where(n => n is "from" or "to" or "articleId" or "partnerId" or "warehouseId" or "categoryId" or "days").ToList();
            notes.Add($"The report has {rows.Count} rows; only the first {MaxRows} are shown. Totals cover all {rows.Count} rows. "
                + (narrow.Count > 0 ? $"Narrow it with {string.Join(", ", narrow)}." : "Narrow it with a filter."));
        }
        return new ReportResultDto(report.Key, echo, now, report.Columns.Select(c => new ReportColumnDto(c.Key, c.Label, c.Type)).ToList(),
            shown.Select(r => (IReadOnlyDictionary<string, object?>)r).ToList(), totals, shown.Count, truncated, notes);
    }

    private static object? Normalize(ReportColumn column, object? value)
    {
        if (value is null)
            return null;
        return column.Type switch
        {
            "money" => decimal.Round(ToDecimal(value), 2, MidpointRounding.AwayFromZero) + 0.00m,
            "quantity" or "percent" => Strip(ToDecimal(value)),
            "number" => value is decimal d ? Strip(d) : value,
            _ => value,
        };
    }

    private static decimal ToDecimal(object value) => value is decimal d ? d : Convert.ToDecimal(value, CultureInfo.InvariantCulture);

    /// <summary>The same number without trailing zeros: <c>35.000000</c> is <c>35</c>.</summary>
    private static decimal Strip(decimal value) => value / 1.0000000000000000000000000000m;

    // ---- CSV

    /// <summary>
    /// The CSV text of a result: a header of the column labels and one line per row, <c>;</c> as separator (so that Croatian
    /// Excel opens it), RFC 4180 quoting, a decimal point as in the JSON, dates as YYYY-MM-DD. The caller prefixes the UTF-8 BOM.
    /// </summary>
    public static string ToCsv(ReportResultDto result)
    {
        var text = new StringBuilder();
        text.Append(string.Join(';', result.Columns.Select(c => Cell(c.Label)))).Append("\r\n");
        foreach (var row in result.Rows)
            text.Append(string.Join(';', result.Columns.Select(c => Cell(Format(row.GetValueOrDefault(c.Key)))))).Append("\r\n");
        return text.ToString();
    }

    private static string Format(object? value) => value switch
    {
        null => "",
        decimal d => d.ToString(CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "",
    };

    private static string Cell(string text) =>
        text.IndexOfAny([';', '"', '\r', '\n']) >= 0 ? "\"" + text.Replace("\"", "\"\"") + "\"" : text;
}
