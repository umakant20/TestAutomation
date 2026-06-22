using System.Text.RegularExpressions;
using PRImpactAnalyzer.Core.Interfaces;
using PRImpactAnalyzer.Core.Models;

namespace PRImpactAnalyzer.Plugins.DotNet;

/// <summary>
/// Extracts changed symbols from .NET C# source files in a PR diff.
/// Handles: method signatures, HTTP route/verb annotations, SOAP OperationContract names.
/// </summary>
public class DotNetAnalyzer : ICodeAnalyzer
{
    public string Name => ".NET / C# Analyzer";

    // Access modifiers that signal a method declaration
    private static readonly Regex MethodPattern = new(
        @"^[-+ ]\s*(?:(?:\[[\w\(\)""/,\s=\.]+\]\s*)*)?(?:public|private|protected|internal)\s+(?:static\s+|async\s+|virtual\s+|override\s+|abstract\s+)*(?:Task<?[\w<>,\s]*>?|[\w<>,\[\]\s]+)\s+(\w+)\s*\(",
        RegexOptions.Compiled | RegexOptions.Multiline);

    private static readonly Regex RoutePattern = new(
        @"\[Route\(""([^""]+)""\)\]", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex HttpVerbPattern = new(
        @"\[(Http(?:Get|Post|Put|Delete|Patch))(?:\(""([^""]+)""\))?\]", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex SoapOperationPattern = new(
        @"\[OperationContract[^\]]*\][\s\S]{0,300}?(?:Task<?\w*>?|void)\s+(\w+)\s*\(", RegexOptions.Compiled);

    private static readonly Regex ClassPattern = new(
        @"^[-+ ]\s*(?:public|internal)\s+(?:partial\s+)?class\s+(\w+)", RegexOptions.Compiled | RegexOptions.Multiline);

    public bool CanAnalyze(string filePath) =>
        filePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) ||
        filePath.EndsWith(".svc", StringComparison.OrdinalIgnoreCase);

    public IEnumerable<ChangedSymbol> ExtractSymbols(FileDiff fileDiff)
    {
        var allLines = fileDiff.Hunks
            .SelectMany(h => h.Lines)
            .ToList();

        var fullText = string.Join("\n", allLines.Select(l =>
            l.Type == DiffLineType.Added ? "+ " + l.Content :
            l.Type == DiffLineType.Removed ? "- " + l.Content :
            "  " + l.Content));

        var symbols = new List<ChangedSymbol>();

        // Extract methods
        foreach (Match m in MethodPattern.Matches(fullText))
        {
            var rawLine = fullText[..m.Index].Split('\n').Last() + fullText[m.Index..].Split('\n').First();
            var isAdd    = m.Value.TrimStart().StartsWith('+');
            var isRemove = m.Value.TrimStart().StartsWith('-');

            symbols.Add(new ChangedSymbol
            {
                File   = fileDiff.FilePath,
                Symbol = m.Groups[1].Value,
                Kind   = SymbolKind.DotNetMethod,
                Change = isRemove ? ChangeType.Removed : isAdd ? ChangeType.Added : ChangeType.BodyChanged
            });
        }

        // Extract route annotations
        foreach (Match m in RoutePattern.Matches(fullText))
        {
            symbols.Add(new ChangedSymbol
            {
                File   = fileDiff.FilePath,
                Symbol = m.Groups[1].Value,
                Kind   = SymbolKind.HttpRoute,
                Change = ChangeType.Stable
            });
        }

        // Extract HTTP verb annotations — these carry the route sometimes
        foreach (Match m in HttpVerbPattern.Matches(fullText))
        {
            var verb  = m.Groups[1].Value;
            var route = m.Groups[2].Success ? m.Groups[2].Value : string.Empty;
            symbols.Add(new ChangedSymbol
            {
                File              = fileDiff.FilePath,
                Symbol            = route.Length > 0 ? route : verb,
                Kind              = SymbolKind.HttpVerb,
                Change            = ChangeType.Stable,
                AdditionalContext = verb
            });
        }

        // Extract SOAP operations
        foreach (Match m in SoapOperationPattern.Matches(fullText))
        {
            symbols.Add(new ChangedSymbol
            {
                File   = fileDiff.FilePath,
                Symbol = m.Groups[1].Value,
                Kind   = SymbolKind.SoapOperation,
                Change = ChangeType.Added
            });
        }

        ReconcileRenames(symbols);
        return symbols;
    }

    private static void ReconcileRenames(List<ChangedSymbol> symbols)
    {
        var removed = symbols.Where(s => s.Kind == SymbolKind.DotNetMethod && s.Change == ChangeType.Removed).ToList();
        var added   = symbols.Where(s => s.Kind == SymbolKind.DotNetMethod && s.Change == ChangeType.Added).ToList();

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
