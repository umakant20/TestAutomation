using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using PRImpactAnalyzer.Core;
using PRImpactAnalyzer.Core.Models;
using PRImpactAnalyzer.Infrastructure;

// PR Test Impact Analyzer CLI
// Usage:
//   pr-impact prepare [config.json] [prId]   (prId optional — see below)
//   pr-impact report  [config.json]
//
// Every "prepare" run creates its own dated folder under Reports/{prId}_{timestamp}/
// containing prompt.txt and state.json — past runs are never overwritten. "report" finds
// the most recent run automatically (no config editing needed) and writes the HTML report
// into that same folder, named after the PR.

if (args.Length == 0 || args[0] is "--help" or "-h") { PrintUsage(); return args.Length == 0 ? 1 : 0; }

var command      = args[0];
var configPath   = args.Length > 1 ? args[1] : "pr-impact-config.json";
var explicitPrId = args.Length > 2 ? args[2] : null;

if (!File.Exists(configPath))
{
    Console.Error.WriteLine($"ERROR: config file not found at '{Path.GetFullPath(configPath)}'.");
    PrintUsage();
    return 1;
}

var config = JsonSerializer.Deserialize<PrImpactConfig>(
    await File.ReadAllTextAsync(configPath),
    new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
    ?? throw new InvalidOperationException($"Could not parse config file at '{configPath}'.");

return command switch
{
    "prepare" => await RunPrepareAsync(config, explicitPrId),
    "report"  => await RunReportAsync(config),
    _         => Unknown(command)
};

static int Unknown(string cmd) { Console.Error.WriteLine($"Unknown command '{cmd}'. Expected 'prepare' or 'report'."); PrintUsage(); return 1; }

static async Task<int> RunPrepareAsync(PrImpactConfig config, string? explicitPrId)
{
    string? prUrl;
    if (!string.IsNullOrWhiteSpace(explicitPrId))
    {
        if (string.IsNullOrWhiteSpace(config.PrBaseUrl))
        { Console.Error.WriteLine("ERROR: PR ID given but config is missing 'prBaseUrl'."); return 1; }
        prUrl = $"{config.PrBaseUrl.TrimEnd('/')}/pullrequest/{explicitPrId}";
    }
    else
    {
        prUrl = config.Pr;
    }

    if (string.IsNullOrWhiteSpace(prUrl) || string.IsNullOrWhiteSpace(config.TestRepoPath))
    { Console.Error.WriteLine("ERROR: no PR to analyze (pass a PR ID with 'prBaseUrl' set, or set a full 'pr' URL) and 'testRepoPath' is required."); return 1; }

    var pat = config.AzureDevOpsPat ?? Environment.GetEnvironmentVariable("ADO_PAT");
    if (string.IsNullOrWhiteSpace(pat))
    { Console.Error.WriteLine("ERROR: No Azure DevOps PAT. Set 'azureDevOpsPat' in config or the ADO_PAT env var."); return 1; }

    var reportsBaseDir = config.ReportsBaseDir ?? "Reports";

    await using var analyzer = PrImpactAnalyzerFacade.Create(
        services => PRImpactAnalyzer.Infrastructure.PrImpactAnalyzerRegistration.AddPrImpactAnalyzer(services),
        logging  => logging.AddConsole().SetMinimumLevel(LogLevel.Information));

    var run = await analyzer.PrepareAndWriteFilesAutoAsync(
        new AnalysisRequest
        {
            DevRepoPrUrl      = prUrl,
            TestRepoLocalPath = config.TestRepoPath,
            DevRepoLocalPath  = config.DevRepoPath ?? string.Empty,
            AzureDevOpsPat    = pat,
        },
        reportsBaseDir);

    if (!string.IsNullOrEmpty(run.Prepared.Warning))
    {
        Console.Error.WriteLine($"WARNING: {run.Prepared.Warning}");
        return 1;
    }

    Console.WriteLine();
    Console.WriteLine($"PR #{run.PrId} prepared.");
    Console.WriteLine($"Run folder : {Path.GetFullPath(run.RunFolder)}");
    Console.WriteLine($"Scenarios  : {run.Prepared.PromptChunks.Sum(c => c.ScenarioCount)}");
    Console.WriteLine();
    Console.WriteLine("Next: paste prompt.txt into Copilot Chat, save its JSON reply as");
    Console.WriteLine($"  {Path.Combine(run.RunFolder, "response.txt")}");
    Console.WriteLine("then run: pr-impact report " + "pr-impact-config.json");
    return 0;
}

static async Task<int> RunReportAsync(PrImpactConfig config)
{
    var reportsBaseDir = config.ReportsBaseDir ?? "Reports";
    var pointer = PrImpactAnalyzerFacade.ReadCurrentRunPointer(reportsBaseDir);

    if (pointer is null)
    { Console.Error.WriteLine($"ERROR: no run found under '{Path.GetFullPath(reportsBaseDir)}'. Run 'prepare' first."); return 1; }

    var responsePath = Path.Combine(pointer.RunFolder, "response.txt");
    if (!File.Exists(responsePath))
    {
        Console.Error.WriteLine($"ERROR: response file not found at '{Path.GetFullPath(responsePath)}'.");
        Console.Error.WriteLine("Paste Copilot's JSON reply there (all chunks in one file, if more than one) and try again.");
        return 1;
    }

    var reportPath = Path.Combine(pointer.RunFolder, $"pr-{pointer.PrId}-impact-report.html");

    await using var analyzer = PrImpactAnalyzerFacade.Create(
        services => PRImpactAnalyzer.Infrastructure.PrImpactAnalyzerRegistration.AddPrImpactAnalyzer(services),
        logging  => logging.AddConsole().SetMinimumLevel(LogLevel.Warning));

    var result = await analyzer.FinalizeFromFilesAsync(pointer.StatePath, new[] { responsePath }, reportPath);

    if (!result.Success) { Console.Error.WriteLine($"ERROR: {result.ErrorMessage}"); return 1; }

    Console.WriteLine();
    Console.WriteLine($"PR #{pointer.PrId}: {result.PrMetadata?.Title}");
    Console.WriteLine($"Impacted: {result.ImpactedScenarios.Count}");
    Console.WriteLine($"Report  : {Path.GetFullPath(reportPath)}");

    return result.ImpactedScenarios.Count > 0 && config.FailOnImpact ? 2 : 0;
}

static void PrintUsage() => Console.WriteLine(@"
Usage:
  pr-impact prepare [config.json] [prId]
  pr-impact report  [config.json]

Each prepare run creates Reports/{prId}_{timestamp}/ with prompt.txt + state.json.
report auto-finds the latest run and writes the HTML report into that same folder —
no manual file path bookkeeping needed.
");

public class PrImpactConfig
{
    [JsonPropertyName("pr")]             public string? Pr             { get; set; }
    [JsonPropertyName("prBaseUrl")]      public string? PrBaseUrl      { get; set; }
    [JsonPropertyName("testRepoPath")]   public string? TestRepoPath   { get; set; }
    [JsonPropertyName("devRepoPath")]    public string? DevRepoPath    { get; set; }
    [JsonPropertyName("azureDevOpsPat")] public string? AzureDevOpsPat { get; set; }
    [JsonPropertyName("reportsBaseDir")] public string? ReportsBaseDir { get; set; }
    [JsonPropertyName("failOnImpact")]   public bool    FailOnImpact   { get; set; }
}
