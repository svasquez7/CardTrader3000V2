using CardTrader3000.Data.Entities;
using CardTrader3000.Services.Pricing;

namespace CardTrader3000.Services.Claude;

/// <summary>One card to evaluate. <see cref="Id"/> is unique within a run and is how results are matched back.</summary>
public sealed record CardInput(int Id, string CardSet, string PlayerName, string CardNumber, string Parallel);

/// <summary>A validated evaluation: Claude's judgment plus fee math computed in code.</summary>
public sealed record EvaluatedCard(
    CardInput Input,
    string Team,
    bool IsRookie,
    FeeBreakdown Fees,
    SgcCandidate SgcCandidate,
    string SalesStrategy,
    string EbayTitle,
    string EbayDescription);

public sealed record CardEvaluationFailure(CardInput Input, string Error);

public sealed record EvaluationProgress(int ChunksCompleted, int ChunksTotal, int CardsCompleted, int CardsTotal);

public sealed class EvaluationRunResult
{
    public required string Model { get; init; }
    public List<EvaluatedCard> Succeeded { get; } = [];
    public List<CardEvaluationFailure> Failed { get; } = [];

    /// <summary>Raw JSON text returned for each request, in order. Stored on the batch for debugging.</summary>
    public List<string> RawResponses { get; } = [];

    public int RequestCount { get; set; }
    public int InputTokens { get; set; }
    public int OutputTokens { get; set; }
}
