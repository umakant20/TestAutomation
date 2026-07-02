using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using PRImpactAnalyzer.Core.Interfaces;
using PRImpactAnalyzer.Core.Models;
using SymbolKind = PRImpactAnalyzer.Core.Models.SymbolKind;

namespace PRImpactAnalyzer.Plugins.DotNet;

public class DotNetAnalyzer : ICodeAnalyzer
{
    public string Name => ".NET / C# Analyzer (Roslyn)";

    public bool CanAnalyze(string filePath) =>
        filePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) ||
        filePath.EndsWith(".svc", StringComparison.OrdinalIgnoreCase);

    public IEnumerable<ChangedSymbol> ExtractSymbols(FileDiff fileDiff)
    {
        if (string.IsNullOrWhiteSpace(fileDiff.NewContent) && string.IsNullOrWhiteSpace(fileDiff.OldContent))
            return Enumerable.Empty<ChangedSymbol>();

        var oldSymbols = ExtractFileSymbols(fileDiff.OldContent, fileDiff.FilePath);
        var newSymbols = ExtractFileSymbols(fileDiff.NewContent, fileDiff.FilePath);
        return DiffSymbolSets(oldSymbols, newSymbols, fileDiff.FilePath);
    }

    private record RawSymbol(string Name, SymbolKind Kind, string? HttpVerb, string BodyHash);

    private List<RawSymbol> ExtractFileSymbols(string sourceText, string filePath)
    {
        var symbols = new List<RawSymbol>();
        if (string.IsNullOrWhiteSpace(sourceText)) return symbols;

        SyntaxTree tree;
        try { tree = CSharpSyntaxTree.ParseText(sourceText); }
        catch { return symbols; }

        var root = tree.GetRoot();
        foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
        {
            symbols.Add(new RawSymbol(method.Identifier.Text, SymbolKind.DotNetMethod, null, ComputeBodyHash(method)));

            foreach (var attrList in method.AttributeLists)
                foreach (var attr in attrList.Attributes)
                {
                    var attrName = attr.Name.ToString();
                    if (attrName.Equals("Route", StringComparison.OrdinalIgnoreCase))
                    {
                        var rv = GetFirstStringArgument(attr);
                        if (rv != null) symbols.Add(new RawSymbol(rv, SymbolKind.HttpRoute, null, rv));
                    }
                    else if (attrName.StartsWith("Http", StringComparison.OrdinalIgnoreCase))
                    {
                        var verb = attrName.Replace("Attribute", "", StringComparison.OrdinalIgnoreCase);
                        var rv = GetFirstStringArgument(attr) ?? verb;
                        symbols.Add(new RawSymbol(rv, SymbolKind.HttpVerb, verb, rv));
                    }
                    else if (attrName.Equals("OperationContract", StringComparison.OrdinalIgnoreCase))
                        symbols.Add(new RawSymbol(method.Identifier.Text, SymbolKind.SoapOperation, null, method.Identifier.Text));
                }
        }
        return symbols;
    }

    private static string ComputeBodyHash(MethodDeclarationSyntax method)
    {
        var body = method.Body?.ToString() ?? method.ExpressionBody?.ToString() ?? string.Empty;
        return string.Join(" ", body.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).GetHashCode().ToString();
    }

    private static string? GetFirstStringArgument(AttributeSyntax attr)
    {
        var first = attr.ArgumentList?.Arguments.FirstOrDefault();
        if (first?.Expression is LiteralExpressionSyntax lit && lit.IsKind(SyntaxKind.StringLiteralExpression))
            return lit.Token.ValueText;
        return null;
    }

    private IEnumerable<ChangedSymbol> DiffSymbolSets(List<RawSymbol> oldSyms, List<RawSymbol> newSyms, string filePath)
    {
        var result = new List<ChangedSymbol>();
        var oldMethods = oldSyms.Where(s => s.Kind == SymbolKind.DotNetMethod).ToList();
        var newMethods = newSyms.Where(s => s.Kind == SymbolKind.DotNetMethod).ToList();
        var oldNames = oldMethods.Select(m => m.Name).ToHashSet();
        var newNames = newMethods.Select(m => m.Name).ToHashSet();

        var removed = oldMethods.Where(m => !newNames.Contains(m.Name)).ToList();
        var added   = newMethods.Where(m => !oldNames.Contains(m.Name)).ToList();

        foreach (var name in oldNames.Intersect(newNames))
        {
            var o = oldMethods.First(m => m.Name == name);
            var n = newMethods.First(m => m.Name == name);
            if (o.BodyHash != n.BodyHash)
                result.Add(new ChangedSymbol { File = filePath, Symbol = name, Kind = SymbolKind.DotNetMethod, Change = ChangeType.BodyChanged });
        }

        foreach (var rem in removed.ToList())
        {
            var target = added.FirstOrDefault(a => a.BodyHash == rem.BodyHash);
            if (target != null)
            {
                result.Add(new ChangedSymbol { File = filePath, Symbol = target.Name, OldSymbol = rem.Name, Kind = SymbolKind.DotNetMethod, Change = ChangeType.RenameTo });
                result.Add(new ChangedSymbol { File = filePath, Symbol = rem.Name, Kind = SymbolKind.DotNetMethod, Change = ChangeType.RenameFrom });
                removed.Remove(rem); added.Remove(target);
            }
        }

        foreach (var r in removed) result.Add(new ChangedSymbol { File = filePath, Symbol = r.Name, Kind = SymbolKind.DotNetMethod, Change = ChangeType.Removed });
        foreach (var a in added)   result.Add(new ChangedSymbol { File = filePath, Symbol = a.Name, Kind = SymbolKind.DotNetMethod, Change = ChangeType.Added });

        foreach (var route in newSyms.Where(s => s.Kind == SymbolKind.HttpRoute))
            result.Add(new ChangedSymbol { File = filePath, Symbol = route.Name, Kind = SymbolKind.HttpRoute, Change = ChangeType.Stable });
        foreach (var verb in newSyms.Where(s => s.Kind == SymbolKind.HttpVerb))
            result.Add(new ChangedSymbol { File = filePath, Symbol = verb.Name, Kind = SymbolKind.HttpVerb, Change = ChangeType.Stable, AdditionalContext = verb.HttpVerb });
        foreach (var soap in newSyms.Where(s => s.Kind == SymbolKind.SoapOperation))
        {
            var existed = oldSyms.Any(s => s.Kind == SymbolKind.SoapOperation && s.Name == soap.Name);
            result.Add(new ChangedSymbol { File = filePath, Symbol = soap.Name, Kind = SymbolKind.SoapOperation, Change = existed ? ChangeType.Stable : ChangeType.Added });
        }

        return result;
    }
}
