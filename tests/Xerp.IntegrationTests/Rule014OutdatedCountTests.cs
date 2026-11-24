using System.Net;
using Xerp.IntegrationTests.Support;

namespace Xerp.IntegrationTests;

/// <summary>Spec 014, AC-90 to AC-92: <c>stock.outdatedCountAllowed</c>.</summary>
[Collection(XerpCollection.Name)]
public class Rule014OutdatedCountTests(XerpFixture app)
{
    [Fact]
    public async Task AC90_AC91_With_the_rule_true_an_outdated_count_posts_against_the_stock_of_that_moment()
    {
        var s = await Orders.SetupAsync(app);
        var http = s.Http;
        await Rules.SetAsync(http, DocRules.OutdatedCount, true);
        await Stock.ReceiveAsync(http, s.W1, s.A, 10);
        var draft = await Counts.CountAsync(http, s.W1, s.A, 7);
        Counts.AssertLine(draft.DocumentLines()[0], quantity: 7m, book: 10m);
        await Stock.IssueAsync(http, s.W1, s.A, 4);

        // AC-91 (R29): the draft is not rewritten by the change of stock.
        Counts.AssertLine((await Stock.GetAsync(http, draft.Id())).DocumentLines()[0], quantity: 7m, book: 10m);

        var posted = await Stock.PostDocumentAsync(http, draft.Id());

        Assert.Equal("posted", posted.Str("status"));
        Counts.AssertLine(posted.DocumentLines()[0], quantity: 7m, baseQuantity: 7m, book: 6m, difference: 1m);
        Counts.AssertLine((await Stock.GetAsync(http, draft.Id())).DocumentLines()[0], quantity: 7m, baseQuantity: 7m, book: 6m, difference: 1m);
        Assert.Equal(7m, await Stock.QuantityAsync(http, s.A, s.W1));
        Assert.Equal(new[] { (1, 1m) }, await Counts.MovementsAsync(http, draft.Id()));
        await Rules.AssertBalancedAsync(http, "AC-90");
    }

    [Fact]
    public async Task AC91_A_count_that_is_not_outdated_posts_as_before()
    {
        var s = await Orders.SetupAsync(app);
        var http = s.Http;
        await Rules.SetAsync(http, DocRules.OutdatedCount, true);
        await Stock.ReceiveAsync(http, s.W1, s.A, 10);

        var posted = await Counts.PostedAsync(http, s.W1, (s.A, 8, null));

        Counts.AssertLine(posted.DocumentLines()[0], quantity: 8m, book: 10m);
        Assert.Equal(8m, await Stock.QuantityAsync(http, s.A, s.W1));
        await Rules.AssertBalancedAsync(http, "AC-91");
    }

    [Fact]
    public async Task AC92_Back_to_false_the_posted_count_reads_unchanged_and_a_new_outdated_count_is_refused()
    {
        var s = await Orders.SetupAsync(app);
        var http = s.Http;
        await Rules.SetAsync(http, DocRules.OutdatedCount, true);
        await Stock.ReceiveAsync(http, s.W1, s.A, 10);
        var draft = await Counts.CountAsync(http, s.W1, s.A, 7);
        await Stock.IssueAsync(http, s.W1, s.A, 4);
        var posted = await Stock.PostDocumentAsync(http, draft.Id());
        await Rules.SetAsync(http, DocRules.OutdatedCount, false);

        McpAssert.JsonEqual(posted, await Stock.GetAsync(http, posted.Id()), "The posted count changed");
        var outdated = await Counts.CountAsync(http, s.W1, s.A, 5);
        await Stock.ReceiveAsync(http, s.W1, s.A, 1);
        using var refused = await Stock.SendPostAsync(http, outdated.Id());

        await Rules.ConflictAsync(refused, "COUNT_OUTDATED", DocRules.OutdatedCount, false, "lines[0].quantity");
        await Stock.AssertDraftAsync(http, outdated.Id());
        Assert.Equal(8m, await Stock.QuantityAsync(http, s.A, s.W1));
    }
}
