using System.Text.RegularExpressions;
using PRImpactAnalyzer.Core.Interfaces;
using PRImpactAnalyzer.Core.Models;

namespace PRImpactAnalyzer.Plugins.Markup;

/// <summary>
/// Extracts changed selector attributes (id, class, data-testid, name) from any
/// markup-producing file: HTML, JSX/TSX (React), Vue single-file components, or Angular
/// templates.
///
/// WHY ONE SHARED ANALYZER INSTEAD OF ONE PER FRAMEWORK:
/// A Selenium test locates an element by its id/class/data-testid/name attribute — it has
/// no awareness of, or interest in, which frontend framework rendered that markup. A
/// React-specific or Vue-specific parser would need to separately re-implement the same
/// "find the selector attributes" logic for each framework's syntax, for no real benefit:
/// the thing that actually breaks a test is the rendered attribute value changing, not the
/// component logic around it. This analyzer instead looks for the selector attribute
/// patterns directly, which look nearly identical across HTML, JSX, Vue templates, and
/// Angular templates because they all ultimately produce HTML attributes.
///
/// WHAT THIS ANALYZER DELIBERATELY DOES NOT DO:
/// It does not parse component logic, props, state, hooks, or framework-specific directives
/// (v-if, *ngIf, etc.) — none of that maps to a Selenium locator. If a future need arises to
/// match on component *behavior* rather than rendered selectors, that would be a different,
/// framework-specific analyzer, not an extension of this one.
/// </summary>
public class MarkupSelectorAnalyzer : ICodeAnalyzer
{
    public string Name => "Markup Selector Analyzer (HTML / JSX / Vue / Angular)";

    // id="orderSubmitBtn"  id='orderSubmitBtn'  id={`order-${id}`} (dynamic id - captured as a template marker)
    private static readonly Regex IdPattern = new(
        @"\bid\s*=\s*[""']([^""'{}]+)[""']", RegexOptions.Compiled);

    // class="btn primary"  className="btn primary"  (React uses className; everything else uses class)
    private static readonly Regex ClassPattern = new(
        @"\b(?:class|className)\s*=\s*[""']([^""'{}]+)[""']", RegexOptions.Compiled);

    // data-testid="submit-order"  data-test="submit-order"  data-cy="submit-order" (Cypress convention, sometimes reused)
    private static readonly Regex TestIdPattern = new(
        @"\bdata-(?:testid|test|cy)\s*=\s*[""']([^""'{}]+)[""']", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // name="email"  (form fields — common Selenium target for forms across all frameworks)
    private static readonly Regex NamePattern = new(
        @"\bname\s*=\s*[""']([^""'{}]+)[""']", RegexOptions.Compiled);

    public bool CanAnalyze(string filePath) =>
        filePath.EndsWith(".html", StringComparison.OrdinalIgnoreCase) ||
        filePath.EndsWith(".htm", StringComparison.OrdinalIgnoreCase) ||
        filePath.EndsWith(".jsx", StringComparison.OrdinalIgnoreCase) ||
        filePath.EndsWith(".tsx", StringComparison.OrdinalIgnoreCase) ||
        filePath.EndsWith(".vue", StringComparison.OrdinalIgnoreCase) ||
        filePath.EndsWith(".component.html", StringComparison.OrdinalIgnoreCase); // Angular template convention

    public IEnumerable<ChangedSymbol> ExtractSymbols(FileDiff fileDiff)
    {
        var symbols = new List<ChangedSymbol>();

        foreach (var hunk in fileDiff.Hunks)
        {
            foreach (var line in hunk.Lines.Where(l => l.Type != DiffLineType.Context))
            {
                var change = line.Type == DiffLineType.Added ? ChangeType.Added : ChangeType.Removed;

                // data-testid is the strongest signal — tests are usually written to target
                // these specifically because they're stable across visual/structural refactors.
                foreach (Match m in TestIdPattern.Matches(line.Content))
                    symbols.Add(new ChangedSymbol { File = fileDiff.FilePath, Symbol = m.Groups[1].Value, Kind = SymbolKind.MarkupSelector, Change = change, AdditionalContext = "data-testid" });

                foreach (Match m in IdPattern.Matches(line.Content))
                    symbols.Add(new ChangedSymbol { File = fileDiff.FilePath, Symbol = m.Groups[1].Value, Kind = SymbolKind.MarkupSelector, Change = change, AdditionalContext = "id" });

                foreach (Match m in NamePattern.Matches(line.Content))
                    symbols.Add(new ChangedSymbol { File = fileDiff.FilePath, Symbol = m.Groups[1].Value, Kind = SymbolKind.MarkupSelector, Change = change, AdditionalContext = "name" });

                // class/className changes are the weakest signal — class names change often for
                // purely visual/styling reasons unrelated to test-relevant behavior. Still worth
                // capturing since some legacy test suites do target classes, but expect this to
                // surface more VERIFY-confidence matches than id/data-testid will.
                foreach (Match m in ClassPattern.Matches(line.Content))
                {
                    foreach (var className in m.Groups[1].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                        symbols.Add(new ChangedSymbol { File = fileDiff.FilePath, Symbol = className, Kind = SymbolKind.MarkupSelector, Change = change, AdditionalContext = "class" });
                }
            }
        }

        return symbols.GroupBy(s => (s.Symbol, s.AdditionalContext, s.Change))
                       .Select(g => g.First()); // de-dupe identical selector changes appearing on multiple lines
    }
}
