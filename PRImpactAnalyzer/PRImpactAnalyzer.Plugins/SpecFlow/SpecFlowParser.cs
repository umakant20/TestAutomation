using System.Text.RegularExpressions;
using PRImpactAnalyzer.Core.Interfaces;
using PRImpactAnalyzer.Core.Models;

namespace PRImpactAnalyzer.Plugins.SpecFlow;

/// <summary>
/// Parses .feature files and enriches each scenario with bound endpoint, page object,
/// SOAP proxy, and ColdFusion page references found in the co-located *Steps.cs /
/// *StepDefinitions.cs files. Works for both SpecFlow and Reqnroll (same syntax).
/// </summary>
public class SpecFlowParser : ITestParser
{
    public string Name => "SpecFlow / Reqnroll Parser";

    // Matches Scenario and Scenario Outline blocks including preceding tag lines
    private static readonly Regex ScenarioPattern = new(
        @"(?<tags>(?:\s*@\S+)+)?\s*Scenario(?<outline>\s+Outline)?:\s*(?<name>.+?)\r?\n(?<body>[\s\S]*?)(?=\s*(?:@|\bScenario\b|\bExamples\b|\bFeature\b|\z))",
        RegexOptions.Compiled | RegexOptions.ExplicitCapture);

    private static readonly Regex StepPattern = new(
        @"^\s*(?<kw>Given|When|Then|And|But)\s+(?<text>.+)$",
        RegexOptions.Compiled | RegexOptions.Multiline);

    private static readonly Regex TagPattern = new(@"@(\S+)", RegexOptions.Compiled);
    private static readonly Regex FeatureTitlePattern = new(@"Feature:\s*(.+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Step definition binding patterns
    private static readonly Regex StepBindingPattern = new(
        @"\[(?:Given|When|Then)\(@?""(?<pattern>[^""]+)""\)\]",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex HttpCallPattern = new(
        @"(?:Post|Get|Put|Delete|Patch)Async\(\s*\$?""(?<url>[^""]+)""",
        RegexOptions.Compiled);

    private static readonly Regex PageObjectPattern = new(
        @"\bnew\s+(?<name>\w+Page)\s*\(", RegexOptions.Compiled);

    private static readonly Regex SoapProxyPattern = new(
        @"\bnew\s+(?<name>\w+(?:Client|Service|Proxy))\s*\(", RegexOptions.Compiled);

    // Catches ColdFusion page references in step defs / page objects in any of these forms:
    //   driver.Navigate().GoToUrl("https://app.example.com/new_pending.cfm")
    //   public const string Url = "/orders/new_pending.cfm";
    //   driver.FindElement(By...).Click(); // followed by a literal containing "checkout.cfm"
    // We simply pull out any quoted string literal ending in .cfm, anywhere in the chunk.
    private static readonly Regex CfPagePattern = new(
        @"[""']([^""']*?([\w\-]+\.cfm))[""']",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public bool CanParse(string testRepoPath)
    {
        if (!Directory.Exists(testRepoPath)) return false;
        return Directory.GetFiles(testRepoPath, "*.feature", SearchOption.AllDirectories).Any();
    }

    public IEnumerable<ScenarioRecord> ParseScenarios(string testRepoPath)
    {
        var featureFiles = Directory.GetFiles(testRepoPath, "*.feature", SearchOption.AllDirectories);

        // Step definitions AND page object classes both get scanned for .cfm references —
        // page objects often hold the Url constant rather than the step def itself.
        var csFiles = Directory.GetFiles(testRepoPath, "*.cs", SearchOption.AllDirectories).ToList();
        var stepFiles = csFiles
            .Where(f => f.IndexOf("step", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        f.IndexOf("definition", StringComparison.OrdinalIgnoreCase) >= 0)
            .ToList();
        var pageObjectFiles = csFiles
            .Where(f => f.IndexOf("page", StringComparison.OrdinalIgnoreCase) >= 0)
            .ToList();

        var bindingMap = BuildBindingMap(stepFiles, pageObjectFiles);

        foreach (var featurePath in featureFiles)
        {
            var content = File.ReadAllText(featurePath);
            var relativePath = Path.GetRelativePath(testRepoPath, featurePath);

            var featureTitleMatch = FeatureTitlePattern.Match(content);
            var featureTitle = featureTitleMatch.Success ? featureTitleMatch.Groups[1].Value.Trim() : string.Empty;

            foreach (Match m in ScenarioPattern.Matches(content))
            {
                var name     = m.Groups["name"].Value.Trim();
                var tagsRaw  = m.Groups["tags"].Value;
                var bodyRaw  = m.Groups["body"].Value;
                var isOutline = m.Groups["outline"].Success;

                var tags  = TagPattern.Matches(tagsRaw).Select(t => t.Groups[1].Value).ToList();
                var steps = StepPattern.Matches(bodyRaw)
                                .Select(s => $"{s.Groups["kw"].Value} {s.Groups["text"].Value.Trim()}")
                                .ToList();

                var record = new ScenarioRecord
                {
                    ScenarioName = name,
                    FeatureFile  = relativePath,
                    FeatureTitle = featureTitle,
                    Tags         = tags,
                    Steps        = steps,
                    IsOutline    = isOutline
                };

                // Enrich with bound symbols from step def files
                foreach (var step in steps)
                {
                    var stepText = Regex.Replace(step, @"^(Given|When|Then|And|But)\s+", "", RegexOptions.IgnoreCase).Trim();
                    foreach (var binding in bindingMap)
                    {
                        if (!IsLikelyMatch(binding.Pattern, stepText)) continue;
                        record.BoundEndpoints.AddRange(binding.Endpoints);
                        record.BoundPageObjects.AddRange(binding.Pages);
                        record.BoundSoapProxies.AddRange(binding.Proxies);
                        record.BoundColdFusionPages.AddRange(binding.CfPages);

                        // A step that constructs a known page object (e.g. "new NewOrderPage(driver)")
                        // also inherits that page object's own .cfm references, even if the step
                        // text itself didn't literally contain ".cfm" anywhere.
                        foreach (var pageName in binding.Pages)
                        {
                            if (pageObjectCfPages.TryGetValue(pageName, out var cfPagesForThisPageObject))
                                record.BoundColdFusionPages.AddRange(cfPagesForThisPageObject);
                        }
                    }
                }

                record.BoundEndpoints         = record.BoundEndpoints.Distinct().ToList();
                record.BoundPageObjects       = record.BoundPageObjects.Distinct().ToList();
                record.BoundSoapProxies       = record.BoundSoapProxies.Distinct().ToList();
                record.BoundColdFusionPages   = record.BoundColdFusionPages.Distinct().ToList();

                yield return record;
            }
        }
    }

    // ── Step definition enrichment ────────────────────────────────────────────

    private record BindingEntry(string Pattern, List<string> Endpoints, List<string> Pages, List<string> Proxies, List<string> CfPages);

    // Maps a page object class name (e.g. "NewOrderPage") -> the .cfm page(s) it navigates to.
    // Populated once in BuildBindingMap, consulted during enrichment so a step that merely
    // instantiates the page object still picks up that page's .cfm reference.
    private Dictionary<string, List<string>> pageObjectCfPages = new();

    private List<BindingEntry> BuildBindingMap(List<string> stepFiles, List<string> pageObjectFiles)
    {
        // First pass: scan page object files for their own .cfm references, keyed by class name.
        // This lets us connect "new CheckoutPage(driver)" in a step def to checkout.cfm even if
        // the step def file itself never mentions the .cfm filename directly.
        pageObjectCfPages = new Dictionary<string, List<string>>();
        foreach (var file in pageObjectFiles)
        {
            var content = File.ReadAllText(file);
            var classMatch = Regex.Match(content, @"class\s+(\w+Page)\b");
            if (!classMatch.Success) continue;

            var className = classMatch.Groups[1].Value;
            var cfPages = CfPagePattern.Matches(content)
                .Select(m => Path.GetFileName(m.Groups[1].Value))
                .Distinct()
                .ToList();

            if (cfPages.Any())
                pageObjectCfPages[className] = cfPages;
        }

        // Second pass: scan step definition files for bindings, endpoints, page object
        // construction, SOAP proxies, and any direct .cfm references in the step def itself.
        var map = new List<BindingEntry>();

        foreach (var file in stepFiles)
        {
            var content = File.ReadAllText(file);
            var bindings = StepBindingPattern.Matches(content).Cast<Match>().ToList();

            for (int i = 0; i < bindings.Count; i++)
            {
                var start = bindings[i].Index;
                var end   = i + 1 < bindings.Count ? bindings[i + 1].Index : content.Length;
                var chunk = content.Substring(start, Math.Min(end - start, 2000));

                var endpoints = HttpCallPattern.Matches(chunk).Select(m => m.Groups["url"].Value).Distinct().ToList();
                var pages     = PageObjectPattern.Matches(chunk).Select(m => m.Groups["name"].Value).Distinct().ToList();
                var proxies   = SoapProxyPattern.Matches(chunk).Select(m => m.Groups["name"].Value).Distinct().ToList();
                var cfPages   = CfPagePattern.Matches(chunk)
                                    .Select(m => Path.GetFileName(m.Groups[1].Value))
                                    .Distinct()
                                    .ToList();

                map.Add(new BindingEntry(bindings[i].Groups["pattern"].Value, endpoints, pages, proxies, cfPages));
            }
        }

        return map;
    }

    /// <summary>
    /// Fuzzy match: strips binding placeholders and checks word overlap.
    /// Threshold: 60% of significant words in the binding pattern appear in the step text.
    /// </summary>
    private static bool IsLikelyMatch(string bindingPattern, string stepText)
    {
        var cleaned = Regex.Replace(bindingPattern, @"\{[^}]+\}|\(.*?\)|\\d\+|\.\*|\[\^.*?\]", " ");
        var words   = cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                             .Where(w => w.Length > 2)
                             .Select(w => w.ToLowerInvariant())
                             .ToList();

        if (words.Count == 0) return false;

        var stepLower  = stepText.ToLowerInvariant();
        var matchCount = words.Count(w => stepLower.Contains(w));
        return matchCount >= Math.Max(1, (int)Math.Ceiling(words.Count * 0.6));
    }
}
