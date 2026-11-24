namespace Xerp.Domain.Inventory;

/// <summary>How a document's date stands to today (spec 014, R13).</summary>
public enum DateJudgement
{
    Allowed,
    BeforeToday,
    AfterToday,
}

/// <summary>
/// The date rules of spec 014 (R11-R16): <c>document.pastDateAllowed</c> and <c>document.futureDateAllowed</c>.
/// The system has no time zone per tenant, so "today" is lenient: a date is before today only when it is
/// earlier than the date at UTC-12, and after today only when it is later than the date at UTC+14 - a date
/// that is today somewhere in the world is never refused.
/// </summary>
public static class DocumentDateRules
{
    private static readonly TimeSpan Westmost = TimeSpan.FromHours(-12);
    private static readonly TimeSpan Eastmost = TimeSpan.FromHours(14);

    /// <param name="nowUtc">The server's clock at the operation, in UTC.</param>
    public static DateJudgement Judge(DateOnly date, DateTime nowUtc, bool pastAllowed, bool futureAllowed)
    {
        var utc = DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc);
        if (!pastAllowed && date < DateOnly.FromDateTime(utc + Westmost))
            return DateJudgement.BeforeToday;
        if (!futureAllowed && date > DateOnly.FromDateTime(utc + Eastmost))
            return DateJudgement.AfterToday;
        return DateJudgement.Allowed;
    }
}
