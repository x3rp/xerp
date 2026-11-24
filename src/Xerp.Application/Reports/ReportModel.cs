using System.Globalization;
using System.Text.Json;
using Xerp.Application.Common;
using Xerp.Application.Ports;

namespace Xerp.Application.Reports;

/// <summary>Spec RP01: one column of a report. <c>Summable</c> columns are added up in <c>totals</c>.</summary>
public sealed record ReportColumn(string Key, string Label, string Type, bool Summable = false);

/// <summary>A parameter a report takes: its name, type (<c>date</c>, <c>uuid</c>, <c>integer</c>, <c>enum</c>), whether it is required, its default and the values it allows.</summary>
public sealed record ReportParameterDto(string Name, string Type, bool Required, object? Default, IReadOnlyList<string>? AllowedValues, string Description);

public sealed record ReportColumnDto(string Key, string Label, string Type);

public sealed record ReportDefinitionDto(
    string Key, string Name, string Description, IReadOnlyList<ReportParameterDto> Parameters, IReadOnlyList<ReportColumnDto> Columns);

/// <summary>The answer of <c>report_run</c> (spec RP01, section 1).</summary>
public sealed record ReportResultDto(
    string Report, IReadOnlyDictionary<string, object?> Parameters, DateTime GeneratedAt, IReadOnlyList<ReportColumnDto> Columns,
    IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows, IReadOnlyDictionary<string, object?>? Totals, int RowCount, bool Truncated,
    IReadOnlyList<string> Notes);

/// <summary>The answer of <c>report_run</c> with <c>format</c> = <c>csv</c> over a tool: the CSV text (without the byte-order mark).</summary>
public sealed record ReportCsvDto(string Report, string Format, string Csv, int RowCount, bool Truncated);

public sealed record RunReportInput(string? Key = null, Dictionary<string, JsonElement>? Parameters = null, string? Format = null);

/// <summary>The parameters of one run, parsed and checked against the report's own list.</summary>
public sealed class ReportArgs
{
    public DateOnly Today { get; init; }
    public DateOnly From { get; init; }
    public DateOnly To { get; init; }
    public string GroupBy { get; init; } = "month";
    public Guid? ArticleId { get; init; }
    public Guid? PartnerId { get; init; }
    public Guid? WarehouseId { get; init; }
    public Guid? CategoryId { get; init; }
    public int Days { get; init; } = 90;
}

/// <summary>A report: its definition and its query. Read-only and tenant-filtered (<see cref="IXerpDb"/> is already); it decides nothing.</summary>
public abstract class ReportBase
{
    public abstract string Key { get; }
    public abstract string Name { get; }
    public abstract string Description { get; }
    public abstract IReadOnlyList<ReportParameter> Parameters { get; }
    public abstract IReadOnlyList<ReportColumn> Columns { get; }

    /// <summary>Whether the answer has a <c>totals</c> object (the sum of the summable columns) when it has rows.</summary>
    public virtual bool HasTotals => true;

    /// <summary>The rows, in the order they are to be shown. May return more than the limit; the caller truncates.</summary>
    public abstract Task<IReadOnlyList<Dictionary<string, object?>>> RowsAsync(IXerpDb db, ReportArgs args, CancellationToken ct);

    public ReportDefinitionDto ToDefinition() => new(Key, Name, Description,
        Parameters.Select(p => new ReportParameterDto(p.Name, p.Type, p.Required, p.Default, p.AllowedValues, p.Description)).ToList(),
        Columns.Select(c => new ReportColumnDto(c.Key, c.Label, c.Type)).ToList());

    protected static Dictionary<string, object?> Row(params (string Key, object? Value)[] cells) =>
        cells.ToDictionary(c => c.Key, c => c.Value, StringComparer.Ordinal);

    protected static string Date(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    protected static string? Date(DateOnly? date) => date is { } d ? Date(d) : null;
}

public sealed record ReportParameter(string Name, string Type, bool Required, object? Default, IReadOnlyList<string>? AllowedValues, string Description)
{
    public static readonly string[] GroupBys = ["day", "week", "month", "quarter", "year"];

    public static ReportParameter From { get; } = new("from", "date", false, null, null, "First day of the period, YYYY-MM-DD, inclusive. Default: the first day of the current month.");
    public static ReportParameter To { get; } = new("to", "date", false, null, null, "Last day of the period, YYYY-MM-DD, inclusive. Default: the last day of the current month.");
    public static ReportParameter GroupBy { get; } = new("groupBy", "enum", false, "month", GroupBys, "How the periods are cut: day, week (starting Monday), month, quarter or year. A period is shown as its first day.");
    public static ReportParameter ArticleId(string what = "Only this article") => new("articleId", "uuid", false, null, null, what + " (the `id` of an article).");
    public static ReportParameter PartnerId(string what) => new("partnerId", "uuid", false, null, null, what + " (the `id` of a partner).");
    public static ReportParameter WarehouseId(string what = "Only this warehouse") => new("warehouseId", "uuid", false, null, null, what + " (the `id` of a warehouse).");
    public static ReportParameter CategoryId(string what = "Only articles of this category") => new("categoryId", "uuid", false, null, null, what + " (the `id` of an article category).");
}

/// <summary>Cutting dates into periods; a period is identified by its first day (spec RP01: Monday for a week).</summary>
public static class ReportPeriods
{
    public static DateOnly Start(DateOnly date, string groupBy) => groupBy switch
    {
        "day" => date,
        "week" => date.AddDays(-(((int)date.DayOfWeek + 6) % 7)),
        "month" => new DateOnly(date.Year, date.Month, 1),
        "quarter" => new DateOnly(date.Year, (date.Month - 1) / 3 * 3 + 1, 1),
        "year" => new DateOnly(date.Year, 1, 1),
        _ => throw new ArgumentOutOfRangeException(nameof(groupBy)),
    };
}
