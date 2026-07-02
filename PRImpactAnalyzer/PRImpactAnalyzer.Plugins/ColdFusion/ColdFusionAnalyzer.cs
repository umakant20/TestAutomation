using System.Text.RegularExpressions;
using PRImpactAnalyzer.Core.Interfaces;
using PRImpactAnalyzer.Core.Models;

namespace PRImpactAnalyzer.Plugins.ColdFusion;

public class ColdFusionAnalyzer : ICodeAnalyzer
{
    public string Name => "ColdFusion Analyzer";

    private static readonly Regex FunctionPattern = new(@"<cffunction[^>]+name\s*=\s*""([^""]+)""", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex FieldPattern    = new(@"<cf(?:input|select|textarea)[^>]+name\s*=\s*""([^""]+)""", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex IncludePattern  = new(@"<cfinclude[^>]+template\s*=\s*""([^""]+\.cfm)""", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public bool CanAnalyze(string filePath) =>
        filePath.EndsWith(".cfm", StringComparison.OrdinalIgnoreCase) ||
        filePath.EndsWith(".cfc", StringComparison.OrdinalIgnoreCase);

    public IEnumerable<ChangedSymbol> ExtractSymbols(FileDiff fileDiff)
    {
        var symbols = new List<ChangedSymbol>();
        var change = fileDiff.ChangeType == FileChangeType.Deleted ? ChangeType.Removed : ChangeType.BodyChanged;

        // Always record the changed file itself as a ColdFusionPage symbol
        symbols.Add(new ChangedSymbol { File = fileDiff.FilePath, Symbol = Path.GetFileName(fileDiff.FilePath), Kind = SymbolKind.ColdFusionPage, Change = change });

        foreach (var hunk in fileDiff.Hunks)
            foreach (var line in hunk.Lines.Where(l => l.Type != DiffLineType.Context))
            {
                var lc = line.Type == DiffLineType.Added ? ChangeType.Added : ChangeType.Removed;
                foreach (Match m in FunctionPattern.Matches(line.Content))
                    symbols.Add(new ChangedSymbol { File = fileDiff.FilePath, Symbol = m.Groups[1].Value, Kind = SymbolKind.ColdFusionFunction, Change = lc });
                foreach (Match m in FieldPattern.Matches(line.Content))
                    symbols.Add(new ChangedSymbol { File = fileDiff.FilePath, Symbol = m.Groups[1].Value, Kind = SymbolKind.ColdFusionField, Change = lc });
                foreach (Match m in IncludePattern.Matches(line.Content))
                    symbols.Add(new ChangedSymbol { File = fileDiff.FilePath, Symbol = Path.GetFileName(m.Groups[1].Value), Kind = SymbolKind.ColdFusionPage, Change = lc });
            }

        return symbols;
    }
}
