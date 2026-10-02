using System.Text.RegularExpressions;

namespace CardTrader3000.Services.Pricing;

/// <summary>Guards on model-written listing text: eBay's 80-char title limit and rookie keywords.</summary>
public static partial class ListingText
{
    /// <summary>
    /// Collapses whitespace, strips rookie keywords from non-rookie cards, and trims to
    /// <paramref name="maxLength"/> at a word boundary.
    /// </summary>
    public static string FitTitle(string? title, int maxLength, bool isRookie)
    {
        var t = CollapseWhitespace(title ?? string.Empty);

        if (!isRookie)
            t = CollapseWhitespace(RookieTerms().Replace(t, " "));

        if (t.Length <= maxLength) return t;

        var cut = t[..maxLength];
        var lastSpace = cut.LastIndexOf(' ');
        if (lastSpace > maxLength / 2) cut = cut[..lastSpace];

        return cut.TrimEnd(' ', '-', ',', '|', '/');
    }

    private static string CollapseWhitespace(string s) => Whitespace().Replace(s, " ").Trim();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    // "RC" and "Rookie Card" as standalone words. Plain "Rookie" is left alone (e.g. set names like "Rated Rookie").
    [GeneratedRegex(@"\b(RC|Rookie\s+Card)\b", RegexOptions.IgnoreCase)]
    private static partial Regex RookieTerms();
}
