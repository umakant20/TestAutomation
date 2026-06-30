using Microsoft.Extensions.Logging;
using PRImpactAnalyzer.Core.Interfaces;
using PRImpactAnalyzer.Core.Models;

namespace PRImpactAnalyzer.Core.Pipeline;

/// <summary>
/// Orchestrates the full PR test-impact analysis in a single automated call, designed to be
/// invoked as a library from a test framework, CI step, or CLI — no UI, no manual paste.
///
/// The pipeline runs three phases end to end:
///   Phase 1 — fetch the PR diff and extract changed symbols (via the registered ICodeAnalyzers)
///   Phase 2 — scan the local test repo for scenarios (via the registered ITestParser)
///   Phase 3 — pre-filter scenarios, chunk them, send each chunk to the LLM (ILlmOrchestrator),
///             parse each response, then dedupe and rank into a final report
///
/// The ILlmOrchestrator dependency is what makes this fully automated: it is now backed by the
/// GitHub Copilot SDK (CopilotSdkOrchestrator), so the prompt-send/response-parse loop that used
/// to be a manual copy/paste is a real programmatic call.
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

    // Scenarios per LLM call. Larger than the old manual-paste limit since there's no human
    // pasting now — bounded mainly by model context and per-request token cost.
    private const int ChunkSize = 80;

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

    /// <summary>
    /// Runs the complete analysis and returns the ranked, de-duplicated impacted-scenario report.
    /// This is the single entry point for library/CI/CLI callers.
    /// </summary>
    public async Task<AnalysisResult> RunAsync(AnalysisRequest request, CancellationToken cancellationToken = default)
    {
        var result = new AnalysisResult();

        try
        {
            // ── Phase 1: Fetch PR diff and extract symbols ──────────────────────────
            _logger.LogInformation("Phase 1: Fetching PR diff from Azure DevOps…");
            var prDiff = await _prDiffProvider.GetDiffAsync(request, cancellationToken);
            result.PrMetadata = prDiff.Metadata;
            result.RawDiffText = prDiff.RawDiffText;

            var symbols = new List<ChangedSymbol>();
            foreach (var fileDiff in prDiff.Files)
            {
                // Run every analyzer that claims this file, not just the first match —
                // a .cs SOAP service file needs both Roslyn method and SOAP operation extraction.
                var matchingAnalyzers = _codeAnalyzers.Where(a => a.CanAnalyze(fileDiff.FilePath)).ToList();
                if (matchingAnalyzers.Count == 0)
                {
                    _logger.LogDebug("No analyzer for {File}", fileDiff.FilePath);
                    continue;
                }

                foreach (var analyzer in matchingAnalyzers)
                {
                    var extracted = analyzer.ExtractSymbols(fileDiff).ToList();
                    _logger.LogInformation("{Analyzer} extracted {Count} symbols from {File}",
                        analyzer.Name, extracted.Count, fileDiff.FilePath);
                    symbols.AddRange(extracted);
                }
            }

            result.ChangedSymbols = symbols;
            _logger.LogInformation("Phase 1 complete. {Count} changed symbols.", symbols.Count);

            // ── Phase 2: Scan test repo ──────────────────────────────────────────────
            _logger.LogInformation("Phase 2: Scanning test repo at {Path}…", request.TestRepoLocalPath);

            var parser = _testParsers.FirstOrDefault(p => p.CanParse(request.TestRepoLocalPath))
                ?? throw new InvalidOperationException(
                    $"No test parser can handle the repo at '{request.TestRepoLocalPath}'. " +
                    "Ensure the SpecFlow/Reqnroll parser plugin is registered.");

            var scenarios = parser.ParseScenarios(request.TestRepoLocalPath).ToList();
            result.AllScenarios = scenarios;
            _logger.LogInformation("Phase 2 complete. {Count} scenarios found.", scenarios.Count);

            if (scenarios.Count == 0)
            {
                result.Success = true;
                result.ErrorMessage = "No test scenarios found in the test repo path.";
                return result;
            }

            // ── Phase 3: Pre-filter, chunk, call LLM per chunk, parse, merge ─────────
            var relevant = _promptBuilder.PreFilter(symbols, scenarios);
            _logger.LogInformation("Pre-filter kept {Relevant} of {Total} scenarios.", relevant.Count, scenarios.Count);

            if (relevant.Count == 0)
            {
                result.Success = true;
                result.ImpactedScenarios = new();
                result.ErrorMessage = "No scenarios shared any keyword with the changed symbols (no matching test coverage, or symbol extraction found nothing useful).";
                return result;
            }

            var chunks = ChunkScenarios(relevant, ChunkSize);
            var impacted = new List<ImpactedScenario>();

            for (int i = 0; i < chunks.Count; i++)
            {
                var prompt = _promptBuilder.Build(prDiff.Metadata, symbols, chunks[i]);
                result.RawLlmPrompt = prompt; // last chunk's prompt, kept for debugging

                _logger.LogInformation("Phase 3: Sending chunk {Index}/{Total} to Copilot…", i + 1, chunks.Count);
                var rawResponse = await _llm.GetCompletionAsync(prompt, cancellationToken);
                result.RawLlmResponse = rawResponse;

                var parsedForChunk = _responseParser.Parse(rawResponse);
                impacted.AddRange(parsedForChunk);

                // Capture the full exchange so the HTML report can show exactly what was
                // sent and received for every chunk, not just the last one.
                result.LlmExchanges.Add(new LlmExchange
                {
                    ChunkIndex = i,
                    TotalChunks = chunks.Count,
                    ScenarioCount = chunks[i].Count,
                    Prompt = prompt,
                    RawResponse = rawResponse,
                    ParsedImpactedCount = parsedForChunk.Count(p => !p.ScenarioName.StartsWith("["))
                });
            }

            // Dedupe by (feature file + scenario name) so same-named scenarios in different
            // features aren't collapsed; keep highest confidence; rank.
            result.ImpactedScenarios = impacted
                .GroupBy(s => (s.FeatureFile, s.ScenarioName))
                .Select(g => g.OrderByDescending(s => s.Confidence).First())
                .OrderByDescending(s => s.Confidence)
                .ThenBy(s => s.FeatureFile)
                .ToList();

            result.Success = true;
            _logger.LogInformation("Analysis complete. {Count} impacted scenarios.", result.ImpactedScenarios.Count);
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
