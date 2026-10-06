using System.Text.Json.Nodes;

namespace CardTrader3000.Services.Ebay;

public sealed class EbayApiException(string message, int? statusCode = null, IReadOnlyList<int>? errorIds = null)
    : Exception(message)
{
    public int? StatusCode { get; } = statusCode;

    /// <summary>eBay error IDs from the response (e.g. 25002 = offer already exists).</summary>
    public IReadOnlyList<int> ErrorIds { get; } = errorIds ?? [];

    /// <summary>
    /// Turns an eBay error body into readable text. Handles REST errors
    /// (<c>{"errors":[{"errorId","message","longMessage","parameters"}]}</c>) and OAuth errors
    /// (<c>{"error","error_description"}</c>); falls back to the raw body.
    /// </summary>
    public static string Describe(string body) => Parse(body).Message;

    public static (string Message, List<int> Ids) Parse(string body)
    {
        var ids = new List<int>();
        if (string.IsNullOrWhiteSpace(body)) return ("(no details)", ids);

        try
        {
            var root = JsonNode.Parse(body);

            if (root?["errors"] is JsonArray errors && errors.Count > 0)
            {
                var parts = new List<string>();
                foreach (var e in errors)
                {
                    if (e is null) continue;
                    if (e["errorId"]?.GetValue<int>() is { } id) ids.Add(id);

                    var text = e["longMessage"]?.GetValue<string>() ?? e["message"]?.GetValue<string>() ?? "Unknown error";
                    var parameters = (e["parameters"] as JsonArray)?
                        .Select(p => $"{p?["name"]}={p?["value"]}")
                        .ToList();
                    parts.Add(parameters is { Count: > 0 } ? $"{text} ({string.Join(", ", parameters)})" : text);
                }
                return (string.Join(" | ", parts), ids);
            }

            if (root?["error_description"]?.GetValue<string>() is { } oauthError)
                return (oauthError, ids);
        }
        catch (Exception) when (body.Length > 0)
        {
            // Not JSON: use the raw text below.
        }

        return (body.Length > 500 ? body[..500] + "…" : body, ids);
    }
}
