namespace CardTrader3000.Data.Entities;

public enum EbayEnvironment
{
    Sandbox = 0,
    Production = 1
}

public enum EbayListingStatus
{
    /// <summary>Saved locally, never sent to eBay.</summary>
    Draft = 0,

    /// <summary>Live on eBay.</summary>
    Published = 1,

    /// <summary>The last publish/update attempt failed. See <see cref="EbayListing.LastError"/>.</summary>
    Failed = 2,

    /// <summary>Ended (offer withdrawn).</summary>
    Ended = 3
}

/// <summary>
/// One row per eBay environment: the OAuth connection plus listing defaults.
/// Tokens are encrypted with ASP.NET Data Protection before they are stored.
/// </summary>
public class EbaySettings
{
    public int Id { get; set; }
    public EbayEnvironment Environment { get; set; }

    // ---- OAuth (encrypted) ----
    public string? RefreshTokenProtected { get; set; }
    public DateTime? RefreshTokenExpiresUtc { get; set; }
    public string? AccessTokenProtected { get; set; }
    public DateTime? AccessTokenExpiresUtc { get; set; }
    public DateTime? ConnectedUtc { get; set; }

    // ---- Listing defaults ----
    public string? DefaultFulfillmentPolicyId { get; set; }
    public string? DefaultReturnPolicyId { get; set; }
    public string? DefaultPaymentPolicyId { get; set; }

    /// <summary>Inventory location every offer ships from (required by eBay).</summary>
    public string? MerchantLocationKey { get; set; }

    /// <summary>General (cost-per-sale) Promoted Listings ad rate. 0 = don't promote.</summary>
    public decimal DefaultPromotionPercent { get; set; } = 2.0m;

    /// <summary>Best Offer: offers below this % of the price are auto-declined.</summary>
    public decimal BestOfferMinPercent { get; set; } = 80m;

    /// <summary>Best Offer: offers at or above this % of the price are auto-accepted.</summary>
    public decimal BestOfferAutoAcceptPercent { get; set; } = 90m;

    /// <summary>The general-strategy campaign new listings are added to (created on first use).</summary>
    public string? PromotionCampaignId { get; set; }

    /// <summary>The seller's eBay Store categories, one per line, as shown in the store.</summary>
    public string StoreCategories { get; set; } = DefaultStoreCategories;

    public const string DefaultStoreCategories =
        "Additional Sports & TCG\nApparel\nBaseball\nBasketball\nFootball\nOther";

    [System.ComponentModel.DataAnnotations.Schema.NotMapped]
    public List<string> StoreCategoryList =>
        // A blank value (e.g. a row created before this setting existed) means the defaults.
        (string.IsNullOrWhiteSpace(StoreCategories) ? DefaultStoreCategories : StoreCategories).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    public DateTime UpdatedUtc { get; set; }
}

/// <summary>An eBay listing for an inventory card. Editable as a draft, then published, revised or ended.</summary>
public class EbayListing
{
    public int Id { get; set; }

    public int? InventoryCardId { get; set; }
    public InventoryCard? InventoryCard { get; set; }

    public EbayEnvironment Environment { get; set; }
    public EbayListingStatus Status { get; set; } = EbayListingStatus.Draft;

    /// <summary>Inventory API SKU, e.g. "CT3K-123".</summary>
    public required string Sku { get; set; }

    // ---- Listing content ----
    public required string Title { get; set; }
    public required string Description { get; set; }
    public decimal Price { get; set; }
    public int Quantity { get; set; } = 1;
    public string CategoryId { get; set; } = "261328";

    /// <summary>Ungraded card condition descriptor value (40001). 400010 = Near Mint or Better.</summary>
    public string CardConditionValueId { get; set; } = "400010";

    /// <summary>Item specifics as JSON: { "Player/Athlete": ["Von Miller"], ... }.</summary>
    public string AspectsJson { get; set; } = "{}";

    // ---- Policies, offers, promotion ----
    public string? FulfillmentPolicyId { get; set; }
    public string? ReturnPolicyId { get; set; }
    public string? PaymentPolicyId { get; set; }

    /// <summary>Best Offer is always on; these are the % of price used to compute the eBay amounts.</summary>
    public decimal BestOfferMinPercent { get; set; } = 80m;
    public decimal BestOfferAutoAcceptPercent { get; set; } = 90m;

    /// <summary>General Promoted Listings ad rate. 0 = not promoted.</summary>
    public decimal PromotionPercent { get; set; } = 2.0m;

    /// <summary>eBay Store category the listing is filed under (null = none).</summary>
    public string? StoreCategoryName { get; set; }

    // ---- eBay identifiers ----
    public string? OfferId { get; set; }
    public string? ListingId { get; set; }
    public string? CampaignId { get; set; }
    public string? AdId { get; set; }

    /// <summary>Last error, or a warning (e.g. promotion failed while the listing itself went live).</summary>
    public string? LastError { get; set; }

    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }
    public DateTime? PublishedUtc { get; set; }

    public List<EbayListingImage> Images { get; set; } = [];
}

/// <summary>A listing photo: a file uploaded into the app, or an image URL the user pasted.</summary>
public class EbayListingImage
{
    public int Id { get; set; }
    public int EbayListingId { get; set; }
    public EbayListing EbayListing { get; set; } = null!;

    public int SortOrder { get; set; }

    /// <summary>File name in the local image store (uploaded photos). Null for URL images.</summary>
    public string? LocalFileName { get; set; }

    /// <summary>HTTPS URL the user supplied. Sent to eBay as-is.</summary>
    public string? SourceUrl { get; set; }

    /// <summary>eBay Picture Services URL after upload (Media API). Reused until it expires.</summary>
    public string? EbayUrl { get; set; }
    public DateTime? EbayUrlExpiresUtc { get; set; }
}
