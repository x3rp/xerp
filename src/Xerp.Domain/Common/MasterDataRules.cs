namespace Xerp.Domain.Common;

/// <summary>
/// The decisions of the master-data rules (spec 013, 6.3-6.6), each a pure function of the values and of the
/// rule's boolean for the tenant (ADR-0020: a check never fetches a rule). The four masters share them.
/// </summary>
public static class MasterDataRules
{
    /// <summary>
    /// R5: with the field's rule true, a write without the field is refused. <paramref name="value"/> is the
    /// normalised one: omitted, null, empty and whitespace-only are all null (ADR-0011, decision 7).
    /// </summary>
    public static bool RequiredFieldMissing(bool required, string? value) => required && value is null;

    /// <summary>
    /// R9: with <c>codeChangeable</c> false, a replace whose code differs from the stored one is refused. Both
    /// codes are normalised (trimmed) and compared exactly: a change of letter case only is a change.
    /// </summary>
    public static bool CodeChangeRefused(bool codeChangeable, string storedCode, string code) =>
        !codeChangeable && !string.Equals(storedCode, code, StringComparison.Ordinal);

    /// <summary>
    /// R12, R15: with the uniqueness rule true, a value another record of the same resource has is refused.
    /// A record never collides with itself (R14): <paramref name="anotherRecordHasIt"/> does not count it.
    /// </summary>
    public static bool Taken(bool unique, bool anotherRecordHasIt) => unique && anotherRecordHasIt;
}
