using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using PRImpactAnalyzer.Core.Interfaces;
using PRImpactAnalyzer.Core.Models;
// Microsoft.CodeAnalysis defines its own SymbolKind (for compiler symbols like classes/
// methods/namespaces), which collides with our domain model's SymbolKind (HttpRoute,
// DotNetMethod, etc). Alias ours explicitly so every reference below is unambiguous.
using SymbolKind = PRImpactAnalyzer.Core.Models.SymbolKind;

namespace PRImpactAnalyzer.Plugins.DotNet;

/// <summary>
/// Extracts changed symbols from .NET C# source files using Roslyn (the real C# compiler
/// front-end) rather than regex over diff text.
///
/// WHY ROSLYN INSTEAD OF REGEX:
/// Regex over a text diff is guessing at structure — it can be fooled by attributes that
/// span multiple lines, generics, expression-bodied members, local functions, or comments
/// that happen to contain method-shaped text. Roslyn parses the actual file into a syntax
/// tree, so every symbol extracted here is a real, compiler-verified method/route/attribute,
/// not a text pattern match.
///
/// HOW THIS WORKS MECHANICALLY:
/// Roslyn cannot parse a diff fragment (a file made of +/- lines is not valid C#) — it needs
/// a complete, syntactically valid file. So this analyzer parses FileDiff.OldContent and
/// FileDiff.NewContent as two full syntax trees, extracts every method/route/attribute symbol
/// from each tree independently, then diffs the two symbol sets to determine what was added,
/// removed, or changed. This is structurally more reliable than diffing text lines because a
/// method can move line position (e.g. due to an unrelated reformat) without being "changed."
/// </summary>
public class DotNetAnalyzer : ICodeAnalyzer
{
    public string Name => ".NET / C# Analyzer (Roslyn)";

    public bool CanAnalyze(string filePath) =>
        filePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) ||
        filePath.EndsWith(".svc", StringComparison.OrdinalIgnoreCase);

    public IEnumerable<ChangedSymbol> ExtractSymbols(FileDiff fileDiff)
    {
        // Fall back gracefully if old/new content wasn't populated (e.g. a diff provider
        // that doesn't supply full file content) — return nothing rather than crash, since
        // Roslyn genuinely cannot work from diff fragments alone.
        if (string.IsNullOrWhiteSpace(fileDiff.NewContent) && string.IsNullOrWhiteSpace(fileDiff.OldContent))
            return Enumerable.Empty<ChangedSymbol>();

        var oldSymbols = ExtractFileSymbols(fileDiff.OldContent, fileDiff.FilePath);
        var newSymbols = ExtractFileSymbols(fileDiff.NewContent, fileDiff.FilePath);

        return DiffSymbolSets(oldSymbols, newSymbols, fileDiff.FilePath);
    }

    // ── Per-file symbol extraction via Roslyn syntax tree ───────────────────────

    private record RawSymbol(string Name, SymbolKind Kind, string? HttpVerb, string BodyHash);

    private List<RawSymbol> ExtractFileSymbols(string sourceText, string filePath)
    {
        var symbols = new List<RawSymbol>();
        if (string.IsNullOrWhiteSpace(sourceText)) return symbols;

        SyntaxTree tree;
        try
        {
            tree = CSharpSyntaxTree.ParseText(sourceText);
        }
        catch
        {
            // Malformed/unparsable source (e.g. a non-C# .svc marker file) — skip rather than throw.
            return symbols;
        }

        var root = tree.GetRoot();

        foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
        {
            var methodName = method.Identifier.Text;
            var bodyHash = ComputeBodyHash(method);

            symbols.Add(new RawSymbol(methodName, SymbolKind.DotNetMethod, null, bodyHash));

            // Walk this method's attributes for [Route], [Http*], and [OperationContract]
            foreach (var attrList in method.AttributeLists)
            {
                foreach (var attr in attrList.Attributes)
                {
                    var attrName = attr.Name.ToString();

                    if (attrName.Equals("Route", StringComparison.OrdinalIgnoreCase))
                    {
                        var routeValue = GetFirstStringArgument(attr);
                        if (routeValue != null)
                            symbols.Add(new RawSymbol(routeValue, SymbolKind.HttpRoute, null, routeValue));
                    }
                    else if (attrName.StartsWith("Http", StringComparison.OrdinalIgnoreCase))
                    {
                        var verb = attrName.Replace("Attribute", "", StringComparison.OrdinalIgnoreCase);
                        var routeValue = GetFirstStringArgument(attr) ?? verb;
                        symbols.Add(new RawSymbol(routeValue, SymbolKind.HttpVerb, verb, routeValue));
                    }
                    else if (attrName.Equals("OperationContract", StringComparison.OrdinalIgnoreCase))
                    {
                        symbols.Add(new RawSymbol(methodName, SymbolKind.SoapOperation, null, methodName));
                    }
                }
            }
        }

        return symbols;
    }

    /// <summary>
    /// A coarse content hash of a method's body, used to distinguish "method renamed but
    /// body unchanged" (a true rename — high confidence) from "method renamed AND body
    /// changed" (treated as a body change too, since behavior may have shifted along with the name).
    /// </summary>
    private static string ComputeBodyHash(MethodDeclarationSyntax method)
    {
        var bodyText = method.Body?.ToString() ?? method.ExpressionBody?.ToString() ?? string.Empty;
        // Normalize whitespace so pure reformatting doesn't register as a body change.
        var normalized = string.Join(" ", bodyText.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return normalized.GetHashCode().ToString();
    }

    private static string? GetFirstStringArgument(AttributeSyntax attr)
    {
        var firstArg = attr.ArgumentList?.Arguments.FirstOrDefault();
        if (firstArg?.Expression is LiteralExpressionSyntax literal && literal.IsKind(SyntaxKind.StringLiteralExpression))
            return literal.Token.ValueText;
        return null;
    }

    // ── Diff the old vs new symbol sets ─────────────────────────────────────────

    private IEnumerable<ChangedSymbol> DiffSymbolSets(List<RawSymbol> oldSymbols, List<RawSymbol> newSymbols, string filePath)
    {
        var result = new List<ChangedSymbol>();

        // Methods: compare by name first; anything in new-not-old is added, old-not-new is removed.
        var oldMethods = oldSymbols.Where(s => s.Kind == SymbolKind.DotNetMethod).ToList();
        var newMethods = newSymbols.Where(s => s.Kind == SymbolKind.DotNetMethod).ToList();

        var oldNames = oldMethods.Select(m => m.Name).ToHashSet();
        var newNames = newMethods.Select(m => m.Name).ToHashSet();

        var removedMethods = oldMethods.Where(m => !newNames.Contains(m.Name)).ToList();
        var addedMethods    = newMethods.Where(m => !oldNames.Contains(m.Name)).ToList();

        // Same-named methods present in both: check if the body actually changed.
        foreach (var name in oldNames.Intersect(newNames))
        {
            var oldM = oldMethods.First(m => m.Name == name);
            var newM = newMethods.First(m => m.Name == name);
            if (oldM.BodyHash != newM.BodyHash)
            {
                result.Add(new ChangedSymbol { File = filePath, Symbol = name, Kind = SymbolKind.DotNetMethod, Change = ChangeType.BodyChanged });
            }
            // If hashes match, the method is unchanged — correctly emit nothing for it,
            // which a line-based diff could not have known.
        }

        // Reconcile removed+added into renames using body hash equality as the rename signal:
        // a true rename keeps the same body; an unrelated add+remove pair won't.
        foreach (var removed in removedMethods.ToList())
        {
            var renameTarget = addedMethods.FirstOrDefault(a => a.BodyHash == removed.BodyHash);
            if (renameTarget != null)
            {
                result.Add(new ChangedSymbol { File = filePath, Symbol = renameTarget.Name, OldSymbol = removed.Name, Kind = SymbolKind.DotNetMethod, Change = ChangeType.RenameTo });
                result.Add(new ChangedSymbol { File = filePath, Symbol = removed.Name, Kind = SymbolKind.DotNetMethod, Change = ChangeType.RenameFrom });
                removedMethods.Remove(removed);
                addedMethods.Remove(renameTarget);
            }
        }

        foreach (var removed in removedMethods)
            result.Add(new ChangedSymbol { File = filePath, Symbol = removed.Name, Kind = SymbolKind.DotNetMethod, Change = ChangeType.Removed });
        foreach (var added in addedMethods)
            result.Add(new ChangedSymbol { File = filePath, Symbol = added.Name, Kind = SymbolKind.DotNetMethod, Change = ChangeType.Added });

        // Routes / HTTP verbs / SOAP operations: report whatever exists in the new version.
        // These are markers of current API surface, not line-level edits, so "current state"
        // is what matters for matching against test scenarios.
        foreach (var route in newSymbols.Where(s => s.Kind == SymbolKind.HttpRoute))
            result.Add(new ChangedSymbol { File = filePath, Symbol = route.Name, Kind = SymbolKind.HttpRoute, Change = ChangeType.Stable });

        foreach (var verb in newSymbols.Where(s => s.Kind == SymbolKind.HttpVerb))
            result.Add(new ChangedSymbol { File = filePath, Symbol = verb.Name, Kind = SymbolKind.HttpVerb, Change = ChangeType.Stable, AdditionalContext = verb.HttpVerb });

        foreach (var soap in newSymbols.Where(s => s.Kind == SymbolKind.SoapOperation))
        {
            var existedBefore = oldSymbols.Any(s => s.Kind == SymbolKind.SoapOperation && s.Name == soap.Name);
            result.Add(new ChangedSymbol { File = filePath, Symbol = soap.Name, Kind = SymbolKind.SoapOperation, Change = existedBefore ? ChangeType.Stable : ChangeType.Added });
        }

        return result;
    }
}
