using System.Diagnostics;
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
//   pr-impact execute [config.json]
//
// Every "prepare" run creates its own dated folder under Reports/{prId}_{timestamp}/
// containing prompt.txt and state.json. "report" auto-finds the latest run, writes the HTML
// report AND a small impacted-tests.json manifest into that same folder. "execute" reads
// that manifest, filters by confidence per config's testExecutionScope, and runs exactly
// those scenarios via `dotnet test --filter` against your Selenium/Reqnroll/NUnit test project.

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
    "execute" => await RunExecuteAsync(config),
    _         => Unknown(command)
};

static int Unknown(string cmd) { Console.Error.WriteLine($"Unknown command '{cmd}'. Expected 'prepare', 'report', or 'execute'."); PrintUsage(); return 1; }

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
    Console.WriteLine("then run: pr-impact report pr-impact-config.json");
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

    // Write the impacted-tests manifest — a small, stable handoff file so "execute" doesn't
    // need to re-parse response.txt or re-run Finalize(). One line per impacted scenario.
    var manifest = new ImpactedTestsManifest
    {
        PrId = pointer.PrId,
        Scenarios = result.ImpactedScenarios.Select(s => new ImpactedTestEntry
        {
            ScenarioName = s.ScenarioName,
            FeatureFile  = s.FeatureFile,
            Confidence   = s.Confidence.ToString(),
        }).ToList()
    };
    var manifestPath = Path.Combine(pointer.RunFolder, "impacted-tests.json");
    await File.WriteAllTextAsync(manifestPath,
        JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));

    Console.WriteLine();
    Console.WriteLine($"PR #{pointer.PrId}: {result.PrMetadata?.Title}");
    Console.WriteLine($"Impacted: {result.ImpactedScenarios.Count}");
    Console.WriteLine($"Report  : {Path.GetFullPath(reportPath)}");
    Console.WriteLine($"Manifest: {Path.GetFullPath(manifestPath)}  (used by 'pr-impact execute')");

    return result.ImpactedScenarios.Count > 0 && config.FailOnImpact ? 2 : 0;
}

static async Task<int> RunExecuteAsync(PrImpactConfig config)
{
    var reportsBaseDir = config.ReportsBaseDir ?? "Reports";
    var pointer = PrImpactAnalyzerFacade.ReadCurrentRunPointer(reportsBaseDir);

    if (pointer is null)
    { Console.Error.WriteLine($"ERROR: no run found under '{Path.GetFullPath(reportsBaseDir)}'. Run 'prepare' then 'report' first."); return 1; }

    var manifestPath = Path.Combine(pointer.RunFolder, "impacted-tests.json");
    if (!File.Exists(manifestPath))
    { Console.Error.WriteLine($"ERROR: manifest not found at '{Path.GetFullPath(manifestPath)}'. Run 'report' first."); return 1; }

    if (string.IsNullOrWhiteSpace(config.TestProjectPath))
    { Console.Error.WriteLine("ERROR: config is missing 'testProjectPath' — path to your Selenium/Reqnroll/NUnit test project (.csproj) or its built .dll."); return 1; }

    var manifest = JsonSerializer.Deserialize<ImpactedTestsManifest>(
        await File.ReadAllTextAsync(manifestPath),
        new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
        ?? throw new InvalidOperationException("Could not parse impacted-tests.json.");

    // ── Confidence scope filter ─────────────────────────────────────────────
    // "HighOnly"       -> only HIGH confidence scenarios run
    // "HighAndMedium"  -> HIGH + MEDIUM
    // "All"            -> HIGH + MEDIUM + VERIFY (everything in the report)
    var scope = (config.TestExecutionScope ?? "HighOnly").Trim();
    var allowedConfidences = scope switch
    {
        "All"           => new HashSet<string> { "High", "Medium", "Verify" },
        "HighAndMedium" => new HashSet<string> { "High", "Medium" },
        "HighOnly"      => new HashSet<string> { "High" },
        _ => throw new InvalidOperationException(
            $"Unknown testExecutionScope '{scope}'. Valid values: HighOnly, HighAndMedium, All.")
    };

    var toRun = manifest.Scenarios.Where(s => allowedConfidences.Contains(s.Confidence)).ToList();

    Console.WriteLine();
    Console.WriteLine($"Test execution scope: {scope}  ({string.Join("+", allowedConfidences)})");
    Console.WriteLine($"Scenarios selected   : {toRun.Count} of {manifest.Scenarios.Count} impacted");

    if (toRun.Count == 0)
    {
        Console.WriteLine("Nothing to run at this scope.");
        return 0;
    }

    // ── Build the dotnet test filter ────────────────────────────────────────
    // Reqnroll/SpecFlow generates one NUnit test method per scenario, named from the
    // scenario title with non-alphanumeric characters replaced. We sanitize the same way
    // and use the "~" (contains) operator rather than exact match, since generated names
    // can carry small suffixes (e.g. Scenario Outline example indices) that exact matching
    // would miss.
    var filterTerms = toRun
        .Select(s => SanitizeForTestNameFilter(s.ScenarioName))
        .Where(s => !string.IsNullOrWhiteSpace(s))
        .Distinct()
        .Select(s => $"FullyQualifiedName~{s}");

    var filterExpression = string.Join("|", filterTerms);

    Console.WriteLine();
    Console.WriteLine("Running:");
    Console.WriteLine($"  dotnet test \"{config.TestProjectPath}\" --filter \"{filterExpression}\"");
    Console.WriteLine();

    var psi = new ProcessStartInfo
    {
        FileName = "dotnet",
        Arguments = $"test \"{config.TestProjectPath}\" --filter \"{filterExpression}\"",
        UseShellExecute = false,
        RedirectStandardOutput = false,
        RedirectStandardError = false,
    };

    using var process = Process.Start(psi)
        ?? throw new InvalidOperationException("Failed to start dotnet test process.");
    await process.WaitForExitAsync();

    Console.WriteLine();
    Console.WriteLine($"dotnet test exited with code {process.ExitCode}.");
    return process.ExitCode;
}

/// <summary>
/// Approximates Reqnroll/SpecFlow's generated NUnit test method naming: non-alphanumeric
/// characters become underscores, consecutive underscores collapse to one. Not a guaranteed
/// exact match (SpecFlow's own algorithm has minor edge cases), which is exactly why the
/// filter uses "~" (contains) rather than "=" (exact) — a close approximation is enough to
/// find the right generated test name inside FullyQualifiedName.
/// </summary>
static string SanitizeForTestNameFilter(string scenarioName)
{
    var chars = scenarioName.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray();
    var collapsed = new string(chars);
    while (collapsed.Contains("__")) collapsed = collapsed.Replace("__", "_");
    return collapsed.Trim('_');
}

static void PrintUsage() => Console.WriteLine(@"
Usage:
  pr-impact prepare [config.json] [prId]
  pr-impact report  [config.json]
  pr-impact execute [config.json]

Each prepare run creates Reports/{prId}_{timestamp}/ with prompt.txt + state.json.
report auto-finds the latest run, writes the HTML report AND impacted-tests.json.
execute reads that manifest and runs exactly the impacted scenarios via
`dotnet test --filter`, scoped by config's testExecutionScope:
  HighOnly       - only HIGH confidence scenarios (default, safest)
  HighAndMedium  - HIGH + MEDIUM
  All            - everything in the report, including VERIFY
");

public class PrImpactConfig
{
    [JsonPropertyName("pr")]                  public string? Pr                  { get; set; }
    [JsonPropertyName("prBaseUrl")]           public string? PrBaseUrl           { get; set; }
    [JsonPropertyName("testRepoPath")]        public string? TestRepoPath        { get; set; }
    [JsonPropertyName("devRepoPath")]         public string? DevRepoPath         { get; set; }
    [JsonPropertyName("azureDevOpsPat")]      public string? AzureDevOpsPat      { get; set; }
    [JsonPropertyName("reportsBaseDir")]      public string? ReportsBaseDir      { get; set; }
    [JsonPropertyName("failOnImpact")]        public bool    FailOnImpact        { get; set; }

    /// <summary>Path to the Selenium/Reqnroll/NUnit test project (.csproj) or its built .dll —
    /// passed straight to `dotnet test`.</summary>
    [JsonPropertyName("testProjectPath")]     public string? TestProjectPath     { get; set; }

    /// <summary>Which confidence tiers to include when running 'execute':
    /// "HighOnly" (default), "HighAndMedium", or "All".</summary>
    [JsonPropertyName("testExecutionScope")]  public string? TestExecutionScope  { get; set; }
}

public class ImpactedTestsManifest
{
    public int PrId { get; set; }
    public List<ImpactedTestEntry> Scenarios { get; set; } = new();
}

public class ImpactedTestEntry
{
    public string ScenarioName { get; set; } = string.Empty;
    public string FeatureFile  { get; set; } = string.Empty;
    public string Confidence   { get; set; } = string.Empty;
}
