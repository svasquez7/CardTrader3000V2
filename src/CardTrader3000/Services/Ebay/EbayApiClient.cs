using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace CardTrader3000.Services.Ebay;

public sealed record EbayPolicy(string Id, string Name, string? Description);
public sealed record EbayLocation(string Key, string Name, string? PostalCode, string Status);
public sealed record EbayCampaign(string Id, string Name, string Status, string? FundingModel);
public sealed record EbayOfferSummary(string OfferId, string Status, string? ListingId);
public sealed record EbayImageUpload(string Url, DateTime? ExpiresUtc);

/// <summary>
/// Thin wrapper over the eBay Sell REST APIs used for listing: Account (business policies),
/// Inventory (location, inventory item, offer, publish), Media (images) and Marketing
/// (Promoted Listings general strategy). Every call uses the signed-in user's token.
/// </summary>
public sealed class EbayApiClient(HttpClient http, EbayAuthService auth, IOptions<EbayOptions> options)
{
    private readonly EbayOptions _opt = options.Value;

    private static readonly JsonSerializerOptions WriteJson = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly object[] AllCategoryTypes = [new { name = "ALL_EXCLUDING_MOTORS_VEHICLES" }];

    // ======================= Account API: business policies =======================

    public async Task<List<EbayPolicy>> GetFulfillmentPoliciesAsync(CancellationToken ct = default) =>
        Policies(await SendAsync(HttpMethod.Get, $"sell/account/v1/fulfillment_policy?marketplace_id={_opt.MarketplaceId}", ct: ct),
            "fulfillmentPolicies", "fulfillmentPolicyId");

    public async Task<List<EbayPolicy>> GetReturnPoliciesAsync(CancellationToken ct = default) =>
        Policies(await SendAsync(HttpMethod.Get, $"sell/account/v1/return_policy?marketplace_id={_opt.MarketplaceId}", ct: ct),
            "returnPolicies", "returnPolicyId");

    public async Task<List<EbayPolicy>> GetPaymentPoliciesAsync(CancellationToken ct = default) =>
        Policies(await SendAsync(HttpMethod.Get, $"sell/account/v1/payment_policy?marketplace_id={_opt.MarketplaceId}", ct: ct),
            "paymentPolicies", "paymentPolicyId");

    /// <summary>Business policies must be enabled on the account before they can be created or used.</summary>
    public Task OptInToBusinessPoliciesAsync(CancellationToken ct = default) =>
        SendAsync(HttpMethod.Post, "sell/account/v1/program/opt_in", new { programType = "SELLING_POLICY_MANAGEMENT" }, ct: ct);

    /// <summary>Free shipping via eBay Standard Envelope, 1 business day handling.</summary>
    public async Task<string> CreateStandardEnvelopeFulfillmentPolicyAsync(string name, CancellationToken ct = default)
    {
        var body = new
        {
            name,
            marketplaceId = _opt.MarketplaceId,
            categoryTypes = AllCategoryTypes,
            handlingTime = new { value = 1, unit = "DAY" },
            shippingOptions = new[]
            {
                new
                {
                    optionType = "DOMESTIC",
                    costType = "FLAT_RATE",
                    shippingServices = new[]
                    {
                        new
                        {
                            sortOrder = 1,
                            shippingServiceCode = _opt.StandardEnvelopeServiceCode,
                            freeShipping = true,
                            shippingCost = new { value = "0.00", currency = _opt.Currency }
                        }
                    }
                }
            }
        };
        var result = await SendAsync(HttpMethod.Post, "sell/account/v1/fulfillment_policy", body, ct: ct);
        return result.Json?["fulfillmentPolicyId"]?.GetValue<string>() ?? throw new EbayApiException("eBay didn't return a fulfillment policy id.");
    }

    /// <summary>30-day returns, buyer pays return shipping, money back.</summary>
    public async Task<string> CreateReturnPolicyAsync(string name, bool returnsAccepted, int returnDays, CancellationToken ct = default)
    {
        object body = returnsAccepted
            ? new
            {
                name,
                marketplaceId = _opt.MarketplaceId,
                categoryTypes = AllCategoryTypes,
                returnsAccepted = true,
                returnPeriod = new { value = returnDays, unit = "DAY" },
                returnShippingCostPayer = "BUYER",
                refundMethod = "MONEY_BACK"
            }
            : new
            {
                name,
                marketplaceId = _opt.MarketplaceId,
                categoryTypes = AllCategoryTypes,
                returnsAccepted = false
            };
        var result = await SendAsync(HttpMethod.Post, "sell/account/v1/return_policy", body, ct: ct);
        return result.Json?["returnPolicyId"]?.GetValue<string>() ?? throw new EbayApiException("eBay didn't return a return policy id.");
    }

    /// <summary>Managed payments with immediate payment required.</summary>
    public async Task<string> CreatePaymentPolicyAsync(string name, CancellationToken ct = default)
    {
        var body = new
        {
            name,
            marketplaceId = _opt.MarketplaceId,
            categoryTypes = AllCategoryTypes,
            immediatePay = true
        };
        var result = await SendAsync(HttpMethod.Post, "sell/account/v1/payment_policy", body, ct: ct);
        return result.Json?["paymentPolicyId"]?.GetValue<string>() ?? throw new EbayApiException("eBay didn't return a payment policy id.");
    }

    // ======================= Inventory API =======================

    public async Task<List<EbayLocation>> GetInventoryLocationsAsync(CancellationToken ct = default)
    {
        var result = await SendAsync(HttpMethod.Get, "sell/inventory/v1/location?limit=100", ct: ct);
        return (result.Json?["locations"] as JsonArray ?? new JsonArray())
            .Where(l => l is not null)
            .Select(l => new EbayLocation(
                l!["merchantLocationKey"]?.GetValue<string>() ?? "",
                l["name"]?.GetValue<string>() ?? "",
                l["location"]?["address"]?["postalCode"]?.GetValue<string>(),
                l["merchantLocationStatus"]?.GetValue<string>() ?? ""))
            .ToList();
    }

    /// <summary>Creates a warehouse location identified by postal code + country.</summary>
    public Task CreateInventoryLocationAsync(string key, string name, string postalCode, string country, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Post, $"sell/inventory/v1/location/{Uri.EscapeDataString(key)}", new
        {
            location = new { address = new { postalCode, country } },
            locationTypes = new[] { "WAREHOUSE" },
            name,
            merchantLocationStatus = "ENABLED"
        }, ct: ct);

    /// <summary>Creates or replaces the inventory item (product details, condition, quantity) for a SKU.</summary>
    public Task PutInventoryItemAsync(string sku, object inventoryItem, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Put, $"sell/inventory/v1/inventory_item/{Uri.EscapeDataString(sku)}", inventoryItem, contentLanguage: true, ct: ct);

    public async Task<string> CreateOfferAsync(object offer, CancellationToken ct = default)
    {
        var result = await SendAsync(HttpMethod.Post, "sell/inventory/v1/offer", offer, contentLanguage: true, ct: ct);
        return result.Json?["offerId"]?.GetValue<string>() ?? throw new EbayApiException("eBay didn't return an offer id.");
    }

    /// <summary>Updates an offer. If the offer is published, eBay revises the live listing.</summary>
    public Task UpdateOfferAsync(string offerId, object offer, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Put, $"sell/inventory/v1/offer/{Uri.EscapeDataString(offerId)}", offer, contentLanguage: true, ct: ct);

    public async Task<List<EbayOfferSummary>> GetOffersForSkuAsync(string sku, CancellationToken ct = default)
    {
        try
        {
            var result = await SendAsync(HttpMethod.Get,
                $"sell/inventory/v1/offer?sku={Uri.EscapeDataString(sku)}&marketplace_id={_opt.MarketplaceId}", ct: ct);
            return (result.Json?["offers"] as JsonArray ?? new JsonArray())
                .Where(o => o is not null)
                .Select(o => new EbayOfferSummary(
                    o!["offerId"]?.GetValue<string>() ?? "",
                    o["status"]?.GetValue<string>() ?? "",
                    o["listing"]?["listingId"]?.GetValue<string>()))
                .ToList();
        }
        catch (EbayApiException ex) when (ex.StatusCode == 404)
        {
            return [];
        }
    }

    /// <summary>Publishes the offer and returns the eBay listing id.</summary>
    public async Task<string> PublishOfferAsync(string offerId, CancellationToken ct = default)
    {
        var result = await SendAsync(HttpMethod.Post, $"sell/inventory/v1/offer/{Uri.EscapeDataString(offerId)}/publish", ct: ct);
        return result.Json?["listingId"]?.GetValue<string>() ?? throw new EbayApiException("eBay didn't return a listing id.");
    }

    /// <summary>Ends the listing. The offer goes back to unpublished and can be published again.</summary>
    public Task WithdrawOfferAsync(string offerId, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Post, $"sell/inventory/v1/offer/{Uri.EscapeDataString(offerId)}/withdraw", ct: ct);

    // ======================= Media API: images =======================

    /// <summary>Uploads a photo to eBay Picture Services and returns its HTTPS URL for product.imageUrls.</summary>
    public async Task<EbayImageUpload> UploadImageAsync(byte[] bytes, string fileName, string contentType, CancellationToken ct = default)
    {
        var token = await auth.GetAccessTokenAsync(ct);

        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        form.Add(file, "image", fileName);

        using var request = new HttpRequestMessage(HttpMethod.Post, _opt.MediaBaseUrl + "commerce/media/v1_beta/image/create_image_from_file")
        {
            Content = form
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new EbayApiException(
                $"Couldn't reach eBay's image service at {_opt.MediaBaseUrl} ({ex.Message}). " +
                "The Media API sandbox has been unreliable; add the photo as an image URL instead.");
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
            {
                var (message, ids) = EbayApiException.Parse(body);
                throw new EbayApiException($"Image upload failed ({(int)response.StatusCode}): {message}", (int)response.StatusCode, ids);
            }

            var json = string.IsNullOrWhiteSpace(body) ? null : JsonNode.Parse(body);
            var url = json?["imageUrl"]?.GetValue<string>();

            // Some responses only carry the Location header; fetch the image resource for its URL.
            if (url is null && response.Headers.Location is { } location)
            {
                var image = await SendAbsoluteAsync(HttpMethod.Get, location.IsAbsoluteUri ? location.ToString() : _opt.MediaBaseUrl + location.ToString().TrimStart('/'), ct);
                json = image;
                url = image?["imageUrl"]?.GetValue<string>();
            }

            if (url is null) throw new EbayApiException("eBay accepted the image but didn't return its URL.");

            DateTime? expires = DateTime.TryParse(json?["expirationDate"]?.GetValue<string>(), null,
                System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var e) ? e : null;
            return new EbayImageUpload(url, expires);
        }
    }

    // ======================= Marketing API: Promoted Listings (general) =======================

    public async Task<List<EbayCampaign>> GetCampaignsAsync(CancellationToken ct = default)
    {
        var result = await SendAsync(HttpMethod.Get, "sell/marketing/v1/ad_campaign?limit=100", ct: ct);
        return (result.Json?["campaigns"] as JsonArray ?? new JsonArray())
            .Where(c => c is not null)
            .Select(c => new EbayCampaign(
                c!["campaignId"]?.GetValue<string>() ?? "",
                c["campaignName"]?.GetValue<string>() ?? "",
                c["campaignStatus"]?.GetValue<string>() ?? "",
                c["fundingStrategy"]?["fundingModel"]?.GetValue<string>()))
            .ToList();
    }

    /// <summary>Creates a general-strategy (cost per sale) campaign and returns its id.</summary>
    public async Task<string> CreateGeneralCampaignAsync(string name, decimal bidPercentage, CancellationToken ct = default)
    {
        var result = await SendAsync(HttpMethod.Post, "sell/marketing/v1/ad_campaign", new
        {
            campaignName = name,
            marketplaceId = _opt.MarketplaceId,
            startDate = DateTime.UtcNow.AddMinutes(1).ToString("yyyy-MM-ddTHH:mm:ss'Z'"),
            fundingStrategy = new
            {
                fundingModel = "COST_PER_SALE",
                bidPercentage = FormatPercent(bidPercentage)
            }
        }, ct: ct);

        // The new campaign's URI (ending in its id) is in the Location header.
        var location = result.Location?.ToString();
        var id = location?.TrimEnd('/').Split('/').LastOrDefault();
        return string.IsNullOrEmpty(id) ? throw new EbayApiException("eBay created the campaign but didn't return its id.") : id;
    }

    /// <summary>Adds a listing to a general campaign at the given ad rate. Returns the ad id.</summary>
    public async Task<string?> CreateAdAsync(string campaignId, string listingId, decimal bidPercentage, CancellationToken ct = default)
    {
        var result = await SendAsync(HttpMethod.Post,
            $"sell/marketing/v1/ad_campaign/{Uri.EscapeDataString(campaignId)}/bulk_create_ads_by_listing_id", new
            {
                requests = new[] { new { listingId, bidPercentage = FormatPercent(bidPercentage) } }
            }, ct: ct);

        var first = (result.Json?["responses"] as JsonArray)?.FirstOrDefault();
        if (first?["errors"] is JsonArray { Count: > 0 } errors)
            throw new EbayApiException("Promotion failed: " + EbayApiException.Describe(new JsonObject { ["errors"] = errors.DeepClone() }.ToJsonString()));

        return first?["adId"]?.GetValue<string>();
    }

    // ======================= Plumbing =======================

    private sealed record ApiResult(JsonNode? Json, Uri? Location);

    private async Task<ApiResult> SendAsync(HttpMethod method, string path, object? body = null, bool contentLanguage = false, CancellationToken ct = default)
    {
        var token = await auth.GetAccessTokenAsync(ct);

        using var request = new HttpRequestMessage(method, _opt.ApiBaseUrl + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.TryAddWithoutValidation("X-EBAY-C-MARKETPLACE-ID", _opt.MarketplaceId);

        if (body is not null)
        {
            request.Content = new StringContent(JsonSerializer.Serialize(body, WriteJson), Encoding.UTF8, "application/json");
            if (contentLanguage) request.Content.Headers.ContentLanguage.Add(_opt.ContentLanguage);
        }

        using var response = await http.SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            var (message, ids) = EbayApiException.Parse(text);
            throw new EbayApiException($"eBay {(int)response.StatusCode}: {message}", (int)response.StatusCode, ids);
        }

        JsonNode? json = null;
        if (!string.IsNullOrWhiteSpace(text) && response.StatusCode != HttpStatusCode.NoContent)
        {
            try { json = JsonNode.Parse(text); } catch (JsonException) { }
        }
        return new ApiResult(json, response.Headers.Location);
    }

    private async Task<JsonNode?> SendAbsoluteAsync(HttpMethod method, string url, CancellationToken ct)
    {
        var token = await auth.GetAccessTokenAsync(ct);
        using var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await http.SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new EbayApiException($"eBay {(int)response.StatusCode}: {EbayApiException.Describe(text)}", (int)response.StatusCode);
        return string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text);
    }

    private static List<EbayPolicy> Policies(ApiResult result, string arrayName, string idName) =>
        (result.Json?[arrayName] as JsonArray ?? new JsonArray())
            .Where(p => p is not null)
            .Select(p => new EbayPolicy(
                p![idName]?.GetValue<string>() ?? "",
                p["name"]?.GetValue<string>() ?? "",
                p["description"]?.GetValue<string>()))
            .ToList();

    private static string FormatPercent(decimal value) =>
        Math.Round(value, 1, MidpointRounding.AwayFromZero).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
}
