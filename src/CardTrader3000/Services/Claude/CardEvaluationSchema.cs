using System.Text.Json.Nodes;

namespace CardTrader3000.Services.Claude;

/// <summary>
/// JSON schema the model's answer is constrained to. Fee math, buckets and summary totals are
/// intentionally absent; they are calculated in <see cref="Pricing.FeeCalculator"/>.
/// </summary>
internal static class CardEvaluationSchema
{
    public const string Json = """
    {
      "type": "object",
      "properties": {
        "cards": {
          "type": "array",
          "items": {
            "type": "object",
            "properties": {
              "id": { "type": "integer", "description": "The id of the input card this entry evaluates." },
              "team": { "type": "string" },
              "is_rookie": { "type": "boolean" },
              "estimated_list_price": { "type": "number", "description": "Realistic Buy It Now price in USD, 2 decimals." },
              "sgc_grading_candidate": { "type": "string", "enum": ["High Prospect", "Secondary", "No"] },
              "sales_strategy": { "type": "string" },
              "ebay_title": { "type": "string", "description": "80 characters or fewer." },
              "description": { "type": "string" }
            },
            "required": ["id", "team", "is_rookie", "estimated_list_price", "sgc_grading_candidate", "sales_strategy", "ebay_title", "description"],
            "additionalProperties": false
          }
        }
      },
      "required": ["cards"],
      "additionalProperties": false
    }
    """;

    /// <summary>A fresh copy per request (a JsonNode can only have one parent).</summary>
    public static JsonNode Create() => JsonNode.Parse(Json)!;
}
