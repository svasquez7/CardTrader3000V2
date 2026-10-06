using System.Net;
using CardTrader3000.Data;
using CardTrader3000.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CardTrader3000.Services.Ebay;

public sealed record PublishResult(bool Success, string Message, string? ListingUrl, IReadOnlyList<string> Warnings);

/// <summary>
/// Listing workflow: build a draft from an inventory card with the seller's defaults, manage its
/// photos, then publish it to eBay (inventory item → offer → publish → promote), revise it, or end it.
/// Every listing is Buy It Now (fixed price, Good 'Til Cancelled) with Best Offer enabled.
/// </summary>
public sealed class EbayListingService(
    IDbContextFactory<AppDbContext> dbFactory,
    EbayApiClient api,
    EbayAuthService auth,
    ListingImageStore images,
    IOptions<EbayOptions> options,
    ILogger<EbayListingService> logger)
{
    public const string CampaignName = "CardTrader3000 General";
    public const decimal MinPromotionPercent = 2.0m;   // eBay's minimum general ad rate
    public const decimal StandardEnvelopeMaxPrice = 20m; // eBay Standard Envelope is for items up to $20

    private readonly EbayOptions _opt = options.Value;

    // ======================= Settings =======================

    public async Task<EbaySettings> GetSettingsAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await auth.GetOrCreateSettingsAsync(db, ct);
    }

    /// <summary>Saves the listing defaults (not the OAuth tokens).</summary>
    public async Task SaveDefaultsAsync(EbaySettings edited, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var s = await auth.GetOrCreateSettingsAsync(db, ct);

        s.DefaultFulfillmentPolicyId = Blank(edited.DefaultFulfillmentPolicyId);
        s.DefaultReturnPolicyId = Blank(edited.DefaultReturnPolicyId);
        s.DefaultPaymentPolicyId = Blank(edited.DefaultPaymentPolicyId);
        s.MerchantLocationKey = Blank(edited.MerchantLocationKey);
        s.DefaultPromotionPercent = edited.DefaultPromotionPercent;
        s.BestOfferMinPercent = edited.BestOfferMinPercent;
        s.BestOfferAutoAcceptPercent = edited.BestOfferAutoAcceptPercent;
        s.StoreCategories = string.Join('\n', edited.StoreCategoryList);
        s.UpdatedUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    // ======================= Drafts =======================

    /// <summary>The card's current (not ended) listing in this environment, or a new draft built from the card.</summary>
    public async Task<int> GetOrCreateDraftForCardAsync(int cardId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var existing = await db.EbayListings
            .Where(l => l.InventoryCardId == cardId && l.Environment == _opt.Environment && l.Status != EbayListingStatus.Ended)
            .OrderByDescending(l => l.Id)
            .Select(l => (int?)l.Id)
            .FirstOrDefaultAsync(ct);
        if (existing is not null) return existing.Value;

        var card = await db.InventoryCards.FindAsync([cardId], ct)
                   ?? throw new InvalidOperationException("That card no longer exists.");
        var settings = await auth.GetOrCreateSettingsAsync(db, ct);
        var now = DateTime.UtcNow;

        // SKUs must be unique per environment; a re-list after "Ended" gets a suffix.
        var baseSku = $"CT3K-{card.Id}";
        var sku = baseSku;
        for (var n = 2; await db.EbayListings.AnyAsync(l => l.Environment == _opt.Environment && l.Sku == sku, ct); n++)
            sku = $"{baseSku}-{n}";

        var listing = new EbayListing
        {
            InventoryCardId = card.Id,
            Environment = _opt.Environment,
            Status = EbayListingStatus.Draft,
            Sku = sku,
            Title = Truncate(string.IsNullOrWhiteSpace(card.EbayTitle) ? FallbackTitle(card) : card.EbayTitle, 80),
            Description = string.IsNullOrWhiteSpace(card.EbayDescription) ? FallbackDescription(card) : card.EbayDescription,
            Price = card.EstimatedListPrice,
            Quantity = Math.Max(1, card.Quantity),
            CategoryId = _opt.CategoryId,
            CardConditionValueId = "400010", // Near Mint or Better
            AspectsJson = CardAspects.Serialize(CardAspects.FromCard(card)),
            FulfillmentPolicyId = settings.DefaultFulfillmentPolicyId,
            ReturnPolicyId = settings.DefaultReturnPolicyId,
            PaymentPolicyId = settings.DefaultPaymentPolicyId,
            BestOfferMinPercent = settings.BestOfferMinPercent,
            BestOfferAutoAcceptPercent = settings.BestOfferAutoAcceptPercent,
            PromotionPercent = settings.DefaultPromotionPercent,
            StoreCategoryName = DefaultStoreCategory(card, settings.StoreCategoryList),
            CreatedUtc = now,
            UpdatedUtc = now
        };

        db.EbayListings.Add(listing);
        await db.SaveChangesAsync(ct);
        return listing.Id;
    }

    public async Task<EbayListing?> GetAsync(int id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var listing = await db.EbayListings.AsNoTracking()
            .Include(l => l.Images)
            .Include(l => l.InventoryCard)
            .FirstOrDefaultAsync(l => l.Id == id, ct);
        listing?.Images.Sort((a, b) => a.SortOrder.CompareTo(b.SortOrder));
        return listing;
    }

    public async Task<List<EbayListing>> ListAsync(EbayListingStatus? status = null, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var query = db.EbayListings.AsNoTracking()
            .Include(l => l.Images)
            .Where(l => l.Environment == _opt.Environment);
        if (status is { } s) query = query.Where(l => l.Status == s);
        return await query.OrderByDescending(l => l.UpdatedUtc).Take(500).ToListAsync(ct);
    }

    /// <summary>Saves the editable fields of a listing (images are managed separately).</summary>
    public async Task SaveAsync(EbayListing edited, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var l = await db.EbayListings.FirstOrDefaultAsync(x => x.Id == edited.Id, ct)
                ?? throw new InvalidOperationException("Listing not found.");

        l.Title = Truncate(edited.Title.Trim(), 80);
        l.Description = edited.Description.Trim();
        l.Price = Math.Round(edited.Price, 2, MidpointRounding.AwayFromZero);
        l.Quantity = edited.Quantity;
        l.CardConditionValueId = edited.CardConditionValueId;
        l.AspectsJson = edited.AspectsJson;
        l.FulfillmentPolicyId = Blank(edited.FulfillmentPolicyId);
        l.ReturnPolicyId = Blank(edited.ReturnPolicyId);
        l.PaymentPolicyId = Blank(edited.PaymentPolicyId);
        l.BestOfferMinPercent = edited.BestOfferMinPercent;
        l.BestOfferAutoAcceptPercent = edited.BestOfferAutoAcceptPercent;
        l.PromotionPercent = edited.PromotionPercent;
        l.StoreCategoryName = Blank(edited.StoreCategoryName);
        l.UpdatedUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Deletes a listing that isn't live on eBay, with its stored photos.</summary>
    public async Task<bool> DeleteAsync(int id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var l = await db.EbayListings.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (l is null) return false;
        if (l.Status == EbayListingStatus.Published)
            throw new InvalidOperationException("End the listing on eBay before deleting it.");

        db.EbayListings.Remove(l);
        await db.SaveChangesAsync(ct);
        images.DeleteAll(id);
        return true;
    }

    // ======================= Images =======================

    public async Task AddUploadedImageAsync(int listingId, Stream content, string fileName, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var count = await db.EbayListingImages.CountAsync(i => i.EbayListingId == listingId, ct);
        if (count >= ListingImageStore.MaxImagesPerListing)
            throw new InvalidOperationException($"eBay allows up to {ListingImageStore.MaxImagesPerListing} photos per listing.");

        var stored = await images.SaveAsync(listingId, content, fileName, ct);
        db.EbayListingImages.Add(new EbayListingImage { EbayListingId = listingId, LocalFileName = stored, SortOrder = count });
        await Touch(db, listingId, ct);
        await db.SaveChangesAsync(ct);
    }

    public async Task AddImageUrlAsync(int listingId, string url, CancellationToken ct = default)
    {
        url = url.Trim();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException("Image URLs must be full https:// links.");

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var count = await db.EbayListingImages.CountAsync(i => i.EbayListingId == listingId, ct);
        if (count >= ListingImageStore.MaxImagesPerListing)
            throw new InvalidOperationException($"eBay allows up to {ListingImageStore.MaxImagesPerListing} photos per listing.");

        db.EbayListingImages.Add(new EbayListingImage { EbayListingId = listingId, SourceUrl = uri.ToString(), SortOrder = count });
        await Touch(db, listingId, ct);
        await db.SaveChangesAsync(ct);
    }

    public async Task RemoveImageAsync(int imageId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var img = await db.EbayListingImages.FirstOrDefaultAsync(i => i.Id == imageId, ct);
        if (img is null) return;

        db.EbayListingImages.Remove(img);
        await Touch(db, img.EbayListingId, ct);
        await db.SaveChangesAsync(ct);
        if (img.LocalFileName is not null) images.Delete(img.EbayListingId, img.LocalFileName);
        await RenumberAsync(img.EbayListingId, ct);
    }

    /// <summary>Moves an image one position earlier (-1) or later (+1). The first image is the gallery photo.</summary>
    public async Task MoveImageAsync(int imageId, int direction, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var img = await db.EbayListingImages.FirstOrDefaultAsync(i => i.Id == imageId, ct);
        if (img is null) return;

        var all = await db.EbayListingImages.Where(i => i.EbayListingId == img.EbayListingId).OrderBy(i => i.SortOrder).ToListAsync(ct);
        var index = all.FindIndex(i => i.Id == imageId);
        var target = index + Math.Sign(direction);
        if (target < 0 || target >= all.Count) return;

        (all[index], all[target]) = (all[target], all[index]);
        for (var i = 0; i < all.Count; i++) all[i].SortOrder = i;
        await Touch(db, img.EbayListingId, ct);
        await db.SaveChangesAsync(ct);
    }

    // ======================= Validation =======================

    /// <summary>Problems that block publishing (errors) and things worth a second look (warnings).</summary>
    public async Task<(List<string> Errors, List<string> Warnings)> ValidateAsync(EbayListing l, CancellationToken ct = default)
    {
        var errors = new List<string>();
        var warnings = new List<string>();

        if (string.IsNullOrWhiteSpace(l.Title)) errors.Add("Title is required.");
        else if (l.Title.Length > 80) errors.Add("Title must be 80 characters or fewer.");
        if (string.IsNullOrWhiteSpace(l.Description)) errors.Add("Description is required.");
        else if (ToHtml(l.Description).Length > 4000) errors.Add("Description is too long for eBay (4,000 characters including formatting).");
        if (l.Price <= 0) errors.Add("Price must be greater than $0.");
        if (l.Quantity < 1) errors.Add("Quantity must be at least 1.");
        if (l.Images.Count == 0) errors.Add("Add at least one photo.");
        var aspects = CardAspects.Deserialize(l.AspectsJson);
        if (!aspects.TryGetValue("Sport", out var sport) || sport.All(string.IsNullOrWhiteSpace))
            errors.Add("eBay requires the Sport item specific. Pick one under Item specifics (e.g. Football).");
        if (l.FulfillmentPolicyId is null) errors.Add("Choose a shipping policy.");
        if (l.ReturnPolicyId is null) errors.Add("Choose a return policy.");
        if (l.PaymentPolicyId is null) errors.Add("Choose a payment policy.");

        if (l.BestOfferMinPercent is <= 0 or >= 100 || l.BestOfferAutoAcceptPercent is <= 0 or >= 100)
            errors.Add("Best Offer percentages must be between 0 and 100.");
        else if (l.BestOfferMinPercent >= l.BestOfferAutoAcceptPercent)
            errors.Add("The Best Offer minimum must be lower than the auto-accept percentage.");

        if (l.PromotionPercent != 0 && l.PromotionPercent is < MinPromotionPercent or > 100)
            errors.Add($"Promotion ad rate must be 0 (off) or between {MinPromotionPercent}% and 100%.");

        var settings = await GetSettingsAsync(ct);
        if (settings.MerchantLocationKey is null) errors.Add("Set up an inventory location in eBay Settings.");
        if (settings.RefreshTokenProtected is null) errors.Add("Connect your eBay account in eBay Settings.");

        if (l.Price > StandardEnvelopeMaxPrice)
            warnings.Add($"eBay Standard Envelope is only allowed for items up to ${StandardEnvelopeMaxPrice:0}. Use a tracked shipping policy for this price.");
        if (l.InventoryCard is { } card && l.Quantity > card.Quantity)
            warnings.Add($"Listing quantity ({l.Quantity}) is more than you have in stock ({card.Quantity}).");

        return (errors, warnings);
    }

    // ======================= Publish / revise / end =======================

    /// <summary>
    /// Sends the listing to eBay. New or ended listings are published; live listings are revised in place.
    /// Each step's outcome is saved, so a retry after a failure picks up where it stopped.
    /// </summary>
    public async Task<PublishResult> PublishAsync(int listingId, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var listing = await GetAsync(listingId, ct) ?? throw new InvalidOperationException("Listing not found.");
        var (errors, warningsList) = await ValidateAsync(listing, ct);
        if (errors.Count > 0)
            return new PublishResult(false, string.Join(" ", errors), null, warningsList);

        var warnings = new List<string>(warningsList);
        var settings = await GetSettingsAsync(ct);
        var wasLive = listing.Status == EbayListingStatus.Published && listing.ListingId is not null;

        try
        {
            // 1. Photos → eBay Picture Services (or the URLs as given).
            progress?.Report("Uploading photos…");
            var imageUrls = await ResolveImageUrlsAsync(listing, ct);

            // 2. Inventory item: product details, condition, stock.
            progress?.Report("Saving the inventory item…");
            var description = ToHtml(listing.Description);
            await api.PutInventoryItemAsync(listing.Sku, new
            {
                availability = new { shipToLocationAvailability = new { quantity = listing.Quantity } },
                condition = "USED_VERY_GOOD", // = Ungraded (4000) for trading cards
                conditionDescriptors = new[] { new { name = "40001", values = new[] { listing.CardConditionValueId } } },
                product = new
                {
                    title = listing.Title,
                    description,
                    aspects = CardAspects.Deserialize(listing.AspectsJson),
                    imageUrls
                }
            }, ct);

            // 3. Offer: Buy It Now + Best Offer, policies, price.
            progress?.Report(listing.OfferId is null ? "Creating the offer…" : "Updating the offer…");
            var offerId = await UpsertOfferAsync(listing, settings, description, ct);
            await UpdateAsync(listingId, l => l.OfferId = offerId, ct);

            // 4. Publish (a live listing was already revised by the offer update).
            var listingIdOnEbay = listing.ListingId;
            if (!wasLive)
            {
                progress?.Report("Publishing to eBay…");
                listingIdOnEbay = await api.PublishOfferAsync(offerId, ct);
            }

            await UpdateAsync(listingId, l =>
            {
                l.ListingId = listingIdOnEbay;
                l.Status = EbayListingStatus.Published;
                l.PublishedUtc ??= DateTime.UtcNow;
                l.LastError = null;
            }, ct);
            await MarkCardListedAsync(listing.InventoryCardId, ct);

            // 5. Promote (general strategy). A failure here doesn't undo the live listing.
            if (listing.PromotionPercent > 0 && (!wasLive || listing.AdId is null))
            {
                progress?.Report($"Promoting at {listing.PromotionPercent:0.0#}%…");
                try
                {
                    var (campaignId, adId) = await PromoteAsync(listingIdOnEbay!, listing.PromotionPercent, settings, ct);
                    await UpdateAsync(listingId, l => { l.CampaignId = campaignId; l.AdId = adId; }, ct);
                }
                catch (EbayApiException ex)
                {
                    logger.LogWarning(ex, "Promotion failed for listing {ListingId}", listingIdOnEbay);
                    warnings.Add($"Listing is live, but promotion failed: {ex.Message}");
                    await UpdateAsync(listingId, l => l.LastError = "Promotion failed: " + ex.Message, ct);
                }
            }

            var url = _opt.ListingUrl(listingIdOnEbay!);
            return new PublishResult(true, wasLive ? "Listing updated on eBay." : "Listing is live on eBay.", url, warnings);
        }
        catch (EbayApiException ex)
        {
            logger.LogError(ex, "Publishing listing {Id} failed", listingId);
            await UpdateAsync(listingId, l =>
            {
                l.LastError = ex.Message;
                if (!wasLive) l.Status = EbayListingStatus.Failed;
            }, ct);
            return new PublishResult(false, ex.Message, null, warnings);
        }
    }

    /// <summary>Ends the eBay listing (withdraws the offer). The card goes back to In Stock.</summary>
    public async Task<PublishResult> EndAsync(int listingId, CancellationToken ct = default)
    {
        var listing = await GetAsync(listingId, ct) ?? throw new InvalidOperationException("Listing not found.");
        if (listing.OfferId is null || listing.Status != EbayListingStatus.Published)
            return new PublishResult(false, "This listing isn't live on eBay.", null, []);

        try
        {
            await api.WithdrawOfferAsync(listing.OfferId, ct);
        }
        catch (EbayApiException ex)
        {
            await UpdateAsync(listingId, l => l.LastError = ex.Message, ct);
            return new PublishResult(false, ex.Message, null, []);
        }

        await UpdateAsync(listingId, l =>
        {
            l.Status = EbayListingStatus.Ended;
            l.AdId = null;
            l.LastError = null;
        }, ct);

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if (listing.InventoryCardId is { } cardId && await db.InventoryCards.FindAsync([cardId], ct) is { Status: InventoryStatus.Listed } card)
        {
            card.Status = card.Quantity > 0 ? InventoryStatus.InStock : InventoryStatus.Sold;
            card.UpdatedUtc = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
        }

        return new PublishResult(true, "Listing ended on eBay.", null, []);
    }

    /// <summary>Best Offer amounts eBay will use for a price.</summary>
    public static (decimal AutoDecline, decimal AutoAccept) BestOfferAmounts(decimal price, decimal minPercent, decimal acceptPercent) =>
        (Math.Round(price * minPercent / 100m, 2, MidpointRounding.AwayFromZero),
         Math.Round(price * acceptPercent / 100m, 2, MidpointRounding.AwayFromZero));

    // ======================= Internals =======================

    private async Task<List<string>> ResolveImageUrlsAsync(EbayListing listing, CancellationToken ct)
    {
        var urls = new List<string>();
        foreach (var img in listing.Images.OrderBy(i => i.SortOrder))
        {
            if (img.SourceUrl is not null)
            {
                urls.Add(img.SourceUrl);
                continue;
            }

            if (img.EbayUrl is not null && (img.EbayUrlExpiresUtc is null || img.EbayUrlExpiresUtc > DateTime.UtcNow.AddDays(1)))
            {
                urls.Add(img.EbayUrl);
                continue;
            }

            if (img.LocalFileName is null) continue;
            var file = images.Find(listing.Id, img.LocalFileName)
                       ?? throw new EbayApiException($"Photo {img.SortOrder + 1} is missing from disk. Remove it and upload it again.");

            var upload = await api.UploadImageAsync(await File.ReadAllBytesAsync(file.Path, ct), img.LocalFileName, file.ContentType, ct);

            await using var db = await dbFactory.CreateDbContextAsync(ct);
            if (await db.EbayListingImages.FindAsync([img.Id], ct) is { } tracked)
            {
                tracked.EbayUrl = upload.Url;
                tracked.EbayUrlExpiresUtc = upload.ExpiresUtc;
                await db.SaveChangesAsync(ct);
            }
            urls.Add(upload.Url);
        }
        return urls;
    }

    /// <summary>
    /// eBay documents store category names as paths ("/Football"), but sellers report the plain
    /// display name ("Football") is what some accounts accept. Send the documented form first and
    /// fall back to the plain name if eBay rejects the store category.
    /// </summary>
    private async Task<string> UpsertOfferAsync(EbayListing l, EbaySettings settings, string description, CancellationToken ct)
    {
        var name = l.StoreCategoryName?.Trim().Trim('/');
        if (string.IsNullOrEmpty(name))
            return await UpsertOfferCoreAsync(l, settings, description, null, ct);

        try
        {
            return await UpsertOfferCoreAsync(l, settings, description, ["/" + name], ct);
        }
        catch (EbayApiException ex) when (ex.Message.Contains("store categor", StringComparison.OrdinalIgnoreCase))
        {
            logger.LogInformation("Store category \"/{Name}\" rejected; retrying with the plain name", name);
            return await UpsertOfferCoreAsync(l, settings, description, [name], ct);
        }
    }

    /// <summary>A sensible store category from the card's sport, if the seller has a matching one.</summary>
    private static string? DefaultStoreCategory(InventoryCard card, List<string> categories)
    {
        if (categories.Count == 0) return null;
        string? Find(string name) => categories.FirstOrDefault(c => c.Equals(name, StringComparison.OrdinalIgnoreCase));

        var sport = CardAspects.InferSport(card)?.Sport;
        return sport switch
        {
            "Football" or "Baseball" or "Basketball" => Find(sport) ?? Find("Additional Sports & TCG"),
            null => null,
            _ => Find("Additional Sports & TCG")
        };
    }

    private async Task<string> UpsertOfferCoreAsync(EbayListing l, EbaySettings settings, string description, string[]? storeCategoryNames, CancellationToken ct)
    {
        var (decline, accept) = BestOfferAmounts(l.Price, l.BestOfferMinPercent, l.BestOfferAutoAcceptPercent);
        var money = (decimal v) => new { value = v.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture), currency = _opt.Currency };

        var listingPolicies = new
        {
            fulfillmentPolicyId = l.FulfillmentPolicyId,
            paymentPolicyId = l.PaymentPolicyId,
            returnPolicyId = l.ReturnPolicyId,
            bestOfferTerms = new
            {
                bestOfferEnabled = true,
                autoAcceptPrice = money(accept),
                autoDeclinePrice = money(decline)
            }
        };

        // Fields shared by create and update (update takes no sku/marketplace/format).
        var updateBody = new
        {
            availableQuantity = l.Quantity,
            categoryId = l.CategoryId,
            listingDescription = description,
            listingDuration = "GTC",
            listingPolicies,
            merchantLocationKey = settings.MerchantLocationKey,
            pricingSummary = new { price = money(l.Price) },
            storeCategoryNames
        };

        if (l.OfferId is not null)
        {
            await api.UpdateOfferAsync(l.OfferId, updateBody, ct);
            return l.OfferId;
        }

        try
        {
            return await api.CreateOfferAsync(new
            {
                sku = l.Sku,
                marketplaceId = _opt.MarketplaceId,
                format = "FIXED_PRICE",
                updateBody.availableQuantity,
                updateBody.categoryId,
                updateBody.listingDescription,
                updateBody.listingDuration,
                updateBody.listingPolicies,
                updateBody.merchantLocationKey,
                updateBody.pricingSummary,
                updateBody.storeCategoryNames
            }, ct);
        }
        catch (EbayApiException ex) when (ex.ErrorIds.Contains(25002))
        {
            // An offer for this SKU already exists (e.g. a previous attempt timed out after eBay created it).
            var existing = (await api.GetOffersForSkuAsync(l.Sku, ct)).FirstOrDefault();
            if (existing is null) throw;
            await api.UpdateOfferAsync(existing.OfferId, updateBody, ct);
            return existing.OfferId;
        }
    }

    private async Task<(string CampaignId, string? AdId)> PromoteAsync(string ebayListingId, decimal percent, EbaySettings settings, CancellationToken ct)
    {
        var campaignId = await EnsureCampaignAsync(settings, percent, forceNew: false, ct);
        try
        {
            return (campaignId, await api.CreateAdAsync(campaignId, ebayListingId, percent, ct));
        }
        catch (EbayApiException ex) when (ex.StatusCode is 404 or 409)
        {
            // The saved campaign was ended or deleted on eBay: make a new one and retry once.
            campaignId = await EnsureCampaignAsync(settings, percent, forceNew: true, ct);
            return (campaignId, await api.CreateAdAsync(campaignId, ebayListingId, percent, ct));
        }
    }

    private async Task<string> EnsureCampaignAsync(EbaySettings settings, decimal percent, bool forceNew, CancellationToken ct)
    {
        if (!forceNew && settings.PromotionCampaignId is not null) return settings.PromotionCampaignId;

        string? id = null;
        if (!forceNew)
        {
            id = (await api.GetCampaignsAsync(ct))
                .FirstOrDefault(c => c.Name == CampaignName
                                     && c.FundingModel == "COST_PER_SALE"
                                     && c.Status is "RUNNING" or "SCHEDULED")?.Id;
        }

        id ??= await api.CreateGeneralCampaignAsync(forceNew ? $"{CampaignName} {DateTime.UtcNow:yyyyMMddHHmm}" : CampaignName, percent, ct);

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var s = await auth.GetOrCreateSettingsAsync(db, ct);
        s.PromotionCampaignId = id;
        s.UpdatedUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        settings.PromotionCampaignId = id;
        return id;
    }

    private async Task MarkCardListedAsync(int? cardId, CancellationToken ct)
    {
        if (cardId is null) return;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if (await db.InventoryCards.FindAsync([cardId.Value], ct) is { } card && card.Status == InventoryStatus.InStock)
        {
            card.Status = InventoryStatus.Listed;
            card.UpdatedUtc = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
        }
    }

    private async Task UpdateAsync(int listingId, Action<EbayListing> change, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var l = await db.EbayListings.FirstOrDefaultAsync(x => x.Id == listingId, ct);
        if (l is null) return;
        change(l);
        l.UpdatedUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    private static async Task Touch(AppDbContext db, int listingId, CancellationToken ct)
    {
        if (await db.EbayListings.FindAsync([listingId], ct) is { } l) l.UpdatedUtc = DateTime.UtcNow;
    }

    private async Task RenumberAsync(int listingId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var all = await db.EbayListingImages.Where(i => i.EbayListingId == listingId).OrderBy(i => i.SortOrder).ToListAsync(ct);
        for (var i = 0; i < all.Count; i++) all[i].SortOrder = i;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Plain-text description → eBay-safe HTML (encoded, line breaks kept).</summary>
    public static string ToHtml(string text) =>
        WebUtility.HtmlEncode(text.Replace("\r\n", "\n").Trim()).Replace("\n", "<br>");

    private static string FallbackTitle(InventoryCard c)
    {
        var parallel = c.Parallel.Equals("Base", StringComparison.OrdinalIgnoreCase) ? "" : c.Parallel + " ";
        var rc = c.IsRookie ? "RC Rookie Card " : "";
        return $"{c.CardSet} #{c.CardNumber} {c.PlayerName} {parallel}{rc}{c.Team}".Trim();
    }

    private static string FallbackDescription(InventoryCard c) =>
        $"Card Details:\nPlayer: {c.PlayerName}\nSet: {c.CardSet}\nCard #: {c.CardNumber}\nParallel: {c.Parallel}\nTeam: {c.Team}\n\n" +
        "Condition: Ungraded - Near Mint or Better. Please see photos.\n\n" +
        "Shipping & Handling: Ships via eBay Standard Envelope with tracking, in a penny sleeve and top loader inside a team bag.";

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max].TrimEnd();

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
