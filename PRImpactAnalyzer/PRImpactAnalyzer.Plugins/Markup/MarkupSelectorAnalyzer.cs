using System.Text.RegularExpressions;
using PRImpactAnalyzer.Core.Interfaces;
using PRImpactAnalyzer.Core.Models;

namespace PRImpactAnalyzer.Plugins.Markup;

public class MarkupSelectorAnalyzer : ICodeAnalyzer
{
    public string Name => "Markup Selector Analyzer (HTML / JSX / Vue / Angular)";

    private static readonly Regex IdPattern      = new(@"\bid\s*=\s*[""']([^""'{}]+)[""']", RegexOptions.Compiled);
    private static readonly Regex ClassPattern   = new(@"\b(?:class|className)\s*=\s*[""']([^""'{}]+)[""']", RegexOptions.Compiled);
    private static readonly Regex TestIdPattern  = new(@"\bdata-(?:testid|test|cy)\s*=\s*[""']([^""'{}]+)[""']", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex NamePattern    = new(@"\bname\s*=\s*[""']([^""'{}]+)[""']", RegexOptions.Compiled);

    public bool CanAnalyze(string filePath) =>
        filePath.EndsWith(".html", StringComparison.OrdinalIgnoreCase) ||
        filePath.EndsWith(".htm",  StringComparison.OrdinalIgnoreCase) ||
        filePath.EndsWith(".jsx",  StringComparison.OrdinalIgnoreCase) ||
        filePath.EndsWith(".tsx",  StringComparison.OrdinalIgnoreCase) ||
        filePath.EndsWith(".vue",  StringComparison.OrdinalIgnoreCase) ||
        filePath.EndsWith(".component.html", StringComparison.OrdinalIgnoreCase);

    public IEnumerable<ChangedSymbol> ExtractSymbols(FileDiff fileDiff)
    {
        var symbols = new List<ChangedSymbol>();
        foreach (var hunk in fileDiff.Hunks)
            foreach (var line in hunk.Lines.Where(l => l.Type != DiffLineType.Context))
            {
                var change = line.Type == DiffLineType.Added ? ChangeType.Added : ChangeType.Removed;
                foreach (Match m in TestIdPattern.Matches(line.Content))
                    symbols.Add(new ChangedSymbol { File = fileDiff.FilePath, Symbol = m.Groups[1].Value, Kind = SymbolKind.MarkupSelector, Change = change, AdditionalContext = "data-testid" });
                foreach (Match m in IdPattern.Matches(line.Content))
                    symbols.Add(new ChangedSymbol { File = fileDiff.FilePath, Symbol = m.Groups[1].Value, Kind = SymbolKind.MarkupSelector, Change = change, AdditionalContext = "id" });
                foreach (Match m in NamePattern.Matches(line.Content))
                    symbols.Add(new ChangedSymbol { File = fileDiff.FilePath, Symbol = m.Groups[1].Value, Kind = SymbolKind.MarkupSelector, Change = change, AdditionalContext = "name" });
                foreach (Match m in ClassPattern.Matches(line.Content))
                    foreach (var cls in m.Groups[1].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                        symbols.Add(new ChangedSymbol { File = fileDiff.FilePath, Symbol = cls, Kind = SymbolKind.MarkupSelector, Change = change, AdditionalContext = "class" });
            }
        return symbols.GroupBy(s => (s.Symbol, s.AdditionalContext, s.Change)).Select(g => g.First()).ToList();
    }
}
