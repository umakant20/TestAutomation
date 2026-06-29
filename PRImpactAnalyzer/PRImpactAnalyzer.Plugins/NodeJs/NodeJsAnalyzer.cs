using System.Text.RegularExpressions;
using PRImpactAnalyzer.Core.Interfaces;
using PRImpactAnalyzer.Core.Models;

namespace PRImpactAnalyzer.Plugins.NodeJs;

/// <summary>
/// Extracts changed symbols from JavaScript/TypeScript backend (Node.js/Express) files
/// in a PR diff.
///
/// WHY THIS PLUGIN: if the organization's backend is partly or fully Node.js rather than
/// .NET, API test scenarios will be calling routes defined here, not in a .NET controller.
/// Without this analyzer, every Node.js-side change is invisible to the impact pipeline —
/// the prompt would have no symbols to match test scenarios against for that PR.
///
/// This intentionally mirrors DotNetAnalyzer's regex-extraction shape (rather than a full
/// JS/TS AST parser) because Express route definitions and exported function signatures are
/// simple, well-known text patterns — the same trade-off the original .NET analyzer made
/// before its Roslyn rewrite. A TypeScript-aware AST parser (e.g. via a Node.js subprocess
/// running the TypeScript compiler API) is the natural next upgrade if regex proves too
/// fragile in practice, exactly as happened with the .NET analyzer.
/// </summary>
public class NodeJsAnalyzer : ICodeAnalyzer
{
    public string Name => "Node.js / JavaScript / TypeScript Analyzer";

    // Express-style route definitions: router.get('/orders', ...), app.post("/orders/:id", ...)
    private static readonly Regex ExpressRoutePattern = new(
        @"(?:router|app)\.(get|post|put|delete|patch)\s*\(\s*[`'""]([^`'""]+)[`'""]",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Exported named functions: export function getOrder(...) / export const getOrder = (...) =>
    private static readonly Regex ExportedFunctionPattern = new(
        @"export\s+(?:async\s+)?function\s+(\w+)\s*\(|export\s+const\s+(\w+)\s*=\s*(?:async\s*)?\(",
        RegexOptions.Compiled);

    // module.exports.getOrder = ... / exports.getOrder = ...  (CommonJS style)
    private static readonly Regex CommonJsExportPattern = new(
        @"(?:module\.)?exports\.(\w+)\s*=", RegexOptions.Compiled);

    // NestJS / decorator-based route controllers: @Get('/orders'), @Post(':id')
    private static readonly Regex DecoratorRoutePattern = new(
        @"@(Get|Post|Put|Delete|Patch)\s*\(\s*[`'""]?([^`'"")]*)[`'""]?\s*\)",
        RegexOptions.Compiled);

    public bool CanAnalyze(string filePath) =>
        filePath.EndsWith(".js", StringComparison.OrdinalIgnoreCase) ||
        filePath.EndsWith(".ts", StringComparison.OrdinalIgnoreCase) ||
        filePath.EndsWith(".mjs", StringComparison.OrdinalIgnoreCase);

    public IEnumerable<ChangedSymbol> ExtractSymbols(FileDiff fileDiff)
    {
        var symbols = new List<ChangedSymbol>();

        foreach (var hunk in fileDiff.Hunks)
        {
            foreach (var line in hunk.Lines.Where(l => l.Type != DiffLineType.Context))
            {
                var change = line.Type == DiffLineType.Added ? ChangeType.Added : ChangeType.Removed;

                foreach (Match m in ExpressRoutePattern.Matches(line.Content))
                {
                    var verb = m.Groups[1].Value.ToUpperInvariant();
                    var route = m.Groups[2].Value;
                    symbols.Add(new ChangedSymbol { File = fileDiff.FilePath, Symbol = route, Kind = SymbolKind.HttpRoute, Change = change, AdditionalContext = verb });
                }

                foreach (Match m in DecoratorRoutePattern.Matches(line.Content))
                {
                    var verb = m.Groups[1].Value.ToUpperInvariant();
                    var route = m.Groups[2].Value;
                    if (string.IsNullOrWhiteSpace(route)) route = "/"; // bare @Get() with no path argument
                    symbols.Add(new ChangedSymbol { File = fileDiff.FilePath, Symbol = route, Kind = SymbolKind.HttpRoute, Change = change, AdditionalContext = verb });
                }

                foreach (Match m in ExportedFunctionPattern.Matches(line.Content))
                {
                    var name = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
                    symbols.Add(new ChangedSymbol { File = fileDiff.FilePath, Symbol = name, Kind = SymbolKind.JsFunction, Change = change });
                }

                foreach (Match m in CommonJsExportPattern.Matches(line.Content))
                {
                    symbols.Add(new ChangedSymbol { File = fileDiff.FilePath, Symbol = m.Groups[1].Value, Kind = SymbolKind.JsFunction, Change = change });
                }
            }
        }

        ReconcileRenames(symbols);
        return symbols;
    }

    /// <summary>Same rename-pairing heuristic used by DotNetAnalyzer's pre-Roslyn version: a removed name and an added name in the same file are treated as a likely rename.</summary>
    private static void ReconcileRenames(List<ChangedSymbol> symbols)
    {
        var removed = symbols.Where(s => s.Kind == SymbolKind.JsFunction && s.Change == ChangeType.Removed).ToList();
        var added   = symbols.Where(s => s.Kind == SymbolKind.JsFunction && s.Change == ChangeType.Added).ToList();

        foreach (var rem in removed)
        {
            var match = added.FirstOrDefault(a => a.File == rem.File && a.Symbol != rem.Symbol);
            if (match == null) continue;

            match.Change    = ChangeType.RenameTo;
            match.OldSymbol = rem.Symbol;
            rem.Change      = ChangeType.RenameFrom;
        }
    }
}
