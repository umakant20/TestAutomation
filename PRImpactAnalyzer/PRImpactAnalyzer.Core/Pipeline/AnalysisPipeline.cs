using Microsoft.Extensions.Logging;
using PRImpactAnalyzer.Core.Interfaces;
using PRImpactAnalyzer.Core.Models;

namespace PRImpactAnalyzer.Core.Pipeline;

/// <summary>
/// Orchestrates PR test-impact analysis as a manual two-step flow:
///
///   Step A — PrepareAsync: fetch the PR diff, extract changed symbols, scan the test repo,
///            pre-filter scenarios, and build the prompt(s). NO LLM call happens here — this
///            is pure local computation against Azure DevOps and your local test repo.
///
///   Step B — Finalize: takes the raw text you pasted back from Copilot Chat (one string per
///            prompt chunk, in order) and parses, dedupes, and ranks the impacted scenarios.
///
/// WHY MANUAL, NOT AN API CALL: there is no programmatic Copilot access available for an
/// individual/non-enterprise subscription without an explicit GitHub token grant that isn't
/// obtainable for this use case. So the LLM step is YOU pasting the prompt into Copilot Chat
/// and pasting the JSON reply back — this pipeline does everything else automatically around
/// that one manual step.
/// </summary>
public class AnalysisPipeline
{
    private readonly IPrDiffProvider _prDiffProvider;
    private readonly IEnumerable<ICodeAnalyzer> _codeAnalyzers;
    private readonly IEnumerable<ITestParser> _testParsers;
    private readonly PromptBuilder _promptBuilder;
    private readonly LlmResponseParser _responseParser;
    private readonly ILogger<AnalysisPipeline> _logger;

    private const int ChunkSize = 80; // scenarios per prompt chunk — keeps each paste pasteable

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
    /// Step A — runs everything that doesn't need an LLM: fetch diff, extract symbols, scan
    /// test repo, pre-filter, chunk, and build the prompt text for each chunk. Returns
    /// everything needed both to write the prompt file and to later finalize the report once
    /// you've pasted Copilot's response(s) back.
    /// </summary>
    public async Task<PreparedAnalysis> PrepareAsync(AnalysisRequest request, CancellationToken cancellationToken = default)
    {
        var prepared = new PreparedAnalysis();

        _logger.LogInformation("Fetching PR diff from Azure DevOps…");
        var prDiff = await _prDiffProvider.GetDiffAsync(request, cancellationToken);
        prepared.PrMetadata = prDiff.Metadata;
        prepared.RawDiffText = prDiff.RawDiffText;

        var symbols = new List<ChangedSymbol>();
        foreach (var fileDiff in prDiff.Files)
        {
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
        prepared.ChangedSymbols = symbols;
        _logger.LogInformation("{Count} changed symbols extracted.", symbols.Count);

        _logger.LogInformation("Scanning test repo at {Path}…", request.TestRepoLocalPath);
        var parser = _testParsers.FirstOrDefault(p => p.CanParse(request.TestRepoLocalPath))
            ?? throw new InvalidOperationException(
                $"No test parser can handle the repo at '{request.TestRepoLocalPath}'.");

        var scenarios = parser.ParseScenarios(request.TestRepoLocalPath).ToList();
        prepared.AllScenarioCount = scenarios.Count;
        _logger.LogInformation("{Count} scenarios found.", scenarios.Count);

        if (scenarios.Count == 0)
        {
            prepared.Warning = "No test scenarios found in the test repo path. Check the path and ensure .feature files exist.";
            return prepared;
        }

        var relevant = _promptBuilder.PreFilter(symbols, scenarios);
        _logger.LogInformation("Pre-filter kept {Relevant} of {Total} scenarios.", relevant.Count, scenarios.Count);

        if (relevant.Count == 0)
        {
            prepared.Warning = "No scenarios shared any keyword with the changed symbols — check the Changed Symbols list; extraction may have found nothing test-relevant.";
            return prepared;
        }

        var chunks = ChunkScenarios(relevant, ChunkSize);
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

        _logger.LogInformation("Prepared {Count} prompt chunk(s).", chunks.Count);
        return prepared;
    }

    /// <summary>
    /// Step B — call once with the raw text you pasted back from Copilot Chat for EVERY chunk,
    /// in the same order PreparedAnalysis.PromptChunks were generated. Parses each response,
    /// dedupes by (feature file + scenario name), and ranks by confidence.
    /// </summary>
    public AnalysisResult Finalize(PreparedAnalysis prepared, List<string> rawResponsesInOrder)
    {
        var result = new AnalysisResult
        {
            Success = true,
            PrMetadata = prepared.PrMetadata,
            ChangedSymbols = prepared.ChangedSymbols,
            AllScenarioCount = prepared.AllScenarioCount,
            RawDiffText = prepared.RawDiffText,
        };

        if (rawResponsesInOrder.Count != prepared.PromptChunks.Count)
        {
            result.Success = false;
            result.ErrorMessage =
                $"Expected {prepared.PromptChunks.Count} response(s) (one per prompt chunk) but got {rawResponsesInOrder.Count}. " +
                "Make sure you pasted Copilot's reply for every chunk, in the same order the prompts were generated.";
            return result;
        }

        var allImpacted = new List<ImpactedScenario>();
        for (int i = 0; i < prepared.PromptChunks.Count; i++)
        {
            var chunk = prepared.PromptChunks[i];
            var parsed = _responseParser.Parse(rawResponsesInOrder[i]);
            allImpacted.AddRange(parsed);

            result.LlmExchanges.Add(new LlmExchange
            {
                ChunkIndex = chunk.ChunkIndex,
                TotalChunks = chunk.TotalChunks,
                ScenarioCount = chunk.ScenarioCount,
                Prompt = chunk.PromptText,
                RawResponse = rawResponsesInOrder[i],
                ParsedImpactedCount = parsed.Count(p => !p.ScenarioName.StartsWith("["))
            });
        }

        result.ImpactedScenarios = allImpacted
            .GroupBy(s => (s.FeatureFile, s.ScenarioName))
            .Select(g => g.OrderByDescending(s => s.Confidence).First())
            .OrderByDescending(s => s.Confidence)
            .ThenBy(s => s.FeatureFile)
            .ToList();

        _logger.LogInformation("Finalized — {Count} impacted scenarios.", result.ImpactedScenarios.Count);
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

/// <summary>
/// Result of Step A — everything needed to write the prompt file now, and to finalize the
/// report later once you've pasted Copilot's response(s) back in. Serializable to JSON so it
/// can be saved to disk between the `prepare` and `report` CLI commands (which may run as
/// two separate process invocations).
/// </summary>
public class PreparedAnalysis
{
    public PrMetadata? PrMetadata { get; set; }
    public List<ChangedSymbol> ChangedSymbols { get; set; } = new();
    public int AllScenarioCount { get; set; }
    public string RawDiffText { get; set; } = string.Empty;
    public List<PromptChunk> PromptChunks { get; set; } = new();
    public string? Warning { get; set; }
}

/// <summary>One prompt ready to paste into Copilot Chat.</summary>
public class PromptChunk
{
    public int ChunkIndex { get; set; }
    public int TotalChunks { get; set; }
    public string PromptText { get; set; } = string.Empty;
    public int ScenarioCount { get; set; }
}
