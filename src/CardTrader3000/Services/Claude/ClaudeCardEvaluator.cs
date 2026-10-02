using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CardTrader3000.Data.Entities;
using CardTrader3000.Services.Pricing;
using Microsoft.Extensions.Options;

namespace CardTrader3000.Services.Claude;

public interface IClaudeCardEvaluator
{
    /// <summary>
    /// Evaluates cards in chunks. Never throws for per-card or per-chunk problems; those come back in
    /// <see cref="EvaluationRunResult.Failed"/>. Throws only for configuration errors or cancellation.
    /// </summary>
    Task<EvaluationRunResult> EvaluateAsync(
        IReadOnlyList<CardInput> cards,
        IProgress<EvaluationProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

public sealed class ClaudeCardEvaluator(
    HttpClient http,
    IOptions<ClaudeOptions> options,
    PromptTemplateProvider prompts,
    FeeCalculator fees,
    ILogger<ClaudeCardEvaluator> logger) : IClaudeCardEvaluator
{
    private static readonly JsonSerializerOptions ResponseJson = new() { PropertyNameCaseInsensitive = true };

    private static readonly HashSet<HttpStatusCode> TransientStatuses =
    [
        HttpStatusCode.TooManyRequests,      // 429 rate limited
        HttpStatusCode.InternalServerError,  // 500
        HttpStatusCode.BadGateway,           // 502
        HttpStatusCode.ServiceUnavailable,   // 503
        HttpStatusCode.GatewayTimeout,       // 504
        (HttpStatusCode)529                  // overloaded
    ];

    private readonly ClaudeOptions _opt = options.Value;

    public async Task<EvaluationRunResult> EvaluateAsync(
        IReadOnlyList<CardInput> cards,
        IProgress<EvaluationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_opt.ApiKey))
            throw new InvalidOperationException(
                "Claude API key is not configured. From the project folder run: dotnet user-secrets set \"Claude:ApiKey\" \"sk-ant-...\"");

        if (cards.Select(c => c.Id).Distinct().Count() != cards.Count)
            throw new ArgumentException("Card ids must be unique within a run.", nameof(cards));

        var result = new EvaluationRunResult { Model = _opt.Model };
        var chunks = cards.Chunk(Math.Max(1, _opt.ChunkSize)).ToList();
        var cardsDone = 0;

        for (var i = 0; i < chunks.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            await EvaluateChunkAsync(chunks[i], result, cancellationToken);

            cardsDone += chunks[i].Length;
            progress?.Report(new EvaluationProgress(i + 1, chunks.Count, cardsDone, cards.Count));
        }

        logger.LogInformation(
            "Claude evaluation finished: {Ok} ok, {Failed} failed, {Requests} requests, {In} input / {Out} output tokens",
            result.Succeeded.Count, result.Failed.Count, result.RequestCount, result.InputTokens, result.OutputTokens);

        return result;
    }

    private async Task EvaluateChunkAsync(CardInput[] chunk, EvaluationRunResult result, CancellationToken ct)
    {
        MessagesResponse response;
        try
        {
            response = await SendWithRetryAsync(chunk, ct);
        }
        catch (ClaudeApiException ex)
        {
            logger.LogError(ex, "Claude request failed for chunk of {Count} cards", chunk.Length);
            result.Failed.AddRange(chunk.Select(c => new CardEvaluationFailure(c, ex.Message)));
            return;
        }

        result.RequestCount++;
        result.InputTokens += response.Usage?.InputTokens ?? 0;
        result.OutputTokens += response.Usage?.OutputTokens ?? 0;

        var text = string.Concat(response.Content.Where(b => b.Type == "text").Select(b => b.Text));
        result.RawResponses.Add(text);

        // Ran out of output tokens: the JSON is cut off. Split the chunk and try each half.
        if (response.StopReason == "max_tokens")
        {
            if (chunk.Length > 1)
            {
                logger.LogWarning("Output truncated for {Count} cards; splitting chunk and retrying", chunk.Length);
                var half = chunk.Length / 2;
                await EvaluateChunkAsync(chunk[..half], result, ct);
                await EvaluateChunkAsync(chunk[half..], result, ct);
            }
            else
            {
                result.Failed.Add(new CardEvaluationFailure(chunk[0], "Model output was truncated (max_tokens). Increase Claude:MaxTokens."));
            }
            return;
        }

        if (response.StopReason == "refusal")
        {
            result.Failed.AddRange(chunk.Select(c => new CardEvaluationFailure(c, "The model declined to evaluate this chunk.")));
            return;
        }

        CardEvaluationEnvelope? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<CardEvaluationEnvelope>(text, ResponseJson);
        }
        catch (JsonException ex)
        {
            logger.LogError(ex, "Could not parse Claude response JSON");
            result.Failed.AddRange(chunk.Select(c => new CardEvaluationFailure(c, $"Invalid JSON from model: {ex.Message}")));
            return;
        }

        var byId = chunk.ToDictionary(c => c.Id);
        var seen = new HashSet<int>();

        foreach (var raw in envelope?.Cards ?? new List<RawCardEvaluation>())
        {
            if (!byId.TryGetValue(raw.Id, out var input) || !seen.Add(raw.Id))
            {
                logger.LogWarning("Ignoring unknown or duplicate card id {Id} in model output", raw.Id);
                continue;
            }

            if (TryBuild(input, raw, out var evaluated, out var error))
                result.Succeeded.Add(evaluated!);
            else
                result.Failed.Add(new CardEvaluationFailure(input, error!));
        }

        foreach (var missing in chunk.Where(c => !seen.Contains(c.Id)))
            result.Failed.Add(new CardEvaluationFailure(missing, "The model did not return an evaluation for this card."));
    }

    /// <summary>Validates the model's answer for one card and computes all derived values.</summary>
    private bool TryBuild(CardInput input, RawCardEvaluation raw, out EvaluatedCard? card, out string? error)
    {
        card = null;
        error = null;

        if (raw.EstimatedListPrice < 0 || raw.EstimatedListPrice > 100_000m)
        {
            error = $"Implausible list price {raw.EstimatedListPrice}.";
            return false;
        }

        var breakdown = fees.Calculate(raw.EstimatedListPrice);

        var sgc = raw.SgcGradingCandidate?.Trim() switch
        {
            "High Prospect" => SgcCandidate.HighProspect,
            "Secondary" => SgcCandidate.Secondary,
            _ => SgcCandidate.No
        };

        var title = ListingText.FitTitle(raw.EbayTitle, fees.Rates.MaxTitleLength, raw.IsRookie);
        if (title.Length == 0)
            title = ListingText.FitTitle(FallbackTitle(input, raw), fees.Rates.MaxTitleLength, raw.IsRookie);

        var description = string.IsNullOrWhiteSpace(raw.Description)
            ? FallbackDescription(input, raw.Team)
            : raw.Description.Trim();

        card = new EvaluatedCard(
            input,
            Team: raw.Team?.Trim() ?? string.Empty,
            IsRookie: raw.IsRookie,
            Fees: breakdown,
            SgcCandidate: sgc,
            SalesStrategy: raw.SalesStrategy?.Trim() ?? string.Empty,
            EbayTitle: title,
            EbayDescription: description);
        return true;
    }

    private async Task<MessagesResponse> SendWithRetryAsync(CardInput[] chunk, CancellationToken ct)
    {
        var body = new MessagesRequest(
            _opt.Model,
            _opt.MaxTokens,
            [new RequestMessage("user", prompts.BuildCardEvaluationPrompt(chunk))],
            new OutputConfig(new OutputFormat("json_schema", CardEvaluationSchema.Create())));

        for (var attempt = 0; ; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "v1/messages")
            {
                Content = JsonContent.Create(body)
            };
            request.Headers.Add("x-api-key", _opt.ApiKey);
            request.Headers.Add("anthropic-version", _opt.ApiVersion);

            HttpResponseMessage response;
            try
            {
                response = await http.SendAsync(request, ct);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                if (attempt >= _opt.MaxRetries)
                    throw new ClaudeApiException($"Network error calling Claude: {ex.Message}");

                await DelayAsync(attempt, null, ct);
                continue;
            }

            using (response)
            {
                if (response.IsSuccessStatusCode)
                {
                    return await response.Content.ReadFromJsonAsync<MessagesResponse>(ResponseJson, ct)
                           ?? throw new ClaudeApiException("Empty response from Claude.");
                }

                var errorBody = await response.Content.ReadAsStringAsync(ct);

                if (TransientStatuses.Contains(response.StatusCode) && attempt < _opt.MaxRetries)
                {
                    logger.LogWarning("Claude returned {Status}; retrying (attempt {Attempt})", (int)response.StatusCode, attempt + 1);
                    await DelayAsync(attempt, response.Headers.RetryAfter?.Delta, ct);
                    continue;
                }

                throw ToApiException(response.StatusCode, errorBody);
            }
        }
    }

    private static Task DelayAsync(int attempt, TimeSpan? retryAfter, CancellationToken ct)
    {
        var backoff = retryAfter ?? TimeSpan.FromSeconds(Math.Pow(2, attempt + 1));
        var jitter = TimeSpan.FromMilliseconds(Random.Shared.Next(0, 750));
        return Task.Delay(backoff + jitter, ct);
    }

    private static ClaudeApiException ToApiException(HttpStatusCode status, string body)
    {
        string? type = null, message = null;
        try
        {
            var env = JsonSerializer.Deserialize<ApiErrorEnvelope>(body, ResponseJson);
            type = env?.Error?.Type;
            message = env?.Error?.Message;
        }
        catch (JsonException) { /* non-JSON error body */ }

        var hint = status switch
        {
            HttpStatusCode.Unauthorized => " Check that Claude:ApiKey is correct.",
            HttpStatusCode.NotFound => " Check that Claude:Model is a valid model id.",
            _ => string.Empty
        };

        return new ClaudeApiException(
            $"Claude API error {(int)status} ({type ?? "unknown"}): {message ?? body}.{hint}",
            (int)status, type);
    }

    private static string FallbackTitle(CardInput c, RawCardEvaluation raw)
    {
        var parallel = string.Equals(c.Parallel, "Base", StringComparison.OrdinalIgnoreCase) ? "" : c.Parallel;
        var rc = raw.IsRookie ? "RC Rookie Card" : "";
        return $"{c.CardSet} #{c.CardNumber.TrimStart('#')} {c.PlayerName} {parallel} {rc} {raw.Team}";
    }

    private static string FallbackDescription(CardInput c, string? team) =>
        $"""
        Card Details:
        Player: {c.PlayerName}
        Set: {c.CardSet}
        Card #: {c.CardNumber}
        Parallel: {c.Parallel}
        Team: {team}

        Condition: Ungraded - Near Mint or Better. Please see photos.

        Shipping & Handling: Ships via eBay Standard Envelope with tracking, in a penny sleeve and top loader inside a team bag.
        """;
}
