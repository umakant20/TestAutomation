using System.Text.Json;
using System.Text.Json.Serialization;
using PRImpactAnalyzer.Core.Models;

namespace PRImpactAnalyzer.Core.Pipeline;

/// <summary>
/// Parses the raw text Copilot Chat returns into a list of ImpactedScenario objects.
///
/// Handles every realistic Copilot output format:
///   1. {"impacted":[{"s":"...","f":"...","m":"...","c":"H","r":"..."}]}  short keys (what we asked for)
///   2. {"impacted":[{"scenarioName":"...","featureFile":"...","matchedChange":"...","confidence":"HIGH","reason":"..."}]}  long keys (Copilot ignores key brevity instructions)
///   3. [{"s":"..."}]  bare array without the "impacted" wrapper
///   4. Any of the above wrapped in ```json ... ``` markdown fences
///   5. Any of the above preceded or followed by explanatory prose
///   6. Confidence expressed as "H"/"M"/"V", "High"/"Medium"/"Verify", or "HIGH"/"MEDIUM"/"VERIFY"
/// </summary>
public class LlmResponseParser
{
    private static readonly JsonSerializerOptions JOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    public List<ImpactedScenario> Parse(string rawResponse)
    {
        if (string.IsNullOrWhiteSpace(rawResponse))
            return new List<ImpactedScenario>();

        // Step 1 — strip markdown fences
        var cleaned = StripFences(rawResponse);

        // Step 2 — try to parse the whole thing as-is first (ideal path)
        var result = TryParse(cleaned);
        if (result is not null) return result;

        // Step 3 — extract the first valid JSON block (handles leading/trailing prose)
        var block = ExtractFirstJsonBlock(cleaned);
        if (block is not null)
        {
            result = TryParse(block);
            if (result is not null) return result;
        }

        return ErrorResult($"Could not extract valid JSON from Copilot response. " +
            $"First 300 chars: {cleaned[..Math.Min(300, cleaned.Length)]}");
    }

    private List<ImpactedScenario>? TryParse(string json)
    {
        json = json.Trim();
        if (string.IsNullOrWhiteSpace(json)) return null;

        try
        {
            // Format A — {"impacted":[...]}  (what we asked for — short OR long keys)
            if (json.StartsWith("{"))
            {
                var wrapper = JsonSerializer.Deserialize<WrapperDto>(json, JOpts);
                if (wrapper?.Impacted is { Count: > 0 })
                    return MapDtos(wrapper.Impacted);

                // Sometimes Copilot wraps in a different top-level key
                var doc = JsonDocument.Parse(json);
                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    if (prop.Value.ValueKind == JsonValueKind.Array)
                    {
                        var dtos = JsonSerializer.Deserialize<List<ImpactedDto>>(prop.Value.GetRawText(), JOpts);
                        if (dtos is { Count: > 0 }) return MapDtos(dtos);
                    }
                }
            }

            // Format B — [...] bare array
            if (json.StartsWith("["))
            {
                var dtos = JsonSerializer.Deserialize<List<ImpactedDto>>(json, JOpts);
                if (dtos is { Count: > 0 }) return MapDtos(dtos);
            }

            return null;
        }
        catch { return null; }
    }

    private static List<ImpactedScenario> MapDtos(List<ImpactedDto> dtos) =>
        dtos
            .Where(i => !string.IsNullOrWhiteSpace(i.ResolvedName))
            .Select(i => new ImpactedScenario
            {
                ScenarioName  = i.ResolvedName       ?? string.Empty,
                FeatureFile   = i.ResolvedFeature    ?? string.Empty,
                MatchedChange = i.ResolvedMatch       ?? string.Empty,
                Confidence    = ParseConfidence(i.ResolvedConfidence),
                Reason        = i.ResolvedReason     ?? string.Empty
            })
            .ToList();

    /// <summary>
    /// Extracts the first complete JSON object or array from text that may have
    /// leading/trailing prose (e.g. "Here are the results:\n{...}").
    /// Uses brace/bracket depth tracking — not a regex — so nested objects are handled correctly.
    /// </summary>
    private static string? ExtractFirstJsonBlock(string text)
    {
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] is not '{' and not '[') continue;

            var open  = text[i];
            var close = open == '{' ? '}' : ']';
            int depth = 0, end = -1;
            bool inString = false;
            bool escape   = false;

            for (int j = i; j < text.Length; j++)
            {
                var c = text[j];
                if (escape)      { escape = false; continue; }
                if (c == '\\' && inString) { escape = true; continue; }
                if (c == '"')    { inString = !inString; continue; }
                if (inString)    continue;
                if (c == open)   depth++;
                else if (c == close) { depth--; if (depth == 0) { end = j; break; } }
            }

            if (end > i)
            {
                var block = text[i..(end + 1)].Trim();
                // Only return if it looks like it contains impacted scenario data
                if (block.Contains("impacted", StringComparison.OrdinalIgnoreCase) ||
                    block.Contains("scenarioName", StringComparison.OrdinalIgnoreCase) ||
                    block.Contains("\"s\"", StringComparison.OrdinalIgnoreCase))
                    return block;
            }
        }
        return null;
    }

    private static string StripFences(string text)
    {
        // Remove ```json, ```JSON, ``` on their own lines
        var lines = text.Split('\n')
            .Where(l => !l.Trim().StartsWith("```"))
            .ToArray();
        return string.Join('\n', lines).Trim();
    }

    private static List<ImpactedScenario> ErrorResult(string message) =>
        new() { new ImpactedScenario { ScenarioName = "[Parse error]", Reason = message, Confidence = ConfidenceLevel.Verify } };

    private static ConfidenceLevel ParseConfidence(string? raw) => raw?.Trim().ToUpperInvariant() switch
    {
        "H" or "HIGH"              => ConfidenceLevel.High,
        "M" or "MEDIUM"            => ConfidenceLevel.Medium,
        "V" or "VERIFY" or "LOW"   => ConfidenceLevel.Verify,
        _                          => ConfidenceLevel.Verify
    };

    // ── DTOs — handles both short keys AND long keys from Copilot ────────────
    // We deserialize twice if needed: once against the short-key schema, once against
    // the long-key schema, keeping whichever produces non-empty results.

    private class WrapperDto
    {
        [JsonPropertyName("impacted")]
        public List<ImpactedDto>? Impacted { get; set; }
    }

    /// <summary>
    /// Unified DTO that accepts every key name Copilot might use.
    /// JsonPropertyName covers the short key; JsonExtensionData picks up everything else
    /// (long keys, alternate spellings) so we can check them in MapDtos.
    /// </summary>
    private class ImpactedDto
    {
        [JsonPropertyName("s")]            public string? S { get; set; }   // short: scenario name
        [JsonPropertyName("f")]            public string? F { get; set; }   // short: feature file
        [JsonPropertyName("m")]            public string? M { get; set; }   // short: matched change
        [JsonPropertyName("c")]            public string? C { get; set; }   // short: confidence
        [JsonPropertyName("r")]            public string? R { get; set; }   // short: reason

        // Long-key fallbacks — Copilot frequently uses these despite the prompt instruction
        [JsonPropertyName("scenarioName")]  public string? ScenarioName  { get; set; }
        [JsonPropertyName("featureFile")]   public string? FeatureFile   { get; set; }
        [JsonPropertyName("matchedChange")] public string? MatchedChange { get; set; }
        [JsonPropertyName("confidence")]    public string? Confidence    { get; set; }
        [JsonPropertyName("reason")]        public string? Reason        { get; set; }

        // Convenience getters — prefer short key if present, fall back to long key
        [JsonIgnore] public string? ResolvedName       => S ?? ScenarioName;
        [JsonIgnore] public string? ResolvedFeature    => F ?? FeatureFile;
        [JsonIgnore] public string? ResolvedMatch      => M ?? MatchedChange;
        [JsonIgnore] public string? ResolvedConfidence => C ?? Confidence;
        [JsonIgnore] public string? ResolvedReason     => R ?? Reason;
    }
}
