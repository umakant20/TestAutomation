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

        sb.AppendLine("Test impact analyzer for .NET/ColdFusion/SOAP code tested via C# SpecFlow/Reqnroll.");
        sb.AppendLine("Do NOT ask clarifying questions. Use ONLY the data below. If a scenario lacks bound endpoints/selectors, match on scenario name/steps text alone and rate M or V accordingly.");
        sb.AppendLine("Return ONLY this JSON, no prose, no markdown fences:");
        sb.AppendLine("{\"impacted\":[{\"s\":\"<scenario name>\",\"f\":\"<feature file>\",\"m\":\"<matched change>\",\"c\":\"H|M|V\",\"r\":\"<reason, <12 words>\"}]}");
        sb.AppendLine("c: H=direct symbol match, M=semantic/behavioral match, V=plausible but unconfirmed. Omit non-matches.");
        sb.AppendLine();

        sb.AppendLine($"PR: {prMetadata.Title}");
        if (!string.IsNullOrWhiteSpace(prMetadata.Description))
            sb.AppendLine(Truncate(prMetadata.Description, 250));
        sb.AppendLine();

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
                    ChangeType.RenameFrom       => $"RENAME {sym.OldSymbol ?? sym.Symbol}->{sym.Symbol}",
                    ChangeType.RenameTo         => null,
                    ChangeType.Removed          => $"DEL {sym.Symbol}",
                    ChangeType.Added            => $"ADD {sym.Symbol}",
                    ChangeType.SignatureChanged  => $"SIG {sym.Symbol}",
                    ChangeType.BodyChanged      => $"BODY {sym.Symbol}",
                    _                           => $"= {sym.Symbol}"
                };
                if (tag is null) continue;

                var kindLabel = sym.Kind switch
                {
                    SymbolKind.DotNetMethod        => "method",
                    SymbolKind.HttpRoute            => "route",
                    SymbolKind.HttpVerb             => "verb",
                    SymbolKind.SoapOperation        => "soap",
                    SymbolKind.ColdFusionFunction   => "cffn",
                    SymbolKind.ColdFusionField      => "cffield",
                    SymbolKind.ColdFusionPage       => "cfpage",
                    SymbolKind.JsFunction           => "jsfn",
                    SymbolKind.MarkupSelector       => "selector",
                    SymbolKind.ConfigValue          => "config",
                    _                               => "sym"
                };
                sb.AppendLine($"{ShortFile(sym.File)} | {kindLabel} | {tag}{(sym.AdditionalContext is { Length: > 0 } a ? $" ({a})" : "")}");
            }
        }
        sb.AppendLine();

        sb.AppendLine($"SCENARIOS IN THIS CHUNK ({scenarios.Count} - already pre-filtered to those sharing a keyword with a changed symbol):");
        foreach (var s in scenarios)
        {
            var bound = string.Join(",", s.BoundEndpoints
                .Concat(s.BoundPageObjects)
                .Concat(s.BoundSoapProxies)
                .Concat(s.BoundColdFusionPages)
                .Concat(s.BoundSelectors));
            var steps = CompactSteps(s.Steps);
            sb.AppendLine($"{s.ScenarioName} | {s.FeatureFile} | {bound} | {steps}");
        }

        return sb.ToString();
    }

    /// <summary>
    /// Pre-filters scenarios globally (once, before chunking) by keyword overlap with
    /// changed symbols. This is the primary token-saving step — Copilot never sees scenarios
    /// with no textual connection to anything that changed.
    /// </summary>
    public List<ScenarioRecord> PreFilter(List<ChangedSymbol> symbols, List<ScenarioRecord> scenarios)
    {
        if (symbols.Count == 0)
            return scenarios.Take(MaxScenariosGlobal).ToList();

        var keywords = symbols
            .SelectMany(s => SplitWords(s.Symbol)
                .Concat(s.OldSymbol != null ? SplitWords(s.OldSymbol) : Enumerable.Empty<string>()))
            .Where(w => w.Length > 2)
            .Select(w => w.ToLowerInvariant())
            .Distinct()
            .ToHashSet();

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

    private static string CompactSteps(List<string> steps) =>
        string.Join(" / ", steps.Take(5)
            .Select(s => Regex.Replace(s, @"^(Given|When|Then|And|But)\s+", "", RegexOptions.IgnoreCase)));

    private static string ShortFile(string path)
    {
        var parts = path.Replace('\\', '/').Split('/');
        return parts.Length <= 2 ? path : string.Join('/', parts[^2..]);
    }

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..max] + "...";
}
