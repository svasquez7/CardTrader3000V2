using System.Text.Json;
using CardTrader3000.Data.Entities;

namespace CardTrader3000.Services.Import;

/// <summary>
/// Optional card data points supplied by an import file (currently JSON). Every field is nullable:
/// null means "not provided", so Claude's value (or the existing inventory value) is kept.
/// </summary>
public sealed record ProvidedCardData
{
    public string? Team { get; init; }
    public bool? IsRookie { get; init; }

    /// <summary>When set, the card is priced from this value and skips Claude. Fees are recalculated in code.</summary>
    public decimal? ListPrice { get; init; }

    public SgcCandidate? Sgc { get; init; }
    public string? SalesStrategy { get; init; }
    public string? EbayTitle { get; init; }
    public string? EbayDescription { get; init; }
    public InventoryStatus? Status { get; init; }

    public bool IsEmpty =>
        Team is null && IsRookie is null && ListPrice is null && Sgc is null && SalesStrategy is null
        && EbayTitle is null && EbayDescription is null && Status is null;

    private static readonly JsonSerializerOptions Options = new() { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };

    public string Serialize() => JsonSerializer.Serialize(this, Options);

    public static ProvidedCardData? Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<ProvidedCardData>(json, Options); }
        catch (JsonException) { return null; }
    }

    /// <summary>Copies every provided non-pricing field onto the card (provided values win).</summary>
    public void ApplyDetailsTo(InventoryCard card)
    {
        if (Team is not null) card.Team = Team;
        if (IsRookie is { } rookie) card.IsRookie = rookie;
        if (Sgc is { } sgc) card.SgcCandidate = sgc;
        if (SalesStrategy is not null) card.SalesStrategy = SalesStrategy;
        if (EbayTitle is not null) card.EbayTitle = EbayTitle;
        if (EbayDescription is not null) card.EbayDescription = EbayDescription;
        if (Status is { } status) card.Status = status;
    }
}
