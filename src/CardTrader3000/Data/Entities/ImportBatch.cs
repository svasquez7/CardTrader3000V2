namespace CardTrader3000.Data.Entities;

/// <summary>One import run (a CSV upload or a manual entry submission) and its overall results.</summary>
public class ImportBatch
{
    public int Id { get; set; }

    public ImportSource Source { get; set; }
    public string? SourceFileName { get; set; }
    public ImportBatchStatus Status { get; set; } = ImportBatchStatus.Pending;

    public DateTime CreatedUtc { get; set; }
    public DateTime? StartedUtc { get; set; }
    public DateTime? CompletedUtc { get; set; }

    // ---- Counts ----
    /// <summary>Lines submitted (after skipping blanks), including duplicates within the file.</summary>
    public int TotalInputLines { get; set; }

    /// <summary>Copies successfully evaluated (duplicate lines count once per copy).</summary>
    public int TotalCardsEvaluated { get; set; }

    /// <summary>Distinct cards that created a new inventory row.</summary>
    public int NewCardCount { get; set; }
    /// <summary>Distinct cards that increased the quantity of an existing inventory row.</summary>
    public int MergedCardCount { get; set; }
    /// <summary>Distinct cards that could not be evaluated.</summary>
    public int FailedCardCount { get; set; }

    /// <summary>Copies added to inventory without pricing (Price flag = false). No Claude call was made for these.</summary>
    public int UnpricedCardCount { get; set; }

    /// <summary>Copies per bucket (quantity-weighted, like the money totals).</summary>
    public int Bucket1Count { get; set; }
    public int Bucket2Count { get; set; }

    // ---- Summary money (computed in code, quantity-weighted) ----
    public decimal TotalGrossRevenue { get; set; }
    public decimal TotalEbayFees { get; set; }
    public decimal TotalPostage { get; set; }
    public decimal TotalMaxNetReturn { get; set; }
    public decimal OverallNetMarginPercent { get; set; }

    /// <summary>Fee rates in effect for this run, so old batches stay explainable if rates change.</summary>
    public decimal FinalValueFeeRate { get; set; }
    public decimal FixedOrderFee { get; set; }
    public decimal StandardEnvelopeRate { get; set; }

    // ---- Claude usage / diagnostics ----
    public string? Model { get; set; }
    public int ChunkCount { get; set; }
    public int InputTokens { get; set; }
    public int OutputTokens { get; set; }

    /// <summary>JSON array of each chunk's raw model output, kept for debugging and reprocessing.</summary>
    public string? RawResponseJson { get; set; }

    public string? ErrorMessage { get; set; }

    public List<ImportBatchItem> Items { get; set; } = [];
}
