using System.Globalization;
using System.Text.RegularExpressions;
using CardTrader3000.Services.Pricing;

namespace CardTrader3000.Services.Ebay;

/// <summary>What the buyer pays for shipping, read from a shipping policy's name.</summary>
/// <param name="Amount">Shipping charged to the buyer (0 = free).</param>
/// <param name="FromName">True if the policy name said "Free" or contained a $ amount; false if assumed.</param>
/// <param name="Note">Short explanation for a tooltip.</param>
public sealed record ShippingCharge(decimal Amount, bool FromName, string Note);

/// <summary>Per-listing money for one sale (quantity 1).</summary>
public sealed record ListingEstimate(
    decimal Price,
    decimal ShippingCharged,
    decimal SaleTotal,
    decimal EbayFee,
    decimal Postage,
    decimal AdFee,
    decimal Net,
    decimal MarginPercent);

/// <summary>
/// Estimates for eBay listings, used by the listing editor and the eBay Listings page so both show
/// the same numbers. Shipping comes from the shipping policy's name ("Free …" or "… $0.99 …").
/// eBay's final value fee and the promotion ad fee are both taken on item price + shipping charged;
/// your actual postage (the envelope rate) is a cost either way.
/// </summary>
public static partial class ListingEconomics
{
    public static ShippingCharge ParseShipping(string? policyName)
    {
        if (string.IsNullOrWhiteSpace(policyName))
            return new(0m, false, "No shipping policy selected; assuming free shipping.");

        if (FreeWord().IsMatch(policyName))
            return new(0m, true, $"\"{policyName}\" → free shipping");

        if (DollarAmount().Match(policyName) is { Success: true } m
            && decimal.TryParse(m.Groups[1].Value, NumberStyles.Number, CultureInfo.InvariantCulture, out var amount))
            return new(amount, true, $"\"{policyName}\" → buyer pays {amount:C} shipping");

        return new(0m, false, $"Couldn't find \"Free\" or a $ amount in \"{policyName}\"; assuming free shipping.");
    }

    /// <summary>
    /// (Price + shipping charged) − (FVF% of that + fixed fee) − envelope postage − ad fee.
    /// For one card shipped alone; combined-shipping orders split the shipping across items.
    /// </summary>
    public static ListingEstimate Estimate(decimal price, decimal shippingCharged, decimal promotionPercent, FeeOptions rates)
    {
        var saleTotal = price + shippingCharged;
        var ebayFee = Round(saleTotal * rates.FinalValueFeeRate + rates.FixedOrderFee);
        var adFee = promotionPercent > 0 ? Round(saleTotal * promotionPercent / 100m) : 0m;
        var postage = rates.StandardEnvelopeRate;
        var net = saleTotal - ebayFee - postage - adFee;
        var margin = saleTotal == 0 ? 0m : Math.Round(net / saleTotal * 100m, 1, MidpointRounding.AwayFromZero);

        return new ListingEstimate(price, shippingCharged, saleTotal, ebayFee, postage, adFee, net, margin);
    }

    private static decimal Round(decimal v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);

    [GeneratedRegex(@"\bfree\b", RegexOptions.IgnoreCase)]
    private static partial Regex FreeWord();

    // "$0.99", "$ .99", "$1" → the number after the dollar sign.
    [GeneratedRegex(@"\$\s*(\d*\.?\d+)")]
    private static partial Regex DollarAmount();
}
