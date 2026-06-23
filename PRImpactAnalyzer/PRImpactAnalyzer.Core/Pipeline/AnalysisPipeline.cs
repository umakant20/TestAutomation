using Microsoft.Extensions.Logging;
using PRImpactAnalyzer.Core.Interfaces;
using PRImpactAnalyzer.Core.Models;

namespace PRImpactAnalyzer.Core.Pipeline;

/// <summary>
/// Orchestrates the analysis pipeline, split into two explicit steps so the UI can
/// pause between them for the manual Copilot Chat paste step:
///
///   Step A — PrepareAsync: Phase 1 (fetch PR diff, extract symbols) + Phase 2
///            (scan test repo, build scenario index) + builds the prompt(s).
///            Returns a PreparedAnalysis with one prompt per chunk, ready to paste
///            into Visual Studio's Copilot Chat window.
///
///   Step B — CompleteAsync: takes the pasted Copilot response(s) for each chunk
///            and runs Phase 3 (parse JSON, dedupe, rank by confidence).
///
/// This split means the UI never needs to "block" on an open HTTP call — it shows
/// the prepared prompt, waits for you to paste a response and click a button, then
/// calls CompleteAsync synchronously per chunk.
/// </summary>
public class AnalysisPipeline
{
    private readonly IPrDiffProvider _prDiffProvider;
    private readonly IEnumerable<ICodeAnalyzer> _codeAnalyzers;
    private readonly IEnumerable<ITestParser> _testParsers;
    private readonly PromptBuilder _promptBuilder;
    private readonly LlmResponseParser _responseParser;
    private readonly ILogger<AnalysisPipeline> _logger;

    private const int ChunkSize = 80; // scenarios per prompt — keeps each paste pasteable in Copilot Chat

    public AnalysisPipeline(
        IPrDiffProvider prDiffProvider,
        IEnumerable<ICodeAnalyzer> codeAnalyzers,
        IEnumerable<ITestParser> testParsers,
        PromptBuilder promptBuilder,
        LlmResponseParser responseParser,
        ILogger<AnalysisPipeline> logger)
    {
        _prDiffProvider = prDiffProvider;
        _codeAnalyzers = codeAnalyzers;
        _testParsers = testParsers;
        _promptBuilder = promptBuilder;
        _responseParser = responseParser;
        _logger = logger;
    }

    /// <summary>
    /// Step A — runs Phase 1 + Phase 2 and builds one prompt per scenario chunk.
    /// No LLM call happens here. The caller pastes each prompt into Copilot Chat manually.
    /// </summary>
    public async Task<PreparedAnalysis> PrepareAsync(AnalysisRequest request, CancellationToken cancellationToken = default)
    {
        var prepared = new PreparedAnalysis();

        // ── Phase 1: Fetch PR diff and extract symbols ──────────────────────────
        _logger.LogInformation("Phase 1: Fetching PR diff from Azure DevOps…");
        var prDiff = await _prDiffProvider.GetDiffAsync(request, cancellationToken);
        prepared.PrMetadata = prDiff.Metadata;

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

        prepared.ChangedSymbols = symbols;
        _logger.LogInformation("Phase 1 complete. {Count} total changed symbols extracted.", symbols.Count);

        // ── Phase 2: Scan test repo ──────────────────────────────────────────────
        _logger.LogInformation("Phase 2: Scanning test repo at {Path}…", request.TestRepoLocalPath);

        var parser = _testParsers.FirstOrDefault(p => p.CanParse(request.TestRepoLocalPath))
            ?? throw new InvalidOperationException(
                $"No test parser found that can handle the repo at '{request.TestRepoLocalPath}'. " +
                "Ensure the SpecFlow/Reqnroll parser plugin is registered.");

        var scenarios = parser.ParseScenarios(request.TestRepoLocalPath).ToList();
        prepared.AllScenarios = scenarios;
        _logger.LogInformation("Phase 2 complete. {Count} scenarios found.", scenarios.Count);

        if (scenarios.Count == 0)
        {
            prepared.Warning = "No test scenarios found in the test repo path. Check the path and ensure .feature files exist.";
            return prepared;
        }

        // ── Pre-filter ONCE across the whole suite, then chunk only the survivors ──
        // (Filtering must happen before chunking — filtering inside each chunk
        // independently does nothing, since every scenario still ends up in some chunk.)
        var relevantScenarios = _promptBuilder.PreFilter(symbols, scenarios);
        _logger.LogInformation("Pre-filter kept {Relevant} of {Total} scenarios as relevant to the changed symbols.",
            relevantScenarios.Count, scenarios.Count);

        if (relevantScenarios.Count == 0)
        {
            prepared.Warning = "No scenarios shared any keyword with the changed symbols. " +
                "This usually means the PR touches code with no matching test coverage, or symbol extraction found nothing useful — check the 'Changed Symbols' debug list.";
            return prepared;
        }

        var chunks = ChunkScenarios(relevantScenarios, ChunkSize);
        for (int i = 0; i < chunks.Count; i++)
        {
            var prompt = _promptBuilder.Build(prDiff.Metadata, symbols, chunks[i]);
            prepared.PromptChunks.Add(new PromptChunk
            {
                ChunkIndex = i,
                TotalChunks = chunks.Count,
                PromptText = prompt,
                ScenarioCount = chunks[i].Count
            });
        }

        _logger.LogInformation("Prepared {Count} prompt chunk(s) for manual Copilot Chat paste.", chunks.Count);
        return prepared;
    }

    /// <summary>
    /// Step B — call once per chunk after pasting that chunk's prompt into Copilot Chat
    /// and copying back the response. Once all chunks are submitted, call FinalizeResult.
    /// </summary>
    public List<ImpactedScenario> ParseChunkResponse(string rawCopilotResponse)
    {
        return _responseParser.Parse(rawCopilotResponse);
    }

    /// <summary>
    /// Combines parsed results from all chunks into the final ranked, deduplicated list.
    /// Call after every chunk has been submitted via ParseChunkResponse.
    /// </summary>
    public AnalysisResult FinalizeResult(PreparedAnalysis prepared, List<List<ImpactedScenario>> chunkResults)
    {
        var impacted = chunkResults.SelectMany(r => r).ToList();

        var deduped = impacted
            .GroupBy(s => s.ScenarioName)
            .Select(g => g.OrderByDescending(s => s.Confidence).First())
            .OrderByDescending(s => s.Confidence)
            .ThenBy(s => s.FeatureFile)
            .ToList();

        _logger.LogInformation("Finalized. {Count} impacted scenarios identified.", deduped.Count);

        return new AnalysisResult
        {
            Success = true,
            PrMetadata = prepared.PrMetadata,
            ChangedSymbols = prepared.ChangedSymbols,
            AllScenarios = prepared.AllScenarios,
            ImpactedScenarios = deduped
        };
    }

    private static List<List<ScenarioRecord>> ChunkScenarios(List<ScenarioRecord> scenarios, int chunkSize)
    {
        var chunks = new List<List<ScenarioRecord>>();
        for (int i = 0; i < scenarios.Count; i += chunkSize)
            chunks.Add(scenarios.Skip(i).Take(chunkSize).ToList());
        return chunks;
    }
}

/// <summary>Result of Step A — everything needed to drive the manual paste UI.</summary>
public class PreparedAnalysis
{
    public PrMetadata? PrMetadata { get; set; }
    public List<ChangedSymbol> ChangedSymbols { get; set; } = new();
    public List<ScenarioRecord> AllScenarios { get; set; } = new();
    public List<PromptChunk> PromptChunks { get; set; } = new();
    public string? Warning { get; set; }
}

/// <summary>One prompt ready to paste into Copilot Chat, plus a slot for the pasted-back response.</summary>
public class PromptChunk
{
    public int ChunkIndex { get; set; }
    public int TotalChunks { get; set; }
    public string PromptText { get; set; } = string.Empty;
    public int ScenarioCount { get; set; }
    public string? PastedResponse { get; set; }
}
