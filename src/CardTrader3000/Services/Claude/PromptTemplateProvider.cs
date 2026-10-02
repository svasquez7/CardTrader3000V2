using System.Globalization;
using System.Reflection;
using System.Text.Json;
using CardTrader3000.Services.Pricing;

namespace CardTrader3000.Services.Claude;

/// <summary>Loads Prompts/card-evaluation.md (embedded resource) and fills its placeholders.</summary>
public class PromptTemplateProvider
{
    private const string ResourceSuffix = "card-evaluation.md";

    private static readonly JsonSerializerOptions CardListJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = false
    };

    private readonly Lazy<string> _template = new(LoadTemplate);
    private readonly FeeCalculator _fees;

    public PromptTemplateProvider(FeeCalculator fees) => _fees = fees;

    public string BuildCardEvaluationPrompt(IEnumerable<CardInput> cards)
    {
        var r = _fees.Rates;
        var inv = CultureInfo.InvariantCulture;

        // One JSON object per line keeps the list compact and easy for the model to scan.
        var cardList = string.Join('\n', cards.Select(c => JsonSerializer.Serialize(c, CardListJson)));

        return _template.Value
            .Replace("{{FVF_PERCENT}}", (r.FinalValueFeeRate * 100m).ToString("0.##", inv))
            .Replace("{{FIXED_FEE}}", r.FixedOrderFee.ToString("0.00", inv))
            .Replace("{{ENVELOPE_RATE}}", r.StandardEnvelopeRate.ToString("0.00", inv))
            .Replace("{{BUCKET1_THRESHOLD}}", r.Bucket1Threshold.ToString("0.00", inv))
            .Replace("{{CARD_LIST}}", cardList);
    }

    private static string LoadTemplate()
    {
        var asm = Assembly.GetExecutingAssembly();
        var name = asm.GetManifestResourceNames()
                       .FirstOrDefault(n => n.EndsWith(ResourceSuffix, StringComparison.OrdinalIgnoreCase))
                   ?? throw new InvalidOperationException($"Embedded prompt '{ResourceSuffix}' not found. Check the EmbeddedResource item in the .csproj.");

        using var stream = asm.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
