using System.Text;

namespace CardTrader3000.Data;

/// <summary>
/// Builds the duplicate-detection key for a card. Two lines are the "same card" when set, player,
/// number and parallel match ignoring case, extra whitespace and a leading '#' on the number.
/// </summary>
public static class CardKey
{
    public static string Normalize(string cardSet, string playerName, string cardNumber, string parallel)
    {
        var number = Clean(cardNumber).TrimStart('#');
        var par = Clean(parallel);
        if (par.Length == 0) par = "base";

        return string.Join('|', Clean(cardSet), Clean(playerName), number, par);
    }

    private static string Clean(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;

        var sb = new StringBuilder(value.Length);
        var lastWasSpace = false;
        foreach (var ch in value.Trim())
        {
            if (char.IsWhiteSpace(ch))
            {
                if (!lastWasSpace) sb.Append(' ');
                lastWasSpace = true;
            }
            else
            {
                sb.Append(char.ToLowerInvariant(ch));
                lastWasSpace = false;
            }
        }
        return sb.ToString();
    }
}
