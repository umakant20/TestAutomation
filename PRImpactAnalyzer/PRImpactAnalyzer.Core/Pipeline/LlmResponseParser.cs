using System.Text.Json;
using System.Text.Json.Serialization;
using PRImpactAnalyzer.Core.Models;

namespace PRImpactAnalyzer.Core.Pipeline;

public class LlmResponseParser
{
    public List<ImpactedScenario> Parse(string rawResponse)
    {
        if (string.IsNullOrWhiteSpace(rawResponse))
            return new List<ImpactedScenario>();

        // Strip markdown fences if present
        var cleaned = rawResponse
            .Replace("```json", "", StringComparison.OrdinalIgnoreCase)
            .Replace("```", "")
            .Trim();

        // Find the JSON object boundaries
        var start = cleaned.IndexOf('{');
        var end   = cleaned.LastIndexOf('}');
        if (start < 0 || end < 0 || end <= start)
            return ErrorResult($"No JSON object found in response. Raw (first 200): {cleaned[..Math.Min(200, cleaned.Length)]}");

        var json = cleaned[start..(end + 1)];

        try
        {
            var dto = JsonSerializer.Deserialize<LlmResponseDto>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (dto?.Impacted is null)
                return new List<ImpactedScenario>();

            return dto.Impacted
                .Where(i => !string.IsNullOrWhiteSpace(i.ScenarioName))
                .Select(i => new ImpactedScenario
                {
                    ScenarioName  = i.ScenarioName  ?? string.Empty,
                    FeatureFile   = i.FeatureFile   ?? string.Empty,
                    MatchedChange = i.MatchedChange ?? string.Empty,
                    Confidence    = ParseConfidence(i.Confidence),
                    Reason        = i.Reason        ?? string.Empty
                })
                .ToList();
        }
        catch (Exception ex)
        {
            return ErrorResult($"JSON parse error: {ex.Message}. Raw (first 200): {json[..Math.Min(200, json.Length)]}");
        }
    }

    private static List<ImpactedScenario> ErrorResult(string message) =>
        new() { new ImpactedScenario { ScenarioName = "[Parse error]", Reason = message, Confidence = ConfidenceLevel.Verify } };

    private static ConfidenceLevel ParseConfidence(string? raw) => raw?.Trim().ToUpperInvariant() switch
    {
        "H" or "HIGH"   => ConfidenceLevel.High,
        "M" or "MEDIUM" => ConfidenceLevel.Medium,
        _               => ConfidenceLevel.Verify
    };

    private class LlmResponseDto
    {
        [JsonPropertyName("impacted")]
        public List<ImpactedDto>? Impacted { get; set; }
    }

    private class ImpactedDto
    {
        [JsonPropertyName("s")] public string? ScenarioName  { get; set; }
        [JsonPropertyName("f")] public string? FeatureFile   { get; set; }
        [JsonPropertyName("m")] public string? MatchedChange { get; set; }
        [JsonPropertyName("c")] public string? Confidence    { get; set; }
        [JsonPropertyName("r")] public string? Reason        { get; set; }
    }
}
