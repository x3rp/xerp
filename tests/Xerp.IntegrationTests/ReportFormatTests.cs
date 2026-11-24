using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Xerp.IntegrationTests.Support;

namespace Xerp.IntegrationTests;

/// <summary>Spec RP01: <c>format=csv</c> is the JSON rows as UTF-8 text with a BOM and ';' as separator; Croatian characters survive.</summary>
[Collection(XerpCollection.Name)]
public class ReportFormatTests(XerpFixture app)
{
    public static IEnumerable<object[]> AllKeys() => ReportApi.KeyData();

    [Theory]
    [MemberData(nameof(AllKeys))]
    public async Task AC4_csv_has_the_same_columns_and_rows_as_the_json(string key)
    {
        var seed = await ReportSeed.GetAsync(app);
        var query = ReportQueries.Seeded(key);

        var json = await ReportApi.RunAsync(seed.Http, key, query);
        var (response, bytes) = await ReportApi.CsvAsync(seed.Http, key, query);
        using var _ = response;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/csv", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("utf-8", response.Content.Headers.ContentType?.CharSet?.ToLowerInvariant());
        ReportCsv.AssertEqualsJson(ReportCsv.Decode(bytes), json);
    }

    [Fact]
    public async Task AC4_csv_is_also_chosen_by_the_Accept_header()
    {
        var seed = await ReportSeed.GetAsync(app);
        var query = ReportApi.Period(ReportSeed.From, ReportSeed.To, "month");
        using var request = new HttpRequestMessage(HttpMethod.Get, ReportApi.Url("sales-by-period", query));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/csv"));

        using var response = await seed.Http.SendAsync(request);
        var bytes = await response.Content.ReadAsByteArrayAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/csv", response.Content.Headers.ContentType?.MediaType);
        ReportCsv.AssertEqualsJson(ReportCsv.Decode(bytes), await ReportApi.RunAsync(seed.Http, "sales-by-period", query));
    }

    [Fact]
    public async Task AC4_the_file_starts_with_a_utf8_BOM_and_separates_with_semicolons()
    {
        var seed = await ReportSeed.GetAsync(app);

        var (response, bytes) = await ReportApi.CsvAsync(seed.Http, "sales-by-customer", ReportApi.Period(ReportSeed.From, ReportSeed.To));
        using var _ = response;

        Assert.Equal(ReportCsv.Bom, bytes.Take(3).ToArray());
        var text = new UTF8Encoding(false, true).GetString(bytes, 3, bytes.Length - 3);   // strict: invalid UTF-8 throws
        var header = text.Split('\n')[0].TrimEnd('\r');
        Assert.Equal(ReportApi.Columns["sales-by-customer"].Length, header.Split(';').Length);
        Assert.DoesNotContain("﻿", text);   // exactly one BOM
    }

    [Fact]
    public async Task AC4_croatian_characters_survive_and_a_name_with_a_semicolon_and_quotes_is_quoted()
    {
        var seed = await ReportSeed.GetAsync(app);

        var (response, bytes) = await ReportApi.CsvAsync(seed.Http, "sales-by-customer", ReportApi.Period(ReportSeed.From, ReportSeed.To));
        using var _ = response;
        var text = ReportCsv.Decode(bytes);
        var records = ReportCsv.Parse(text);

        Assert.Contains("Čakovec trgovina d.o.o.", text);
        Assert.True(bytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes("Čakovec")) >= 0, "The bytes must hold the UTF-8 encoding of the name.");
        // The name holds ';' and '"': one cell, quoted, quotes doubled (RFC 4180).
        Assert.Contains("\"Šimić & sinovi; obrt \"\"Đuro\"\"\"", text);
        Assert.Contains(records, r => r.Contains(ReportSeed.Kup2Text));
        Assert.All(records, r => Assert.Equal(ReportApi.Columns["sales-by-customer"].Length, r.Length));
    }

    [Fact]
    public async Task AC4_croatian_characters_in_stock_reports_and_the_category()
    {
        var seed = await ReportSeed.GetAsync(app);

        var (response, bytes) = await ReportApi.CsvAsync(seed.Http, "sales-by-article", ReportApi.Period(ReportSeed.From, ReportSeed.To));
        using var _ = response;
        var records = ReportCsv.Parse(ReportCsv.Decode(bytes));

        Assert.Contains(records, r => r.Contains(ReportSeed.AName) && r.Contains("Pića"));
        Assert.Contains(records, r => r.Contains(ReportSeed.CName) && r.Contains("Hrana"));
    }

    [Fact]
    public async Task AC4_an_empty_result_is_still_a_file_with_BOM_and_header()
    {
        var tenant = await app.NewTenantAsync();

        var (response, bytes) = await ReportApi.CsvAsync(tenant.Client, "sales-by-period");
        using var _ = response;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var records = ReportCsv.Parse(ReportCsv.Decode(bytes));
        Assert.NotEmpty(records);
        Assert.Equal(ReportApi.Columns["sales-by-period"].Length, records[0].Length);
    }

    [Fact]
    public async Task AC4_a_refused_parameter_is_a_json_problem_even_when_csv_is_asked_for()
    {
        var seed = await ReportSeed.GetAsync(app);

        var (response, _) = await ReportApi.CsvAsync(seed.Http, "sales-by-period", ("groupBy", "fortnight"));
        using var __ = response;

        await HttpAssert.ValidationAsync(response, "groupBy");
    }
}
