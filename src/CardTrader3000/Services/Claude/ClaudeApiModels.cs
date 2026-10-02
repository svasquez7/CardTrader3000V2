using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace CardTrader3000.Services.Claude;

// Minimal wire types for POST /v1/messages with structured outputs (output_config.format).
// Docs: https://platform.claude.com/docs/en/build-with-claude/structured-outputs

internal sealed record MessagesRequest(
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("max_tokens")] int MaxTokens,
    [property: JsonPropertyName("messages")] IReadOnlyList<RequestMessage> Messages,
    [property: JsonPropertyName("output_config")] OutputConfig OutputConfig);

internal sealed record RequestMessage(
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("content")] string Content);

internal sealed record OutputConfig(
    [property: JsonPropertyName("format")] OutputFormat Format);

internal sealed record OutputFormat(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("schema")] JsonNode Schema);

internal sealed class MessagesResponse
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("model")] public string? Model { get; set; }
    [JsonPropertyName("stop_reason")] public string? StopReason { get; set; }
    [JsonPropertyName("content")] public List<ContentBlock> Content { get; set; } = [];
    [JsonPropertyName("usage")] public Usage? Usage { get; set; }
}

internal sealed class ContentBlock
{
    [JsonPropertyName("type")] public string? Type { get; set; }
    [JsonPropertyName("text")] public string? Text { get; set; }
}

internal sealed class Usage
{
    [JsonPropertyName("input_tokens")] public int InputTokens { get; set; }
    [JsonPropertyName("output_tokens")] public int OutputTokens { get; set; }
}

internal sealed class ApiErrorEnvelope
{
    [JsonPropertyName("error")] public ApiError? Error { get; set; }
}

internal sealed class ApiError
{
    [JsonPropertyName("type")] public string? Type { get; set; }
    [JsonPropertyName("message")] public string? Message { get; set; }
}

// ---- The model's structured answer (matches CardEvaluationSchema) ----

internal sealed class CardEvaluationEnvelope
{
    [JsonPropertyName("cards")] public List<RawCardEvaluation> Cards { get; set; } = [];
}

internal sealed class RawCardEvaluation
{
    [JsonPropertyName("id")] public int Id { get; set; }
    [JsonPropertyName("team")] public string? Team { get; set; }
    [JsonPropertyName("is_rookie")] public bool IsRookie { get; set; }
    [JsonPropertyName("estimated_list_price")] public decimal EstimatedListPrice { get; set; }
    [JsonPropertyName("sgc_grading_candidate")] public string? SgcGradingCandidate { get; set; }
    [JsonPropertyName("sales_strategy")] public string? SalesStrategy { get; set; }
    [JsonPropertyName("ebay_title")] public string? EbayTitle { get; set; }
    [JsonPropertyName("description")] public string? Description { get; set; }
}

public sealed class ClaudeApiException(string message, int? statusCode = null, string? errorType = null)
    : Exception(message)
{
    public int? StatusCode { get; } = statusCode;
    public string? ErrorType { get; } = errorType;
}
