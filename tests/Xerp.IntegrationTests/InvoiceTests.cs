using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xerp.IntegrationTests.Support;

namespace Xerp.IntegrationTests;

/// <summary>Spec I01: invoices - drafts, issue, tax, cancellation, payments, stock, rules, company profile.</summary>
[Collection(XerpCollection.Name)]
public class InvoiceTests(XerpFixture app)
{
    private static decimal Dec(JsonElement e, string p) => e.GetProperty(p).GetDecimal();

    [Fact]
    public async Task A_draft_takes_tax_and_net_price_from_the_articles_and_computes_the_recapitulation()
    {
        var s = await Invoices.SetupAsync(app);
        var discounted = Invoices.Line(s.Goods, 3);
        discounted["discountPercent"] = 10;
        var draft = await Invoices.CreateAsync(s.Http, Invoices.Body(s, lines: [discounted, Invoices.Line(s.Service, 1)]));

        Assert.Equal("draft", draft.Str("status"));
        Assert.Equal(JsonValueKind.Null, draft.GetProperty("number").ValueKind);
        var lines = draft.GetProperty("lines");
        Assert.Equal(10m, Dec(lines[0], "unitPrice"));      // 12.50 / 1.25
        Assert.Equal(27m, Dec(lines[0], "netAmount"));      // 3 × 10 less 10 %
        Assert.Equal(100m, Dec(lines[1], "unitPrice"));     // 113 / 1.13
        Assert.Equal("Goods", lines[0].Str("description"));
        Assert.Equal(127m, Dec(draft, "netAmount"));
        Assert.Equal(6.75m + 13m, Dec(draft, "taxAmount"));
        Assert.Equal(146.75m, Dec(draft, "totalAmount"));
        var taxes = draft.GetProperty("taxes");
        Assert.Equal(2, taxes.GetArrayLength());
        Assert.Equal(25m, Dec(taxes[0], "taxPercent"));
        Assert.Equal(6.75m, Dec(taxes[0], "taxAmount"));
        Assert.Equal("none", draft.Str("paymentStatus"));
    }

    [Fact]
    public async Task Issue_numbers_without_gaps_and_freezes_the_invoice()
    {
        var s = await Invoices.SetupAsync(app);
        var first = await Invoices.CreateAsync(s.Http, Invoices.Body(s));
        var second = await Invoices.CreateAsync(s.Http, Invoices.Body(s));

        var issued = await Invoices.IssueAsync(s.Http, second.GetProperty("id").GetGuid());
        var issuedFirst = await Invoices.IssueAsync(s.Http, first.GetProperty("id").GetGuid());

        Assert.Equal("RN-000001", issued.Str("number"));
        Assert.Equal("RN-000002", issuedFirst.Str("number"));
        Assert.Equal("unpaid", issued.Str("paymentStatus"));
        using var change = await s.Http.PutAsJsonAsync($"{Invoices.Path}/{issued.GetProperty("id").GetGuid()}", Invoices.Body(s));
        await HttpAssert.ProblemAsync(change, HttpStatusCode.Conflict, "INVALID_STATE");
        using var delete = await s.Http.DeleteAsync($"{Invoices.Path}/{issued.GetProperty("id").GetGuid()}");
        await HttpAssert.ProblemAsync(delete, HttpStatusCode.Conflict, "INVALID_STATE");
        using var byNumber = await s.Http.GetAsync($"{Invoices.Path}/by-number/rn-000002");
        Assert.Equal(first.GetProperty("id").GetGuid(), (await HttpAssert.JsonAsync(byNumber, HttpStatusCode.OK)).GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task Customer_rule_refuses_an_issue_without_a_customer_unless_switched_off()
    {
        var s = await Invoices.SetupAsync(app);
        var body = Invoices.Body(s);
        body["customerId"] = null;
        var draft = await Invoices.CreateAsync(s.Http, body);

        using var refused = await Invoices.SendIssueAsync(s.Http, draft.GetProperty("id").GetGuid());
        var problem = await HttpAssert.ProblemAsync(refused, HttpStatusCode.BadRequest, "VALIDATION_FAILED");
        Assert.Equal(Invoices.CustomerRule, problem.GetProperty("rules")[0].Str("key"));

        await Rules.SetAsync(s.Http, Invoices.CustomerRule, false);
        await Invoices.IssueAsync(s.Http, draft.GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task Backdating_is_refused_by_default()
    {
        var s = await Invoices.SetupAsync(app);
        var march = await Invoices.CreateAsync(s.Http, Invoices.Body(s, "2026-03-10"));
        await Invoices.IssueAsync(s.Http, march.GetProperty("id").GetGuid());
        var february = await Invoices.CreateAsync(s.Http, Invoices.Body(s, "2026-02-01"));

        using var refused = await Invoices.SendIssueAsync(s.Http, february.GetProperty("id").GetGuid());
        var problem = await HttpAssert.ValidationAsync(refused, "issueDate");
        Assert.Equal(Invoices.BackdatingRule, problem.GetProperty("rules")[0].Str("key"));

        await Rules.SetAsync(s.Http, Invoices.BackdatingRule, true);
        await Invoices.IssueAsync(s.Http, february.GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task Payments_make_an_invoice_partial_then_paid_and_overpayment_is_refused()
    {
        var s = await Invoices.SetupAsync(app);
        var draft = await Invoices.CreateAsync(s.Http, Invoices.Body(s, lines: [Invoices.Line(s.Goods, 4)])); // 40 + 10 tax
        var id = draft.GetProperty("id").GetGuid();
        await Invoices.IssueAsync(s.Http, id);

        using var part = await s.Http.PostAsJsonAsync($"{Invoices.Path}/{id}/payments", new { amount = 20m, paymentDate = "2026-03-05" });
        var partial = await HttpAssert.JsonAsync(part, HttpStatusCode.OK);
        Assert.Equal(("partial", 30m), (partial.Str("paymentStatus"), Dec(partial, "openAmount")));

        using var tooMuch = await s.Http.PostAsJsonAsync($"{Invoices.Path}/{id}/payments", new { amount = 30.01m });
        await HttpAssert.ValidationAsync(tooMuch, "amount");

        using var rest = await s.Http.PostAsJsonAsync($"{Invoices.Path}/{id}/payments", new { amount = 30m });
        var paid = await HttpAssert.JsonAsync(rest, HttpStatusCode.OK);
        Assert.Equal(("paid", 0m, 50m), (paid.Str("paymentStatus"), Dec(paid, "openAmount"), Dec(paid, "paidAmount")));

        var paymentId = paid.GetProperty("payments")[0].GetProperty("id").GetGuid();
        using var removed = await s.Http.DeleteAsync($"{Invoices.Path}/{id}/payments/{paymentId}");
        Assert.Equal("partial", (await HttpAssert.JsonAsync(removed, HttpStatusCode.OK)).Str("paymentStatus"));

        using var listPartial = await s.Http.GetAsync($"{Invoices.Path}?paymentStatus=partial");
        Assert.Equal(1, (await HttpAssert.JsonAsync(listPartial, HttpStatusCode.OK)).GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task Cancel_issues_a_negated_invoice_and_marks_the_original()
    {
        var s = await Invoices.SetupAsync(app);
        var draft = await Invoices.CreateAsync(s.Http, Invoices.Body(s));
        var id = draft.GetProperty("id").GetGuid();
        await Invoices.IssueAsync(s.Http, id);

        using var cancel = await s.Http.PostAsJsonAsync($"{Invoices.Path}/{id}/cancel", new { note = "Wrong customer" });
        var cancelling = await HttpAssert.JsonAsync(cancel, HttpStatusCode.OK);

        Assert.Equal(("issued", "RN-000002"), (cancelling.Str("status"), cancelling.Str("number")));
        Assert.Equal(-Dec(draft, "totalAmount"), Dec(cancelling, "totalAmount"));
        Assert.Equal(id, cancelling.GetProperty("cancellationOf").GetProperty("id").GetGuid());
        using var get = await s.Http.GetAsync($"{Invoices.Path}/{id}");
        var original = await HttpAssert.JsonAsync(get, HttpStatusCode.OK);
        Assert.Equal("cancelled", original.Str("status"));
        Assert.Equal("RN-000002", original.GetProperty("cancelledBy").Str("number"));

        using var again = await s.Http.PostAsync($"{Invoices.Path}/{cancelling.GetProperty("id").GetGuid()}/cancel", null);
        await HttpAssert.ProblemAsync(again, HttpStatusCode.Conflict, "INVALID_STATE");
    }

    [Fact]
    public async Task With_the_stock_rule_the_issue_takes_goods_out_and_the_cancel_puts_them_back()
    {
        var s = await Invoices.SetupAsync(app);
        await Rules.SetAsync(s.Http, Invoices.StockRule, true);
        var draft = await Invoices.CreateAsync(s.Http, Invoices.Body(s));
        var id = draft.GetProperty("id").GetGuid();

        using var short_ = await Invoices.SendIssueAsync(s.Http, id);
        var problem = await HttpAssert.ProblemAsync(short_, HttpStatusCode.Conflict, "INSUFFICIENT_STOCK");
        Assert.True(problem.GetProperty("errors").TryGetProperty("lines[0].quantity", out _));
        using var stillDraft = await s.Http.GetAsync($"{Invoices.Path}/{id}");
        Assert.Equal("draft", (await HttpAssert.JsonAsync(stillDraft, HttpStatusCode.OK)).Str("status"));

        await Invoices.ReceiveAsync(s.Http, s.Goods, 5);
        var issued = await Invoices.IssueAsync(s.Http, id);
        Assert.Equal("RN-000001", issued.Str("number"));   // the refused attempt took no number
        var documentId = issued.GetProperty("stockDocument").GetProperty("id").GetGuid();
        Assert.Equal(2m, await OnHandAsync(s));

        using var reverseAlone = await s.Http.PostAsJsonAsync($"/api/v1/stock-documents/{documentId}/reverse", new { documentDate = "2026-03-02" });
        await HttpAssert.ProblemAsync(reverseAlone, HttpStatusCode.Conflict, "INVALID_STATE");

        using var cancel = await s.Http.PostAsync($"{Invoices.Path}/{id}/cancel", null);
        await HttpAssert.JsonAsync(cancel, HttpStatusCode.OK);
        Assert.Equal(5m, await OnHandAsync(s));
    }

    [Fact]
    public async Task An_article_on_an_invoice_cannot_be_deleted_and_a_line_without_tax_rate_is_refused()
    {
        var s = await Invoices.SetupAsync(app);
        await Invoices.CreateAsync(s.Http, Invoices.Body(s));
        using var delete = await s.Http.DeleteAsync($"/api/v1/articles/{s.Goods}");
        await HttpAssert.ProblemAsync(delete, HttpStatusCode.Conflict, "IN_USE");

        var unit = await Invoices.PostAsync(s.Http, "/api/v1/units-of-measure", new { code = "h", name = "Hour" });
        var bare = await Invoices.PostAsync(s.Http, "/api/v1/articles", new { code = "B1", name = "Bare", type = "service", baseUnitId = unit });
        using var refused = await s.Http.PostAsJsonAsync(Invoices.Path, Invoices.Body(s, lines: [Invoices.Line(bare, 1)]));
        await HttpAssert.ValidationAsync(refused, "lines[0].taxRateId");
    }

    [Fact]
    public async Task Invoices_of_one_tenant_are_invisible_to_another()
    {
        var s = await Invoices.SetupAsync(app);
        var draft = await Invoices.CreateAsync(s.Http, Invoices.Body(s));
        var other = await app.NewTenantAsync();
        using var get = await other.Client.GetAsync($"{Invoices.Path}/{draft.GetProperty("id").GetGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
        using var list = await other.Client.GetAsync(Invoices.Path);
        Assert.Equal(0, (await HttpAssert.JsonAsync(list, HttpStatusCode.OK)).GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task The_company_profile_starts_with_the_tenant_name_and_is_saved_whole()
    {
        var tenant = await app.NewTenantAsync();
        using var empty = await tenant.Client.GetAsync("/api/v1/company");
        Assert.Equal(tenant.Name, (await HttpAssert.JsonAsync(empty, HttpStatusCode.OK)).Str("legalName"));

        using var set = await tenant.Client.PutAsJsonAsync("/api/v1/company",
            new { legalName = "Vulco d.o.o.", taxId = "12345678903", city = "Split", countryCode = "HR", iban = "hr12 3456 7890 1234 5678 9" });
        var saved = await HttpAssert.JsonAsync(set, HttpStatusCode.OK);
        Assert.Equal(("Vulco d.o.o.", "HR1234567890123456789", "Split"), (saved.Str("legalName"), saved.Str("iban"), saved.Str("city")));

        using var invalid = await tenant.Client.PutAsJsonAsync("/api/v1/company", new { legalName = "", countryCode = "Croatia" });
        await HttpAssert.ValidationAsync(invalid, "legalName", "countryCode");
    }

    private static async Task<decimal> OnHandAsync(Invoices.Setup s)
    {
        using var response = await s.Http.GetAsync($"/api/v1/stock-on-hand?articleId={s.Goods}");
        var items = (await HttpAssert.JsonAsync(response, HttpStatusCode.OK)).GetProperty("items");
        return items.GetArrayLength() == 0 ? 0 : items[0].GetProperty("quantity").GetDecimal();
    }
}
