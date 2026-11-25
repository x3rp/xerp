using System.Text;
using System.Text.Json;
using Xerp.Api.Http;
using Xerp.Application.Reports;

namespace Xerp.Api.Endpoints;

/// <summary>Spec RP01: the report catalogue and the run of a report, as JSON or CSV. Thin: bind, call, map.</summary>
public static class ReportEndpoints
{
    public static void MapReportEndpoints(this IEndpointRouteBuilder v1)
    {
        var reports = v1.MapGroup("/reports");
        reports.MapGet("", (ReportOperations operations) => Results.Ok(operations.List().Value));

        var accepted = ReportOperations.ParameterNames.Append("format").ToArray();
        reports.MapGet("/{key}", async (string key, HttpRequest request, ReportOperations operations, CancellationToken ct) =>
        {
            var parameters = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            string? format = null;
            foreach (var (name, values) in request.Query)
            {
                if (name == "format")
                    format = values.ToString();
                else if (values.ToString().Length > 0)
                    parameters[name] = JsonSerializer.SerializeToElement(values.ToString());
            }
            var wantsCsv = string.IsNullOrEmpty(format) && request.Headers.Accept.Any(a => a?.Contains("text/csv", StringComparison.OrdinalIgnoreCase) == true);
            var input = new RunReportInput(key, parameters, wantsCsv ? ReportOperations.FormatCsv : string.IsNullOrEmpty(format) ? null : format);
            var result = await operations.RunAsync(input, ct);
            if (!result.IsSuccess)
                return Problems.From(result.Error);
            if (input.Format != ReportOperations.FormatCsv)
                return Results.Ok(result.Value);
            var bytes = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes(ReportOperations.ToCsv(result.Value))).ToArray();
            return Results.File(bytes, "text/csv; charset=utf-8", $"{key}.csv");
        }).AcceptsQuery(accepted);
    }
}
