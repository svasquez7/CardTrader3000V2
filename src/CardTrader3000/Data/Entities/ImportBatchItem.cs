namespace CardTrader3000.Data.Entities;

/// <summary>
/// One distinct card line within a batch. Links the batch to the inventory row it created or
/// merged into, and snapshots the pricing at that moment (inventory pricing is overwritten on re-import).
/// </summary>
public class ImportBatchItem
{
    public int Id { get; set; }

    public int ImportBatchId { get; set; }
    public ImportBatch ImportBatch { get; set; } = null!;

    /// <summary>Null when the card failed evaluation.</summary>
    public int? InventoryCardId { get; set; }
    public InventoryCard? InventoryCard { get; set; }

    // ---- Input as received ----
    public int LineNumber { get; set; }
    public required string CardSet { get; set; }
    public required string PlayerName { get; set; }
    public required string CardNumber { get; set; }
    public required string Parallel { get; set; }

    /// <summary>
    /// How many copies this batch contributed (duplicate lines in one file are combined).
    /// 0 for a re-price, which refreshes pricing without touching stock.
    /// </summary>
    public int QuantityAdded { get; set; } = 1;

    /// <summary>
    /// True when the line's Price flag was false: the card is added to inventory (or its quantity
    /// increased) without calling Claude. Named so existing rows (false) keep meaning "priced".
    /// </summary>
    public bool SkipPricing { get; set; }

    /// <summary>
    /// True when the import file supplied a list price: the card is stored with that price (fees
    /// recalculated in code) and no Claude call is made.
    /// </summary>
    public bool PricingProvided { get; set; }

    /// <summary>
    /// Card data points from a JSON import (team, rookie, price, SGC, strategy, title, description,
    /// status), serialized <see cref="Services.Import.ProvidedCardData"/>. Applied over Claude's
    /// values when both exist. Null for CSV/manual lines.
    /// </summary>
    public string? ProvidedDataJson { get; set; }

    public ImportItemStatus Status { get; set; } = ImportItemStatus.Pending;

    /// <summary>True if this line created a new inventory row; false if it increased an existing one.</summary>
    public bool CreatedNewCard { get; set; }

    // ---- Snapshot ----
    public decimal? EstimatedListPrice { get; set; }
    public decimal? MaxNetReturn { get; set; }
    public PriceBucket? Bucket { get; set; }
    public SgcCandidate? SgcCandidate { get; set; }

    public string? ErrorMessage { get; set; }
}
