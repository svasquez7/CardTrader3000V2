using System.Globalization;
using System.Text;
using CardTrader3000.Data;
using CsvHelper;
using CsvHelper.Configuration;

namespace CardTrader3000.Services.Import;

/// <summary>
/// A sanitized, validated input line: card set, player name, card number, parallel, and whether
/// to price it with Claude (<see cref="Price"/> false = add to inventory only).
/// </summary>
public sealed record CardLine(int LineNumber, string CardSet, string PlayerName, string CardNumber, string Parallel, bool Price = true)
{
    public string Key => CardKey.Normalize(CardSet, PlayerName, CardNumber, Parallel);
}

public sealed record LineError(int LineNumber, string Message, string RawText);

/// <summary>
/// Several identical lines in one import, combined into one card with a quantity.
/// Priced if any of the lines asked for pricing.
/// </summary>
public sealed record DistinctCardLine(CardLine First, int Quantity, IReadOnlyList<int> LineNumbers, bool Price);

public sealed class ParseResult
{
    public List<CardLine> Lines { get; } = [];
    public List<LineError> Errors { get; } = [];

    /// <summary>Groups duplicate lines (same normalized key), keeping first-seen order.</summary>
    public IReadOnlyList<DistinctCardLine> Distinct() =>
        Lines.GroupBy(l => l.Key)
             .Select(g => new DistinctCardLine(g.First(), g.Count(), g.Select(l => l.LineNumber).ToList(), g.Any(l => l.Price)))
             .ToList();
}

/// <summary>
/// Parses CSV uploads and manual entry with the same rules. Format per line:
/// <c>card set, player name, card number, parallel, price</c>.
/// Parallel is optional (defaults to "Base"). Price is optional true/false (also yes/no, y/n, 1/0);
/// when blank the caller's <c>priceByDefault</c> applies. Handles quoted fields, an optional header
/// row, and tab-separated text pasted from a spreadsheet.
/// </summary>
public static class CardLineParser
{
    public const int MaxLines = 5000;

    private const int MaxSetLength = 200;
    private const int MaxPlayerLength = 200;
    private const int MaxNumberLength = 50;
    private const int MaxParallelLength = 200;

    public static ParseResult Parse(string? text, bool priceByDefault = true)
    {
        var result = new ParseResult();
        if (string.IsNullOrWhiteSpace(text)) return result;

        var config = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            HasHeaderRecord = false,
            Delimiter = DetectDelimiter(text),
            TrimOptions = TrimOptions.Trim,
            IgnoreBlankLines = true,
            BadDataFound = null,       // tolerate stray quotes; we validate fields ourselves
            MissingFieldFound = null
        };

        using var reader = new StringReader(text);
        using var parser = new CsvParser(reader, config);

        var firstRecord = true;
        while (parser.Read())
        {
            var record = parser.Record ?? [];
            var lineNumber = parser.Row;

            if (record.All(string.IsNullOrWhiteSpace)) continue;

            if (firstRecord)
            {
                firstRecord = false;
                if (LooksLikeHeader(record)) continue;
            }

            if (result.Lines.Count + result.Errors.Count >= MaxLines)
            {
                result.Errors.Add(new LineError(lineNumber, $"Import limit of {MaxLines:N0} lines reached; remaining lines were skipped.", ""));
                break;
            }

            var raw = string.Join(config.Delimiter, record);

            var set = Sanitize(Field(record, 0), MaxSetLength);
            var player = Sanitize(Field(record, 1), MaxPlayerLength);
            var number = Sanitize(Field(record, 2), MaxNumberLength).TrimStart('#').Trim();
            var parallel = Sanitize(Field(record, 3), MaxParallelLength);
            var priceText = Sanitize(Field(record, 4), 20);

            // "set, player, number, false": a 4th value of exactly true/false is the price flag
            // with the parallel left out (no parallel is literally named "true" or "false").
            if (record.Length == 4 && IsBareBoolean(parallel))
            {
                priceText = parallel;
                parallel = "";
            }

            var missing = new List<string>(3);
            if (set.Length == 0) missing.Add("card set");
            if (player.Length == 0) missing.Add("player name");
            if (number.Length == 0) missing.Add("card number");

            if (missing.Count > 0)
            {
                result.Errors.Add(new LineError(lineNumber, $"Missing {string.Join(", ", missing)}.", raw));
                continue;
            }

            var price = priceByDefault;
            if (priceText.Length > 0 && !TryParseFlag(priceText, out price))
            {
                result.Errors.Add(new LineError(lineNumber,
                    $"Price flag \"{priceText}\" isn't recognized. Use true or false (yes/no, y/n, 1/0 also work).", raw));
                continue;
            }

            if (record.Length > 5 && record.Skip(5).Any(f => !string.IsNullOrWhiteSpace(f)))
            {
                result.Errors.Add(new LineError(lineNumber,
                    "Too many columns. If a value contains a comma, wrap it in double quotes.", raw));
                continue;
            }

            result.Lines.Add(new CardLine(lineNumber, set, player, number, parallel.Length == 0 ? "Base" : parallel, price));
        }

        return result;
    }

    /// <summary>
    /// Removes control and zero-width characters, normalizes Unicode and curly quotes,
    /// collapses whitespace, strips spreadsheet-formula prefixes and truncates.
    /// </summary>
    public static string Sanitize(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;

        var normalized = value.Normalize(NormalizationForm.FormC);
        var sb = new StringBuilder(normalized.Length);
        var lastWasSpace = false;

        foreach (var ch in normalized)
        {
            var c = ch switch
            {
                '‘' or '’' => '\'',
                '“' or '”' => '"',
                '–' or '—' => '-',
                ' ' => ' ',
                _ => ch
            };

            if (c is '​' or '‌' or '‍' or '﻿') continue;

            if (char.IsWhiteSpace(c) || char.IsControl(c))
            {
                if (!lastWasSpace && sb.Length > 0) sb.Append(' ');
                lastWasSpace = true;
                continue;
            }

            sb.Append(c);
            lastWasSpace = false;
        }

        var s = sb.ToString().Trim();

        // Values starting with these can execute as formulas when exported to Excel/Sheets later.
        s = s.TrimStart('=', '+', '@').Trim();

        return s.Length > maxLength ? s[..maxLength].TrimEnd() : s;
    }

    public static bool TryParseFlag(string text, out bool value)
    {
        switch (text.Trim().ToLowerInvariant())
        {
            case "true" or "yes" or "y" or "1" or "t":
                value = true;
                return true;
            case "false" or "no" or "n" or "0" or "f":
                value = false;
                return true;
            default:
                value = false;
                return false;
        }
    }

    private static bool IsBareBoolean(string s) =>
        s.Equals("true", StringComparison.OrdinalIgnoreCase) || s.Equals("false", StringComparison.OrdinalIgnoreCase);

    private static string? Field(string[] record, int index) => index < record.Length ? record[index] : null;

    private static bool LooksLikeHeader(string[] record) =>
        record.Length >= 2
        && record[0].Contains("set", StringComparison.OrdinalIgnoreCase)
        && record[1].Contains("player", StringComparison.OrdinalIgnoreCase);

    /// <summary>Tab-separated when the first non-empty line has tabs but no commas (pasted from a spreadsheet).</summary>
    private static string DetectDelimiter(string text)
    {
        var firstLine = text.Split('\n').FirstOrDefault(l => !string.IsNullOrWhiteSpace(l)) ?? "";
        return firstLine.Contains('\t') && !firstLine.Contains(',') ? "\t" : ",";
    }
}
