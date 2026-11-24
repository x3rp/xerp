using Xerp.Application.Ports;
using Xerp.Application.Stock;
using Xerp.Domain.Inventory;
using Xerp.Domain.Rules;
using Xerp.Application.Common;

namespace Xerp.UnitTests;

/// <summary>
/// Spec 014, AC-03: the date rules of R13 as a pure function of a date, a moment and the two booleans; the
/// date stage of a reversal (R15, R17); the count's differences when an outdated count is posted (R28).
/// </summary>
public class DocumentRuleChecksTests
{
    private static readonly DateOnly Today = new(2026, 11, 24);

    private static DateTime At(int hour, int minute = 0) => new(2026, 11, 24, hour, minute, 0, DateTimeKind.Utc);

    private static TenantRules With(params (RuleDefinition Rule, bool Value)[] values) =>
        new(values.ToDictionary(v => v.Rule.Key, v => v.Value));

    // ---- R13: the date judgement

    [Theory]
    [InlineData(10, 0)] // UTC-12 is the 23rd, UTC+14 the 25th: three dates around the UTC one
    [InlineData(11, 59)]
    public void R13_At_a_moment_when_UTC_minus_12_and_UTC_plus_14_are_on_three_different_dates_the_day_before_and_after_are_both_today_somewhere(int hour, int minute)
    {
        var now = At(hour, minute);
        Assert.Equal(new DateOnly(2026, 11, 23), DateOnly.FromDateTime(now.AddHours(-12)));
        Assert.Equal(new DateOnly(2026, 11, 25), DateOnly.FromDateTime(now.AddHours(14)));

        Assert.Equal(DateJudgement.BeforeToday, DocumentDateRules.Judge(Today.AddDays(-2), now, false, false));
        Assert.Equal(DateJudgement.Allowed, DocumentDateRules.Judge(Today.AddDays(-1), now, false, false));
        Assert.Equal(DateJudgement.Allowed, DocumentDateRules.Judge(Today, now, false, false));
        Assert.Equal(DateJudgement.Allowed, DocumentDateRules.Judge(Today.AddDays(1), now, false, false));
        Assert.Equal(DateJudgement.AfterToday, DocumentDateRules.Judge(Today.AddDays(2), now, false, false));
    }

    [Fact]
    public void R13_The_boundaries_move_with_the_hour()
    {
        // 09:59Z: UTC+14 is still the 24th, 23:59 -> the 25th is already after today everywhere.
        Assert.Equal(DateJudgement.AfterToday, DocumentDateRules.Judge(Today.AddDays(1), At(9, 59), true, false));
        Assert.Equal(DateJudgement.Allowed, DocumentDateRules.Judge(Today.AddDays(1), At(10, 0), true, false));
        // 11:59Z: UTC-12 is still the 23rd; at 12:00Z it is the 24th, so the 23rd is past everywhere.
        Assert.Equal(DateJudgement.Allowed, DocumentDateRules.Judge(Today.AddDays(-1), At(11, 59), false, true));
        Assert.Equal(DateJudgement.BeforeToday, DocumentDateRules.Judge(Today.AddDays(-1), At(12, 0), false, true));
    }

    [Theory]
    [InlineData(-30)]
    [InlineData(-3)]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(400)]
    public void R16_With_both_rules_true_no_date_is_refused(int offset)
    {
        foreach (var hour in new[] { 0, 9, 11, 12, 23 })
            Assert.Equal(DateJudgement.Allowed, DocumentDateRules.Judge(Today.AddDays(offset), At(hour), true, true));
    }

    [Fact]
    public void R14_Each_rule_judges_only_its_own_side()
    {
        var now = At(12);
        Assert.Equal(DateJudgement.Allowed, DocumentDateRules.Judge(Today.AddDays(-3), now, true, false));
        Assert.Equal(DateJudgement.AfterToday, DocumentDateRules.Judge(Today.AddDays(3), now, true, false));
        Assert.Equal(DateJudgement.BeforeToday, DocumentDateRules.Judge(Today.AddDays(-3), now, false, true));
        Assert.Equal(DateJudgement.Allowed, DocumentDateRules.Judge(Today.AddDays(3), now, false, true));
        Assert.Equal(DateJudgement.Allowed, DocumentDateRules.Judge(Today, now, false, false));
    }

    [Fact]
    public void R14_A_refusal_is_409_DATE_NOT_ALLOWED_with_the_field_and_the_rule_that_refused()
    {
        var rules = With((RuleRegistry.DocumentPastDateAllowed, false));
        var error = DocumentRuleChecks.Date(Today.AddDays(-3), At(12), rules, "documentDate", "stock document");
        Assert.NotNull(error);
        Assert.Equal(ErrorCodes.DateNotAllowed, error.Code);
        Assert.Equal(["documentDate"], error.Errors!.Keys);
        var refusal = Assert.Single(error.Rules!);
        Assert.Equal(("document.pastDateAllowed", false), (refusal.Key, refusal.Value));

        Assert.Null(DocumentRuleChecks.Date(Today, At(12), rules, "documentDate", "stock document"));
        Assert.Null(DocumentRuleChecks.Date(Today.AddDays(3), At(12), rules, "documentDate", "stock document"));

        var future = DocumentRuleChecks.Date(Today.AddDays(3), At(12), With((RuleRegistry.DocumentFutureDateAllowed, false)), "orderDate", "order");
        Assert.Equal(["orderDate"], future!.Errors!.Keys);
        Assert.Equal("document.futureDateAllowed", Assert.Single(future.Rules!).Key);
        Assert.Null(DocumentRuleChecks.Date(Today.AddDays(-3), At(12), TenantRules.Defaults, "documentDate", "stock document"));
    }

    // ---- R15, R17: the reversal's date stage

    [Fact]
    public void R17_Defaults_refuse_only_a_reversal_before_its_original_naming_that_rule()
    {
        var error = DocumentRuleChecks.ReversalDate(Today.AddDays(-1), Today, At(12), TenantRules.Defaults);
        Assert.Equal(ErrorCodes.ValidationFailed, error!.Code);
        Assert.Equal(["documentDate"], error.Errors!.Keys);
        var refusal = Assert.Single(error.Rules!);
        Assert.Equal(("stock.reversalBeforeOriginalAllowed", false), (refusal.Key, refusal.Value));
        Assert.Equal(["documentDate"], refusal.Fields);

        Assert.Null(DocumentRuleChecks.ReversalDate(Today, Today, At(12), TenantRules.Defaults));
        Assert.Null(DocumentRuleChecks.ReversalDate(Today.AddDays(30), Today, At(12), TenantRules.Defaults));
    }

    [Fact]
    public void R17_With_the_rule_true_a_reversal_may_precede_its_original()
    {
        var rules = With((RuleRegistry.StockReversalBeforeOriginalAllowed, true));
        Assert.Null(DocumentRuleChecks.ReversalDate(Today.AddDays(-5), Today, At(12), rules));
    }

    [Fact]
    public void E7_One_error_with_one_element_for_every_date_rule_that_refused()
    {
        var rules = With((RuleRegistry.DocumentPastDateAllowed, false));
        var error = DocumentRuleChecks.ReversalDate(Today.AddDays(-3), Today, At(12), rules);

        Assert.Equal(["documentDate"], error!.Errors!.Keys);
        Assert.Equal(
            ["document.pastDateAllowed", "stock.reversalBeforeOriginalAllowed"],
            error.Rules!.Select(r => r.Key).Order(StringComparer.Ordinal));
        Assert.All(error.Rules!, r =>
        {
            Assert.False(r.Value);
            Assert.Equal(["documentDate"], r.Fields);
        });
    }

    [Fact]
    public void R15_The_original_date_is_not_judged_against_today_and_a_reversal_is_judged_by_both_date_rules()
    {
        var past = With((RuleRegistry.DocumentPastDateAllowed, false));
        // E6: the original is old; a reversal with today's date is fine, one with the original's date is not.
        var original = Today.AddDays(-3);
        Assert.Null(DocumentRuleChecks.ReversalDate(Today, original, At(12), past));
        var refused = DocumentRuleChecks.ReversalDate(original, original, At(12), past);
        Assert.Equal("document.pastDateAllowed", Assert.Single(refused!.Rules!).Key);

        var future = With((RuleRegistry.DocumentFutureDateAllowed, false));
        var after = DocumentRuleChecks.ReversalDate(Today.AddDays(3), Today, At(12), future);
        Assert.Equal("document.futureDateAllowed", Assert.Single(after!.Rules!).Key);
        Assert.Equal(ErrorCodes.ValidationFailed, after.Code);
    }

    [Fact]
    public void A_reversal_a_work_order_or_an_invoice_makes_is_not_judged_against_today_but_against_its_original()
    {
        var rules = With((RuleRegistry.DocumentPastDateAllowed, false), (RuleRegistry.DocumentFutureDateAllowed, false));
        Assert.Null(DocumentRuleChecks.ReversalDate(Today.AddDays(-3), Today.AddDays(-3), At(12), rules, judgeToday: false));
        Assert.Null(DocumentRuleChecks.ReversalDate(Today.AddDays(3), Today.AddDays(3), At(12), rules, judgeToday: false));

        var before = DocumentRuleChecks.ReversalDate(Today.AddDays(-4), Today.AddDays(-3), At(12), rules, judgeToday: false);
        Assert.Equal("stock.reversalBeforeOriginalAllowed", Assert.Single(before!.Rules!).Key);
    }

    // ---- R28: an outdated count posted against the stock of that moment

    private static readonly Guid Actor = Guid.CreateVersion7();
    private static readonly Guid W1 = Guid.CreateVersion7();
    private static readonly Guid A = Guid.CreateVersion7();
    private static readonly Guid B = Guid.CreateVersion7();
    private static readonly Guid Pcs = Guid.CreateVersion7();

    private static StockDocument Count(decimal[] counted, decimal[] book, params Guid[] articles) =>
        StockDocument.Create(StockDocumentType.Count, new DateOnly(2026, 10, 9), W1, null, null, null,
            articles.Select((a, i) => new StockLineEntry(a, Pcs, counted[i])).ToList(), At(12), Actor, book);

    [Fact]
    public void E12_A_retaken_book_quantity_gives_the_difference_to_the_stock_of_the_moment_of_posting()
    {
        // Saved at book 10, counted 7 (difference -3); 4 issued meanwhile: stock 6.
        var count = Count([7m], [10m], A);

        count.RetakeBookQuantities([6m]);

        Assert.Equal(6m, count.Lines.Single().BookQuantity);
        var entries = count.Post("SC-000001", [1m], At(13), Actor);
        var entry = Assert.Single(entries);
        Assert.Equal(1m, entry.Quantity);
        // R28: the posted document shows the book quantity and difference it was posted with, for good.
        Assert.Equal(6m, count.Lines.Single().BookQuantity);
        Assert.Equal(1m, count.Lines.Single().DifferenceQuantity);
    }

    [Fact]
    public void R28_Lines_take_their_own_stock_in_line_order_and_a_line_that_now_agrees_writes_no_entry()
    {
        var count = Count([7m, 2m], [10m, 5m], A, B);

        count.RetakeBookQuantities([7m, 0m]);

        Assert.Equal([7m, 0m], count.Lines.Select(l => l.BookQuantity!.Value));
        var entries = count.Post("SC-000002", [1m, 1m], At(13), Actor);
        Assert.Equal([0m, 2m], count.Lines.Select(l => l.DifferenceQuantity!.Value));
        var entry = Assert.Single(entries);
        Assert.Equal(B, entry.ArticleId);
        Assert.Equal(2m, entry.Quantity);
    }

    [Fact]
    public void R28_Only_a_draft_count_has_book_quantities_to_retake_one_for_every_line()
    {
        var count = Count([7m], [10m], A);
        Assert.Throws<ArgumentException>(() => count.RetakeBookQuantities([]));
        Assert.Throws<ArgumentException>(() => count.RetakeBookQuantities([1m, 2m]));

        var receipt = StockDocument.Create(StockDocumentType.Receipt, new DateOnly(2026, 10, 9), W1, null, null, null,
            [new StockLineEntry(A, Pcs, 1m)], At(12), Actor);
        Assert.Throws<InvalidOperationException>(() => receipt.RetakeBookQuantities([1m]));

        count.Post("SC-000003", [1m], At(13), Actor);
        Assert.Throws<InvalidOperationException>(() => count.RetakeBookQuantities([6m]));
    }
}
