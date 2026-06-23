using System.Text;
using System.Text.RegularExpressions;
using PRImpactAnalyzer.Core.Models;

namespace PRImpactAnalyzer.Core.Pipeline;

/// <summary>
/// Builds a token-optimized prompt for manual paste into Copilot Chat.
///
/// Two optimizations vs. a naive prompt:
///   1. PRE-FILTER (free, local, zero tokens): only scenarios that share a keyword
///      with a changed symbol are sent in full. Everything else is dropped before
///      the prompt is even built — Copilot never has to read what can't match.
///   2. COMPACT ENCODING: scenarios and symbols are encoded as single-line, pipe-delimited
///      records instead of labelled multi-line blocks. LLMs parse delimited fields just as
///      reliably as labelled text, at roughly half the token cost.
/// </summary>
public class PromptBuilder
{
    /// <summary>
    /// Global cap on how many scenarios survive the pre-filter across the WHOLE suite,
    /// before chunking. With ChunkSize=80 in AnalysisPipeline, 400 here means at most
    /// 5 chunks to paste — adjust both together if your suite needs more headroom.
    /// </summary>
    private const int MaxScenariosPerPrompt = 400;

    public string Build(
        PrMetadata prMetadata,
        List<ChangedSymbol> symbols,
        List<ScenarioRecord> scenarios)
    {
        // NOTE: `scenarios` here is expected to already be the pre-filtered, chunked
        // subset — AnalysisPipeline.PrepareAsync calls PreFilter() once across the
        // whole suite before chunking, so no filtering happens inside Build itself.
        var relevant = scenarios;

        var sb = new StringBuilder();

        // -- Compact system instructions (~60 tokens vs ~140 in the verbose version) --
        sb.AppendLine("Test impact analyzer for .NET/ColdFusion/SOAP code tested via C# SpecFlow/Reqnroll.");
        sb.AppendLine("Do NOT ask clarifying questions or request more data. Use ONLY the data below. If a scenario lacks bound endpoints/page objects, match on scenario name/steps text alone and rate it M or V accordingly — never V because data is 'missing', only because the match itself is uncertain.");
        sb.AppendLine("Return ONLY this JSON, no prose, no markdown fences:");
        sb.AppendLine("{\"impacted\":[{\"s\":\"<scenario name>\",\"f\":\"<feature file>\",\"m\":\"<matched change>\",\"c\":\"H|M|V\",\"r\":\"<reason, <12 words>\"}]}");
        sb.AppendLine("c: H=direct symbol match, M=semantic/behavioral match, V=plausible but unconfirmed. Omit non-matches.");
        sb.AppendLine();

        // -- PR context -- title/description only, no labels --
        sb.AppendLine($"PR: {prMetadata.Title}");
        if (!string.IsNullOrWhiteSpace(prMetadata.Description))
            sb.AppendLine(Truncate(prMetadata.Description, 250));
        sb.AppendLine();

        // -- Changed symbols -- one line each, file inline, no per-file headers --
        sb.AppendLine($"CHANGED SYMBOLS ({symbols.Count}):");
        if (symbols.Count == 0)
        {
            sb.AppendLine("(none extracted - match on PR title/description only)");
        }
        else
        {
            foreach (var sym in symbols)
            {
                string? tag = sym.Change switch
                {
                    ChangeType.RenameFrom      => $"RENAME {sym.OldSymbol ?? sym.Symbol}->{sym.Symbol}",
                    ChangeType.RenameTo        => null,
                    ChangeType.Removed         => $"DEL {sym.Symbol}",
                    ChangeType.Added           => $"ADD {sym.Symbol}",
                    ChangeType.SignatureChanged=> $"SIG {sym.Symbol}",
                    ChangeType.BodyChanged     => $"BODY {sym.Symbol}",
                    _                          => $"= {sym.Symbol}"
                };
                if (tag is null) continue;

                var kindLabel = sym.Kind switch
                {
                    SymbolKind.DotNetMethod => "method",
                    SymbolKind.HttpRoute => "route",
                    SymbolKind.HttpVerb => "verb",
                    SymbolKind.SoapOperation => "soap",
                    SymbolKind.ColdFusionFunction => "cffn",
                    SymbolKind.ColdFusionField => "cffield",
                    SymbolKind.ColdFusionPage => "cfpage",
                    _ => "sym"
                };

                sb.AppendLine($"{ShortFile(sym.File)} | {kindLabel} | {tag}{(sym.AdditionalContext is { Length: > 0 } a ? $" ({a})" : "")}");
            }
        }
        sb.AppendLine();

        // -- Scenarios -- single-line pipe-delimited, steps trimmed of keywords --
        sb.AppendLine($"SCENARIOS IN THIS CHUNK ({relevant.Count} — already pre-filtered to those sharing a keyword with a changed symbol):");
        foreach (var s in relevant)
        {
            var bound = string.Join(",", s.BoundEndpoints.Concat(s.BoundPageObjects).Concat(s.BoundSoapProxies).Concat(s.BoundColdFusionPages));
            var steps = CompactSteps(s.Steps);

            sb.AppendLine($"{s.ScenarioName} | {s.FeatureFile} | {bound} | {steps}");
        }

        return sb.ToString();
    }

    // -- Pre-filter: local, free, zero-token relevance check --

    /// <summary>
    /// Keeps only scenarios that share at least one significant keyword with a
    /// changed symbol - in the scenario name, steps, tags, or bound endpoint/page/proxy.
    /// This is the single biggest token saver: Copilot never sees scenarios that
    /// have no textual connection to anything that changed.
    ///
    /// Call this ONCE across the whole scenario list before chunking — filtering
    /// inside each chunk independently has no effect, since every scenario already
    /// belongs to some chunk by the time Build() runs.
    /// </summary>
    public List<ScenarioRecord> PreFilter(List<ChangedSymbol> symbols, List<ScenarioRecord> scenarios)
    {
        if (symbols.Count == 0)
            return scenarios.Take(MaxScenariosPerPrompt).ToList();

        var keywords = symbols
            .SelectMany(s => SplitWords(s.Symbol).Concat(s.OldSymbol != null ? SplitWords(s.OldSymbol) : Enumerable.Empty<string>()))
            .Where(w => w.Length > 2)
            .Select(w => w.ToLowerInvariant())
            .Distinct()
            .ToHashSet();

        var scored = scenarios.Select(s =>
        {
            var haystack = string.Join(' ',
                new[] { s.ScenarioName, s.FeatureFile }
                .Concat(s.Tags)
                .Concat(s.Steps)
                .Concat(s.BoundEndpoints)
                .Concat(s.BoundPageObjects)
                .Concat(s.BoundSoapProxies)
                .Concat(s.BoundColdFusionPages))
                .ToLowerInvariant();

            var score = keywords.Count(k => haystack.Contains(k));
            return (Scenario: s, Score: score);
        })
        .Where(x => x.Score > 0)
        .OrderByDescending(x => x.Score)
        .Take(MaxScenariosPerPrompt)
        .Select(x => x.Scenario)
        .ToList();

        return scored;
    }

    /// <summary>Splits PascalCase/camelCase/snake_case symbol names into individual words for keyword matching.</summary>
    private static IEnumerable<string> SplitWords(string symbol)
    {
        var current = new StringBuilder();
        foreach (var c in symbol)
        {
            if (c is '_' or '-')
            {
                if (current.Length > 0) { yield return current.ToString(); current.Clear(); }
                continue;
            }
            if (char.IsUpper(c) && current.Length > 0)
            {
                yield return current.ToString();
                current.Clear();
            }
            current.Append(c);
        }
        if (current.Length > 0) yield return current.ToString();
    }

    /// <summary>Strips Given/When/Then/And/But keywords and caps step count - 5 steps is almost always enough signal.</summary>
    private static string CompactSteps(List<string> steps)
    {
        var trimmed = steps
            .Take(5)
            .Select(s => Regex.Replace(s, @"^(Given|When|Then|And|But)\s+", "", RegexOptions.IgnoreCase));
        return string.Join(" / ", trimmed);
    }

    /// <summary>Shortens a full file path to just enough to disambiguate - drops repo-root noise.</summary>
    private static string ShortFile(string path)
    {
        var parts = path.Replace('\\', '/').Split('/');
        return parts.Length <= 2 ? path : string.Join('/', parts[^2..]);
    }

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..max] + "...";
}
