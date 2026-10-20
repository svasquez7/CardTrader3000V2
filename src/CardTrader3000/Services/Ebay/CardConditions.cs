using System.Text.RegularExpressions;

namespace CardTrader3000.Services.Ebay;

/// <summary>
/// eBay's ungraded card condition values (condition descriptor 40001) and the matching
/// "Condition: Ungraded - …" line in listing descriptions.
/// </summary>
public static class CardConditions
{
    public const string DefaultId = "400010";

    public static readonly (string Id, string Label)[] All =
    [
        ("400010", "Near Mint or Better"),
        ("400011", "Excellent"),
        ("400012", "Very Good"),
        ("400013", "Poor")
    ];

    public static string LabelFor(string? id) =>
        All.FirstOrDefault(c => c.Id == id).Label ?? All[0].Label;

    // "Condition: Ungraded - Near Mint or Better. Please see photos." → captures the grade text up to the first period.
    private static readonly Regex UngradedLine = new(
        @"^(?<prefix>[ \t]*Condition\s*:[^\r\n]*?Ungraded\s*[-–—:]\s*)(?<grade>[^.\r\n]*)",
        RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Compiled);

    // Fallback: any "Condition:" line that names a known grade without the "Ungraded -" wording.
    private static readonly Regex ConditionLine = new(
        @"^[ \t]*Condition\s*:[^\r\n]*$",
        RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Compiled);

    private static readonly Regex KnownGrade = new(
        @"Near Mint or Better|Near Mint|Excellent|Very Good|Poor",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Rewrites the grade in the description's Condition line to match <paramref name="conditionId"/>.
    /// Descriptions without a Condition line are returned unchanged.
    /// </summary>
    public static string ApplyToDescription(string? description, string? conditionId)
    {
        if (string.IsNullOrEmpty(description)) return description ?? "";
        var label = LabelFor(conditionId);

        if (UngradedLine.IsMatch(description))
            return UngradedLine.Replace(description, m => m.Groups["prefix"].Value + label, 1);

        return ConditionLine.Replace(description, m => KnownGrade.Replace(m.Value, label, 1), 1);
    }
}
