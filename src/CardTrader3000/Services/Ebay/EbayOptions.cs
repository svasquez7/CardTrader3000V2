using CardTrader3000.Data.Entities;

namespace CardTrader3000.Services.Ebay;

/// <summary>
/// Bound from the "Ebay" config section. Keep ClientSecret in user-secrets:
/// <c>dotnet user-secrets set "Ebay:ClientSecret" "SBX-..."</c>
/// </summary>
public class EbayOptions
{
    public const string SectionName = "Ebay";

    /// <summary>Sandbox until you have production keys.</summary>
    public EbayEnvironment Environment { get; set; } = EbayEnvironment.Sandbox;

    /// <summary>App ID (Client ID) from the eBay developer portal keyset.</summary>
    public string ClientId { get; set; } = "";

    /// <summary>Cert ID (Client Secret) from the keyset.</summary>
    public string ClientSecret { get; set; } = "";

    /// <summary>
    /// The RuName (eBay Redirect URL name) from User Tokens → "Get a Token from eBay via Your Application".
    /// Its "auth accepted URL" must point to https://localhost:7243/ebay/callback.
    /// </summary>
    public string RuName { get; set; } = "";

    public string MarketplaceId { get; set; } = "EBAY_US";
    public string Currency { get; set; } = "USD";
    public string ContentLanguage { get; set; } = "en-US";

    /// <summary>Sports Trading Card Singles.</summary>
    public string CategoryId { get; set; } = "261328";

    /// <summary>Where uploaded listing photos are kept (relative to the app folder).</summary>
    public string ImageStoragePath { get; set; } = "App_Data/listing-images";

    /// <summary>Used only by "Create starter policies".</summary>
    public string StandardEnvelopeServiceCode { get; set; } = "US_eBayStandardEnvelope";

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(ClientId) && !string.IsNullOrWhiteSpace(ClientSecret) && !string.IsNullOrWhiteSpace(RuName);

    // ---- Hosts ----
    private bool Sandbox => Environment == EbayEnvironment.Sandbox;

    /// <summary>REST APIs (Inventory, Account, Marketing) and the OAuth token endpoint.</summary>
    public string ApiBaseUrl => Sandbox ? "https://api.sandbox.ebay.com/" : "https://api.ebay.com/";

    /// <summary>Media API (image upload).</summary>
    public string MediaBaseUrl => Sandbox ? "https://apim.sandbox.ebay.com/" : "https://apim.ebay.com/";

    /// <summary>User consent page.</summary>
    public string AuthorizeUrl => Sandbox ? "https://auth.sandbox.ebay.com/oauth2/authorize" : "https://auth.ebay.com/oauth2/authorize";

    public string ListingUrl(string listingId) =>
        Sandbox ? $"https://sandbox.ebay.com/itm/{listingId}" : $"https://www.ebay.com/itm/{listingId}";

    /// <summary>OAuth scopes (the same URLs are used for sandbox and production).</summary>
    public static readonly string[] Scopes =
    [
        "https://api.ebay.com/oauth/api_scope",
        "https://api.ebay.com/oauth/api_scope/sell.inventory",
        "https://api.ebay.com/oauth/api_scope/sell.account",
        "https://api.ebay.com/oauth/api_scope/sell.marketing"
    ];
}
