namespace CardTrader3000.Services.Pricing;

/// <summary>eBay fee and postage assumptions (Phase 1, 2026 rates). Bound from the "Fees" config section.</summary>
public class FeeOptions
{
    public const string SectionName = "Fees";

    /// <summary>Final Value Fee for sports trading cards (13.25%).</summary>
    public decimal FinalValueFeeRate { get; set; } = 0.1325m;

    /// <summary>Fixed per-order transaction fee.</summary>
    public decimal FixedOrderFee { get; set; } = 0.30m;

    /// <summary>eBay Standard Envelope, 1 oz plain envelope.</summary>
    public decimal StandardEnvelopeRate { get; set; } = 0.78m;

    /// <summary>Cards at or above this list price go in Bucket 1.</summary>
    public decimal Bucket1Threshold { get; set; } = 2.99m;

    public int MaxTitleLength { get; set; } = 80;
}
