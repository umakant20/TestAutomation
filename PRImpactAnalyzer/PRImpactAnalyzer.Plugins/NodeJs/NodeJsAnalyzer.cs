using System.Text.RegularExpressions;
using PRImpactAnalyzer.Core.Interfaces;
using PRImpactAnalyzer.Core.Models;

namespace PRImpactAnalyzer.Plugins.NodeJs;

public class NodeJsAnalyzer : ICodeAnalyzer
{
    public string Name => "Node.js / JavaScript / TypeScript Analyzer";

    private static readonly Regex ExpressRoutePattern    = new(@"(?:router|app)\.(get|post|put|delete|patch)\s*\(\s*[`'""]([^`'""]+)[`'""]", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex DecoratorRoutePattern  = new(@"@(Get|Post|Put|Delete|Patch)\s*\(\s*[`'""]?([^`'"")]*)[`'""]?\s*\)", RegexOptions.Compiled);
    private static readonly Regex ExportedFnPattern      = new(@"export\s+(?:async\s+)?function\s+(\w+)\s*\(|export\s+const\s+(\w+)\s*=\s*(?:async\s*)?\(", RegexOptions.Compiled);
    private static readonly Regex CommonJsExportPattern  = new(@"(?:module\.)?exports\.(\w+)\s*=", RegexOptions.Compiled);

    public bool CanAnalyze(string filePath) =>
        filePath.EndsWith(".js",  StringComparison.OrdinalIgnoreCase) ||
        filePath.EndsWith(".ts",  StringComparison.OrdinalIgnoreCase) ||
        filePath.EndsWith(".mjs", StringComparison.OrdinalIgnoreCase);

    public IEnumerable<ChangedSymbol> ExtractSymbols(FileDiff fileDiff)
    {
        var symbols = new List<ChangedSymbol>();
        foreach (var hunk in fileDiff.Hunks)
            foreach (var line in hunk.Lines.Where(l => l.Type != DiffLineType.Context))
            {
                var change = line.Type == DiffLineType.Added ? ChangeType.Added : ChangeType.Removed;
                foreach (Match m in ExpressRoutePattern.Matches(line.Content))
                    symbols.Add(new ChangedSymbol { File = fileDiff.FilePath, Symbol = m.Groups[2].Value, Kind = SymbolKind.HttpRoute, Change = change, AdditionalContext = m.Groups[1].Value.ToUpper() });
                foreach (Match m in DecoratorRoutePattern.Matches(line.Content))
                    symbols.Add(new ChangedSymbol { File = fileDiff.FilePath, Symbol = string.IsNullOrWhiteSpace(m.Groups[2].Value) ? "/" : m.Groups[2].Value, Kind = SymbolKind.HttpRoute, Change = change, AdditionalContext = m.Groups[1].Value.ToUpper() });
                foreach (Match m in ExportedFnPattern.Matches(line.Content))
                    symbols.Add(new ChangedSymbol { File = fileDiff.FilePath, Symbol = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value, Kind = SymbolKind.JsFunction, Change = change });
                foreach (Match m in CommonJsExportPattern.Matches(line.Content))
                    symbols.Add(new ChangedSymbol { File = fileDiff.FilePath, Symbol = m.Groups[1].Value, Kind = SymbolKind.JsFunction, Change = change });
            }
        return symbols;
    }
}
