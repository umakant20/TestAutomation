using System.Text.RegularExpressions;
using PRImpactAnalyzer.Core.Interfaces;
using PRImpactAnalyzer.Core.Models;

namespace PRImpactAnalyzer.Plugins.Config;

public class XmlConfigAnalyzer : ICodeAnalyzer
{
    public string Name => "XML Config Analyzer (scoped/allowlisted)";

    /// <summary>
    /// Add the filenames of your test-relevant config files here.
    /// Generic XML (*.csproj, nuget.config, etc.) is deliberately excluded.
    /// </summary>
    public static List<string> FileNamePatterns { get; } = new()
    {
        "featureflags",
        "featureflags.config",
        "validationrules",
        "routingtable",
        "businessrules"
    };

    private static readonly Regex ElementPattern  = new(@"<(\w+)>([^<]+)</\1>", RegexOptions.Compiled);
    private static readonly Regex AddKeyPattern   = new(@"<add\s+key\s*=\s*""([^""]+)""\s+value\s*=\s*""([^""]*)""", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public bool CanAnalyze(string filePath) =>
        (filePath.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) ||
         filePath.EndsWith(".config", StringComparison.OrdinalIgnoreCase)) &&
        FileNamePatterns.Any(p => filePath.Contains(p, StringComparison.OrdinalIgnoreCase));

    public IEnumerable<ChangedSymbol> ExtractSymbols(FileDiff fileDiff)
    {
        var symbols = new List<ChangedSymbol>();
        foreach (var hunk in fileDiff.Hunks)
            foreach (var line in hunk.Lines.Where(l => l.Type != DiffLineType.Context))
            {
                var change = line.Type == DiffLineType.Added ? ChangeType.Added : ChangeType.Removed;
                foreach (Match m in ElementPattern.Matches(line.Content))
                    symbols.Add(new ChangedSymbol { File = fileDiff.FilePath, Symbol = m.Groups[1].Value, Kind = SymbolKind.ConfigValue, Change = change, AdditionalContext = Trunc(m.Groups[2].Value) });
                foreach (Match m in AddKeyPattern.Matches(line.Content))
                    symbols.Add(new ChangedSymbol { File = fileDiff.FilePath, Symbol = m.Groups[1].Value, Kind = SymbolKind.ConfigValue, Change = change, AdditionalContext = Trunc(m.Groups[2].Value) });
            }
        return symbols;
    }

    private static string Trunc(string v) => v.Length > 60 ? v[..60] + "…" : v;
}
