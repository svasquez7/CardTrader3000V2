using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using CardTrader3000.Data.Entities;
using Microsoft.Extensions.Options;

namespace CardTrader3000.Services.Ebay;

/// <summary>One comparable: an active listing or a sold item.</summary>
public sealed record MarketItem(
    string Title,
    decimal Price,
    decimal? Shipping,
    string? Url,
    string? ImageUrl,
    DateTime? SoldDate = null,
    int? SoldQuantity = null)
{
    /// <summary>Price + shipping (shipping unknown counts as 0).</summary>
    public decimal Total => Price + (Shipping ?? 0m);
}

public sealed class MarketResult
{
    public List<MarketItem> Items { get; init; } = [];

    /// <summary>Total matches eBay reported (may exceed the items returned).</summary>
    public int TotalMatches { get; init; }

    /// <summary>The app isn't approved for this API (Marketplace Insights is a limited release).</summary>
    public bool AccessDenied { get; init; }

    public string? Error { get; init; }

    public bool HasData => Items.Count > 0;
    public decimal Low => Items.Count == 0 ? 0 : Items.Min(i => i.Price);
    public decimal High => Items.Count == 0 ? 0 : Items.Max(i => i.Price);
    public decimal Average => Items.Count == 0 ? 0 : Math.Round(Items.Average(i => i.Price), 2);
    public decimal Median => MedianOf(Items.Select(i => i.Price));
    public decimal MedianTotal => MedianOf(Items.Select(i => i.Total));

    private static decimal MedianOf(IEnumerable<decimal> values)
    {
        var v = values.OrderBy(x => x).ToList();
        if (v.Count == 0) return 0;
        var mid = v.Count / 2;
        return v.Count % 2 == 1 ? v[mid] : Math.Round((v[mid - 1] + v[mid]) / 2m, 2);
    }
}

/// <summary>
/// Price research on eBay:
/// <list type="bullet">
/// <item>Active Buy It Now listings via the Browse API (application token; available to all apps).</item>
/// <item>Sold items (last 90 days) via the Marketplace Insights API, a limited release that requires
///   eBay approval. Without approval the result comes back with <see cref="MarketResult.AccessDenied"/>.</item>
/// </list>
/// Searches are limited to Sports Trading Card Singles and, optionally, ungraded cards (condition 4000).
/// </summary>
public sealed class EbayMarketDataService(HttpClient http, EbayAuthService auth, IOptions<EbayOptions> options, ILogger<EbayMarketDataService> logger)
{
    private const string BrowseScope = "https://api.ebay.com/oauth/api_scope";
    private const string InsightsScope = "https://api.ebay.com/oauth/api_scope/buy.marketplace.insights";
    private const int Limit = 50;

    private readonly EbayOptions _opt = options.Value;

    public async Task<MarketResult> SearchActiveAsync(string query, bool ungradedOnly, CancellationToken ct = default)
    {
        var filter = "buyingOptions:{FIXED_PRICE}" + (ungradedOnly ? ",conditionIds:{4000}" : "");
        var url = $"buy/browse/v1/item_summary/search?q={Uri.EscapeDataString(query)}&category_ids={_opt.CategoryId}" +
                  $"&limit={Limit}&filter={Uri.EscapeDataString(filter)}";

        try
        {
            var json = await GetAsync(url, BrowseScope, ct);
            var items = (json?["itemSummaries"] as JsonArray ?? new JsonArray())
                .Where(n => n is not null)
                .Select(n => new MarketItem(
                    n!["title"]?.GetValue<string>() ?? "",
                    Money(n["price"]) ?? 0m,
                    Money((n["shippingOptions"] as JsonArray)?.FirstOrDefault()?["shippingCost"]),
                    n["itemWebUrl"]?.GetValue<string>(),
                    n["image"]?["imageUrl"]?.GetValue<string>()))
                .Where(i => i.Price > 0)
                .ToList();

            return new MarketResult { Items = items, TotalMatches = json?["total"]?.GetValue<int>() ?? items.Count };
        }
        catch (EbayApiException ex)
        {
            logger.LogWarning(ex, "Browse search failed for {Query}", query);
            return new MarketResult { Error = ex.Message };
        }
    }

    public async Task<MarketResult> SearchSoldAsync(string query, bool ungradedOnly, CancellationToken ct = default)
    {
        var url = $"buy/marketplace_insights/v1_beta/item_sales/search?q={Uri.EscapeDataString(query)}&category_ids={_opt.CategoryId}&limit={Limit}" +
                  (ungradedOnly ? $"&filter={Uri.EscapeDataString("conditionIds:{4000}")}" : "");

        try
        {
            var json = await GetAsync(url, InsightsScope, ct);
            var items = (json?["itemSales"] as JsonArray ?? new JsonArray())
                .Where(n => n is not null)
                .Select(n => new MarketItem(
                    n!["title"]?.GetValue<string>() ?? "",
                    Money(n["lastSoldPrice"]) ?? 0m,
                    Money((n["shippingOptions"] as JsonArray)?.FirstOrDefault()?["shippingCost"]),
                    n["itemWebUrl"]?.GetValue<string>(),
                    n["image"]?["imageUrl"]?.GetValue<string>(),
                    DateTime.TryParse(n["lastSoldDate"]?.GetValue<string>(), CultureInfo.InvariantCulture,
                        DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d) ? (DateTime?)d : null,
                    n["totalSoldQuantity"]?.GetValue<int>()))
                .Where(i => i.Price > 0)
                .OrderByDescending(i => i.SoldDate)
                .ToList();

            return new MarketResult { Items = items, TotalMatches = json?["total"]?.GetValue<int>() ?? items.Count };
        }
        catch (EbayApiException ex) when (ex.StatusCode is 401 or 403
                                          || ex.Message.Contains("scope", StringComparison.OrdinalIgnoreCase))
        {
            // Expected for most apps: Marketplace Insights needs eBay approval.
            return new MarketResult { AccessDenied = true, Error = ex.Message };
        }
        catch (EbayApiException ex)
        {
            logger.LogWarning(ex, "Sold search failed for {Query}", query);
            return new MarketResult { Error = ex.Message };
        }
    }

    /// <summary>eBay's own sold-listings search page (always the live site; sold research is real data).</summary>
    public string SoldSearchUrl(string query, bool ungradedOnly) =>
        $"https://www.ebay.com/sch/i.html?_nkw={Uri.EscapeDataString(query)}&_sacat={_opt.CategoryId}&LH_Sold=1&LH_Complete=1" +
        (ungradedOnly ? "&Graded=No" : "");

    /// <summary>eBay's active Buy It Now search page.</summary>
    public string ActiveSearchUrl(string query) =>
        $"https://www.ebay.com/sch/i.html?_nkw={Uri.EscapeDataString(query)}&_sacat={_opt.CategoryId}&LH_BIN=1";

    /// <summary>Search text for this exact card: year/brand set, player, number, parallel (sport words dropped to widen matches).</summary>
    public static string CardQuery(InventoryCard card)
    {
        var set = string.Join(' ', card.CardSet.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(w => !SportWords.Contains(w.ToLowerInvariant())));
        var parallel = card.Parallel.Equals("Base", StringComparison.OrdinalIgnoreCase) ? "" : " " + card.Parallel;
        return $"{set} {card.PlayerName} {card.CardNumber.TrimStart('#')}{parallel}".Trim();
    }

    /// <summary>Search text for anything of this player (all years and sets).</summary>
    public static string PlayerQuery(InventoryCard card) => card.PlayerName.Trim();

    private static readonly HashSet<string> SportWords = ["football", "baseball", "basketball", "hockey", "soccer"];

    private async Task<JsonNode?> GetAsync(string pathAndQuery, string scope, CancellationToken ct)
    {
        string token;
        try
        {
            token = await auth.GetApplicationTokenAsync(scope, ct);
        }
        catch (EbayApiException ex)
        {
            // invalid_scope etc. comes back from the token endpoint.
            throw new EbayApiException(ex.Message, ex.StatusCode ?? 403);
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, _opt.ApiBaseUrl + pathAndQuery);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.TryAddWithoutValidation("X-EBAY-C-MARKETPLACE-ID", _opt.MarketplaceId);

        using var response = await http.SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            var (message, ids) = EbayApiException.Parse(text);
            throw new EbayApiException($"eBay {(int)response.StatusCode}: {message}", (int)response.StatusCode, ids);
        }

        if (string.IsNullOrWhiteSpace(text)) return null;
        try { return JsonNode.Parse(text); }
        catch (JsonException) { return null; }
    }

    private static decimal? Money(JsonNode? amount)
    {
        var raw = amount?["value"]?.ToString();
        return decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out var v) ? v : null;
    }
}
