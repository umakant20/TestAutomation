using System.Text;
using System.Text.RegularExpressions;
using PRImpactAnalyzer.Core.Models;

namespace PRImpactAnalyzer.Core.Pipeline;

public class PromptBuilder
{
    private const int MaxScenariosGlobal = 400;

    public string Build(PrMetadata prMetadata, List<ChangedSymbol> symbols, List<ScenarioRecord> scenarios)
    {
        var sb = new StringBuilder();

        // ── System instructions ───────────────────────────────────────────────
        sb.AppendLine("Test impact analyzer for .NET/ColdFusion/SOAP code tested via C# SpecFlow/Reqnroll.");
        sb.AppendLine("Do NOT ask clarifying questions. Use ONLY the data below. Match on name/steps if bound refs are absent, rate M or V accordingly.");
        sb.AppendLine("Return ONLY this JSON, no prose, no markdown fences:");
        sb.AppendLine("{\"impacted\":[{\"s\":\"<scenario name>\",\"f\":\"<feature file>\",\"m\":\"<matched change>\",\"c\":\"H|M|V\",\"r\":\"<reason <12 words>\"}]}");
        sb.AppendLine("c: H=direct symbol match, M=semantic/behavioral, V=plausible unconfirmed. Omit non-matches.");
        sb.AppendLine();

        // ── PR metadata ───────────────────────────────────────────────────────
        sb.AppendLine($"PR: {Sanitize(prMetadata.Title)}");
        if (!string.IsNullOrWhiteSpace(prMetadata.Description))
            sb.AppendLine(Truncate(Sanitize(prMetadata.Description), 200));
        sb.AppendLine();

        // ── Changed symbols — deduplicated by (symbol+kind) ───────────────────
        var dedupedSymbols = symbols
            .GroupBy(s => (s.Symbol, s.Kind))
            .Select(g => g.OrderByDescending(s => s.Change == ChangeType.BodyChanged ? 1 : 0).First())
            .ToList();

        sb.AppendLine($"CHANGED SYMBOLS ({dedupedSymbols.Count}):");
        if (dedupedSymbols.Count == 0)
        {
            sb.AppendLine("(none - match on PR title/description only)");
        }
        else
        {
            foreach (var sym in dedupedSymbols)
            {
                string? tag = sym.Change switch
                {
                    ChangeType.RenameFrom      => $"RENAME {sym.OldSymbol ?? sym.Symbol}->{sym.Symbol}",
                    ChangeType.RenameTo        => null,
                    ChangeType.Removed         => $"DEL {sym.Symbol}",
                    ChangeType.Added           => $"ADD {sym.Symbol}",
                    ChangeType.SignatureChanged => $"SIG {sym.Symbol}",
                    ChangeType.BodyChanged      => $"BODY {sym.Symbol}",
                    _                           => $"= {sym.Symbol}"
                };
                if (tag is null) continue;

                var kindLabel = sym.Kind switch
                {
                    SymbolKind.DotNetMethod      => "method",
                    SymbolKind.HttpRoute         => "route",
                    SymbolKind.HttpVerb          => "verb",
                    SymbolKind.SoapOperation     => "soap",
                    SymbolKind.ColdFusionFunction => "cffn",
                    SymbolKind.ColdFusionField   => "cffield",
                    SymbolKind.ColdFusionPage    => "cfpage",
                    SymbolKind.JsFunction        => "jsfn",
                    SymbolKind.MarkupSelector    => "selector",
                    SymbolKind.ConfigValue       => "config",
                    _                            => "sym"
                };
                sb.AppendLine($"{ShortFile(sym.File)}|{kindLabel}|{tag}{(sym.AdditionalContext is { Length: > 0 } a ? $"({a})" : "")}");
            }
        }
        sb.AppendLine();

        // ── Scenarios — grouped by feature file, path prefix stripped ─────────
        //
        // Token saving techniques applied here:
        //   1. Common path prefix stripped globally — only the meaningful tail is shown
        //   2. Scenarios grouped under their feature file — path shown ONCE per group, not per row
        //   3. Backslashes normalized to forward slashes
        //   4. Double quotes stripped from scenario names (they break the LLM's field parsing)
        //   5. Bound refs filtered to only those that overlap with changed symbol keywords
        //   6. Step count capped at 3 (was 5) — 3 is enough for matching signal
        //   7. Steps omitted entirely for scenarios with strong bound-ref signal (HIGH-likely)

        var symbolKeywords = BuildKeywordSet(dedupedSymbols);
        var commonPrefix   = FindCommonPrefix(scenarios.Select(s => NormPath(s.FeatureFile)).ToList());

        var grouped = scenarios
            .GroupBy(s => NormPath(s.FeatureFile))
            .OrderBy(g => g.Key);

        sb.AppendLine($"SCENARIOS ({scenarios.Count} - grouped by feature file, path prefix '{commonPrefix}' stripped):");

        foreach (var group in grouped)
        {
            var shortPath = StripPrefix(group.Key, commonPrefix);
            sb.AppendLine($"[{shortPath}]");

            foreach (var s in group)
            {
                // Filter bound refs to only those that overlap with changed symbol keywords
                var relevantBound = FilterBoundRefs(s, symbolKeywords);

                // If we have strong bound-ref signal, steps add little and cost tokens — omit them
                bool hasStrongSignal = relevantBound.Length > 0;
                var steps = hasStrongSignal
                    ? string.Empty
                    : CompactSteps(s.Steps, maxSteps: 3);

                // Sanitize scenario name — backslashes and double quotes break parsing
                var name = Sanitize(s.ScenarioName);

                if (hasStrongSignal)
                    sb.AppendLine($"  {name}|{relevantBound}");
                else
                    sb.AppendLine($"  {name}||{steps}");
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Pre-filters scenarios globally (once, before chunking) by keyword overlap with
    /// changed symbols. Primary token-saving step — Copilot never sees irrelevant scenarios.
    /// </summary>
    public List<ScenarioRecord> PreFilter(List<ChangedSymbol> symbols, List<ScenarioRecord> scenarios)
    {
        if (symbols.Count == 0)
            return scenarios.Take(MaxScenariosGlobal).ToList();

        var keywords = BuildKeywordSet(symbols);

        return scenarios.Select(s =>
        {
            var haystack = string.Join(' ',
                new[] { s.ScenarioName, s.FeatureFile }
                .Concat(s.Tags).Concat(s.Steps)
                .Concat(s.BoundEndpoints).Concat(s.BoundPageObjects)
                .Concat(s.BoundSoapProxies).Concat(s.BoundColdFusionPages)
                .Concat(s.BoundSelectors)).ToLowerInvariant();
            var score = keywords.Count(k => haystack.Contains(k));
            return (Scenario: s, Score: score);
        })
        .Where(x => x.Score > 0)
        .OrderByDescending(x => x.Score)
        .Take(MaxScenariosGlobal)
        .Select(x => x.Scenario)
        .ToList();
    }

    // ── Private helpers ────────────────────────────────────────────────────────

    /// <summary>
    /// Filters a scenario's bound refs (endpoints, page objects, proxies, cfm pages, selectors)
    /// to only those that share a keyword with the changed symbols. Returns them comma-joined.
    /// This prevents the prompt bloating with all of a scenario's bound refs when only
    /// one of them is relevant to the PR.
    /// </summary>
    private static string FilterBoundRefs(ScenarioRecord s, HashSet<string> symbolKeywords)
    {
        if (symbolKeywords.Count == 0)
            return string.Empty;

        var allBound = s.BoundEndpoints
            .Concat(s.BoundPageObjects)
            .Concat(s.BoundSoapProxies)
            .Concat(s.BoundColdFusionPages)
            .Concat(s.BoundSelectors)
            .Distinct();

        var relevant = allBound
            .Where(b => symbolKeywords.Any(k => b.ToLowerInvariant().Contains(k)))
            .ToList();

        return string.Join(",", relevant);
    }

    /// <summary>Finds the longest common path prefix across all scenario file paths.</summary>
    private static string FindCommonPrefix(List<string> paths)
    {
        if (paths.Count == 0) return string.Empty;
        if (paths.Count == 1)
        {
            // Strip everything up to and including the last directory separator
            var idx = paths[0].LastIndexOf('/');
            return idx < 0 ? string.Empty : paths[0][..(idx + 1)];
        }

        var segments0 = paths[0].Split('/');
        var common = new List<string>();
        for (int i = 0; i < segments0.Length - 1; i++) // -1: never strip the filename itself
        {
            var seg = segments0[i];
            if (paths.All(p => p.StartsWith(string.Join('/', common.Append(seg)) + "/")))
                common.Add(seg);
            else
                break;
        }
        return common.Count == 0 ? string.Empty : string.Join('/', common) + "/";
    }

    private static string StripPrefix(string path, string prefix) =>
        prefix.Length > 0 && path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? path[prefix.Length..]
            : path;

    /// <summary>Normalizes a file path to forward slashes.</summary>
    private static string NormPath(string path) => path.Replace('\\', '/');

    private static HashSet<string> BuildKeywordSet(IEnumerable<ChangedSymbol> symbols) =>
        symbols
            .SelectMany(s => SplitWords(s.Symbol)
                .Concat(s.OldSymbol != null ? SplitWords(s.OldSymbol) : Enumerable.Empty<string>()))
            .Where(w => w.Length > 2)
            .Select(w => w.ToLowerInvariant())
            .ToHashSet();

    private static IEnumerable<string> SplitWords(string symbol)
    {
        var current = new StringBuilder();
        foreach (var c in symbol)
        {
            if (c is '_' or '-' or '.')
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

    private static string CompactSteps(List<string> steps, int maxSteps = 3) =>
        string.Join("/", steps.Take(maxSteps)
            .Select(s => Regex.Replace(s, @"^(Given|When|Then|And|But)\s+", "", RegexOptions.IgnoreCase)));

    /// <summary>Shortens full file path to last 2 path segments for the symbols section.</summary>
    private static string ShortFile(string path)
    {
        var parts = NormPath(path).Split('/');
        return parts.Length <= 2 ? path : string.Join('/', parts[^2..]);
    }

    /// <summary>
    /// Sanitizes text for safe insertion into the pipe-delimited prompt:
    ///   - Normalizes backslashes to forward slashes
    ///   - Removes double quotes (they confuse the LLM's field boundaries)
    ///   - Collapses whitespace
    /// </summary>
    private static string Sanitize(string text) =>
        text.Replace('\\', '/')
            .Replace("\"\"", "'")   // empty double-quote pairs → single quote
            .Replace("\"", "'")     // remaining double quotes → single quote
            .Replace("|", "-")      // pipes break the delimiter
            .Trim();

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..max] + "...";
}
