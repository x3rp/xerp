using System.Net;
using System.Text.Json;
using Xerp.IntegrationTests.Support;

namespace Xerp.IntegrationTests;

/// <summary>
/// Spec P01, part B, AC-60 to AC-66: barcodes of articles (R16–R21) and the rule <c>article.barcodeUnique</c> —
/// by default a barcode belongs to one article; an article never lists a barcode twice, whatever the rule.
/// </summary>
[Collection(XerpCollection.Name)]
public class ArticleBarcodeTests(XerpFixture app)
{
    private const string Ean = "3850123456789";

    // ---- AC-60 ----

    [Fact]
    public async Task AC60_Create_get_list_and_delete_a_barcode()
    {
        var s = await SalesSetup.NewAsync(app);
        var espresso = await s.ArticleIdAsync("ESP", "Espresso");
        var tea = await s.ArticleIdAsync("TEA");

        using var response = await Barcodes.PostAsync(s.Http, espresso, Ean);
        var created = await HttpAssert.JsonAsync(response, HttpStatusCode.Created);

        Assert.EndsWith($"{Barcodes.Path}/{created.Id()}", response.Headers.Location?.ToString());
        Assert.Equal(new[] { "article", "barcode", "createdAt", "createdBy", "id" }, created.PropertyNames());
        Assert.Equal(Ean, created.Str("barcode"));
        var summary = created.GetProperty("article");
        Assert.Equal(new[] { "code", "id", "name" }, summary.PropertyNames());
        Assert.Equal((espresso, "ESP", "Espresso"), (summary.Id(), summary.Str("code"), summary.Str("name")));
        Assert.Equal(JsonValueKind.String, created.GetProperty("createdAt").ValueKind);
        Assert.Equal(s.Tenant.ApiKeyId, created.GetProperty("createdBy").GetGuid());
        using (var read = await s.Http.GetAsync($"{Barcodes.Path}/{created.Id()}"))
            McpAssert.JsonEqual(created, await HttpAssert.JsonAsync(read, HttpStatusCode.OK));

        var ofTea = await Barcodes.CreateAsync(s.Http, tea, "111");
        await Barcodes.CreateAsync(s.Http, espresso, "222");
        Assert.Equal(new[] { "222", Ean }, (await Barcodes.ListAsync(s.Http, $"?articleId={espresso}")).Items().Select(i => i.Str("barcode")));
        McpAssert.JsonEqual(created, Assert.Single((await Barcodes.LookupAsync(s.Http, Ean)).Items()));
        Assert.Equal(0, (await Barcodes.ListAsync(s.Http, $"?articleId={tea}&barcode={Ean}")).Total());
        McpAssert.JsonEqual(ofTea, Assert.Single((await Barcodes.ListAsync(s.Http, $"?articleId={tea}&barcode=111")).Items()));

        using (var deleted = await Barcodes.DeleteAsync(s.Http, created.Id()))
            Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        using (var gone = await s.Http.GetAsync($"{Barcodes.Path}/{created.Id()}"))
            await HttpAssert.NotFoundAsync(gone);
        using (var again = await Barcodes.DeleteAsync(s.Http, created.Id()))
            await HttpAssert.NotFoundAsync(again);
        Assert.Equal(0, (await Barcodes.LookupAsync(s.Http, Ean)).Total());
        Assert.Equal(2, (await Barcodes.ListAsync(s.Http)).Total());
    }

    // ---- AC-61 ----

    [Fact]
    public async Task AC61_E8_R17_A_barcode_is_trimmed_and_otherwise_stored_as_sent()
    {
        var s = await SalesSetup.NewAsync(app);
        var article = await s.ArticleIdAsync("ESP");

        Assert.Equal("123", (await Barcodes.CreateAsync(s.Http, article, " 123 ")).Str("barcode"));
        // Case-sensitive, leading zeros kept, 50 characters allowed.
        Assert.Equal("abc", (await Barcodes.CreateAsync(s.Http, article, "abc")).Str("barcode"));
        Assert.Equal("ABC", (await Barcodes.CreateAsync(s.Http, article, "ABC")).Str("barcode"));
        Assert.Equal("00123", (await Barcodes.CreateAsync(s.Http, article, "00123")).Str("barcode"));
        Assert.Equal(50, (await Barcodes.CreateAsync(s.Http, article, new string('7', 50))).Str("barcode").Length);

        Assert.Equal(5, (await Barcodes.ListAsync(s.Http, $"?articleId={article}")).Total());
        Assert.Equal(1, (await Barcodes.LookupAsync(s.Http, "123")).Total());
    }

    public static TheoryData<string, string, string> BadBarcodes() => new()
    {
        { "empty", """{ "articleId": "$a", "barcode": "" }""", "barcode" },
        { "only spaces", """{ "articleId": "$a", "barcode": "   " }""", "barcode" },
        { "a space inside", """{ "articleId": "$a", "barcode": "12 3" }""", "barcode" },
        { "51 characters", $$"""{ "articleId": "$a", "barcode": "{{new string('7', 51)}}" }""", "barcode" },
        { "not ASCII", """{ "articleId": "$a", "barcode": "café" }""", "barcode" },
        { "no barcode", """{ "articleId": "$a" }""", "barcode" },
        { "barcode not a string", """{ "articleId": "$a", "barcode": 123 }""", "barcode" },
        { "no articleId", """{ "barcode": "123" }""", "articleId" },
        { "articleId no id", """{ "articleId": "x", "barcode": "123" }""", "articleId" },
    };

    [Theory]
    [MemberData(nameof(BadBarcodes))]
    public async Task AC61_A_malformed_barcode_or_article_id_is_refused_with_its_key(string what, string json, string key)
    {
        var s = await SalesSetup.NewAsync(app);
        var article = await s.ArticleIdAsync("ESP");

        await MasterRules.InvariantAsync(
            s.Http.PostAsync(Barcodes.Path, HttpAssert.Raw(json.Replace("$a", article.ToString(), StringComparison.Ordinal))), key);

        Assert.True((await Barcodes.ListAsync(s.Http)).Total() == 0, $"{what}: a barcode was stored.");
    }

    // ---- AC-62 ----

    [Fact]
    public async Task AC62_An_unknown_article_is_REFERENCE_NOT_FOUND_and_an_inactive_one_REFERENCE_INACTIVE()
    {
        var s = await SalesSetup.NewAsync(app);
        var inactive = await s.ArticleIdAsync("OLD");
        var kept = await Barcodes.CreateAsync(s.Http, inactive, "111");
        await Stock.SetArticleActiveAsync(s.Http, inactive, false);

        using (var response = await Barcodes.PostAsync(s.Http, Guid.CreateVersion7(), Ean))
            Assert.Equal(new[] { "articleId" }, McpAssert.ErrorKeys(await HttpAssert.ReferenceNotFoundAsync(response, "articleId")));
        using (var response = await Barcodes.PostAsync(s.Http, inactive, Ean))
            Assert.Equal(new[] { "articleId" }, McpAssert.ErrorKeys(await HttpAssert.ReferenceInactiveAsync(response, "articleId")));

        // R19: the barcodes of an inactive article are still returned.
        McpAssert.JsonEqual(kept, Assert.Single((await Barcodes.ListAsync(s.Http)).Items()));
    }

    // ---- AC-63 ----

    [Fact]
    public async Task AC63_R16_R21_By_default_a_barcode_belongs_to_one_article()
    {
        var s = await SalesSetup.NewAsync(app);
        var one = await s.ArticleAsync("ONE-ART", "First article");
        var two = await s.ArticleIdAsync("TWO-ART");
        var first = await Barcodes.CreateAsync(s.Http, one.Id(), Ean);

        var refused = await Barcodes.TakenByRuleAsync(Barcodes.PostAsync(s.Http, two, Ean));

        // R21: the refusal names no other record.
        var text = refused.GetRawText();
        Assert.DoesNotContain(one.Id().ToString(), text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(first.Id().ToString(), text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ONE-ART", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("First article", text, StringComparison.OrdinalIgnoreCase);

        // The article's own barcode again: an invariant, no rule is named; it comes before the rule's check.
        await Barcodes.TakenByArticleAsync(Barcodes.PostAsync(s.Http, one.Id(), Ean));
        await Barcodes.TakenByArticleAsync(Barcodes.PostAsync(s.Http, one.Id(), $" {Ean} "));
        McpAssert.JsonEqual(first, Assert.Single((await Barcodes.ListAsync(s.Http)).Items()));
    }

    // ---- AC-64 ----

    [Fact]
    public async Task AC64_E7_R20_With_barcodeUnique_false_articles_share_a_barcode_and_switching_back_keeps_them()
    {
        var s = await SalesSetup.NewAsync(app);
        var one = await s.ArticleIdAsync("A1");
        var two = await s.ArticleIdAsync("A2");
        var three = await s.ArticleIdAsync("A3");
        await Barcodes.CreateAsync(s.Http, one, Ean);
        await Rules.SetAsync(s.Http, SalesRules.BarcodeUnique, false);

        await Barcodes.CreateAsync(s.Http, two, Ean);

        // Ordered by barcode, then article code.
        Assert.Equal(new[] { one, two }, Barcodes.ArticleIds(await Barcodes.LookupAsync(s.Http, Ean)));
        await Barcodes.TakenByArticleAsync(Barcodes.PostAsync(s.Http, one, Ean));

        await Rules.SetAsync(s.Http, SalesRules.BarcodeUnique, true);

        Assert.Equal(new[] { one, two }, Barcodes.ArticleIds(await Barcodes.LookupAsync(s.Http, Ean)));
        await Barcodes.TakenByRuleAsync(Barcodes.PostAsync(s.Http, three, Ean));
        await Barcodes.TakenByArticleAsync(Barcodes.PostAsync(s.Http, two, Ean));
        Assert.Equal(2, (await Barcodes.ListAsync(s.Http)).Total());
    }

    // ---- AC-65 ----

    [Fact]
    public async Task AC65_R19_The_lookup_is_exact_and_no_filter_returns_every_barcode_paged()
    {
        var s = await SalesSetup.NewAsync(app);
        var b = await s.ArticleIdAsync("B");
        var a = await s.ArticleIdAsync("A");
        await Barcodes.CreateAsync(s.Http, b, "123");
        await Barcodes.CreateAsync(s.Http, a, "abc");
        await Barcodes.CreateAsync(s.Http, a, "00123");

        Assert.Equal(0, (await Barcodes.LookupAsync(s.Http, "12")).Total());
        Assert.Equal(0, (await Barcodes.LookupAsync(s.Http, "ABC")).Total());
        Assert.Equal(new[] { a }, Barcodes.ArticleIds(await Barcodes.LookupAsync(s.Http, "abc")));
        Assert.Equal(new[] { b }, Barcodes.ArticleIds(await Barcodes.LookupAsync(s.Http, " 123 ")));
        Assert.Equal(0, (await Barcodes.ListAsync(s.Http, $"?articleId={Guid.CreateVersion7()}")).Total());

        // Every barcode of the tenant, ordered by barcode (ordinal).
        var all = await Barcodes.ListAsync(s.Http);
        Assert.Equal(new[] { "items", "limit", "offset", "total" }, all.PropertyNames());
        Assert.Equal(new[] { "00123", "123", "abc" }, all.Items().Select(i => i.Str("barcode")));
        var page = await Barcodes.ListAsync(s.Http, "?limit=1&offset=1");
        Assert.Equal((3, "123"), (page.Total(), Assert.Single(page.Items()).Str("barcode")));

        // A barcode no scanner sends: 400, key barcode.
        foreach (var query in new[] { $"?barcode={new string('7', 51)}", "?barcode=12%203", "?barcode=caf%C3%A9", "?articleId=x" })
        {
            using var response = await s.Http.GetAsync(Barcodes.Path + query);
            await HttpAssert.ValidationAsync(response, query.Contains("articleId", StringComparison.Ordinal) ? "articleId" : "barcode");
        }
    }

    // ---- AC-66 ----

    [Fact]
    public async Task AC66_R18_Ten_parallel_creates_of_one_barcode_on_ten_articles_give_exactly_one()
    {
        var s = await SalesSetup.NewAsync(app);
        var articles = new List<Guid>();
        for (var i = 0; i < 10; i++)
            articles.Add(await s.ArticleIdAsync($"P{i}"));

        var responses = await Task.WhenAll(articles.Select(a => Barcodes.PostAsync(s.Http, a, Ean)));

        try
        {
            Assert.DoesNotContain(responses, r => (int)r.StatusCode >= 500);
            Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Created);
            foreach (var refused in responses.Where(r => r.StatusCode != HttpStatusCode.Created))
                await Rules.ConflictAsync(refused, "BARCODE_TAKEN", SalesRules.BarcodeUnique, true, "barcode");
            Assert.Equal(1, (await Barcodes.LookupAsync(s.Http, Ean)).Total());
        }
        finally
        {
            foreach (var response in responses)
                response.Dispose();
        }
    }
}
