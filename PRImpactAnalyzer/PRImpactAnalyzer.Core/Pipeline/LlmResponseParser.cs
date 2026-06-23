using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using PRImpactAnalyzer.Core.Models;

namespace PRImpactAnalyzer.Core.Pipeline;

/// <summary>
/// Parses the raw text returned by the LLM back into ImpactedScenario objects.
/// Handles cases where the LLM wraps JSON in markdown fences despite instructions.
/// </summary>
public class LlmResponseParser
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public List<ImpactedScenario> Parse(string rawResponse)
    {
        // Strip markdown fences if the LLM added them despite instructions
        var cleaned = Regex.Replace(rawResponse.Trim(), @"^```json\s*|^```\s*|```$", "", RegexOptions.Multiline).Trim();

        // Find the JSON object — look for the first { ... } block
        var start = cleaned.IndexOf('{');
        var end = cleaned.LastIndexOf('}');
        if (start < 0 || end < 0 || end <= start)
        {
            return new List<ImpactedScenario>
            {
                new()
                {
                    ScenarioName = "[Parse error]",
                    FeatureFile = "N/A",
                    Reason = $"Could not extract JSON from LLM response. Raw: {cleaned[..Math.Min(200, cleaned.Length)]}",
                    Confidence = ConfidenceLevel.Verify
                }
            };
        }

        var json = cleaned[start..(end + 1)];

        try
        {
            var dto = JsonSerializer.Deserialize<LlmResponseDto>(json, JsonOptions);
            if (dto?.Impacted is null) return new List<ImpactedScenario>();

            return dto.Impacted.Select(i => new ImpactedScenario
            {
                ScenarioName  = i.ScenarioName ?? string.Empty,
                FeatureFile   = i.FeatureFile ?? string.Empty,
                MatchedChange = i.MatchedChange ?? string.Empty,
                Confidence    = ParseConfidence(i.Confidence),
                Reason        = i.Reason ?? string.Empty
            }).ToList();
        }
        catch (JsonException ex)
        {
            return new List<ImpactedScenario>
            {
                new()
                {
                    ScenarioName = "[JSON deserialisation error]",
                    FeatureFile  = "N/A",
                    Reason       = ex.Message,
                    Confidence   = ConfidenceLevel.Verify
                }
            };
        }
    }

    private static ConfidenceLevel ParseConfidence(string? raw) => raw?.Trim().ToUpperInvariant() switch
    {
        "H" or "HIGH"   => ConfidenceLevel.High,
        "M" or "MEDIUM" => ConfidenceLevel.Medium,
        _               => ConfidenceLevel.Verify
    };

    // DTOs match the short keys requested in the optimized prompt (s/f/m/c/r)
    // to keep Copilot's *response* compact too, not just the prompt sent to it.
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
