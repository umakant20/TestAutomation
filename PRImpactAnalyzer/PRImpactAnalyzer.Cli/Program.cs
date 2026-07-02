using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using PRImpactAnalyzer.Core;
using PRImpactAnalyzer.Core.Models;
using PRImpactAnalyzer.Infrastructure;

// PR Test Impact Analyzer — manual-paste CLI runner, driven entirely by a JSON config file.
//
//   pr-impact prepare [path-to-config.json]   (defaults to "pr-impact-config.json")
//   pr-impact report  [path-to-config.json]

if (args.Length == 0 || args[0] is "--help" or "-h")
{
    PrintUsage();
    return args.Length == 0 ? 1 : 0;
}

var command   = args[0];
var configPath = args.Length > 1 ? args[1] : "pr-impact-config.json";

if (!File.Exists(configPath))
{
    Console.Error.WriteLine($"ERROR: config file not found at '{Path.GetFullPath(configPath)}'.");
    Console.Error.WriteLine("Create one (see usage below) or pass its path as the second argument.");
    PrintUsage();
    return 1;
}

var config = JsonSerializer.Deserialize<PrImpactConfig>(
    await File.ReadAllTextAsync(configPath),
    new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
    ?? throw new InvalidOperationException($"Could not parse config file at '{configPath}'.");

// configPath is passed explicitly as a parameter — static local functions cannot
// capture variables from the enclosing top-level scope in C#.
return command switch
{
    "prepare" => await RunPrepareAsync(config, configPath),
    "report"  => await RunReportAsync(config),
    _         => Unknown(command)
};

// ── subcommands ──────────────────────────────────────────────────────────────

static int Unknown(string command)
{
    Console.Error.WriteLine($"Unknown command '{command}'. Expected 'prepare' or 'report'.");
    PrintUsage();
    return 1;
}

static async Task<int> RunPrepareAsync(PrImpactConfig config, string configPath)
{
    if (string.IsNullOrWhiteSpace(config.Pr) || string.IsNullOrWhiteSpace(config.TestRepoPath))
    {
        Console.Error.WriteLine("ERROR: config must include 'pr' and 'testRepoPath' for the 'prepare' command.");
        return 1;
    }

    var pat = config.AzureDevOpsPat ?? Environment.GetEnvironmentVariable("ADO_PAT");
    if (string.IsNullOrWhiteSpace(pat))
    {
        Console.Error.WriteLine("ERROR: No Azure DevOps PAT supplied. Set 'azureDevOpsPat' in the config or the ADO_PAT environment variable.");
        return 1;
    }

    var promptOut = config.PromptOutput ?? "prompt.txt";
    var stateOut  = config.StateOutput  ?? "state.json";

    await using var analyzer = PrImpactAnalyzerFacade.Create(
        services => services.AddPrImpactAnalyzer(),
        logging  => logging.AddConsole().SetMinimumLevel(LogLevel.Information));

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
    Console.WriteLine($"Prompt written to : {Path.GetFullPath(promptOut)}");
    Console.WriteLine($"State written to  : {Path.GetFullPath(stateOut)}");
    Console.WriteLine($"Chunks            : {prepared.PromptChunks.Count}");
    Console.WriteLine();
    Console.WriteLine("Next steps:");
    Console.WriteLine($"  1. Open {promptOut} and paste its contents into Copilot Chat" +
        (prepared.PromptChunks.Count > 1 ? " — ONE CHUNK AT A TIME, each in a fresh thread." : "."));
    Console.WriteLine("  2. Save Copilot's JSON reply to a file (e.g. response.txt).");
    Console.WriteLine($"  3. Add that file path to 'responseFiles' in {configPath}, then run:");
    Console.WriteLine($"       pr-impact report {configPath}");

    return 0;
}

static async Task<int> RunReportAsync(PrImpactConfig config)
{
    var stateFile = config.StateOutput ?? "state.json";

    if (!File.Exists(stateFile))
    {
        Console.Error.WriteLine($"ERROR: state file not found at '{Path.GetFullPath(stateFile)}'. Run 'prepare' first.");
        return 1;
    }

    if (config.ResponseFiles is null || config.ResponseFiles.Count == 0)
    {
        Console.Error.WriteLine("ERROR: 'responseFiles' in the config must list at least one file containing Copilot's pasted JSON reply.");
        return 1;
    }

    foreach (var f in config.ResponseFiles)
    {
        if (!File.Exists(f))
        {
            Console.Error.WriteLine($"ERROR: response file not found at '{Path.GetFullPath(f)}'.");
            return 1;
        }
    }

    var reportOut = config.ReportOutput ?? $"pr-impact-report-{DateTime.Now:yyyyMMdd-HHmmss}.html";

    await using var analyzer = PrImpactAnalyzerFacade.Create(
        services => services.AddPrImpactAnalyzer(),
        logging  => logging.AddConsole().SetMinimumLevel(LogLevel.Warning));

    var result = await analyzer.FinalizeFromFilesAsync(stateFile, config.ResponseFiles, reportOut);

    if (!result.Success)
    {
        Console.Error.WriteLine($"ERROR: {result.ErrorMessage}");
        return 1;
    }

    Console.WriteLine();
    Console.WriteLine("=== PR Test Impact Report ===");
    Console.WriteLine($"PR               : {result.PrMetadata?.Title}");
    Console.WriteLine($"Changed symbols  : {result.ChangedSymbols.Count}");
    Console.WriteLine($"Scenarios scanned: {result.AllScenarioCount}");
    Console.WriteLine($"Impacted         : {result.ImpactedScenarios.Count}");
    Console.WriteLine();
    Console.WriteLine($"HTML report: {Path.GetFullPath(reportOut)}");

    return result.ImpactedScenarios.Count > 0 && config.FailOnImpact ? 2 : 0;
}

static void PrintUsage()
{
    Console.WriteLine(@"PR Test Impact Analyzer — JSON-config-driven, manual Copilot Chat paste workflow.

Usage:
  pr-impact prepare [config.json]   (defaults to pr-impact-config.json)
  pr-impact report  [config.json]

Example pr-impact-config.json:
{
  ""pr"":             ""https://dev.azure.com/org/proj/_git/repo/pullrequest/482"",
  ""testRepoPath"":   ""C:\\source\\MyApp.Tests"",
  ""devRepoPath"":    """",
  ""azureDevOpsPat"": null,
  ""promptOutput"":   ""prompt.txt"",
  ""stateOutput"":    ""state.json"",
  ""responseFiles"":  [ ""response.txt"" ],
  ""reportOutput"":   ""report.html"",
  ""failOnImpact"":   false
}

Notes:
  - azureDevOpsPat can be null/omitted and set via the ADO_PAT environment variable instead.
  - responseFiles only matters for 'report' — add the path(s) after pasting Copilot's reply.
  - If 'prepare' produced more than one chunk, list one response file per chunk, in order.

Workflow:
  1. pr-impact prepare config.json
  2. Paste prompt.txt into Copilot Chat; save the JSON reply to response.txt.
     (The code parses the JSON automatically — you just save the raw reply text.)
  3. Add ""response.txt"" to responseFiles in config.json.
  4. pr-impact report config.json  →  writes report.html automatically.
");
}

// ── Config model ─────────────────────────────────────────────────────────────

/// <summary>Shape of pr-impact-config.json — shared by both 'prepare' and 'report'.</summary>
public class PrImpactConfig
{
    [JsonPropertyName("pr")]             public string?       Pr             { get; set; }
    [JsonPropertyName("testRepoPath")]   public string?       TestRepoPath   { get; set; }
    [JsonPropertyName("devRepoPath")]    public string?       DevRepoPath    { get; set; }
    [JsonPropertyName("azureDevOpsPat")] public string?       AzureDevOpsPat { get; set; }
    [JsonPropertyName("promptOutput")]   public string?       PromptOutput   { get; set; }
    [JsonPropertyName("stateOutput")]    public string?       StateOutput    { get; set; }
    [JsonPropertyName("responseFiles")]  public List<string>? ResponseFiles  { get; set; }
    [JsonPropertyName("reportOutput")]   public string?       ReportOutput   { get; set; }
    [JsonPropertyName("failOnImpact")]   public bool          FailOnImpact   { get; set; }
}
