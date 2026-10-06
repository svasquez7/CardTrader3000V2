using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using CardTrader3000.Data.Entities;
using CardTrader3000.Services.Pricing;

namespace CardTrader3000.Services.Import;

/// <summary>
/// Parses a JSON card import into the same <see cref="ParseResult"/> the CSV parser produces, so
/// both flow through one pipeline.
///
/// Accepted shapes:
/// <list type="bullet">
/// <item>An array of card objects: <c>[ { ... }, { ... } ]</c></item>
/// <item>An object with a <c>cards</c> array, including the Phase 1 prompt output
///   (<c>{ "summary": {...}, "cards": [...] }</c> with nested <c>pricing_and_returns</c> and <c>ebay_listing_details</c>)</item>
/// <item>A single card object</item>
/// </list>
///
/// Field names are matched case-insensitively ignoring underscores/dashes, so <c>card_set</c>,
/// <c>cardSet</c> and <c>CardSet</c> all work. Nested objects are flattened one level. Fee, net,
/// margin and bucket fields in the file are ignored: they're recalculated from the list price.
/// </summary>
public static partial class JsonCardParser
{
    public const int MaxCards = CardLineParser.MaxLines;

    /// <summary>Example shown on the Import page and in the README.</summary>
    public const string Example = """
        [
          {
            "card_set": "2010 Topps Football",
            "player": "Jimmy Graham",
            "card_number": "265",
            "parallel": "Base",
            "quantity": 2,
            "team": "New Orleans Saints",
            "is_rookie": true,
            "estimated_list_price": 4.99,
            "sgc_grading_candidate": "High Prospect",
            "sales_strategy": "List individually at BIN.",
            "ebay_title": "2010 Topps #265 Jimmy Graham RC Rookie Card New Orleans Saints",
            "ebay_description": "Card Details: ...",
            "status": "In Stock"
          },
          {
            "card_set": "1991 Fleer",
            "player": "Common Player",
            "card_number": "112",
            "price_with_claude": false
          }
        ]
        """;

    // Field aliases (normalized: lower-case letters and digits only).
    private static readonly string[] SetKeys = ["cardset", "set", "setname", "product"];
    private static readonly string[] PlayerKeys = ["player", "playername", "name"];
    private static readonly string[] NumberKeys = ["cardnumber", "number", "cardno", "no"];
    private static readonly string[] ParallelKeys = ["parallel", "parallelfeature", "variant", "variation"];
    private static readonly string[] TeamKeys = ["team", "teamname"];
    private static readonly string[] RookieKeys = ["isrookie", "rookie", "rc"];
    private static readonly string[] ListPriceKeys = ["estimatedlistprice", "listprice"];
    private static readonly string[] PriceFlagKeys = ["pricewithclaude", "pricewithai", "evaluate"];
    private static readonly string[] SgcKeys = ["sgcgradingcandidate", "sgccandidate", "sgc"];
    private static readonly string[] StrategyKeys = ["salesstrategy", "strategy"];
    private static readonly string[] TitleKeys = ["ebaytitle", "title", "listingtitle"];
    private static readonly string[] DescriptionKeys = ["ebaydescription", "description", "descriptiontemplate", "listingdescription"];
    private static readonly string[] QuantityKeys = ["quantity", "qty", "count"];
    private static readonly string[] StatusKeys = ["status", "inventorystatus", "stockstatus"];

    private const int MaxQuantity = 10_000;

    public static ParseResult Parse(string? text, bool priceByDefault = true, int maxTitleLength = 80)
    {
        var result = new ParseResult();
        if (string.IsNullOrWhiteSpace(text)) return result;

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(text, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
                MaxDepth = 32
            });
        }
        catch (JsonException ex)
        {
            var line = (int)(ex.LineNumber ?? 0) + 1;
            result.Errors.Add(new LineError(line, $"Invalid JSON near line {line}: {ex.Message}", ""));
            return result;
        }

        using (doc)
        {
            var root = doc.RootElement;
            JsonElement cards;

            if (root.ValueKind == JsonValueKind.Array)
            {
                cards = root;
            }
            else if (root.ValueKind == JsonValueKind.Object && TryGetArray(root, out cards, "cards", "items", "inventory"))
            {
                // { "cards": [...] } or the Phase 1 prompt output.
            }
            else if (root.ValueKind == JsonValueKind.Object)
            {
                // A single card object.
                ParseCard(root, 1, priceByDefault, maxTitleLength, result);
                return result;
            }
            else
            {
                result.Errors.Add(new LineError(1, "Expected an array of cards, or an object with a \"cards\" array.", ""));
                return result;
            }

            var index = 0;
            foreach (var element in cards.EnumerateArray())
            {
                index++;
                if (index > MaxCards)
                {
                    result.Errors.Add(new LineError(index, $"Import limit of {MaxCards:N0} cards reached; remaining cards were skipped.", ""));
                    break;
                }

                if (element.ValueKind != JsonValueKind.Object)
                {
                    result.Errors.Add(new LineError(index, "Each card must be a JSON object.", Raw(element)));
                    continue;
                }

                ParseCard(element, index, priceByDefault, maxTitleLength, result);
            }
        }

        return result;
    }

    private static void ParseCard(JsonElement element, int index, bool priceByDefault, int maxTitleLength, ParseResult result)
    {
        var fields = Flatten(element);
        var errors = new List<string>();

        // ---- Identity (required, same rules as CSV) ----
        var set = CardLineParser.Sanitize(GetString(fields, SetKeys), 200);
        var player = CardLineParser.Sanitize(GetString(fields, PlayerKeys), 200);
        var number = CardLineParser.Sanitize(GetString(fields, NumberKeys), 50).TrimStart('#').Trim();
        var parallel = CardLineParser.Sanitize(GetString(fields, ParallelKeys), 200);

        var missing = new List<string>(3);
        if (set.Length == 0) missing.Add("card_set");
        if (player.Length == 0) missing.Add("player");
        if (number.Length == 0) missing.Add("card_number");
        if (missing.Count > 0) errors.Add($"Missing {string.Join(", ", missing)}.");

        // ---- Price flag and list price. "price" means the flag when it's a boolean, the list price when it's a number. ----
        var priceFlag = GetBool(fields, PriceFlagKeys, "price_with_claude", errors);
        decimal? listPrice = GetDecimal(fields, ListPriceKeys, "estimated_list_price", errors);

        if (fields.TryGetValue("price", out var priceEl))
        {
            if (priceEl.ValueKind is JsonValueKind.True or JsonValueKind.False
                || (priceEl.ValueKind == JsonValueKind.String && CardLineParser.TryParseFlag(priceEl.GetString() ?? "", out _)))
            {
                priceFlag ??= GetBool(fields, ["price"], "price", errors);
            }
            else
            {
                listPrice ??= GetDecimal(fields, ["price"], "price", errors);
            }
        }

        if (listPrice is < 0m or > 100_000m)
        {
            errors.Add($"List price {listPrice} is out of range (0 to 100,000).");
            listPrice = null;
        }
        if (listPrice is not null) listPrice = Math.Round(listPrice.Value, 2, MidpointRounding.AwayFromZero);

        // ---- Quantity ----
        var quantity = GetInt(fields, QuantityKeys, "quantity", errors) ?? 1;
        if (quantity is < 1 or > MaxQuantity)
            errors.Add($"Quantity must be between 1 and {MaxQuantity:N0}.");

        // ---- Optional details ----
        var team = NullIfEmpty(CardLineParser.Sanitize(GetString(fields, TeamKeys), 100));
        var isRookie = GetBool(fields, RookieKeys, "is_rookie", errors);
        var strategy = NullIfEmpty(CardLineParser.Sanitize(GetString(fields, StrategyKeys), 1000));
        var titleRaw = NullIfEmpty(CardLineParser.Sanitize(GetString(fields, TitleKeys), 200));
        var description = NullIfEmpty(CleanDescription(GetString(fields, DescriptionKeys)));

        SgcCandidate? sgc = null;
        if (GetString(fields, SgcKeys) is { Length: > 0 } sgcText)
        {
            sgc = ParseSgc(sgcText);
            if (sgc is null) errors.Add($"sgc_grading_candidate \"{sgcText}\" isn't recognized. Use High Prospect, Secondary or No.");
        }

        InventoryStatus? status = null;
        if (GetString(fields, StatusKeys) is { Length: > 0 } statusText)
        {
            status = ParseStatus(statusText);
            if (status is null) errors.Add($"status \"{statusText}\" isn't recognized. Use In Stock, Listed or Sold.");
        }

        // The Phase 1 prompt output has no is_rookie field; infer it from the title's RC / Rookie keywords.
        if (isRookie is null && titleRaw is not null && RookieInTitle().IsMatch(titleRaw))
            isRookie = true;

        // Keep the user's wording; only enforce eBay's length limit.
        var title = titleRaw is null ? null : ListingText.FitTitle(titleRaw, maxTitleLength, isRookie: true);

        if (errors.Count > 0)
        {
            result.Errors.Add(new LineError(index, string.Join(" ", errors), Raw(element)));
            return;
        }

        var data = new ProvidedCardData
        {
            Team = team,
            IsRookie = isRookie,
            ListPrice = listPrice,
            Sgc = sgc,
            SalesStrategy = strategy,
            EbayTitle = title,
            EbayDescription = description,
            Status = status
        };

        result.Lines.Add(new CardLine(
            index, set, player, number,
            parallel.Length == 0 ? "Base" : parallel,
            Price: priceFlag ?? priceByDefault,
            Quantity: quantity,
            Data: data.IsEmpty ? null : data));
    }

    // ---------------- Field access ----------------

    /// <summary>Top-level properties plus the children of nested objects (top level wins on a clash).</summary>
    private static Dictionary<string, JsonElement> Flatten(JsonElement obj)
    {
        var fields = new Dictionary<string, JsonElement>();
        foreach (var p in obj.EnumerateObject())
            fields.TryAdd(Normalize(p.Name), p.Value);

        foreach (var p in obj.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.Object))
            foreach (var child in p.Value.EnumerateObject())
                fields.TryAdd(Normalize(child.Name), child.Value);

        return fields;
    }

    private static string Normalize(string key) =>
        new(key.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static bool TryFind(Dictionary<string, JsonElement> fields, string[] keys, out JsonElement value)
    {
        foreach (var k in keys)
        {
            if (fields.TryGetValue(k, out value) && value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
                return true;
        }
        value = default;
        return false;
    }

    private static string? GetString(Dictionary<string, JsonElement> fields, string[] keys)
    {
        if (!TryFind(fields, keys, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null
        };
    }

    private static bool? GetBool(Dictionary<string, JsonElement> fields, string[] keys, string label, List<string> errors)
    {
        if (!TryFind(fields, keys, out var v)) return null;
        switch (v.ValueKind)
        {
            case JsonValueKind.True: return true;
            case JsonValueKind.False: return false;
            case JsonValueKind.Number when v.TryGetInt32(out var n) && n is 0 or 1: return n == 1;
            case JsonValueKind.String when CardLineParser.TryParseFlag(v.GetString() ?? "", out var b): return b;
            case JsonValueKind.String when string.IsNullOrWhiteSpace(v.GetString()): return null;
            default:
                errors.Add($"{label} must be true or false.");
                return null;
        }
    }

    private static decimal? GetDecimal(Dictionary<string, JsonElement> fields, string[] keys, string label, List<string> errors)
    {
        if (!TryFind(fields, keys, out var v)) return null;

        if (v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out var d)) return d;

        if (v.ValueKind == JsonValueKind.String)
        {
            var s = (v.GetString() ?? "").Replace("$", "").Replace(",", "").Trim();
            if (s.Length == 0) return null;
            if (decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out d)) return d;
        }

        errors.Add($"{label} must be a number.");
        return null;
    }

    private static int? GetInt(Dictionary<string, JsonElement> fields, string[] keys, string label, List<string> errors)
    {
        if (!TryFind(fields, keys, out var v)) return null;

        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n)) return n;
        if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out n)) return n;

        errors.Add($"{label} must be a whole number.");
        return null;
    }

    private static bool TryGetArray(JsonElement obj, out JsonElement array, params string[] names)
    {
        foreach (var p in obj.EnumerateObject())
        {
            if (names.Contains(Normalize(p.Name)) && p.Value.ValueKind == JsonValueKind.Array)
            {
                array = p.Value;
                return true;
            }
        }
        array = default;
        return false;
    }

    // ---------------- Value parsing ----------------

    public static SgcCandidate? ParseSgc(string text) => Normalize(text) switch
    {
        "highprospect" or "high" => SgcCandidate.HighProspect,
        "secondary" => SgcCandidate.Secondary,
        "no" or "none" or "false" => SgcCandidate.No,
        _ => null
    };

    public static InventoryStatus? ParseStatus(string text) => Normalize(text) switch
    {
        "instock" or "available" or "stock" => InventoryStatus.InStock,
        "listed" => InventoryStatus.Listed,
        "sold" => InventoryStatus.Sold,
        _ => null
    };

    /// <summary>Descriptions keep their line breaks; only control characters and outer whitespace are removed.</summary>
    private static string CleanDescription(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var cleaned = new string(text.Replace("\r\n", "\n").Where(c => c == '\n' || !char.IsControl(c)).ToArray()).Trim();
        return cleaned.Length > 4000 ? cleaned[..4000] : cleaned;
    }

    private static string? NullIfEmpty(string s) => s.Length == 0 ? null : s;

    private static string Raw(JsonElement e)
    {
        var raw = e.GetRawText();
        return raw.Length > 300 ? raw[..300] + "…" : raw;
    }

    [GeneratedRegex(@"\b(RC|Rookie)\b", RegexOptions.IgnoreCase)]
    private static partial Regex RookieInTitle();
}
