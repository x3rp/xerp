using Xerp.Application.Reports;

namespace Xerp.UnitTests;

/// <summary>Spec RP01: the catalogue is consistent - the integration tests run every report against a database.</summary>
public class ReportCatalogueTests
{
    [Fact]
    public void Keys_are_unique_every_report_is_described_and_only_numeric_columns_are_summed()
    {
        var catalogue = ReportOperations.Catalogue;
        Assert.Equal(13, catalogue.Count);
        Assert.Equal(catalogue.Count, catalogue.Select(r => r.Key).Distinct().Count());
        foreach (var report in catalogue)
        {
            Assert.False(string.IsNullOrWhiteSpace(report.Name) || string.IsNullOrWhiteSpace(report.Description), report.Key);
            Assert.Equal(report.Columns.Count, report.Columns.Select(c => c.Key).Distinct().Count());
            Assert.All(report.Columns.Where(c => c.Summable), c => Assert.Contains(c.Type, new[] { "number", "money", "quantity" }));
            Assert.All(report.Parameters, p => Assert.Contains(p.Name, ReportOperations.ParameterNames));
        }
    }

    [Theory]
    [InlineData("day", "2026-10-14", "2026-10-14")]
    [InlineData("week", "2026-10-14", "2026-10-12")]
    [InlineData("month", "2026-10-14", "2026-10-01")]
    [InlineData("quarter", "2026-11-30", "2026-10-01")]
    [InlineData("year", "2026-11-30", "2026-01-01")]
    [InlineData("week", "0001-01-03", "0001-01-01")]
    public void A_period_is_identified_by_its_first_day(string groupBy, string date, string expected) =>
        Assert.Equal(DateOnly.Parse(expected), ReportPeriods.Start(DateOnly.Parse(date), groupBy));
}
