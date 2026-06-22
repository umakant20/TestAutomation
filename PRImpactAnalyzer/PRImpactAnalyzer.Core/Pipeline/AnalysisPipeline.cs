using System.Text;
using System.Text.Json;
using PRImpactAnalyzer.Core.Interfaces;
using PRImpactAnalyzer.Core.Models;

namespace PRImpactAnalyzer.Core.Pipeline;

/// <summary>
/// Orchestrates the three-phase pipeline:
///   Phase 1 — Fetch PR diff from Azure DevOps and extract changed symbols
///   Phase 2 — Scan the local test repo and build the scenario index
///   Phase 3 — Build a structured prompt, call the LLM, parse impacted scenarios
/// </summary>
public class AnalysisPipeline
{
    private readonly IPrDiffProvider _prDiffProvider;
    private readonly IEnumerable<ICodeAnalyzer> _codeAnalyzers;
    private readonly IEnumerable<ITestParser> _testParsers;
    private readonly ILlmOrchestrator _llm;
    private readonly PromptBuilder _promptBuilder;
    private readonly LlmResponseParser _responseParser;
    private readonly ILogger<AnalysisPipeline> _logger;

    public AnalysisPipeline(
        IPrDiffProvider prDiffProvider,
        IEnumerable<ICodeAnalyzer> codeAnalyzers,
        IEnumerable<ITestParser> testParsers,
        ILlmOrchestrator llm,
        PromptBuilder promptBuilder,
        LlmResponseParser responseParser,
        ILogger<AnalysisPipeline> logger)
    {
        _prDiffProvider = prDiffProvider;
        _codeAnalyzers = codeAnalyzers;
        _testParsers = testParsers;
        _llm = llm;
        _promptBuilder = promptBuilder;
        _responseParser = responseParser;
        _logger = logger;
    }

    public async Task<AnalysisResult> RunAsync(AnalysisRequest request, CancellationToken cancellationToken = default)
    {
        var result = new AnalysisResult();

        try
        {
            // ── Phase 1: Fetch PR diff and extract symbols ──────────────────────
            _logger.LogInformation("Phase 1: Fetching PR diff from Azure DevOps…");
            var prDiff = await _prDiffProvider.GetDiffAsync(request, cancellationToken);
            result.PrMetadata = prDiff.Metadata;

            var symbols = new List<ChangedSymbol>();
            foreach (var fileDiff in prDiff.Files)
            {
                var analyzer = _codeAnalyzers.FirstOrDefault(a => a.CanAnalyze(fileDiff.FilePath));
                if (analyzer is null)
                {
                    _logger.LogDebug("No analyzer for {File}, skipping symbol extraction", fileDiff.FilePath);
                    continue;
                }

                var extracted = analyzer.ExtractSymbols(fileDiff).ToList();
                _logger.LogInformation("{Analyzer} extracted {Count} symbols from {File}",
                    analyzer.Name, extracted.Count, fileDiff.FilePath);
                symbols.AddRange(extracted);
            }

            result.ChangedSymbols = symbols;
            _logger.LogInformation("Phase 1 complete. {Count} total changed symbols extracted.", symbols.Count);

            // ── Phase 2: Scan test repo ──────────────────────────────────────────
            _logger.LogInformation("Phase 2: Scanning test repo at {Path}…", request.TestRepoLocalPath);

            var parser = _testParsers.FirstOrDefault(p => p.CanParse(request.TestRepoLocalPath))
                ?? throw new InvalidOperationException(
                    $"No test parser found that can handle the repo at '{request.TestRepoLocalPath}'. " +
                    "Ensure the SpecFlow/Reqnroll parser plugin is registered.");

            var scenarios = parser.ParseScenarios(request.TestRepoLocalPath).ToList();
            result.AllScenarios = scenarios;
            _logger.LogInformation("Phase 2 complete. {Count} scenarios found.", scenarios.Count);

            if (scenarios.Count == 0)
            {
                result.Success = true;
                result.ErrorMessage = "No test scenarios found in the test repo path. Check the path and ensure .feature files exist.";
                return result;
            }

            // ── Phase 3: LLM orchestration ───────────────────────────────────────
            _logger.LogInformation("Phase 3: Building prompt and calling GitHub Copilot…");

            // For large suites, chunk to stay within context window (≈80 scenarios per call)
            var impacted = new List<ImpactedScenario>();
            var chunks = ChunkScenarios(scenarios, chunkSize: 80);

            foreach (var chunk in chunks)
            {
                var prompt = _promptBuilder.Build(prDiff.Metadata, symbols, chunk);
                result.RawLlmPrompt = prompt; // stores last chunk's prompt; sufficient for debugging

                var rawResponse = await _llm.GetCompletionAsync(prompt, cancellationToken);
                result.RawLlmResponse = rawResponse;

                var parsed = _responseParser.Parse(rawResponse);
                impacted.AddRange(parsed);
            }

            // Deduplicate by scenario name (same scenario could appear in multiple chunks)
            result.ImpactedScenarios = impacted
                .GroupBy(s => s.ScenarioName)
                .Select(g => g.OrderByDescending(s => s.Confidence).First())
                .OrderByDescending(s => s.Confidence)
                .ThenBy(s => s.FeatureFile)
                .ToList();

            result.Success = true;
            _logger.LogInformation("Phase 3 complete. {Count} impacted scenarios identified.", result.ImpactedScenarios.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Analysis pipeline failed");
            result.Success = false;
            result.ErrorMessage = ex.Message;
        }

        return result;
    }

    private static List<List<ScenarioRecord>> ChunkScenarios(List<ScenarioRecord> scenarios, int chunkSize)
    {
        var chunks = new List<List<ScenarioRecord>>();
        for (int i = 0; i < scenarios.Count; i += chunkSize)
            chunks.Add(scenarios.Skip(i).Take(chunkSize).ToList());
        return chunks;
    }
}
