using Microsoft.Extensions.Logging;
using PRImpactAnalyzer.Core.Interfaces;
using PRImpactAnalyzer.Core.Models;

namespace PRImpactAnalyzer.Core.Pipeline;

public class AnalysisPipeline
{
    private readonly IPrDiffProvider _prDiffProvider;
    private readonly IEnumerable<ICodeAnalyzer> _codeAnalyzers;
    private readonly IEnumerable<ITestParser> _testParsers;
    private readonly PromptBuilder _promptBuilder;
    private readonly LlmResponseParser _responseParser;
    private readonly ILogger<AnalysisPipeline> _logger;

    private const int ChunkSize = 80;

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
    /// Step A — purely local, no LLM call. Fetches the PR diff, extracts symbols, scans
    /// the test repo, pre-filters, chunks, and builds prompt text for each chunk.
    /// </summary>
    public async Task<PreparedAnalysis> PrepareAsync(AnalysisRequest request, CancellationToken ct = default)
    {
        var prepared = new PreparedAnalysis();

        _logger.LogInformation("Fetching PR diff from Azure DevOps…");
        var prDiff = await _prDiffProvider.GetDiffAsync(request, ct);
        prepared.PrMetadata = prDiff.Metadata;
        prepared.RawDiffText = prDiff.RawDiffText;

        var symbols = new List<ChangedSymbol>();
        foreach (var fileDiff in prDiff.Files)
        {
            var analyzers = _codeAnalyzers.Where(a => a.CanAnalyze(fileDiff.FilePath)).ToList();
            if (analyzers.Count == 0) { _logger.LogDebug("No analyzer for {File}", fileDiff.FilePath); continue; }
            foreach (var analyzer in analyzers)
            {
                var extracted = analyzer.ExtractSymbols(fileDiff).ToList();
                _logger.LogInformation("{Analyzer} extracted {Count} symbols from {File}", analyzer.Name, extracted.Count, fileDiff.FilePath);
                symbols.AddRange(extracted);
            }
        }
        prepared.ChangedSymbols = symbols;
        _logger.LogInformation("{Count} changed symbols extracted.", symbols.Count);

        _logger.LogInformation("Scanning test repo at {Path}…", request.TestRepoLocalPath);
        var parser = _testParsers.FirstOrDefault(p => p.CanParse(request.TestRepoLocalPath))
            ?? throw new InvalidOperationException($"No test parser can handle the repo at '{request.TestRepoLocalPath}'.");

        var scenarios = parser.ParseScenarios(request.TestRepoLocalPath).ToList();
        prepared.AllScenarioCount = scenarios.Count;
        _logger.LogInformation("{Count} scenarios found.", scenarios.Count);

        if (scenarios.Count == 0)
        {
            prepared.Warning = "No test scenarios found. Check the test repo path and ensure .feature files exist.";
            return prepared;
        }

        var relevant = _promptBuilder.PreFilter(symbols, scenarios);
        _logger.LogInformation("Pre-filter kept {Relevant} of {Total} scenarios.", relevant.Count, scenarios.Count);

        if (relevant.Count == 0)
        {
            prepared.Warning = "No scenarios shared any keyword with the changed symbols. Check the Changed Symbols list — symbol extraction may have found nothing test-relevant.";
            return prepared;
        }

        var chunks = ChunkScenarios(relevant, ChunkSize);
        for (int i = 0; i < chunks.Count; i++)
        {
            prepared.PromptChunks.Add(new PromptChunk
            {
                ChunkIndex = i,
                TotalChunks = chunks.Count,
                PromptText = _promptBuilder.Build(prDiff.Metadata, symbols, chunks[i]),
                ScenarioCount = chunks[i].Count
            });
        }
        _logger.LogInformation("Prepared {Count} prompt chunk(s).", chunks.Count);
        return prepared;
    }

    /// <summary>
    /// Step B — parses the raw Copilot Chat response(s) you pasted back, dedupes, and ranks.
    /// Accepts either one response per chunk, OR a single combined response file containing
    /// all chunk replies (the facade's SplitCombinedResponses handles the splitting).
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

        // Too many responses is the only hard error — too few means the combined-file
        // splitter gave us what it could; we process what we have.
        if (rawResponsesInOrder.Count > prepared.PromptChunks.Count)
        {
            result.Success = false;
            result.ErrorMessage =
                $"Got {rawResponsesInOrder.Count} response(s) but only {prepared.PromptChunks.Count} chunk(s) exist. " +
                "Too many response files listed in responseFiles config.";
            return result;
        }

        var allImpacted = new List<ImpactedScenario>();
        for (int i = 0; i < rawResponsesInOrder.Count; i++)
        {
            var chunkMeta = i < prepared.PromptChunks.Count ? prepared.PromptChunks[i] : new PromptChunk { ChunkIndex = i, TotalChunks = prepared.PromptChunks.Count };
            var parsed = _responseParser.Parse(rawResponsesInOrder[i]);
            allImpacted.AddRange(parsed);

            result.LlmExchanges.Add(new LlmExchange
            {
                ChunkIndex = chunkMeta.ChunkIndex,
                TotalChunks = chunkMeta.TotalChunks,
                ScenarioCount = chunkMeta.ScenarioCount,
                Prompt = chunkMeta.PromptText,
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

public class PreparedAnalysis
{
    public PrMetadata? PrMetadata { get; set; }
    public List<ChangedSymbol> ChangedSymbols { get; set; } = new();
    public int AllScenarioCount { get; set; }
    public string RawDiffText { get; set; } = string.Empty;
    public List<PromptChunk> PromptChunks { get; set; } = new();
    public string? Warning { get; set; }
}

public class PromptChunk
{
    public int ChunkIndex { get; set; }
    public int TotalChunks { get; set; }
    public string PromptText { get; set; } = string.Empty;
    public int ScenarioCount { get; set; }
}
