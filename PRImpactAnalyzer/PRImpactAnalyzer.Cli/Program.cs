using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using PRImpactAnalyzer.Core;
using PRImpactAnalyzer.Core.Models;
using PRImpactAnalyzer.Infrastructure;

// PR Test Impact Analyzer CLI
// Usage:
//   pr-impact prepare [config.json] [prId]   (prId is optional — overrides/builds the PR URL
//                                              from config's prBaseUrl if supplied; falls back
//                                              to config's static "pr" field if omitted)
//   pr-impact report  [config.json]

if (args.Length == 0 || args[0] is "--help" or "-h") { PrintUsage(); return args.Length == 0 ? 1 : 0; }

var command    = args[0];
var configPath = args.Length > 1 ? args[1] : "pr-impact-config.json";
var explicitPrId = args.Length > 2 ? args[2] : null;

if (!File.Exists(configPath))
{
    Console.Error.WriteLine($"ERROR: config file not found at '{Path.GetFullPath(configPath)}'.");
    Console.Error.WriteLine("Create pr-impact-config.json (see usage below) or pass its path as the second argument.");
    PrintUsage();
    return 1;
}

var config = JsonSerializer.Deserialize<PrImpactConfig>(
    await File.ReadAllTextAsync(configPath),
    new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
    ?? throw new InvalidOperationException($"Could not parse config file at '{configPath}'.");

// configPath and explicitPrId are passed as explicit parameters — static local functions
// cannot capture variables from the enclosing top-level scope in C#.
return command switch
{
    "prepare" => await RunPrepareAsync(config, configPath, explicitPrId),
    "report"  => await RunReportAsync(config),
    _         => Unknown(command)
};

static int Unknown(string cmd) { Console.Error.WriteLine($"Unknown command '{cmd}'. Expected 'prepare' or 'report'."); PrintUsage(); return 1; }

static async Task<int> RunPrepareAsync(PrImpactConfig config, string configPath, string? explicitPrId)
{
    // Resolve the PR URL: an explicit PR ID passed on the command line takes precedence
    // (built from config's prBaseUrl), falling back to a static full URL in config's "pr"
    // field if no PR ID was supplied. This lets the SAME config file be reused across many
    // PRs — only the PR number needs to change per invocation, which is exactly what the
    // Copilot prompt file's ${input:prId} variable supplies at call time.
    string? prUrl;
    if (!string.IsNullOrWhiteSpace(explicitPrId))
    {
        if (string.IsNullOrWhiteSpace(config.PrBaseUrl))
        {
            Console.Error.WriteLine("ERROR: a PR ID was passed on the command line, but config is missing 'prBaseUrl' to build the full URL from it.");
            Console.Error.WriteLine("Add \"prBaseUrl\": \"https://dev.azure.com/yourorg/yourproject/_git/yourrepo\" to the config.");
            return 1;
        }
        prUrl = $"{config.PrBaseUrl.TrimEnd('/')}/pullrequest/{explicitPrId}";
    }
    else
    {
        prUrl = config.Pr;
    }

    if (string.IsNullOrWhiteSpace(prUrl) || string.IsNullOrWhiteSpace(config.TestRepoPath))
    { Console.Error.WriteLine("ERROR: no PR to analyze. Either pass a PR ID as the 3rd argument (with 'prBaseUrl' set in config), or set a full 'pr' URL in config. 'testRepoPath' is also required."); return 1; }

    var pat = config.AzureDevOpsPat ?? Environment.GetEnvironmentVariable("ADO_PAT");
    if (string.IsNullOrWhiteSpace(pat))
    { Console.Error.WriteLine("ERROR: No Azure DevOps PAT supplied. Set 'azureDevOpsPat' in the config or the ADO_PAT environment variable."); return 1; }

    var promptOut = config.PromptOutput ?? "prompt.txt";
    var stateOut  = config.StateOutput  ?? "state.json";

    await using var analyzer = PrImpactAnalyzerFacade.Create(
        services => PRImpactAnalyzer.Infrastructure.PrImpactAnalyzerRegistration.AddPrImpactAnalyzer(services),
        logging  => logging.AddConsole().SetMinimumLevel(LogLevel.Information));

    var prepared = await analyzer.PrepareAndWriteFilesAsync(
        new AnalysisRequest
        {
            DevRepoPrUrl      = prUrl,
            TestRepoLocalPath = config.TestRepoPath,
            DevRepoLocalPath  = config.DevRepoPath ?? string.Empty,
            AzureDevOpsPat    = pat,
        },
        promptOut, stateOut);

    if (!string.IsNullOrEmpty(prepared.Warning))
    {
        Console.Error.WriteLine($"WARNING: {prepared.Warning}");
        Console.Error.WriteLine("No prompt file written — nothing to send to Copilot for this PR.");
        return 1;
    }

    Console.WriteLine();
    Console.WriteLine($"Prompt written to : {Path.GetFullPath(promptOut)}");
    Console.WriteLine($"State written to  : {Path.GetFullPath(stateOut)}");
    Console.WriteLine($"Chunks            : {prepared.PromptChunks.Count}");
    Console.WriteLine();
    Console.WriteLine("Next steps:");
    Console.WriteLine($"  1. Open {promptOut} and paste its contents into Copilot Chat.");
    Console.WriteLine("  2. Save Copilot's JSON reply to a file — e.g. response.txt.");
    Console.WriteLine($"     (All {prepared.PromptChunks.Count} chunk replies can go into ONE file, one after another.)");
    Console.WriteLine($"  3. Add the response file path to 'responseFiles' in {configPath}, then run:");
    Console.WriteLine($"       pr-impact report {configPath}");
    return 0;
}

static async Task<int> RunReportAsync(PrImpactConfig config)
{
    var stateFile = config.StateOutput ?? "state.json";
    if (!File.Exists(stateFile))
    { Console.Error.WriteLine($"ERROR: state file not found at '{Path.GetFullPath(stateFile)}'. Run 'prepare' first."); return 1; }

    if (config.ResponseFiles is null || config.ResponseFiles.Count == 0)
    {
        Console.Error.WriteLine("ERROR: 'responseFiles' must list at least one file containing Copilot's pasted JSON reply.");
        Console.Error.WriteLine("Tip:   All chunk replies can be pasted into ONE file, one after another.");
        Console.Error.WriteLine("       The tool automatically splits and parses each JSON block.");
        return 1;
    }

    foreach (var f in config.ResponseFiles)
        if (!File.Exists(f)) { Console.Error.WriteLine($"ERROR: response file not found: '{Path.GetFullPath(f)}'."); return 1; }

    var reportOut = config.ReportOutput ?? $"pr-impact-report-{DateTime.Now:yyyyMMdd-HHmmss}.html";

    await using var analyzer = PrImpactAnalyzerFacade.Create(
        services => PRImpactAnalyzer.Infrastructure.PrImpactAnalyzerRegistration.AddPrImpactAnalyzer(services),
        logging  => logging.AddConsole().SetMinimumLevel(LogLevel.Warning));

    var result = await analyzer.FinalizeFromFilesAsync(stateFile, config.ResponseFiles, reportOut);

    if (!result.Success) { Console.Error.WriteLine($"ERROR: {result.ErrorMessage}"); return 1; }

    Console.WriteLine();
    Console.WriteLine("=== PR Test Impact Report ===");
    Console.WriteLine($"PR               : {result.PrMetadata?.Title}");
    Console.WriteLine($"Changed symbols  : {result.ChangedSymbols.Count}");
    Console.WriteLine($"Scenarios scanned: {result.AllScenarioCount}");
    Console.WriteLine($"Impacted         : {result.ImpactedScenarios.Count}");
    Console.WriteLine();
    Console.WriteLine($"HTML report      : {Path.GetFullPath(reportOut)}");

    return result.ImpactedScenarios.Count > 0 && config.FailOnImpact ? 2 : 0;
}

static void PrintUsage() => Console.WriteLine(@"
PR Test Impact Analyzer — JSON-config-driven, manual Copilot Chat paste workflow.

Usage:
  pr-impact prepare [config.json] [prId]   (prId optional — see below)
  pr-impact report  [config.json]

Two ways to specify which PR to analyze:
  A) Static: set a full ""pr"" URL in config.json, then just run:
       pr-impact prepare config.json
  B) Dynamic: set ""prBaseUrl"" in config.json (no ""pr"" needed), then pass the PR
     number as a 3rd argument — the full URL is built automatically:
       pr-impact prepare config.json 16773
     This lets ONE config file be reused across every PR — only the number changes.

Workflow:
  1. Fill in pr-impact-config.json (testRepoPath, azureDevOpsPat, and either
     ""pr"" or ""prBaseUrl"", at minimum).
  2. pr-impact prepare config.json [prId]  →  writes prompt.txt and state.json
  3. Paste prompt.txt into Copilot Chat. Save ALL replies (all chunks) into one response.txt.
  4. Add ""response.txt"" to responseFiles in config.json.
  5. pr-impact report config.json  →  parses the JSON automatically, writes report.html

See the sample pr-impact-config.json included in the solution.
");

public class PrImpactConfig
{
    [JsonPropertyName("pr")]             public string?       Pr             { get; set; }
    [JsonPropertyName("prBaseUrl")]      public string?       PrBaseUrl      { get; set; }
    [JsonPropertyName("testRepoPath")]   public string?       TestRepoPath   { get; set; }
    [JsonPropertyName("devRepoPath")]    public string?       DevRepoPath    { get; set; }
    [JsonPropertyName("azureDevOpsPat")] public string?       AzureDevOpsPat { get; set; }
    [JsonPropertyName("promptOutput")]   public string?       PromptOutput   { get; set; }
    [JsonPropertyName("stateOutput")]    public string?       StateOutput    { get; set; }
    [JsonPropertyName("responseFiles")]  public List<string>? ResponseFiles  { get; set; }
    [JsonPropertyName("reportOutput")]   public string?       ReportOutput   { get; set; }
    [JsonPropertyName("failOnImpact")]   public bool          FailOnImpact   { get; set; }
}
