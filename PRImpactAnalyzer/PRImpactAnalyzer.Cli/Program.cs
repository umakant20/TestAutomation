using System.Text.Json;
using Microsoft.Extensions.Logging;
using PRImpactAnalyzer.Core;
using PRImpactAnalyzer.Core.Models;
using PRImpactAnalyzer.Infrastructure;

// PR Test Impact Analyzer — headless CLI runner.
//
// Usage:
//   pr-impact --pr <ado-pr-url> --test-repo <path> [--pat <token>] [--model <name>]
//             [--json] [--fail-on-impact] [--quiet]
//
// Auth:
//   --pat or env ADO_PAT          Azure DevOps Personal Access Token (Code: Read)
//   Copilot auth is handled by the Copilot CLI's own login (copilot login / gh auth login).
//
// Exit codes (useful in CI):
//   0  success (analysis ran)
//   2  success but impacted scenarios found AND --fail-on-impact was set
//   1  error

var argMap = ParseArgs(args);

if (argMap.ContainsKey("help") || !argMap.ContainsKey("pr") || !argMap.ContainsKey("test-repo"))
{
    PrintUsage();
    return argMap.ContainsKey("help") ? 0 : 1;
}

var pat = argMap.GetValueOrDefault("pat") ?? Environment.GetEnvironmentVariable("ADO_PAT");
if (string.IsNullOrWhiteSpace(pat))
{
    Console.Error.WriteLine("ERROR: No Azure DevOps PAT supplied. Pass --pat or set ADO_PAT.");
    return 1;
}

var jsonOutput = argMap.ContainsKey("json");
var quiet = argMap.ContainsKey("quiet") || jsonOutput;
var failOnImpact = argMap.ContainsKey("fail-on-impact");
var model = argMap.GetValueOrDefault("model") ?? "claude-haiku-4.5";

await using var analyzer = PrImpactAnalyzerFacade.Create(
    services => PRImpactAnalyzer.Infrastructure.PrImpactAnalyzerRegistration.AddPrImpactAnalyzer(services, model),
    logging => logging
        .AddConsole()
        .SetMinimumLevel(quiet ? LogLevel.Warning : LogLevel.Information));

var request = new AnalysisRequest
{
    DevRepoPrUrl      = argMap["pr"],
    TestRepoLocalPath = argMap["test-repo"],
    DevRepoLocalPath  = argMap.GetValueOrDefault("dev-repo") ?? string.Empty,
    AzureDevOpsPat    = pat,
};

var result = await analyzer.AnalyzeAsync(request);

// Write an HTML report every run — this is the primary way to review everything together.
var reportPath = argMap.GetValueOrDefault("report");
string writtenReportPath;
try
{
    writtenReportPath = PRImpactAnalyzer.Core.Pipeline.HtmlReportWriter.Write(result, reportPath);
}
catch (Exception ex)
{
    Console.Error.WriteLine($"WARNING: could not write HTML report: {ex.Message}");
    writtenReportPath = "(not written)";
}

if (!result.Success)
{
    Console.Error.WriteLine($"ERROR: {result.ErrorMessage}");
    return 1;
}

if (jsonOutput)
{
    Console.WriteLine(JsonSerializer.Serialize(result.ImpactedScenarios, new JsonSerializerOptions { WriteIndented = true }));
    Console.Error.WriteLine($"HTML report written to: {writtenReportPath}");
}
else
{
    Console.WriteLine();
    Console.WriteLine($"=== PR Test Impact Analysis ===");
    Console.WriteLine($"PR: {result.PrMetadata?.Title}");
    Console.WriteLine($"Changed symbols: {result.ChangedSymbols.Count} | Scenarios scanned: {result.AllScenarios.Count}");
    Console.WriteLine($"Impacted scenarios: {result.ImpactedScenarios.Count}");
    Console.WriteLine();

    if (result.ImpactedScenarios.Count == 0)
    {
        Console.WriteLine("No impacted scenarios found.");
    }
    else
    {
        foreach (var s in result.ImpactedScenarios)
        {
            Console.WriteLine($"[{s.Confidence,-6}] {s.FeatureFile} :: {s.ScenarioName}");
            Console.WriteLine($"          matched: {s.MatchedChange}");
            if (!string.IsNullOrWhiteSpace(s.Reason))
                Console.WriteLine($"          why:     {s.Reason}");
        }
    }
    Console.WriteLine();
    Console.WriteLine($"HTML report written to: {writtenReportPath}");
}

if (failOnImpact && result.ImpactedScenarios.Count > 0)
    return 2;

return 0;

// ── helpers ──────────────────────────────────────────────────────────────────
static Dictionary<string, string> ParseArgs(string[] args)
{
    var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    for (int i = 0; i < args.Length; i++)
    {
        if (!args[i].StartsWith("--")) continue;
        var key = args[i][2..];
        // Flags with no value
        if (i + 1 >= args.Length || args[i + 1].StartsWith("--"))
        {
            map[key] = "true";
        }
        else
        {
            map[key] = args[++i];
        }
    }
    return map;
}

static void PrintUsage()
{
    Console.WriteLine(@"PR Test Impact Analyzer

Usage:
  pr-impact --pr <ado-pr-url> --test-repo <path> [options]

Required:
  --pr <url>          Azure DevOps PR URL (https://dev.azure.com/org/proj/_git/repo/pullrequest/123)
  --test-repo <path>  Local path to the C# SpecFlow/Reqnroll test repo

Options:
  --pat <token>       Azure DevOps PAT (Code: Read). Defaults to env ADO_PAT.
  --dev-repo <path>   Local path to the dev repo (optional extra context)
  --model <name>      Copilot model (default: claude-haiku-4.5)
  --report <path>     HTML report output path (default: ./pr-impact-report-<timestamp>.html)
  --json              Output impacted scenarios as JSON (implies --quiet)
  --fail-on-impact    Exit code 2 if any scenario is impacted (useful as a CI gate)
  --quiet             Suppress info logging
  --help              Show this help

Copilot auth uses the Copilot CLI's own login (run 'copilot login' once).");
}
