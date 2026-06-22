using System.Text.RegularExpressions;
using PRImpactAnalyzer.Core.Interfaces;
using PRImpactAnalyzer.Core.Models;

namespace PRImpactAnalyzer.Plugins.SpecFlow;

/// <summary>
/// Parses .feature files and enriches each scenario with bound endpoint, page object,
/// and SOAP proxy references found in the co-located *Steps.cs / *StepDefinitions.cs files.
/// Works for both SpecFlow and Reqnroll (same syntax).
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

    public bool CanParse(string testRepoPath)
    {
        if (!Directory.Exists(testRepoPath)) return false;
        return Directory.GetFiles(testRepoPath, "*.feature", SearchOption.AllDirectories).Any();
    }

    public IEnumerable<ScenarioRecord> ParseScenarios(string testRepoPath)
    {
        var featureFiles = Directory.GetFiles(testRepoPath, "*.feature", SearchOption.AllDirectories);
        var stepFiles    = Directory.GetFiles(testRepoPath, "*.cs", SearchOption.AllDirectories)
                               .Where(f => f.IndexOf("step", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                           f.IndexOf("definition", StringComparison.OrdinalIgnoreCase) >= 0)
                               .ToList();

        var bindingMap = BuildBindingMap(stepFiles);

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
                    foreach (var (pattern, endpoints, pages, proxies) in bindingMap)
                    {
                        if (!IsLikelyMatch(pattern, stepText)) continue;
                        record.BoundEndpoints.AddRange(endpoints);
                        record.BoundPageObjects.AddRange(pages);
                        record.BoundSoapProxies.AddRange(proxies);
                    }
                }

                record.BoundEndpoints    = record.BoundEndpoints.Distinct().ToList();
                record.BoundPageObjects  = record.BoundPageObjects.Distinct().ToList();
                record.BoundSoapProxies  = record.BoundSoapProxies.Distinct().ToList();

                yield return record;
            }
        }
    }

    // ── Step definition enrichment ────────────────────────────────────────────

    private record BindingEntry(string Pattern, List<string> Endpoints, List<string> Pages, List<string> Proxies);

    private List<BindingEntry> BuildBindingMap(List<string> stepFiles)
    {
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

                map.Add(new BindingEntry(bindings[i].Groups["pattern"].Value, endpoints, pages, proxies));
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
