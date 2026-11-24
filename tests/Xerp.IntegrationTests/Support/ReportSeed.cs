using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Xerp.IntegrationTests.Support;

/// <summary>
/// The seed of spec RP01, section 4 - ONE tenant that every value test reads (reports are read-only, so it is built once
/// per test run). All the expected values of the tests are hand-computed from the table below.
///
/// MASTERS   unit kom; warehouses W1 "Glavno skladište", W2 "Pomoćno skladište"; tax rates 25 % and 13 %;
///           categories PIC "Pića" (A, B) and HRA "Hrana" (C); payment methods GOT (cash), TR (transfer), KAR (card)
///           A "Šljivovica 0,7 l"  net 10.00  25 %      B "Čaj od ružmarina"  net 20.00  13 %      C "Žitarice đurđevak"  net 5.00  25 %
///           customers KUP1 "Čakovec trgovina d.o.o.", KUP2 "Šimić &amp; sinovi; obrt "Đuro"" (a ';' and '"' in the name: CSV quoting)
///           suppliers DOB1 "Dobavljač Đurđević d.o.o.", DOB2 "Žito d.d."
///
/// PURCHASE  PO1 (DOB1, W1, 2026-10-01): A 100, B 50          receipt 2026-10-02: A 60, B 50          -> open: A 40
///           PO2 (DOB2, W1, 2026-10-01): C 200                receipt 2026-10-05: C 200               -> fully received
///           PO3 (DOB2, W1, 2026-10-03): B 20, confirmed, nothing received                            -> open: B 20
///           PO4 draft (never confirmed)
///           direct receipt 2026-10-06 from DOB2 into W2: A 10
/// SALES     SO1 (KUP1, W1, 2026-10-01): A 20, B 10           delivery 2026-10-20: A 20               -> open: B 10
///           SO2 (KUP2, W1, 2026-10-03): C 30, A 5, nothing delivered                                 -> open: C 30, A 5
///           SO3 draft (never confirmed)
/// STOCK     direct issue 2026-11-03 W1: B 5
/// PRODUCTION  BOM of C: 0.5 A + 1 B; work order WO1 completed 2026-11-15 in W1: C 10 (takes A 5, B 10); a draft work order of C 99
/// INVOICES  RN-000001 2026-10-12 KUP1 due 2026-10-27: A 10 x 10.00, B 5 x 20.00        net 200.00 tax 25.00+13.00=38.00 total 238.00
///           RN-000002 2026-10-20 KUP2 due 2026-11-04: C 20 x 5.00, A 5 x 10.00 less 10 %  net 145.00 tax 36.25 total 181.25
///           RN-000003 2026-11-05 KUP1 due 2026-11-20: A 4 x 10.00, C 10 x 5.00             net  90.00 tax 22.50 total 112.50
///           RN-000004 2026-11-10 KUP2: B 10 x 20.00  (net 200.00, tax 26.00, total 226.00) - CANCELLED on 2026-11-12 by RN-000005 (-226.00)
/// PAYMENTS  RN-1 100.00 on 2026-10-15 GOT; RN-2 81.25 on 2026-11-02 TR; RN-3 112.50 on 2026-11-08 KAR
/// </summary>
public sealed record ReportSeed(
    TestTenant Tenant,
    Guid W1, Guid W2, Guid A, Guid B, Guid C, Guid Pics, Guid Food,
    Guid Kup1, Guid Kup2, Guid Dob1, Guid Dob2,
    string Kup2Name, string[] CurrentNames)
{
    public const string From = "2026-10-01";
    public const string To = "2026-11-30";

    public const string Kup1Name = "Čakovec trgovina d.o.o.";
    public const string Dob1Name = "Dobavljač Đurđević d.o.o.";
    public const string AName = "Šljivovica 0,7 l";
    public const string BName = "Čaj od ružmarina";
    public const string CName = "Žitarice đurđevak";
    public const string Kup2Text = "Šimić & sinovi; obrt \"Đuro\"";

    public HttpClient Http => Tenant.Client;

    private static readonly ConditionalWeakTable<XerpFixture, Task<ReportSeed>> Cache = new();

    /// <summary>The seeded tenant of this test run (built on the first call, shared afterwards).</summary>
    public static Task<ReportSeed> GetAsync(XerpFixture app) => Cache.GetValue(app, a => BuildAsync(a));

    private static async Task<Guid> CreateAsync(HttpClient http, string path, object body)
    {
        using var response = await http.PostAsJsonAsync(path, body);
        return (await HttpAssert.JsonAsync(response, HttpStatusCode.Created)).Id();
    }

    private static async Task<JsonElement> PostOkAsync(HttpClient http, string path, object? body = null)
    {
        using var response = await http.PostAsJsonAsync(path, body ?? new { });
        return await HttpAssert.JsonAsync(response, HttpStatusCode.OK);
    }

    public static async Task<ReportSeed> BuildAsync(XerpFixture app)
    {
        var tenant = await app.NewTenantAsync();
        var http = tenant.Client;

        // ---- masters
        var unit = (await Uom.CreateAsync(http, "kom", "Komad")).Id();
        var w1 = (await MasterApi.Warehouses.CreateAsync(http, "W1", "Glavno skladište")).Id();
        var w2 = (await MasterApi.Warehouses.CreateAsync(http, "W2", "Pomoćno skladište")).Id();
        var vat25 = await CreateAsync(http, "/api/v1/tax-rates", new { code = "PDV25", name = "PDV 25%", rate = 25 });
        var vat13 = await CreateAsync(http, "/api/v1/tax-rates", new { code = "PDV13", name = "PDV 13%", rate = 13 });
        var pics = await CreateAsync(http, "/api/v1/article-categories", new { code = "PIC", name = "Pića" });
        var food = await CreateAsync(http, "/api/v1/article-categories", new { code = "HRA", name = "Hrana" });
        var cash = await CreateAsync(http, "/api/v1/payment-methods", new { code = "GOT", name = "Gotovina", kind = "cash" });
        var transfer = await CreateAsync(http, "/api/v1/payment-methods", new { code = "TR", name = "Transakcijski račun", kind = "transfer" });
        var card = await CreateAsync(http, "/api/v1/payment-methods", new { code = "KAR", name = "Kartica", kind = "card" });

        var a = (await Art.CreateAsync(http, "A", AName, unit)).Id();
        var b = (await Art.CreateAsync(http, "B", BName, unit)).Id();
        var c = (await Art.CreateAsync(http, "C", CName, unit)).Id();
        await SalesData.SetAsync(http, a, pics, [vat25], 12.5m);
        await SalesData.SetAsync(http, b, pics, [vat13], 22.6m);
        await SalesData.SetAsync(http, c, food, [vat25], 6.25m);

        var kup1 = (await Orders.PartnerAsync(http, "KUP1", Kup1Name, isSupplier: false, isCustomer: true)).Id();
        var kup2 = (await Orders.PartnerAsync(http, "KUP2", Kup2Text, isSupplier: false, isCustomer: true)).Id();
        var dob1 = (await Orders.PartnerAsync(http, "DOB1", Dob1Name, isSupplier: true, isCustomer: false)).Id();
        var dob2 = (await Orders.PartnerAsync(http, "DOB2", "Žito d.d.", isSupplier: true, isCustomer: false)).Id();

        // ---- purchasing: orders and receipts
        var purchase = OrderApi.Purchase;
        var po1 = await OrderAsync(http, purchase, dob1, w1, "2026-10-01", (a, 100, 5m), (b, 50, 4m));
        var po2 = await OrderAsync(http, purchase, dob2, w1, "2026-10-01", (c, 200, 2m));
        var po3 = await OrderAsync(http, purchase, dob2, w1, "2026-10-03", (b, 20, 4m));
        await purchase.CreateAsync(http, purchase.Body(dob2, w1, (a, 5m, null, 5m)).With("orderDate", "2026-10-03")); // PO4: stays a draft
        await FulfilAsync(http, purchase, w1, po1, "2026-10-02", (a, 60, 1), (b, 50, 2));
        await FulfilAsync(http, purchase, w1, po2, "2026-10-05", (c, 200, 1));
        _ = po3;
        var direct = Stock.Draft("receipt", w2, (a, 10)).With("documentDate", "2026-10-06").With("partnerId", dob2.ToString());
        await Stock.PostDocumentAsync(http, (await Stock.CreateAsync(http, direct)).Id());

        // ---- sales orders and the delivery
        var sales = OrderApi.Sales;
        var so1 = await OrderAsync(http, sales, kup1, w1, "2026-10-01", (a, 20, 10m), (b, 10, 20m));
        await OrderAsync(http, sales, kup2, w1, "2026-10-03", (c, 30, 5m), (a, 5, 10m));
        await sales.CreateAsync(http, sales.Body(kup1, w1, (a, 1m, null, 10m)).With("orderDate", "2026-10-03")); // SO3: stays a draft
        await FulfilAsync(http, sales, w1, so1, "2026-10-20", (a, 20, 1));

        // ---- a direct issue, the work order
        var issue = Stock.Draft("issue", w1, (b, 5)).With("documentDate", "2026-11-03");
        await Stock.PostDocumentAsync(http, (await Stock.CreateAsync(http, issue)).Id());
        await Production.SetBomAsync(http, c, (a, 0.5m), (b, 1m));
        var wo = await Production.CreateAsync(http, Production.Order(c, 10m, w1));
        await Production.ActionAsync(http, wo.Id(), "complete", new JsonObject { ["documentDate"] = "2026-11-15" });
        await Production.CreateAsync(http, Production.Order(c, 99m, w1)); // a draft: never produced

        // ---- invoices, in the order of their dates (backdating is off)
        var rn1 = await InvoiceAsync(http, kup1, "2026-10-12", "2026-10-27", (a, 10, 10m, null), (b, 5, 20m, null));
        var rn2 = await InvoiceAsync(http, kup2, "2026-10-20", "2026-11-04", (c, 20, 5m, null), (a, 5, 10m, 10m));
        var rn3 = await InvoiceAsync(http, kup1, "2026-11-05", "2026-11-20", (a, 4, 10m, null), (c, 10, 5m, null));
        var rn4 = await InvoiceAsync(http, kup2, "2026-11-10", null, (b, 10, 20m, null));
        await PostOkAsync(http, $"{Invoices.Path}/{rn4}/cancel", new { issueDate = "2026-11-12", note = "Pogrešan kupac" });

        await PostOkAsync(http, $"{Invoices.Path}/{rn1}/payments", new { amount = 100m, paymentDate = "2026-10-15", paymentMethodId = cash });
        await PostOkAsync(http, $"{Invoices.Path}/{rn2}/payments", new { amount = 81.25m, paymentDate = "2026-11-02", paymentMethodId = transfer });
        await PostOkAsync(http, $"{Invoices.Path}/{rn3}/payments", new { amount = 112.5m, paymentDate = "2026-11-08", paymentMethodId = card });

        return new ReportSeed(tenant, w1, w2, a, b, c, pics, food, kup1, kup2, dob1, dob2, Kup2Text,
            [AName, BName, CName, Kup1Name, Kup2Text, Dob1Name, "Žito d.d."]);
    }

    private static async Task<Guid> OrderAsync(HttpClient http, OrderApi api, Guid partner, Guid warehouse, string date, params (Guid Article, decimal Quantity, decimal Price)[] lines)
    {
        var body = api.Body(partner, warehouse, lines.Select(l => (l.Article, l.Quantity, (Guid?)null, l.Price)).ToArray()).With("orderDate", date);
        var order = await api.ConfirmAsync(http, (await api.CreateAsync(http, body)).Id());
        return order.Id();
    }

    /// <summary>A posted stock document linked to an order: (article, quantity, order line number) per line.</summary>
    private static async Task FulfilAsync(HttpClient http, OrderApi api, Guid warehouse, Guid order, string date, params (Guid Article, decimal Quantity, int Line)[] lines)
    {
        var body = api.Document(warehouse, order, lines.Select(l => (l.Article, l.Quantity, l.Line, (Guid?)null)).ToArray()).With("documentDate", date);
        await Stock.PostDocumentAsync(http, (await Stock.CreateAsync(http, body)).Id());
    }

    /// <summary>Creates and issues an invoice; a line is (article, quantity, net unit price, discount %).</summary>
    public static async Task<Guid> InvoiceAsync(HttpClient http, Guid customer, string issueDate, string? dueDate,
        params (Guid Article, decimal Quantity, decimal Price, decimal? Discount)[] lines)
    {
        var body = new JsonObject
        {
            ["customerId"] = customer.ToString(),
            ["issueDate"] = issueDate,
            ["dueDate"] = dueDate,
            ["lines"] = new JsonArray(lines.Select(l =>
            {
                var line = new JsonObject { ["articleId"] = l.Article.ToString(), ["quantity"] = l.Quantity, ["unitPrice"] = l.Price };
                if (l.Discount is { } d)
                    line["discountPercent"] = d;
                return (JsonNode)line;
            }).ToArray()),
        };
        var id = (await Invoices.CreateAsync(http, body)).Id();
        await Invoices.IssueAsync(http, id);
        return id;
    }

    /// <summary>A second, tiny tenant for the isolation test: one customer and one issued invoice of 1000.00 net + 250.00 tax in October 2026.</summary>
    public static async Task<(TestTenant Tenant, string CustomerName)> OtherTenantAsync(XerpFixture app)
    {
        var tenant = await app.NewTenantAsync();
        var http = tenant.Client;
        var unit = (await Uom.CreateAsync(http, "kom", "Komad")).Id();
        var vat25 = await CreateAsync(http, "/api/v1/tax-rates", new { code = "PDV25", name = "PDV 25%", rate = 25 });
        var article = (await Art.CreateAsync(http, "A", "Tuđi artikl", unit)).Id();
        await SalesData.SetAsync(http, article, null, [vat25], 12.5m);
        var customer = (await Orders.PartnerAsync(http, "KUP1", "Tuđi kupac d.o.o.", isSupplier: false, isCustomer: true)).Id();
        await InvoiceAsync(http, customer, "2026-10-15", null, (article, 100, 10m, null));
        return (tenant, "Tuđi kupac d.o.o.");
    }
}
