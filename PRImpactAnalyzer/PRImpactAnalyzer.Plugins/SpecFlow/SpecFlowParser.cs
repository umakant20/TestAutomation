using System.Text.RegularExpressions;
using PRImpactAnalyzer.Core.Interfaces;
using PRImpactAnalyzer.Core.Models;

namespace PRImpactAnalyzer.Plugins.SpecFlow;

public class SpecFlowParser : ITestParser
{
    public string Name => "SpecFlow / Reqnroll Parser";

    private static readonly Regex ScenarioPattern = new(
        @"(?<tags>(?:\s*@\S+)+)?\s*Scenario(?<outline>\s+Outline)?:\s*(?<name>.+?)\r?\n(?<body>[\s\S]*?)(?=\s*(?:@|\bScenario\b|\bExamples\b|\bFeature\b|\z))",
        RegexOptions.Compiled | RegexOptions.ExplicitCapture);
    private static readonly Regex StepPattern         = new(@"^\s*(?<kw>Given|When|Then|And|But)\s+(?<text>.+)$", RegexOptions.Compiled | RegexOptions.Multiline);
    private static readonly Regex TagPattern           = new(@"@(\S+)", RegexOptions.Compiled);
    private static readonly Regex FeatureTitlePattern  = new(@"Feature:\s*(.+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex StepBindingPattern   = new(@"\[(?:Given|When|Then)\(@?""(?<pattern>[^""]+)""\)\]", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex HttpCallPattern      = new(@"(?:Post|Get|Put|Delete|Patch)Async\(\s*\$?""(?<url>[^""]+)""", RegexOptions.Compiled);
    private static readonly Regex PageObjectPattern    = new(@"\bnew\s+(?<name>\w+Page)\s*\(", RegexOptions.Compiled);
    private static readonly Regex SoapProxyPattern     = new(@"\bnew\s+(?<name>\w+(?:Client|Service|Proxy))\s*\(", RegexOptions.Compiled);
    private static readonly Regex CfPagePattern        = new(@"[""']([^""']*?([\w\-]+\.cfm))[""']", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex SelectorPattern      = new(
        @"By\.(?:Id|ClassName|Name)\(\s*""([^""]+)""\s*\)|By\.CssSelector\(\s*""[^""]*?data-test(?:id)?=['""]?([^'""\]]+)['""]?[^""]*""\s*\)",
        RegexOptions.Compiled);

    private Dictionary<string, List<string>> _pageObjectCfPages = new();
    private Dictionary<string, List<string>> _pageObjectSelectors = new();

    public bool CanParse(string testRepoPath) =>
        Directory.Exists(testRepoPath) &&
        Directory.GetFiles(testRepoPath, "*.feature", SearchOption.AllDirectories).Any();

    public IEnumerable<ScenarioRecord> ParseScenarios(string testRepoPath)
    {
        var featureFiles = Directory.GetFiles(testRepoPath, "*.feature", SearchOption.AllDirectories);
        var csFiles = Directory.GetFiles(testRepoPath, "*.cs", SearchOption.AllDirectories).ToList();
        var stepFiles = csFiles.Where(f => f.IndexOf("step", StringComparison.OrdinalIgnoreCase) >= 0 || f.IndexOf("definition", StringComparison.OrdinalIgnoreCase) >= 0).ToList();
        var pageObjectFiles = csFiles.Where(f => f.IndexOf("page", StringComparison.OrdinalIgnoreCase) >= 0).ToList();

        var bindingMap = BuildBindingMap(stepFiles, pageObjectFiles);

        foreach (var featurePath in featureFiles)
        {
            var content = File.ReadAllText(featurePath);
            var relativePath = Path.GetRelativePath(testRepoPath, featurePath);
            var featureTitle = FeatureTitlePattern.Match(content) is { Success: true } fm ? fm.Groups[1].Value.Trim() : string.Empty;

            foreach (Match m in ScenarioPattern.Matches(content))
            {
                var name = m.Groups["name"].Value.Trim();
                var tags = TagPattern.Matches(m.Groups["tags"].Value).Select(t => t.Groups[1].Value).ToList();
                var steps = StepPattern.Matches(m.Groups["body"].Value)
                    .Select(s => $"{s.Groups["kw"].Value} {s.Groups["text"].Value.Trim()}").ToList();

                var record = new ScenarioRecord
                {
                    ScenarioName = name, FeatureFile = relativePath, FeatureTitle = featureTitle,
                    Tags = tags, Steps = steps, IsOutline = m.Groups["outline"].Success
                };

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
                        record.BoundSelectors.AddRange(binding.Selectors);

                        foreach (var pageName in binding.Pages)
                        {
                            if (_pageObjectCfPages.TryGetValue(pageName, out var cf)) record.BoundColdFusionPages.AddRange(cf);
                            if (_pageObjectSelectors.TryGetValue(pageName, out var sel)) record.BoundSelectors.AddRange(sel);
                        }
                    }
                }

                record.BoundEndpoints       = record.BoundEndpoints.Distinct().ToList();
                record.BoundPageObjects     = record.BoundPageObjects.Distinct().ToList();
                record.BoundSoapProxies     = record.BoundSoapProxies.Distinct().ToList();
                record.BoundColdFusionPages = record.BoundColdFusionPages.Distinct().ToList();
                record.BoundSelectors       = record.BoundSelectors.Distinct().ToList();
                yield return record;
            }
        }
    }

    private record BindingEntry(string Pattern, List<string> Endpoints, List<string> Pages, List<string> Proxies, List<string> CfPages, List<string> Selectors);

    private List<BindingEntry> BuildBindingMap(List<string> stepFiles, List<string> pageObjectFiles)
    {
        _pageObjectCfPages = new();
        _pageObjectSelectors = new();

        foreach (var file in pageObjectFiles)
        {
            var content = File.ReadAllText(file);
            var cm = Regex.Match(content, @"class\s+(\w+Page)\b");
            if (!cm.Success) continue;
            var className = cm.Groups[1].Value;
            var cfPages = CfPagePattern.Matches(content).Select(m => Path.GetFileName(m.Groups[1].Value)).Distinct().ToList();
            var selectors = SelectorPattern.Matches(content).Select(m => m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value).Where(s => !string.IsNullOrWhiteSpace(s)).Distinct().ToList();
            if (cfPages.Any()) _pageObjectCfPages[className] = cfPages;
            if (selectors.Any()) _pageObjectSelectors[className] = selectors;
        }

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
                map.Add(new BindingEntry(
                    bindings[i].Groups["pattern"].Value,
                    HttpCallPattern.Matches(chunk).Select(m => m.Groups["url"].Value).Distinct().ToList(),
                    PageObjectPattern.Matches(chunk).Select(m => m.Groups["name"].Value).Distinct().ToList(),
                    SoapProxyPattern.Matches(chunk).Select(m => m.Groups["name"].Value).Distinct().ToList(),
                    CfPagePattern.Matches(chunk).Select(m => Path.GetFileName(m.Groups[1].Value)).Distinct().ToList(),
                    SelectorPattern.Matches(chunk).Select(m => m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value).Where(s => !string.IsNullOrWhiteSpace(s)).Distinct().ToList()
                ));
            }
        }
        return map;
    }

    private static bool IsLikelyMatch(string bindingPattern, string stepText)
    {
        var cleaned = Regex.Replace(bindingPattern, @"\{[^}]+\}|\(.*?\)|\\d\+|\.\*|\[\^.*?\]", " ");
        var words = cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => w.Length > 2).Select(w => w.ToLowerInvariant()).ToList();
        if (words.Count == 0) return false;
        var stepLower = stepText.ToLowerInvariant();
        return words.Count(w => stepLower.Contains(w)) >= Math.Max(1, (int)Math.Ceiling(words.Count * 0.6));
    }
}
