using System.Text.RegularExpressions;
using PRImpactAnalyzer.Core.Interfaces;
using PRImpactAnalyzer.Core.Models;

namespace PRImpactAnalyzer.Plugins.Soap;

public class SoapWsdlAnalyzer : ICodeAnalyzer
{
    public string Name => "SOAP / WSDL Analyzer";

    private static readonly Regex WsdlOpPattern   = new(@"<wsdl:operation\s+name=""([^""]+)""", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex OpContractPattern = new(@"\[OperationContract[^\]]*\][\s\S]{0,200}?(?:Task\s+)?(\w+)\s*\(", RegexOptions.Compiled);

    public bool CanAnalyze(string filePath) =>
        filePath.EndsWith(".wsdl", StringComparison.OrdinalIgnoreCase) ||
        (filePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) &&
         filePath.IndexOf("service", StringComparison.OrdinalIgnoreCase) >= 0);

    public IEnumerable<ChangedSymbol> ExtractSymbols(FileDiff fileDiff)
    {
        var symbols = new List<ChangedSymbol>();
        foreach (var hunk in fileDiff.Hunks)
            foreach (var line in hunk.Lines.Where(l => l.Type != DiffLineType.Context))
            {
                var change = line.Type == DiffLineType.Added ? ChangeType.Added : ChangeType.Removed;
                foreach (Match m in WsdlOpPattern.Matches(line.Content))
                    symbols.Add(new ChangedSymbol { File = fileDiff.FilePath, Symbol = m.Groups[1].Value, Kind = SymbolKind.SoapOperation, Change = change });
                foreach (Match m in OpContractPattern.Matches(line.Content))
                    symbols.Add(new ChangedSymbol { File = fileDiff.FilePath, Symbol = m.Groups[1].Value, Kind = SymbolKind.SoapOperation, Change = change });
            }
        return symbols;
    }
}
