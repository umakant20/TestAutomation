using System.Text.RegularExpressions;
using PRImpactAnalyzer.Core.Interfaces;
using PRImpactAnalyzer.Core.Models;

namespace PRImpactAnalyzer.Plugins.Config;

/// <summary>
/// Extracts changed key/value pairs from a deliberately narrow, allowlisted set of XML
/// config files — feature flags, validation rule tables, routing tables, or similar
/// declarative config that actually drives runtime behavior your tests exercise.
///
/// WHY THIS IS ALLOWLIST-DRIVEN RATHER THAN A GENERIC XML PARSER:
/// Most XML in a typical repository (.csproj, nuget.config, CI pipeline YAML/XML, build
/// targets) has zero relationship to test outcomes. A generic "parse every XML file" analyzer
/// would flood the prompt with irrelevant noise and actively hurt match quality, since the
/// pre-filter keyword matching in PromptBuilder would now have a much larger, much less
/// relevant symbol set to score scenarios against. This analyzer only looks at file paths
/// matching FileNamePatterns below — configure that list to name the specific config files
/// your organization knows are test-relevant. Starting empty and adding entries as you
/// discover real cases is the intended usage, not pre-populating it broadly.
///
/// HOW TO EXTEND: add a glob-style substring to FileNamePatterns (matched via Contains, not
/// full glob syntax, to keep this dependency-free) for each config file your team identifies
/// as test-relevant. Everything else is ignored by design.
/// </summary>
public class XmlConfigAnalyzer : ICodeAnalyzer
{
    public string Name => "XML Config Analyzer (scoped/allowlisted)";

    /// <summary>
    /// Substrings matched against the file path (case-insensitive). Only files containing
    /// one of these are analyzed at all — everything else is left alone, including .csproj,
    /// nuget.config, and other structural XML that has no bearing on test behavior.
    ///
    /// Replace these placeholder entries with your organization's actual test-relevant
    /// config file names.
    /// </summary>
    public static List<string> FileNamePatterns { get; } = new()
    {
        "featureflags",
        "featureflags.config",
        "validationrules",
        "routingtable",
        "businessrules"
    };

    // Matches simple <Key>Value</Key> or <add key="X" value="Y" /> style entries, which
    // covers both .NET appSettings-style config and most hand-rolled rule/flag XML.
    private static readonly Regex ElementValuePattern = new(
        @"<(\w+)>([^<]+)</\1>", RegexOptions.Compiled);

    private static readonly Regex KeyValueAttributePattern = new(
        @"<add\s+key\s*=\s*""([^""]+)""\s+value\s*=\s*""([^""]*)""", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public bool CanAnalyze(string filePath)
    {
        if (!filePath.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) &&
            !filePath.EndsWith(".config", StringComparison.OrdinalIgnoreCase))
            return false;

        return FileNamePatterns.Any(pattern => filePath.Contains(pattern, StringComparison.OrdinalIgnoreCase));
    }

    public IEnumerable<ChangedSymbol> ExtractSymbols(FileDiff fileDiff)
    {
        var symbols = new List<ChangedSymbol>();

        foreach (var hunk in fileDiff.Hunks)
        {
            foreach (var line in hunk.Lines.Where(l => l.Type != DiffLineType.Context))
            {
                var change = line.Type == DiffLineType.Added ? ChangeType.Added : ChangeType.Removed;

                foreach (Match m in ElementValuePattern.Matches(line.Content))
                {
                    var key = m.Groups[1].Value;
                    var value = m.Groups[2].Value;
                    symbols.Add(new ChangedSymbol
                    {
                        File = fileDiff.FilePath,
                        Symbol = key,
                        Kind = SymbolKind.ConfigValue,
                        Change = change,
                        AdditionalContext = value.Length > 60 ? value[..60] + "…" : value
                    });
                }

                foreach (Match m in KeyValueAttributePattern.Matches(line.Content))
                {
                    var key = m.Groups[1].Value;
                    var value = m.Groups[2].Value;
                    symbols.Add(new ChangedSymbol
                    {
                        File = fileDiff.FilePath,
                        Symbol = key,
                        Kind = SymbolKind.ConfigValue,
                        Change = change,
                        AdditionalContext = value.Length > 60 ? value[..60] + "…" : value
                    });
                }
            }
        }

        return symbols;
    }
}
