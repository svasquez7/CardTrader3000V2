using CardTrader3000.Data.Entities;
using Microsoft.Extensions.Options;

namespace CardTrader3000.Services.Pricing;

public readonly record struct FeeBreakdown(
    decimal ListPrice,
    decimal EbayFee,
    decimal PostageCost,
    decimal TotalFees,
    decimal MaxNetReturn,
    decimal NetMarginPercent,
    PriceBucket Bucket);

public readonly record struct BatchTotals(
    int Bucket1Count,
    int Bucket2Count,
    decimal TotalGrossRevenue,
    decimal TotalEbayFees,
    decimal TotalPostage,
    decimal TotalMaxNetReturn,
    decimal OverallNetMarginPercent);

/// <summary>
/// Deterministic fee math from the Phase 1 spec. Claude supplies only the list price; every
/// derived number is computed here so the totals are always arithmetically correct.
/// </summary>
public class FeeCalculator(IOptions<FeeOptions> options)
{
    private readonly FeeOptions _o = options.Value;

    public FeeOptions Rates => _o;

    public FeeBreakdown Calculate(decimal listPrice)
    {
        if (listPrice < 0) throw new ArgumentOutOfRangeException(nameof(listPrice), "List price cannot be negative.");

        listPrice = Round2(listPrice);

        // Total Fees = ROUND(List_Price * 0.1325 + 0.30, 2) + 0.78
        var ebayFee = Round2(listPrice * _o.FinalValueFeeRate + _o.FixedOrderFee);
        var postage = _o.StandardEnvelopeRate;
        var totalFees = ebayFee + postage;

        // Max Net Return = List_Price - Total_Fees
        var net = listPrice - totalFees;

        // Margin % = (Max_Net_Return / List_Price) * 100
        var margin = listPrice == 0 ? 0m : Math.Round(net / listPrice * 100m, 1, MidpointRounding.AwayFromZero);

        return new FeeBreakdown(listPrice, ebayFee, postage, totalFees, net, margin, BucketFor(listPrice));
    }

    public PriceBucket BucketFor(decimal listPrice) =>
        listPrice >= _o.Bucket1Threshold ? PriceBucket.Bucket1 : PriceBucket.Bucket2;

    /// <summary>Quantity-weighted totals for a batch summary.</summary>
    public BatchTotals Summarize(IEnumerable<(FeeBreakdown Fees, int Quantity)> lines)
    {
        int b1 = 0, b2 = 0;
        decimal gross = 0, fees = 0, postage = 0, net = 0;

        foreach (var (f, qty) in lines)
        {
            if (f.Bucket == PriceBucket.Bucket1) b1 += qty; else b2 += qty;
            gross += f.ListPrice * qty;
            fees += f.EbayFee * qty;
            postage += f.PostageCost * qty;
            net += f.MaxNetReturn * qty;
        }

        var margin = gross == 0 ? 0m : Math.Round(net / gross * 100m, 1, MidpointRounding.AwayFromZero);
        return new BatchTotals(b1, b2, Round2(gross), Round2(fees), Round2(postage), Round2(net), margin);
    }

    private static decimal Round2(decimal v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);
}
