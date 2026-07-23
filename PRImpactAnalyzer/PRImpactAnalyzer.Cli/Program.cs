using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using PRImpactAnalyzer.Core;
using PRImpactAnalyzer.Core.Models;
using PRImpactAnalyzer.Infrastructure;

// PR Test Impact Analyzer CLI
// Usage:
//   pr-impact prepare         [config.json] [prId]   (prId optional — see below)
//   pr-impact report          [config.json]
//   pr-impact execute         [config.json]           (runs tests locally via dotnet test)
//   pr-impact trigger-pipeline [config.json]           (triggers an ADO pipeline run instead)
//
// Every "prepare" run creates its own dated folder under Reports/{prId}_{timestamp}/
// containing prompt.txt and state.json. "report" auto-finds the latest run, writes the HTML
// report AND a small impacted-tests.json manifest into that same folder. "execute" reads
// that manifest, filters by confidence per config's testExecutionScope, and runs exactly
// those scenarios via `dotnet test --filter` against your Selenium/Reqnroll/NUnit test project.
// "trigger-pipeline" does the same filtering but instead calls the Azure DevOps Pipelines
// REST API to kick off a pipeline run remotely, passing the filter as a pipeline parameter.

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
    "prepare"          => await RunPrepareAsync(config, explicitPrId),
    "report"           => await RunReportAsync(config),
    "execute"          => await RunExecuteAsync(config),
    "trigger-pipeline" => await RunTriggerPipelineAsync(config),
    _                  => Unknown(command)
};

static int Unknown(string cmd) { Console.Error.WriteLine($"Unknown command '{cmd}'. Expected 'prepare', 'report', 'execute', or 'trigger-pipeline'."); PrintUsage(); return 1; }

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
            EmbeddingModelPath = config.EmbeddingModelPath,
            EmbeddingVocabPath = config.EmbeddingVocabPath,
            EmbeddingCacheDir  = reportsBaseDir,
            PySemanticEnabled     = config.PySemanticEnabled,
            PythonExecutablePath  = config.PythonExecutablePath,
            PySemanticScriptPath  = config.PySemanticScriptPath,
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

    var result = await analyzer.FinalizeOnlyAsync(pointer.StatePath, new[] { responsePath });

    if (!result.Success) { Console.Error.WriteLine($"ERROR: {result.ErrorMessage}"); return 1; }

    // ── Historical frequency: compute BEFORE logging this run, so "flagged in N of last M"
    // reflects PAST runs only, not double-counting the run currently being reported. ────────
    var history = PRImpactAnalyzer.Core.Pipeline.HistoryLog.ReadAll(reportsBaseDir);
    result.HistoricalFrequency = PRImpactAnalyzer.Core.Pipeline.HistoryLog.ComputeFrequency(history, result.ImpactedScenarios);

    PrImpactAnalyzerFacade.WriteReport(result, reportPath);

    // Now log this run for future frequency computations.
    await PRImpactAnalyzer.Core.Pipeline.HistoryLog.AppendAsync(reportsBaseDir, new PRImpactAnalyzer.Core.Models.HistoryEntry
    {
        PrId = pointer.PrId,
        Scenarios = result.ImpactedScenarios.Select(s => new PRImpactAnalyzer.Core.Models.HistoryScenario
        {
            ScenarioName = s.ScenarioName,
            FeatureFile  = s.FeatureFile,
            Confidence   = s.Confidence.ToString(),
        }).ToList()
    });

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
    var (manifest, toRun, error) = await LoadScopedManifestAsync(config);
    if (error is not null) { Console.Error.WriteLine(error); return 1; }

    Console.WriteLine();
    Console.WriteLine($"Test execution scope: {config.TestExecutionScope ?? "HighOnly"}");
    Console.WriteLine($"Scenarios selected   : {toRun!.Count} of {manifest!.Scenarios.Count} impacted");

    if (toRun.Count == 0) { Console.WriteLine("Nothing to run at this scope."); return 0; }

    if (string.IsNullOrWhiteSpace(config.TestProjectPath))
    { Console.Error.WriteLine("ERROR: config is missing 'testProjectPath' — path to your Selenium/Reqnroll/NUnit test project (.csproj) or its built .dll."); return 1; }

    var filterExpression = BuildTestFilterExpression(toRun);

    Console.WriteLine();
    Console.WriteLine("Running (assumes the test project is already built — e.g. via Visual Studio):");
    Console.WriteLine($"  dotnet test \"{config.TestProjectPath}\" --filter \"{filterExpression}\" --no-build");
    Console.WriteLine();

    var psi = new ProcessStartInfo
    {
        FileName = "dotnet",
        // --no-build is essential: it skips MSBuild's Build target chain entirely, which is
        // what invokes tasks like ResolveComReference. That task only exists in classic
        // MSBuild.exe (the one bundled with Visual Studio) — the modern .NET SDK's MSBuild
        // used by `dotnet build`/`dotnet test` doesn't implement it at all, and fails hard
        // if the test project (or a dependency) has any <COMReference> items. Skipping the
        // build avoids this entirely — but means the test project MUST already be built
        // (e.g. via Visual Studio, which uses classic MSBuild and handles COM references
        // fine) before running 'execute'.
        Arguments = $"test \"{config.TestProjectPath}\" --filter \"{filterExpression}\" --no-build",
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

static async Task<int> RunTriggerPipelineAsync(PrImpactConfig config)
{
    var (manifest, toRun, error) = await LoadScopedManifestAsync(config);
    if (error is not null) { Console.Error.WriteLine(error); return 1; }

    Console.WriteLine();
    Console.WriteLine($"Test execution scope: {config.TestExecutionScope ?? "HighOnly"}");
    Console.WriteLine($"Scenarios selected   : {toRun!.Count} of {manifest!.Scenarios.Count} impacted");

    if (toRun.Count == 0) { Console.WriteLine("Nothing to trigger at this scope."); return 0; }

    if (string.IsNullOrWhiteSpace(config.PipelineOrgUrl) || string.IsNullOrWhiteSpace(config.PipelineProject) || config.PipelineId is null)
    {
        Console.Error.WriteLine("ERROR: config is missing 'pipelineOrgUrl', 'pipelineProject', or 'pipelineId'.");
        return 1;
    }

    if (string.IsNullOrWhiteSpace(config.PipelineRequestBodyTemplateFile))
    {
        Console.Error.WriteLine("ERROR: config is missing 'pipelineRequestBodyTemplateFile'.");
        Console.Error.WriteLine("Create a JSON file containing your already-working pipeline trigger request body,");
        Console.Error.WriteLine("with placeholder tokens where the identified test scenarios should go. See README.");
        return 1;
    }

    var templatePath = config.PipelineRequestBodyTemplateFile!;
    if (!File.Exists(templatePath))
    { Console.Error.WriteLine($"ERROR: template file not found at '{Path.GetFullPath(templatePath)}'."); return 1; }

    var pat = config.AzureDevOpsPat ?? Environment.GetEnvironmentVariable("ADO_PAT");
    if (string.IsNullOrWhiteSpace(pat))
    { Console.Error.WriteLine("ERROR: No Azure DevOps PAT. Set 'azureDevOpsPat' in config or the ADO_PAT env var."); return 1; }

    // ── Build every substitution value the template might reference ─────────
    var filterExpression = BuildTestFilterExpression(toRun);
    var scenarioNamesCsv = string.Join(", ", toRun.Select(s => s.ScenarioName).Distinct());
    var scenarioNamesJsonArray = JsonSerializer.Serialize(toRun.Select(s => s.ScenarioName).Distinct().ToList());
    var featureFilesCsv = string.Join(", ", toRun.Select(s => s.FeatureFile).Distinct());

    // Feature FILE NAMES (not scenario names, not full paths) — deduplicated, since one
    // feature file commonly contains several impacted scenarios and most pipelines/test
    // runners execute at the feature-file level, not the individual-scenario level.
    var distinctFeatureNames = toRun
        .Select(s => string.IsNullOrWhiteSpace(s.FeatureFile) ? s.FeatureFile : Path.GetFileNameWithoutExtension(s.FeatureFile))
        .Where(n => !string.IsNullOrWhiteSpace(n))
        .Distinct()
        .ToList();
    var featureNamesCsv = string.Join(", ", distinctFeatureNames);
    var featureNamesJsonArray = JsonSerializer.Serialize(distinctFeatureNames);
    var featureNamesBulletList = distinctFeatureNames.Count == 0
        ? string.Empty
        : string.Join("\n", distinctFeatureNames.Select(n => $"- {n}")) + "\n";

    var distinctNames = toRun.Select(s => s.ScenarioName).Distinct().ToList();
    var scenarioNamesBulletList = distinctNames.Count == 0
        ? string.Empty
        : string.Join("\n", distinctNames.Select(n => $"- {n}")) + "\n";

    // ── Load the user's own request body template and substitute tokens ─────
    // The template is THEIR already-validated working request body (e.g. copied from
    // Postman or a curl command they've already tested), untouched otherwise — we only
    // replace these specific placeholder tokens wherever they appear in it. This means
    // zero assumptions about parameter names, zero YAML changes, and the exact payload
    // shape stays entirely under the caller's control.
    var templateText = await File.ReadAllTextAsync(templatePath);
    var body = templateText
        .Replace("{{TEST_FILTER}}", JsonEscape(filterExpression))
        .Replace("{{SCENARIO_NAMES_CSV}}", JsonEscape(scenarioNamesCsv))
        .Replace("{{SCENARIO_NAMES_JSON_ARRAY}}", scenarioNamesJsonArray)
        .Replace("{{SCENARIO_NAMES_BULLET_LIST}}", JsonEscape(scenarioNamesBulletList))
        .Replace("{{FEATURE_NAMES_CSV}}", JsonEscape(featureNamesCsv))
        .Replace("{{FEATURE_NAMES_JSON_ARRAY}}", featureNamesJsonArray)
        .Replace("{{FEATURE_NAMES_BULLET_LIST}}", JsonEscape(featureNamesBulletList))
        .Replace("{{FEATURE_FILES_CSV}}", JsonEscape(featureFilesCsv))
        .Replace("{{PR_ID}}", manifest.PrId.ToString())
        .Replace("{{SCENARIO_COUNT}}", toRun.Count.ToString());

    // Validate the substituted result is still well-formed JSON before sending — catches a
    // bad template (e.g. a token placed outside quotes) with a clear local error instead of
    // a confusing 400 from Azure DevOps.
    try
    {
        JsonDocument.Parse(body);
    }
    catch (JsonException ex)
    {
        Console.Error.WriteLine($"ERROR: after substituting tokens, the request body is not valid JSON: {ex.Message}");
        Console.Error.WriteLine("Check that every {{TOKEN}} in your template sits inside a JSON string value (in quotes),");
        Console.Error.WriteLine("except {{SCENARIO_NAMES_JSON_ARRAY}} which itself expands to a JSON array and should NOT be quoted.");
        Console.Error.WriteLine();
        Console.Error.WriteLine("Substituted body was:");
        Console.Error.WriteLine(body);
        return 1;
    }

    var url = $"{config.PipelineOrgUrl!.TrimEnd('/')}/{config.PipelineProject}/_apis/pipelines/{config.PipelineId}/runs?api-version=7.0";

    using var http = new HttpClient();
    var authToken = Convert.ToBase64String(System.Text.Encoding.ASCII.GetBytes($":{pat}"));
    http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", authToken);

    Console.WriteLine();
    Console.WriteLine($"Triggering pipeline {config.PipelineId} in {config.PipelineProject}...");
    Console.WriteLine($"Test filter substituted: {filterExpression}");
    Console.WriteLine();
    Console.WriteLine("Request body sent:");
    Console.WriteLine(body);

    var response = await http.PostAsync(url, new StringContent(body, System.Text.Encoding.UTF8, "application/json"));
    var responseText = await response.Content.ReadAsStringAsync();

    if (!response.IsSuccessStatusCode)
    {
        Console.Error.WriteLine();
        Console.Error.WriteLine($"ERROR: pipeline trigger failed — HTTP {(int)response.StatusCode}");
        Console.Error.WriteLine(responseText);
        return 1;
    }

    using var doc = JsonDocument.Parse(responseText);
    var runId  = doc.RootElement.TryGetProperty("id", out var idProp) ? idProp.GetInt32().ToString() : "?";
    var runUrl = doc.RootElement.TryGetProperty("_links", out var links) &&
                 links.TryGetProperty("web", out var web) &&
                 web.TryGetProperty("href", out var href) ? href.GetString() : null;

    Console.WriteLine();
    Console.WriteLine($"Pipeline run started: ID {runId}");
    if (runUrl is not null) Console.WriteLine($"  {runUrl}");
    return 0;
}

/// <summary>Escapes a plain string for safe insertion as the CONTENT of a JSON string value
/// (i.e. it does not add the surrounding quotes — the template already has those).</summary>
static string JsonEscape(string text) =>
    JsonSerializer.Serialize(text)[1..^1]; // Serialize gives "..."; strip the outer quotes

/// <summary>Shared by 'execute' and 'trigger-pipeline': loads the manifest and applies the
/// confidence-scope filter from config's testExecutionScope.</summary>
static async Task<(ImpactedTestsManifest? Manifest, List<ImpactedTestEntry>? Scoped, string? Error)> LoadScopedManifestAsync(PrImpactConfig config)
{
    var reportsBaseDir = config.ReportsBaseDir ?? "Reports";
    var pointer = PrImpactAnalyzerFacade.ReadCurrentRunPointer(reportsBaseDir);
    if (pointer is null)
        return (null, null, $"ERROR: no run found under '{Path.GetFullPath(reportsBaseDir)}'. Run 'prepare' then 'report' first.");

    var manifestPath = Path.Combine(pointer.RunFolder, "impacted-tests.json");
    if (!File.Exists(manifestPath))
        return (null, null, $"ERROR: manifest not found at '{Path.GetFullPath(manifestPath)}'. Run 'report' first.");

    var manifest = JsonSerializer.Deserialize<ImpactedTestsManifest>(
        await File.ReadAllTextAsync(manifestPath),
        new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
        ?? throw new InvalidOperationException("Could not parse impacted-tests.json.");

    var scope = (config.TestExecutionScope ?? "HighOnly").Trim();
    var allowedConfidences = scope switch
    {
        "All"           => new HashSet<string> { "High", "Medium", "Verify" },
        "HighAndMedium" => new HashSet<string> { "High", "Medium" },
        "HighOnly"      => new HashSet<string> { "High" },
        _ => throw new InvalidOperationException($"Unknown testExecutionScope '{scope}'. Valid values: HighOnly, HighAndMedium, All.")
    };

    var toRun = manifest.Scenarios.Where(s => allowedConfidences.Contains(s.Confidence)).ToList();
    return (manifest, toRun, null);
}

/// <summary>
/// Approximates Reqnroll/SpecFlow's generated NUnit test method naming: non-alphanumeric
/// characters become underscores, consecutive underscores collapse to one. Not a guaranteed
/// exact match (SpecFlow's own algorithm has minor edge cases), which is exactly why the
/// filter uses "~" (contains) rather than "=" (exact) — a close approximation is enough to
/// find the right generated test name inside FullyQualifiedName.
/// </summary>
static string BuildTestFilterExpression(List<ImpactedTestEntry> scenarios)
{
    var filterTerms = scenarios
        .Select(s => SanitizeForTestNameFilter(s.ScenarioName))
        .Where(s => !string.IsNullOrWhiteSpace(s))
        .Distinct()
        .Select(s => $"FullyQualifiedName~{s}");

    return string.Join("|", filterTerms);
}

static string SanitizeForTestNameFilter(string scenarioName)
{
    var chars = scenarioName.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray();
    var collapsed = new string(chars);
    while (collapsed.Contains("__")) collapsed = collapsed.Replace("__", "_");
    return collapsed.Trim('_');
}

static void PrintUsage() => Console.WriteLine(@"
Usage:
  pr-impact prepare          [config.json] [prId]
  pr-impact report           [config.json]
  pr-impact execute          [config.json]   (runs impacted tests locally via dotnet test)
  pr-impact trigger-pipeline [config.json]   (triggers an Azure DevOps pipeline run instead)

Each prepare run creates Reports/{prId}_{timestamp}/ with prompt.txt + state.json.
report auto-finds the latest run, writes the HTML report AND impacted-tests.json.
Both execute and trigger-pipeline filter that manifest by config's testExecutionScope:
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

    /// <summary>Which confidence tiers to include when running 'execute'/'trigger-pipeline':
    /// "HighOnly" (default), "HighAndMedium", or "All".</summary>
    [JsonPropertyName("testExecutionScope")]  public string? TestExecutionScope  { get; set; }

    /// <summary>Optional — path to an ONNX sentence-embedding model (e.g. all-MiniLM-L6-v2),
    /// for Option B neural-embedding candidate search alongside BM25. Skipped silently if
    /// either this or embeddingVocabPath is missing or the file doesn't exist.</summary>
    [JsonPropertyName("embeddingModelPath")]  public string? EmbeddingModelPath  { get; set; }

    /// <summary>Optional — path to the matching vocab.txt for embeddingModelPath.</summary>
    [JsonPropertyName("embeddingVocabPath")]  public string? EmbeddingVocabPath  { get; set; }

    /// <summary>Optional — enables the Python (scikit-learn TF-IDF+SVD) semantic ranker.
    /// No external model download — trains fresh from this run's own scenario/PR text.
    /// Requires: pip install -r python-semantic-rank/requirements.txt (scikit-learn, numpy).</summary>
    [JsonPropertyName("pySemanticEnabled")]     public bool    PySemanticEnabled     { get; set; }

    /// <summary>Path to the python executable (or a venv's python.exe) — defaults to "python"
    /// on PATH if not set.</summary>
    [JsonPropertyName("pythonExecutablePath")]  public string? PythonExecutablePath  { get; set; }

    /// <summary>Path to python-semantic-rank/semantic_rank.py.</summary>
    [JsonPropertyName("pySemanticScriptPath")]  public string? PySemanticScriptPath  { get; set; }

    // ── trigger-pipeline settings ──────────────────────────────────────────────
    /// <summary>Azure DevOps org URL, e.g. https://dev.azure.com/yourorg — for triggering
    /// a pipeline run remotely instead of running tests locally via 'execute'.</summary>
    [JsonPropertyName("pipelineOrgUrl")]      public string? PipelineOrgUrl      { get; set; }
    [JsonPropertyName("pipelineProject")]     public string? PipelineProject     { get; set; }
    /// <summary>Numeric ID of the pipeline definition to trigger.</summary>
    [JsonPropertyName("pipelineId")]          public int?    PipelineId          { get; set; }

    /// <summary>Path to a JSON file containing YOUR OWN already-working pipeline trigger
    /// request body (e.g. copied from Postman/curl), with placeholder tokens where the
    /// dynamically-identified test scenarios should be substituted in. No pipeline YAML
    /// changes needed — the exact payload shape stays entirely under your control.
    /// Supported tokens: {{TEST_FILTER}}, {{SCENARIO_NAMES_CSV}}, {{SCENARIO_NAMES_JSON_ARRAY}},
    /// {{SCENARIO_NAMES_BULLET_LIST}}, {{FEATURE_NAMES_CSV}}, {{FEATURE_NAMES_JSON_ARRAY}},
    /// {{FEATURE_NAMES_BULLET_LIST}}, {{FEATURE_FILES_CSV}}, {{PR_ID}}, {{SCENARIO_COUNT}}.
    /// See README for examples.</summary>
    [JsonPropertyName("pipelineRequestBodyTemplateFile")] public string? PipelineRequestBodyTemplateFile { get; set; }
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
