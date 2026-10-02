namespace CardTrader3000.Services.Claude;

/// <summary>Bound from the "Claude" config section. Keep ApiKey in user-secrets or an environment variable.</summary>
public class ClaudeOptions
{
    public const string SectionName = "Claude";

    public string ApiKey { get; set; } = string.Empty;
    public string BaseUrl { get; set; } = "https://api.anthropic.com/";
    public string ApiVersion { get; set; } = "2023-06-01";
    public string Model { get; set; } = "claude-sonnet-5-5";

    /// <summary>Output token ceiling per request. ~25 cards with descriptions fit comfortably in 16k.</summary>
    public int MaxTokens { get; set; } = 16000;

    /// <summary>Cards sent per request. Smaller chunks mean less lost work if one fails.</summary>
    public int ChunkSize { get; set; } = 25;

    /// <summary>Retries for rate limits (429), overload (529) and transient 5xx/network errors.</summary>
    public int MaxRetries { get; set; } = 3;

    public int RequestTimeoutSeconds { get; set; } = 300;
}
