namespace CardTrader3000.Data.Entities;

/// <summary>
/// One row per distinct card (set + player + number + parallel). Re-importing the same card
/// increases <see cref="Quantity"/> and refreshes pricing rather than creating a new row.
/// </summary>
public class InventoryCard
{
    public int Id { get; set; }

    // ---- Identity (as entered) ----
    public required string CardSet { get; set; }
    public required string PlayerName { get; set; }
    public required string CardNumber { get; set; }
    public required string Parallel { get; set; }

    /// <summary>Lower-cased, whitespace-collapsed identity used for duplicate detection. Unique.</summary>
    public required string NormalizedKey { get; set; }

    // ---- Enrichment from Claude ----
    public string? Team { get; set; }
    public bool IsRookie { get; set; }
    public PriceBucket Bucket { get; set; }
    public SgcCandidate SgcCandidate { get; set; }
    public string? SalesStrategy { get; set; }
    public string? EbayTitle { get; set; }
    public string? EbayDescription { get; set; }

    // ---- Pricing (list price from Claude; everything else computed by FeeCalculator) ----
    public decimal EstimatedListPrice { get; set; }
    public decimal EbayFee { get; set; }
    public decimal PostageCost { get; set; }
    public decimal TotalFees { get; set; }
    public decimal MaxNetReturn { get; set; }
    public decimal NetMarginPercent { get; set; }

    // ---- Inventory ----
    public int Quantity { get; set; }
    public InventoryStatus Status { get; set; } = InventoryStatus.InStock;

    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }
    /// <summary>Null when the card has never been priced (added with Price = false).</summary>
    public DateTime? LastPricedUtc { get; set; }

    /// <summary>False for track-only cards. Not a column: query with <c>LastPricedUtc != null</c>.</summary>
    [System.ComponentModel.DataAnnotations.Schema.NotMapped]
    public bool IsPriced => LastPricedUtc is not null;

    public List<ImportBatchItem> ImportItems { get; set; } = [];
}
