using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using PRImpactAnalyzer.Core;
using PRImpactAnalyzer.Core.Models;
using PRImpactAnalyzer.Infrastructure;

// PR Test Impact Analyzer — manual-paste CLI runner, driven entirely by a JSON config file
// (no command-line flags to type/remember). Two commands, sharing one config file:
//
//   pr-impact prepare [path-to-config.json]   (defaults to "pr-impact-config.json" in the
//                                               current directory if omitted)
//   pr-impact report  [path-to-config.json]
//
// `prepare` reads PR/repo/PAT fields from the config and writes a prompt file + state file.
// `report` reads the responseFiles list from the same config and writes the HTML report.
//
// There is no programmatic Copilot access for an individual subscription without an explicit
// GitHub token grant that isn't obtainable for this use case — the LLM step stays manual:
// you paste the prompt file into Copilot Chat yourself, save the reply to a file, and list
// that file's path in the config's "responseFiles" array before running `report`.

if (args.Length == 0 || args[0] is "--help" or "-h")
{
    PrintUsage();
    return args.Length == 0 ? 1 : 0;
}

var command = args[0];
var configPath = args.Length > 1 ? args[1] : "pr-impact-config.json";

if (!File.Exists(configPath))
{
    Console.Error.WriteLine($"ERROR: config file not found at '{Path.GetFullPath(configPath)}'.");
    Console.Error.WriteLine("Create one (see PrImpactConfig fields below) or pass its path as the second argument.");
    PrintUsage();
    return 1;
}

var config = JsonSerializer.Deserialize<PrImpactConfig>(
    await File.ReadAllTextAsync(configPath),
    new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
    ?? throw new InvalidOperationException($"Could not parse config file at '{configPath}'.");

return command switch
{
    "prepare" => await RunPrepareAsync(config),
    "report"  => await RunReportAsync(config),
    _ => Unknown(command)
};

static int Unknown(string command)
{
    Console.Error.WriteLine($"Unknown command '{command}'. Expected 'prepare' or 'report'.");
    PrintUsage();
    return 1;
}

static async Task<int> RunPrepareAsync(PrImpactConfig config)
{
    if (string.IsNullOrWhiteSpace(config.Pr) || string.IsNullOrWhiteSpace(config.TestRepoPath))
    {
        Console.Error.WriteLine("ERROR: config must include 'pr' and 'testRepoPath' for the 'prepare' command.");
        return 1;
    }

    var pat = config.AzureDevOpsPat ?? Environment.GetEnvironmentVariable("ADO_PAT");
    if (string.IsNullOrWhiteSpace(pat))
    {
        Console.Error.WriteLine("ERROR: No Azure DevOps PAT supplied. Set 'azureDevOpsPat' in the config, or the ADO_PAT environment variable.");
        return 1;
    }

    var promptOut = config.PromptOutput ?? "prompt.txt";
    var stateOut  = config.StateOutput ?? "state.json";

    await using var analyzer = PrImpactAnalyzerFacade.Create(
        services => services.AddPrImpactAnalyzer(),
        logging => logging.AddConsole().SetMinimumLevel(LogLevel.Information));

    var request = new AnalysisRequest
    {
        DevRepoPrUrl      = config.Pr,
        TestRepoLocalPath = config.TestRepoPath,
        DevRepoLocalPath  = config.DevRepoPath ?? string.Empty,
        AzureDevOpsPat    = pat,
    };

    var prepared = await analyzer.PrepareAndWriteFilesAsync(request, promptOut, stateOut);

    if (!string.IsNullOrEmpty(prepared.Warning))
    {
        Console.Error.WriteLine($"WARNING: {prepared.Warning}");
        Console.Error.WriteLine("No prompt file was written — nothing to send to Copilot for this PR.");
        return 1;
    }

    Console.WriteLine();
    Console.WriteLine($"Prompt written to:  {Path.GetFullPath(promptOut)}");
    Console.WriteLine($"State written to:   {Path.GetFullPath(stateOut)}");
    Console.WriteLine($"Chunks:             {prepared.PromptChunks.Count}");
    Console.WriteLine();
    Console.WriteLine("Next steps:");
    Console.WriteLine($"  1. Open {promptOut} and paste its contents into Copilot Chat" +
        (prepared.PromptChunks.Count > 1 ? " — ONE CHUNK AT A TIME, each in a fresh thread." : "."));
    Console.WriteLine("  2. Save Copilot's JSON reply to a file (one file per chunk, if more than one).");
    Console.WriteLine($"  3. Add the response file path(s) to 'responseFiles' in {configPath}, then run: pr-impact report {configPath}");

    return 0;
}

static async Task<int> RunReportAsync(PrImpactConfig config)
{
    if (string.IsNullOrWhiteSpace(config.StateOutput))
    {
        Console.Error.WriteLine("ERROR: config's 'stateOutput' must point at the state.json written by 'prepare'.");
        return 1;
    }
    if (config.ResponseFiles is null || config.ResponseFiles.Count == 0)
    {
        Console.Error.WriteLine("ERROR: config's 'responseFiles' must list at least one file containing Copilot's pasted JSON reply.");
        return 1;
    }

    var reportOut = config.ReportOutput ?? $"pr-impact-report-{DateTime.Now:yyyyMMdd-HHmmss}.html";

    await using var analyzer = PrImpactAnalyzerFacade.Create(
        services => services.AddPrImpactAnalyzer(),
        logging => logging.AddConsole().SetMinimumLevel(LogLevel.Warning));

    var result = await analyzer.FinalizeFromFilesAsync(config.StateOutput, config.ResponseFiles, reportOut);

    if (!result.Success)
    {
        Console.Error.WriteLine($"ERROR: {result.ErrorMessage}");
        return 1;
    }

    Console.WriteLine();
    Console.WriteLine($"=== PR Test Impact Report ===");
    Console.WriteLine($"PR: {result.PrMetadata?.Title}");
    Console.WriteLine($"Changed symbols: {result.ChangedSymbols.Count} | Scenarios scanned: {result.AllScenarioCount}");
    Console.WriteLine($"Impacted scenarios: {result.ImpactedScenarios.Count}");
    Console.WriteLine();
    Console.WriteLine($"HTML report written to: {Path.GetFullPath(reportOut)}");

    return result.ImpactedScenarios.Count > 0 && config.FailOnImpact ? 2 : 0;
}

static void PrintUsage()
{
    Console.WriteLine(@"PR Test Impact Analyzer — manual-paste workflow, driven by a JSON config file.

Usage:
  pr-impact prepare [config.json]   (defaults to pr-impact-config.json in the current directory)
  pr-impact report  [config.json]

Example config.json:
{
  ""pr"": ""https://dev.azure.com/yourorg/yourproject/_git/yourrepo/pullrequest/482"",
  ""testRepoPath"": ""C:\\source\\MyApp.Tests"",
  ""devRepoPath"": """",
  ""azureDevOpsPat"": null,
  ""promptOutput"": ""prompt.txt"",
  ""stateOutput"": ""state.json"",
  ""responseFiles"": [""response.txt""],
  ""reportOutput"": ""report.html"",
  ""failOnImpact"": false
}

Notes:
  - azureDevOpsPat can be left null/omitted and supplied via the ADO_PAT environment variable instead.
  - responseFiles is only needed for the 'report' command — add the path(s) after you've pasted
    Copilot's reply into a file. List one path per prompt chunk, in order, if 'prepare' produced more than one.

Workflow:
  1. pr-impact prepare config.json
  2. Paste prompt.txt into Copilot Chat; save the reply to a file; add that path to responseFiles in config.json
  3. pr-impact report config.json
");
}

/// <summary>JSON config file shape — every field used by either 'prepare' or 'report'.</summary>
public class PrImpactConfig
{
    // Used by 'prepare'
    [JsonPropertyName("pr")] public string? Pr { get; set; }
    [JsonPropertyName("testRepoPath")] public string? TestRepoPath { get; set; }
    [JsonPropertyName("devRepoPath")] public string? DevRepoPath { get; set; }
    [JsonPropertyName("azureDevOpsPat")] public string? AzureDevOpsPat { get; set; }
    [JsonPropertyName("promptOutput")] public string? PromptOutput { get; set; }
    [JsonPropertyName("stateOutput")] public string? StateOutput { get; set; }

    // Used by 'report'
    [JsonPropertyName("responseFiles")] public List<string>? ResponseFiles { get; set; }
    [JsonPropertyName("reportOutput")] public string? ReportOutput { get; set; }
    [JsonPropertyName("failOnImpact")] public bool FailOnImpact { get; set; }
}
