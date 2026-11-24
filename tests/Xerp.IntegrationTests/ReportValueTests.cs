using System.Net.Http.Json;
using System.Text.Json;
using Xerp.IntegrationTests.Support;

namespace Xerp.IntegrationTests;

/// <summary>
/// Spec RP01, section 4: each report over the seed of <see cref="ReportSeed"/> returns hand-computed values; totals are the
/// sum of the rows; a cancelled invoice and its cancelling invoice net to zero. The arithmetic is in the seed's summary.
/// </summary>
[Collection(XerpCollection.Name)]
public class ReportValueTests(XerpFixture app)
{
    private const string Oct = "2026-10-01";
    private const string Nov = "2026-11-01";

    private static (string, string?)[] Seeded(string? groupBy = null, params (string Name, string? Value)[] more) =>
        ReportApi.Period(ReportSeed.From, ReportSeed.To, groupBy, more);

    private static decimal DaysSince(string date) => (DateTime.UtcNow.Date - DateTime.Parse(date)).Days;

    // ---- sales-by-period

    [Fact]
    public async Task AC_sales_by_period_per_month_adds_up_the_issued_invoices_and_nets_the_cancellation()
    {
        var seed = await ReportSeed.GetAsync(app);

        var body = await ReportApi.RunAsync(seed.Http, "sales-by-period", Seeded("month"));

        Assert.Equal(2, body.Rows().Length);
        body.Row(("period", Oct)).AssertNums(("invoiceCount", 2), ("netAmount", 345m), ("taxAmount", 74.25m), ("totalAmount", 419.25m));
        // RN-000003 only: RN-000004 (226.00) and its cancelling RN-000005 (-226.00) net to zero.
        body.Row(("period", Nov)).AssertNums(("invoiceCount", 1), ("netAmount", 90m), ("taxAmount", 22.5m), ("totalAmount", 112.5m));
        Assert.True(body.HasTotals());
        Assert.Equal(3m, body.Total("invoiceCount"));
        Assert.Equal(435m, body.Total("netAmount"));
        Assert.Equal(96.75m, body.Total("taxAmount"));
        Assert.Equal(531.75m, body.Total("totalAmount"));
    }

    [Fact]
    public async Task AC_sales_by_period_groups_by_year_and_quarter()
    {
        var seed = await ReportSeed.GetAsync(app);

        var year = await ReportApi.RunAsync(seed.Http, "sales-by-period", Seeded("year"));
        var quarter = await ReportApi.RunAsync(seed.Http, "sales-by-period", Seeded("quarter"));

        foreach (var (body, period) in new[] { (year, "2026-01-01"), (quarter, "2026-10-01") })
        {
            var row = Assert.Single(body.Rows());
            Assert.Equal(period, row.Str("period"));
            row.AssertNums(("invoiceCount", 3), ("netAmount", 435m), ("taxAmount", 96.75m), ("totalAmount", 531.75m));
        }
    }

    [Fact]
    public async Task AC_sales_by_period_groups_by_week_starting_on_monday_and_by_day()
    {
        var seed = await ReportSeed.GetAsync(app);

        var week = await ReportApi.RunAsync(seed.Http, "sales-by-period", Seeded("week"));
        var day = await ReportApi.RunAsync(seed.Http, "sales-by-period", Seeded("day"));

        week.Row(("period", "2026-10-12")).AssertNums(("invoiceCount", 1), ("netAmount", 200m), ("totalAmount", 238m));
        week.Row(("period", "2026-10-19")).AssertNums(("invoiceCount", 1), ("netAmount", 145m), ("totalAmount", 181.25m));
        week.Row(("period", "2026-11-02")).AssertNums(("invoiceCount", 1), ("netAmount", 90m), ("totalAmount", 112.5m));
        // The week of 2026-11-09 holds RN-4 and its cancellation: absent or all zeros.
        if (week.RowOrNull(("period", "2026-11-09")) is { } cancelledWeek)
            cancelledWeek.AssertNums(("invoiceCount", 0), ("netAmount", 0m), ("totalAmount", 0m));
        Assert.Equal(435m, week.Total("netAmount"));

        day.Row(("period", "2026-10-12")).AssertNums(("invoiceCount", 1), ("netAmount", 200m), ("taxAmount", 38m), ("totalAmount", 238m));
        day.Row(("period", "2026-10-20")).AssertNums(("invoiceCount", 1), ("netAmount", 145m), ("taxAmount", 36.25m), ("totalAmount", 181.25m));
        day.Row(("period", "2026-11-05")).AssertNums(("invoiceCount", 1), ("netAmount", 90m), ("taxAmount", 22.5m), ("totalAmount", 112.5m));
        Assert.Equal(435m, day.Total("netAmount"));
    }

    [Fact]
    public async Task AC_sales_by_period_from_and_to_are_inclusive()
    {
        var seed = await ReportSeed.GetAsync(app);

        var oneDay = await ReportApi.RunAsync(seed.Http, "sales-by-period", ReportApi.Period("2026-10-12", "2026-10-12", "month"));
        var untilTheNineteenth = await ReportApi.RunAsync(seed.Http, "sales-by-period", ReportApi.Period(ReportSeed.From, "2026-10-19", "month"));
        var fromTheTwentieth = await ReportApi.RunAsync(seed.Http, "sales-by-period", ReportApi.Period("2026-10-20", "2026-10-20", "month"));

        Assert.Equal(1m, oneDay.Total("invoiceCount"));
        Assert.Equal(238m, oneDay.Total("totalAmount"));
        Assert.Equal(238m, untilTheNineteenth.Total("totalAmount"));
        Assert.Equal(181.25m, fromTheTwentieth.Total("totalAmount"));
    }

    [Fact]
    public async Task AC_sales_by_period_filters_by_customer()
    {
        var seed = await ReportSeed.GetAsync(app);

        var body = await ReportApi.RunAsync(seed.Http, "sales-by-period", Seeded("month", ("partnerId", seed.Kup1.ToString())));

        body.Row(("period", Oct)).AssertNums(("invoiceCount", 1), ("netAmount", 200m), ("taxAmount", 38m), ("totalAmount", 238m));
        body.Row(("period", Nov)).AssertNums(("invoiceCount", 1), ("netAmount", 90m), ("taxAmount", 22.5m), ("totalAmount", 112.5m));
        Assert.Equal(350.5m, body.Total("totalAmount"));
    }

    // ---- cancellations net to zero in every sales report

    [Theory]
    [InlineData("sales-by-period")]
    [InlineData("sales-by-customer")]
    [InlineData("sales-by-article")]
    [InlineData("vat-output")]
    public async Task AC3_a_cancelled_invoice_and_its_cancelling_invoice_net_to_zero_in_every_sales_report(string key)
    {
        var seed = await ReportSeed.GetAsync(app);

        // 2026-11-10 (RN-000004 issued) to 2026-11-12 (RN-000005 issued): the pair and nothing else.
        var pair = await ReportApi.RunAsync(seed.Http, key, ReportApi.Period("2026-11-10", "2026-11-12", ReportApi.GroupedKeys.Contains(key) ? "month" : null));
        // The day before the cancellation only RN-000004 stands.
        var before = await ReportApi.RunAsync(seed.Http, key, ReportApi.Period("2026-11-10", "2026-11-11", ReportApi.GroupedKeys.Contains(key) ? "month" : null));

        foreach (var column in pair.GetProperty("columns").EnumerateArray().Where(c => c.Str("type") is "money" or "quantity" or "number"))
        {
            var name = column.Str("key");
            if (name == "taxPercent")
                continue;
            Assert.Equal(0m, pair.Rows().Sum(r => r.Num(name)));
        }
        Assert.Equal(200m, before.Rows().Sum(r => r.Num("netAmount")));
    }

    // ---- sales-by-customer

    [Fact]
    public async Task AC_sales_by_customer_ranks_what_each_customer_was_invoiced_net_of_cancellations()
    {
        var seed = await ReportSeed.GetAsync(app);

        var body = await ReportApi.RunAsync(seed.Http, "sales-by-customer", Seeded());

        Assert.Equal(2, body.Rows().Length);
        var kup1 = body.Row(("customerCode", "KUP1"));
        Assert.Equal(ReportSeed.Kup1Name, kup1.Str("customerName"));
        kup1.AssertNums(("invoiceCount", 2), ("netAmount", 290m), ("taxAmount", 60.5m), ("totalAmount", 350.5m));
        var kup2 = body.Row(("customerCode", "KUP2"));
        Assert.Equal(ReportSeed.Kup2Text, kup2.Str("customerName"));
        kup2.AssertNums(("invoiceCount", 1), ("netAmount", 145m), ("taxAmount", 36.25m), ("totalAmount", 181.25m));
        Assert.Equal(3m, body.Total("invoiceCount"));
        Assert.Equal(435m, body.Total("netAmount"));
        Assert.Equal(96.75m, body.Total("taxAmount"));
        Assert.Equal(531.75m, body.Total("totalAmount"));
    }

    [Fact]
    public async Task AC_sales_by_customer_filters_by_customer_and_period()
    {
        var seed = await ReportSeed.GetAsync(app);

        var one = await ReportApi.RunAsync(seed.Http, "sales-by-customer", Seeded(null, ("partnerId", seed.Kup1.ToString())));
        var october = await ReportApi.RunAsync(seed.Http, "sales-by-customer", ReportApi.Period(ReportSeed.From, "2026-10-31"));

        Assert.Equal("KUP1", Assert.Single(one.Rows()).Str("customerCode"));
        Assert.Equal(350.5m, one.Total("totalAmount"));
        october.Row(("customerCode", "KUP1")).AssertNums(("totalAmount", 238m));
        october.Row(("customerCode", "KUP2")).AssertNums(("totalAmount", 181.25m));
    }

    // ---- sales-by-article

    [Fact]
    public async Task AC_sales_by_article_gives_quantity_and_net_revenue_per_article_with_its_category()
    {
        var seed = await ReportSeed.GetAsync(app);

        var body = await ReportApi.RunAsync(seed.Http, "sales-by-article", Seeded());

        Assert.Equal(3, body.Rows().Length);
        var a = body.Row(("articleCode", "A"));
        Assert.Equal(ReportSeed.AName, a.Str("articleName"));
        Assert.Equal("PIC", a.Str("categoryCode"));
        Assert.Equal("Pića", a.Str("categoryName"));
        a.AssertNums(("quantity", 19m), ("netAmount", 185m));              // 100 + 45 (10 % off) + 40
        body.Row(("articleCode", "B")).AssertNums(("quantity", 5m), ("netAmount", 100m));   // 100 + 200 - 200 (cancelled)
        var c = body.Row(("articleCode", "C"));
        Assert.Equal("Hrana", c.Str("categoryName"));
        c.AssertNums(("quantity", 30m), ("netAmount", 150m));
        Assert.Equal(54m, body.Total("quantity"));
        Assert.Equal(435m, body.Total("netAmount"));
    }

    [Fact]
    public async Task AC_sales_by_article_filters_by_category_and_by_article()
    {
        var seed = await ReportSeed.GetAsync(app);

        var drinks = await ReportApi.RunAsync(seed.Http, "sales-by-article", Seeded(null, ("categoryId", seed.Pics.ToString())));
        var food = await ReportApi.RunAsync(seed.Http, "sales-by-article", Seeded(null, ("articleId", seed.C.ToString())));

        Assert.Equal(new[] { "A", "B" }, drinks.Rows().Select(r => r.Str("articleCode")).Order(StringComparer.Ordinal));
        Assert.Equal(285m, drinks.Total("netAmount"));
        var only = Assert.Single(food.Rows());
        Assert.Equal("C", only.Str("articleCode"));
        Assert.Equal(150m, food.Total("netAmount"));
    }

    // ---- vat-output

    [Fact]
    public async Task AC_vat_output_gives_base_and_tax_per_rate_and_month()
    {
        var seed = await ReportSeed.GetAsync(app);

        var body = await ReportApi.RunAsync(seed.Http, "vat-output", Seeded("month"));

        JsonElement Rate(string period, decimal percent) => Assert.Single(body.Rows(), r => r.Str("period") == period && r.Num("taxPercent") == percent);
        Rate(Oct, 25m).AssertNums(("netAmount", 245m), ("taxAmount", 61.25m));     // RN-1 A 100 + RN-2 C 100 + A 45
        Rate(Oct, 13m).AssertNums(("netAmount", 100m), ("taxAmount", 13m));        // RN-1 B
        Rate(Nov, 25m).AssertNums(("netAmount", 90m), ("taxAmount", 22.5m));       // RN-3
        // 13 % in November: RN-4 (200, 26) and its cancellation net to nothing - the row is absent or zero.
        var cancelled = body.Rows().SingleOrDefault(r => r.Str("period") == Nov && r.Num("taxPercent") == 13m);
        if (cancelled.ValueKind == JsonValueKind.Object)
            cancelled.AssertNums(("netAmount", 0m), ("taxAmount", 0m));
        Assert.Equal(435m, body.Total("netAmount"));
        Assert.Equal(96.75m, body.Total("taxAmount"));
        Assert.Equal("percent", body.GetProperty("columns").EnumerateArray().Single(c => c.Str("key") == "taxPercent").Str("type"));
    }

    // ---- payments-received

    [Fact]
    public async Task AC_payments_received_per_month_and_payment_method()
    {
        var seed = await ReportSeed.GetAsync(app);

        var body = await ReportApi.RunAsync(seed.Http, "payments-received", Seeded("month"));

        Assert.Equal(3, body.Rows().Length);
        var cash = body.Row(("period", Oct), ("paymentMethodCode", "GOT"));
        Assert.Equal("Gotovina", cash.Str("paymentMethodName"));
        cash.AssertNums(("paymentCount", 1), ("amount", 100m));
        body.Row(("period", Nov), ("paymentMethodCode", "TR")).AssertNums(("paymentCount", 1), ("amount", 81.25m));
        body.Row(("period", Nov), ("paymentMethodCode", "KAR")).AssertNums(("paymentCount", 1), ("amount", 112.5m));
        Assert.Equal(3m, body.Total("paymentCount"));
        Assert.Equal(293.75m, body.Total("amount"));
    }

    [Fact]
    public async Task AC_payments_received_respects_the_period_and_groups_by_year()
    {
        var seed = await ReportSeed.GetAsync(app);

        var november = await ReportApi.RunAsync(seed.Http, "payments-received", ReportApi.Period("2026-11-01", "2026-11-05", "month"));
        var year = await ReportApi.RunAsync(seed.Http, "payments-received", Seeded("year"));

        Assert.Equal("TR", Assert.Single(november.Rows()).Str("paymentMethodCode"));
        Assert.Equal(81.25m, november.Total("amount"));
        Assert.Equal(3, year.Rows().Length);
        Assert.All(year.Rows(), r => Assert.Equal("2026-01-01", r.Str("period")));
        Assert.Equal(293.75m, year.Total("amount"));
    }

    // ---- unpaid-invoices

    [Fact]
    public async Task AC_unpaid_invoices_lists_what_is_still_open_with_days_overdue()
    {
        var seed = await ReportSeed.GetAsync(app);

        var body = await ReportApi.RunAsync(seed.Http, "unpaid-invoices");

        // RN-3 is paid, RN-4 is cancelled and RN-5 is the cancelling invoice: none of them is owed.
        Assert.Equal(new[] { "RN-000001", "RN-000002" }, body.Rows().Select(r => r.Str("number")).Order(StringComparer.Ordinal));
        var first = body.Row(("number", "RN-000001"));
        Assert.Equal("KUP1", first.Str("customerCode"));
        Assert.Equal("2026-10-12", first.Str("issueDate"));
        Assert.Equal("2026-10-27", first.Str("dueDate"));
        first.AssertNums(("totalAmount", 238m), ("paidAmount", 100m), ("openAmount", 138m));
        Assert.InRange(first.Num("daysOverdue"), DaysSince("2026-10-27") - 1, DaysSince("2026-10-27") + 1);
        var second = body.Row(("number", "RN-000002"));
        Assert.Equal(ReportSeed.Kup2Text, second.Str("customerName"));
        second.AssertNums(("totalAmount", 181.25m), ("paidAmount", 81.25m), ("openAmount", 100m));
        Assert.InRange(second.Num("daysOverdue"), DaysSince("2026-11-04") - 1, DaysSince("2026-11-04") + 1);
        Assert.Equal(419.25m, body.Total("totalAmount"));
        Assert.Equal(181.25m, body.Total("paidAmount"));
        Assert.Equal(238m, body.Total("openAmount"));
    }

    [Fact]
    public async Task AC_unpaid_invoices_filters_by_customer()
    {
        var seed = await ReportSeed.GetAsync(app);

        var body = await ReportApi.RunAsync(seed.Http, "unpaid-invoices", ("partnerId", seed.Kup2.ToString()));

        Assert.Equal("RN-000002", Assert.Single(body.Rows()).Str("number"));
        Assert.Equal(100m, body.Total("openAmount"));
    }

    [Fact]
    public async Task AC_an_invoice_paid_in_full_leaves_unpaid_invoices()
    {
        var seed = await ReportSeed.BuildAsync(app);   // its own tenant: this test pays an invoice
        var invoices = await seed.Http.GetAsync($"{Invoices.Path}/by-number/RN-000002");
        var id = (await HttpAssert.JsonAsync(invoices, System.Net.HttpStatusCode.OK)).Id();
        using var pay = await seed.Http.PostAsync($"{Invoices.Path}/{id}/payments", JsonContent.Create(new { amount = 100m, paymentDate = "2026-11-20" }));
        await HttpAssert.JsonAsync(pay, System.Net.HttpStatusCode.OK);

        var body = await ReportApi.RunAsync(seed.Http, "unpaid-invoices");

        Assert.Equal("RN-000001", Assert.Single(body.Rows()).Str("number"));
        Assert.Equal(138m, body.Total("openAmount"));
    }

    // ---- stock-on-hand

    [Fact]
    public async Task AC_stock_on_hand_gives_the_quantity_per_article_and_warehouse()
    {
        var seed = await ReportSeed.GetAsync(app);

        var body = await ReportApi.RunAsync(seed.Http, "stock-on-hand");

        Assert.Equal(4, body.Rows().Length);
        var aW1 = body.Row(("articleCode", "A"), ("warehouseCode", "W1"));
        Assert.Equal("Glavno skladište", aW1.Str("warehouseName"));
        Assert.Equal(ReportSeed.AName, aW1.Str("articleName"));
        aW1.AssertNums(("quantity", 35m));                                     // 60 received - 20 delivered - 5 for the work order
        body.Row(("articleCode", "A"), ("warehouseCode", "W2")).AssertNums(("quantity", 10m));
        body.Row(("articleCode", "B"), ("warehouseCode", "W1")).AssertNums(("quantity", 35m));   // 50 - 5 issued - 10 for the work order
        body.Row(("articleCode", "C"), ("warehouseCode", "W1")).AssertNums(("quantity", 210m));  // 200 received + 10 produced
        Assert.Equal(290m, body.Total("quantity"));
    }

    [Fact]
    public async Task AC_stock_on_hand_filters_by_warehouse_and_article()
    {
        var seed = await ReportSeed.GetAsync(app);

        var w2 = await ReportApi.RunAsync(seed.Http, "stock-on-hand", ("warehouseId", seed.W2.ToString()));
        var a = await ReportApi.RunAsync(seed.Http, "stock-on-hand", ("articleId", seed.A.ToString()));
        var category = await ReportApi.RunAsync(seed.Http, "stock-on-hand", ("categoryId", seed.Food.ToString()));

        Assert.Equal("A", Assert.Single(w2.Rows()).Str("articleCode"));
        Assert.Equal(10m, w2.Total("quantity"));
        Assert.Equal(2, a.Rows().Length);
        Assert.Equal(45m, a.Total("quantity"));
        Assert.Equal("C", Assert.Single(category.Rows()).Str("articleCode"));
    }

    // ---- stock-movements

    [Fact]
    public async Task AC_stock_movements_gives_opening_in_out_and_closing_for_october()
    {
        var seed = await ReportSeed.GetAsync(app);

        var body = await ReportApi.RunAsync(seed.Http, "stock-movements", ReportApi.Period(ReportSeed.From, "2026-10-31"));

        Assert.Equal(3, body.Rows().Length);
        body.Row(("articleCode", "A")).AssertNums(("openingQuantity", 0m), ("inQuantity", 70m), ("outQuantity", 20m), ("closingQuantity", 50m));
        body.Row(("articleCode", "B")).AssertNums(("openingQuantity", 0m), ("inQuantity", 50m), ("outQuantity", 0m), ("closingQuantity", 50m));
        body.Row(("articleCode", "C")).AssertNums(("openingQuantity", 0m), ("inQuantity", 200m), ("outQuantity", 0m), ("closingQuantity", 200m));
        Assert.Equal(0m, body.Total("openingQuantity"));
        Assert.Equal(320m, body.Total("inQuantity"));
        Assert.Equal(20m, body.Total("outQuantity"));
        Assert.Equal(300m, body.Total("closingQuantity"));
    }

    [Fact]
    public async Task AC_stock_movements_for_november_opens_where_october_closed_and_closes_at_stock_on_hand()
    {
        var seed = await ReportSeed.GetAsync(app);

        var body = await ReportApi.RunAsync(seed.Http, "stock-movements", ReportApi.Period(Nov, "2026-11-30"));

        body.Row(("articleCode", "A")).AssertNums(("openingQuantity", 50m), ("inQuantity", 0m), ("outQuantity", 5m), ("closingQuantity", 45m));
        body.Row(("articleCode", "B")).AssertNums(("openingQuantity", 50m), ("inQuantity", 0m), ("outQuantity", 15m), ("closingQuantity", 35m));
        body.Row(("articleCode", "C")).AssertNums(("openingQuantity", 200m), ("inQuantity", 10m), ("outQuantity", 0m), ("closingQuantity", 210m));
        Assert.Equal(300m, body.Total("openingQuantity"));
        Assert.Equal(10m, body.Total("inQuantity"));
        Assert.Equal(20m, body.Total("outQuantity"));
        var onHand = await ReportApi.RunAsync(seed.Http, "stock-on-hand");
        Assert.Equal(onHand.Total("quantity"), body.Total("closingQuantity"));
    }

    [Fact]
    public async Task AC_stock_movements_over_the_whole_seed_and_per_warehouse_or_category()
    {
        var seed = await ReportSeed.GetAsync(app);

        var whole = await ReportApi.RunAsync(seed.Http, "stock-movements", Seeded());
        var w2 = await ReportApi.RunAsync(seed.Http, "stock-movements", Seeded(null, ("warehouseId", seed.W2.ToString())));
        var food = await ReportApi.RunAsync(seed.Http, "stock-movements", Seeded(null, ("categoryId", seed.Food.ToString())));

        whole.Row(("articleCode", "A")).AssertNums(("openingQuantity", 0m), ("inQuantity", 70m), ("outQuantity", 25m), ("closingQuantity", 45m));
        w2.Row(("articleCode", "A")).AssertNums(("openingQuantity", 0m), ("inQuantity", 10m), ("outQuantity", 0m), ("closingQuantity", 10m));
        Assert.Equal(10m, w2.Total("closingQuantity"));
        food.Row(("articleCode", "C")).AssertNums(("inQuantity", 210m), ("closingQuantity", 210m));
        Assert.Null(food.RowOrNull(("articleCode", "A")));
    }

    // ---- slow-movers (its own tenant: "no outgoing movement for N days" depends on today)

    private sealed record SlowSetup(StockSetup Stock, Guid Z, Guid E);

    private async Task<SlowSetup> SlowAsync()
    {
        var s = await Stock.SetupAsync(app);
        var z = (await Art.CreateAsync(s.Http, "Z", "Žaba", s.UnitId)).Id();
        var e = (await Art.CreateAsync(s.Http, "E", "Ćevapi", s.UnitId)).Id();
        foreach (var (article, quantity) in new[] { (s.A, 100m), (s.B, 10m), (z, 5m), (e, 3m) })
            await Stock.PostDocumentAsync(s.Http, (await Stock.CreateAsync(s.Http, Stock.Draft("receipt", s.W1, (article, quantity)).With("documentDate", "2026-01-02"))).Id());
        await Out(s, s.B, 2m, "2026-01-15");                       // old: B is slow
        await Out(s, e, 3m, "2026-01-10");                         // sold out: E has no stock, so it is not listed
        await Out(s, s.A, 1m, DateTime.UtcNow.ToString("yyyy-MM-dd"));   // today: A is not slow
        return new SlowSetup(s, z, e);
    }

    private static async Task Out(StockSetup s, Guid article, decimal quantity, string date) =>
        await Stock.PostDocumentAsync(s.Http, (await Stock.CreateAsync(s.Http, Stock.Draft("issue", s.W1, (article, quantity)).With("documentDate", date))).Id());

    [Fact]
    public async Task AC_slow_movers_lists_articles_with_stock_and_no_recent_outgoing_movement()
    {
        var s = await SlowAsync();

        var body = await ReportApi.RunAsync(s.Stock.Http, "slow-movers", ("days", "30"));

        Assert.Equal(new[] { "B", "Z" }, body.Rows().Select(r => r.Str("articleCode")).Order(StringComparer.Ordinal));
        var b = body.Row(("articleCode", "B"));
        b.AssertNums(("quantity", 8m));
        Assert.Equal("2026-01-15", b.Str("lastOutDate"));
        Assert.InRange(b.Num("daysSinceLastOut"), DaysSince("2026-01-15") - 1, DaysSince("2026-01-15") + 1);
        var z = body.Row(("articleCode", "Z"));
        z.AssertNums(("quantity", 5m));
        Assert.Equal(JsonValueKind.Null, z.GetProperty("lastOutDate").ValueKind);
        Assert.Equal(JsonValueKind.Null, z.GetProperty("daysSinceLastOut").ValueKind);
    }

    [Fact]
    public async Task AC_slow_movers_days_defaults_to_90_and_a_long_enough_window_leaves_out_the_moved_article()
    {
        var s = await SlowAsync();

        var byDefault = await ReportApi.RunAsync(s.Stock.Http, "slow-movers");
        var window = await ReportApi.RunAsync(s.Stock.Http, "slow-movers", ("days", "36500"));

        Assert.NotNull(byDefault.RowOrNull(("articleCode", "B")));      // 2026-01-15 is more than 90 days before any date this test can run
        Assert.Null(byDefault.RowOrNull(("articleCode", "A")));
        Assert.Null(byDefault.RowOrNull(("articleCode", "E")));
        Assert.Null(window.RowOrNull(("articleCode", "B")));
    }

    // ---- open-sales-orders

    [Fact]
    public async Task AC_open_sales_orders_lists_what_is_promised_and_not_delivered()
    {
        var seed = await ReportSeed.GetAsync(app);

        var body = await ReportApi.RunAsync(seed.Http, "open-sales-orders");

        // SO-1: A fully delivered (left out), B open. SO-2: C and A open. SO-3 is a draft.
        Assert.Equal(3, body.Rows().Length);
        var b = body.Row(("number", "SO-000001"), ("articleCode", "B"));
        Assert.Equal("KUP1", b.Str("customerCode"));
        Assert.Equal("2026-10-01", b.Str("orderDate"));
        b.AssertNums(("orderedQuantity", 10m), ("deliveredQuantity", 0m), ("outstandingQuantity", 10m));
        var c = body.Row(("number", "SO-000002"), ("articleCode", "C"));
        Assert.Equal(ReportSeed.Kup2Text, c.Str("customerName"));
        c.AssertNums(("orderedQuantity", 30m), ("deliveredQuantity", 0m), ("outstandingQuantity", 30m));
        body.Row(("number", "SO-000002"), ("articleCode", "A")).AssertNums(("orderedQuantity", 5m), ("outstandingQuantity", 5m));
        Assert.Null(body.RowOrNull(("number", "SO-000001"), ("articleCode", "A")));
        Assert.Equal(45m, body.Total("orderedQuantity"));
        Assert.Equal(0m, body.Total("deliveredQuantity"));
        Assert.Equal(45m, body.Total("outstandingQuantity"));
    }

    [Fact]
    public async Task AC_open_sales_orders_filters_by_customer_and_article()
    {
        var seed = await ReportSeed.GetAsync(app);

        var kup1 = await ReportApi.RunAsync(seed.Http, "open-sales-orders", ("partnerId", seed.Kup1.ToString()));
        var a = await ReportApi.RunAsync(seed.Http, "open-sales-orders", ("articleId", seed.A.ToString()));

        Assert.Equal("SO-000001", Assert.Single(kup1.Rows()).Str("number"));
        var row = Assert.Single(a.Rows());
        Assert.Equal(("SO-000002", "A"), (row.Str("number"), row.Str("articleCode")));
    }

    // ---- open-purchase-orders

    [Fact]
    public async Task AC_open_purchase_orders_lists_what_is_ordered_and_not_received()
    {
        var seed = await ReportSeed.GetAsync(app);

        var body = await ReportApi.RunAsync(seed.Http, "open-purchase-orders");

        // PO-1: A 60 of 100 received (40 open), B fully received. PO-2 fully received. PO-3: B 20 open. PO-4 is a draft.
        Assert.Equal(2, body.Rows().Length);
        var a = body.Row(("number", "PO-000001"), ("articleCode", "A"));
        Assert.Equal("DOB1", a.Str("supplierCode"));
        Assert.Equal(ReportSeed.Dob1Name, a.Str("supplierName"));
        a.AssertNums(("orderedQuantity", 100m), ("receivedQuantity", 60m), ("outstandingQuantity", 40m));
        body.Row(("number", "PO-000003"), ("articleCode", "B")).AssertNums(("orderedQuantity", 20m), ("receivedQuantity", 0m), ("outstandingQuantity", 20m));
        Assert.Equal(120m, body.Total("orderedQuantity"));
        Assert.Equal(60m, body.Total("receivedQuantity"));
        Assert.Equal(60m, body.Total("outstandingQuantity"));
    }

    [Fact]
    public async Task AC_open_purchase_orders_filters_by_supplier()
    {
        var seed = await ReportSeed.GetAsync(app);

        var dob1 = await ReportApi.RunAsync(seed.Http, "open-purchase-orders", ("partnerId", seed.Dob1.ToString()));
        var dob2 = await ReportApi.RunAsync(seed.Http, "open-purchase-orders", ("partnerId", seed.Dob2.ToString()));

        Assert.Equal("PO-000001", Assert.Single(dob1.Rows()).Str("number"));
        Assert.Equal("PO-000003", Assert.Single(dob2.Rows()).Str("number"));
    }

    // ---- purchases-by-supplier

    [Fact]
    public async Task AC_purchases_by_supplier_adds_up_the_received_goods_per_supplier()
    {
        var seed = await ReportSeed.GetAsync(app);

        var body = await ReportApi.RunAsync(seed.Http, "purchases-by-supplier", Seeded());

        // The receipt of the work order has no supplier and is not a purchase.
        Assert.Equal(2, body.Rows().Length);
        var dob1 = body.Row(("supplierCode", "DOB1"));
        Assert.Equal(ReportSeed.Dob1Name, dob1.Str("supplierName"));
        dob1.AssertNums(("receiptCount", 1), ("quantity", 110m));        // A 60 + B 50
        body.Row(("supplierCode", "DOB2")).AssertNums(("receiptCount", 2), ("quantity", 210m));   // C 200 + A 10
        Assert.Equal(3m, body.Total("receiptCount"));
        Assert.Equal(320m, body.Total("quantity"));
    }

    [Fact]
    public async Task AC_purchases_by_supplier_respects_the_period_and_filters_by_supplier()
    {
        var seed = await ReportSeed.GetAsync(app);

        var early = await ReportApi.RunAsync(seed.Http, "purchases-by-supplier", ReportApi.Period(ReportSeed.From, "2026-10-04"));
        var day = await ReportApi.RunAsync(seed.Http, "purchases-by-supplier", ReportApi.Period("2026-10-05", "2026-10-05"));
        var one = await ReportApi.RunAsync(seed.Http, "purchases-by-supplier", Seeded(null, ("partnerId", seed.Dob2.ToString())));

        Assert.Equal("DOB1", Assert.Single(early.Rows()).Str("supplierCode"));
        Assert.Equal(110m, early.Total("quantity"));
        day.Row(("supplierCode", "DOB2")).AssertNums(("receiptCount", 1), ("quantity", 200m));
        Assert.Equal("DOB2", Assert.Single(one.Rows()).Str("supplierCode"));
        Assert.Equal(210m, one.Total("quantity"));
    }

    // ---- production-output

    [Fact]
    public async Task AC_production_output_lists_completed_work_orders_per_product_and_period()
    {
        var seed = await ReportSeed.GetAsync(app);

        var body = await ReportApi.RunAsync(seed.Http, "production-output", Seeded("month"));

        // The draft work order of 99 was never completed.
        var row = Assert.Single(body.Rows());
        Assert.Equal(Nov, row.Str("period"));
        Assert.Equal("C", row.Str("articleCode"));
        Assert.Equal(ReportSeed.CName, row.Str("articleName"));
        row.AssertNums(("workOrderCount", 1), ("quantity", 10m));
        Assert.Equal(1m, body.Total("workOrderCount"));
        Assert.Equal(10m, body.Total("quantity"));
    }

    [Fact]
    public async Task AC_production_output_respects_the_period_and_the_product_filter()
    {
        var seed = await ReportSeed.GetAsync(app);

        var october = await ReportApi.RunAsync(seed.Http, "production-output", ReportApi.Period(ReportSeed.From, "2026-10-31", "month"));
        var other = await ReportApi.RunAsync(seed.Http, "production-output", Seeded("month", ("articleId", seed.A.ToString())));
        var product = await ReportApi.RunAsync(seed.Http, "production-output", Seeded("year", ("articleId", seed.C.ToString())));

        Assert.Empty(october.Rows());
        Assert.Empty(other.Rows());
        var row = Assert.Single(product.Rows());
        Assert.Equal("2026-01-01", row.Str("period"));
        row.AssertNums(("quantity", 10m));
    }
}
