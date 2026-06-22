using System.Text.RegularExpressions;
using PRImpactAnalyzer.Core.Interfaces;
using PRImpactAnalyzer.Core.Models;

namespace PRImpactAnalyzer.Plugins.ColdFusion;

/// <summary>
/// Extracts changed symbols from ColdFusion .cfm / .cfc files in a PR diff.
/// Handles: cffunction names, cfinput/cfselect field names, page file name changes.
/// </summary>
public class ColdFusionAnalyzer : ICodeAnalyzer
{
    public string Name => "ColdFusion Analyzer";

    private static readonly Regex CfFunctionPattern = new(
        @"<cffunction\s+[^>]*\bname\s*=\s*[""']?(\w+)[""']?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex CfInputPattern = new(
        @"<(?:cfinput|cfselect|cftextarea)\s+[^>]*\bname\s*=\s*[""']?(\w+)[""']?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex CfIncludePattern = new(
        @"<cfinclude\s+[^>]*\btemplate\s*=\s*[""']([^""']+\.cfm)[""']",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public bool CanAnalyze(string filePath) =>
        filePath.EndsWith(".cfm", StringComparison.OrdinalIgnoreCase) ||
        filePath.EndsWith(".cfc", StringComparison.OrdinalIgnoreCase);

    public IEnumerable<ChangedSymbol> ExtractSymbols(FileDiff fileDiff)
    {
        var symbols = new List<ChangedSymbol>();

        foreach (var hunk in fileDiff.Hunks)
        {
            foreach (var line in hunk.Lines.Where(l => l.Type != DiffLineType.Context))
            {
                var isAdd = line.Type == DiffLineType.Added;
                var change = isAdd ? ChangeType.Added : ChangeType.Removed;

                foreach (Match m in CfFunctionPattern.Matches(line.Content))
                    symbols.Add(new ChangedSymbol { File = fileDiff.FilePath, Symbol = m.Groups[1].Value, Kind = SymbolKind.ColdFusionFunction, Change = change });

                foreach (Match m in CfInputPattern.Matches(line.Content))
                    symbols.Add(new ChangedSymbol { File = fileDiff.FilePath, Symbol = m.Groups[1].Value, Kind = SymbolKind.ColdFusionField, Change = change });

                foreach (Match m in CfIncludePattern.Matches(line.Content))
                    symbols.Add(new ChangedSymbol { File = fileDiff.FilePath, Symbol = m.Groups[1].Value, Kind = SymbolKind.ColdFusionPage, Change = change });
            }
        }

        // Always record the file name itself as a page symbol
        symbols.Add(new ChangedSymbol
        {
            File   = fileDiff.FilePath,
            Symbol = Path.GetFileName(fileDiff.FilePath),
            Kind   = SymbolKind.ColdFusionPage,
            Change = fileDiff.ChangeType == FileChangeType.Added ? ChangeType.Added : ChangeType.BodyChanged
        });

        return symbols;
    }
}
