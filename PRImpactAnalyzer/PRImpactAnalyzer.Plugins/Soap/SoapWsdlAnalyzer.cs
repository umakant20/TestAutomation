using System.Text.RegularExpressions;
using PRImpactAnalyzer.Core.Interfaces;
using PRImpactAnalyzer.Core.Models;

namespace PRImpactAnalyzer.Plugins.Soap;

/// <summary>
/// Extracts changed SOAP operation names from .wsdl files and WCF service contracts.
/// </summary>
public class SoapWsdlAnalyzer : ICodeAnalyzer
{
    public string Name => "SOAP / WSDL Analyzer";

    private static readonly Regex WsdlOperationPattern = new(
        @"<(?:wsdl:)?operation\s+name\s*=\s*[""']([^""']+)[""']",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex WcfOperationPattern = new(
        @"\[OperationContract[^\]]*\]\s*\r?\n\s*(?:Task<?\w*>?|[\w<>]+)\s+(\w+)\s*\(",
        RegexOptions.Compiled);

    public bool CanAnalyze(string filePath) =>
        filePath.EndsWith(".wsdl", StringComparison.OrdinalIgnoreCase) ||
        (filePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) &&
         filePath.Contains("Service", StringComparison.OrdinalIgnoreCase));

    public IEnumerable<ChangedSymbol> ExtractSymbols(FileDiff fileDiff)
    {
        var symbols = new List<ChangedSymbol>();

        var fullText = string.Join("\n", fileDiff.Hunks
            .SelectMany(h => h.Lines)
            .Select(l => l.Content));

        foreach (Match m in WsdlOperationPattern.Matches(fullText))
            symbols.Add(new ChangedSymbol { File = fileDiff.FilePath, Symbol = m.Groups[1].Value, Kind = SymbolKind.SoapOperation, Change = ChangeType.Added });

        foreach (Match m in WcfOperationPattern.Matches(fullText))
            symbols.Add(new ChangedSymbol { File = fileDiff.FilePath, Symbol = m.Groups[1].Value, Kind = SymbolKind.SoapOperation, Change = ChangeType.Added });

        return symbols;
    }
}
