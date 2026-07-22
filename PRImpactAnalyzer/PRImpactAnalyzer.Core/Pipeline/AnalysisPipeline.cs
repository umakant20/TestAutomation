using System.Text.RegularExpressions;
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

    // Task 1: matches a scenario's tag against a work item ID, accepting the common
    // conventions teams actually use for linking Gherkin tags to ADO work items.
    private static readonly Regex WorkItemTagPattern = new(
        @"^(?:WI|WORKITEM|BUG|US|TASK|AB)?#?(\d+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

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
    /// Step A — purely local, no LLM call. Fetches the PR diff + linked work items, extracts
    /// symbols, scans the test repo, matches work-item tags, pre-filters, chunks, and builds
    /// prompt text (now including work item context and code-change snippets) for each chunk.
    /// </summary>
    public async Task<PreparedAnalysis> PrepareAsync(AnalysisRequest request, CancellationToken ct = default)
    {
        var prepared = new PreparedAnalysis();

        _logger.LogInformation("Fetching PR diff from Azure DevOps…");
        var prDiff = await _prDiffProvider.GetDiffAsync(request, ct);
        prepared.PrMetadata = prDiff.Metadata;
        prepared.RawDiffText = prDiff.RawDiffText;
        prepared.LinkedWorkItems = prDiff.LinkedWorkItems;
        prepared.ContentFetchWarnings = prDiff.ContentFetchWarnings;
        _logger.LogInformation("{Count} linked work item(s) found.", prDiff.LinkedWorkItems.Count);
        if (prDiff.ContentFetchWarnings.Count > 0)
            _logger.LogWarning("{Count} file(s) had content fetch issues — symbol extraction for those files will be degraded. See report for details.", prDiff.ContentFetchWarnings.Count);

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

        // Task 2: build compact code-change snippets (actual +/- lines, not just symbol names)
        // so the LLM can see real code context as an extra clue, kept small and capped.
        prepared.CodeSnippetsIncluded = BuildCodeSnippets(prDiff.Files);

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

        // Task 1: deterministically match each scenario's tags against the linked work item IDs.
        var workItemIds = prDiff.LinkedWorkItems.Select(w => w.Id).ToHashSet();
        if (workItemIds.Count > 0)
        {
            foreach (var s in scenarios)
                s.MatchedWorkItemIds = s.Tags
                    .Select(TryExtractWorkItemId)
                    .Where(id => id.HasValue && workItemIds.Contains(id.Value))
                    .Select(id => id!.Value)
                    .Distinct()
                    .ToList();

            var matchedCount = scenarios.Count(s => s.MatchedWorkItemIds.Count > 0);
            _logger.LogInformation("{Count} scenario(s) matched a linked work item via tags.", matchedCount);
            prepared.WorkItemMatchedScenarios = scenarios.Where(s => s.MatchedWorkItemIds.Count > 0).ToList();
        }

        var relevant = _promptBuilder.PreFilter(symbols, scenarios, prDiff.Metadata, prDiff.LinkedWorkItems);
        _logger.LogInformation("Pre-filter kept {Relevant} of {Total} scenarios.", relevant.Count, scenarios.Count);

        // Always keep work-item-matched scenarios even if the keyword pre-filter missed them —
        // a confirmed traceability link is a stronger signal than keyword overlap.
        var workItemMatched = scenarios.Where(s => s.MatchedWorkItemIds.Count > 0).ToList();
        foreach (var wm in workItemMatched)
            if (!relevant.Any(r => r.FeatureFile == wm.FeatureFile && r.ScenarioName == wm.ScenarioName))
                relevant.Add(wm);

        // BM25 semantic-ish candidate search (Option C): surfaces scenarios whose NAME/STEPS/
        // TAGS share meaningful terms with the PR's and linked work items' natural-language
        // text, even with zero literal keyword/symbol-name overlap. This is a SOFT signal —
        // it only earns a scenario a spot in the candidate pool the LLM verifies; unlike the
        // work-item tag match above, it does NOT force-include anything into the final result.
        var semanticQuery = Bm25Ranker.BuildQueryText(prDiff.Metadata, prDiff.LinkedWorkItems);
        var semanticMatches = Bm25Ranker.FindTopMatches(scenarios, semanticQuery, topK: 30);
        prepared.SemanticMatchedScenarios = semanticMatches.Select(m => m.Scenario).ToList();
        foreach (var (semScenario, score) in semanticMatches)
        {
            semScenario.SemanticScore = score;
            if (!relevant.Any(r => r.FeatureFile == semScenario.FeatureFile && r.ScenarioName == semScenario.ScenarioName))
                relevant.Add(semScenario);
        }
        _logger.LogInformation("BM25 semantic search surfaced {Count} candidate(s).", semanticMatches.Count);

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
                PromptText = _promptBuilder.Build(prDiff.Metadata, symbols, chunks[i], prDiff.LinkedWorkItems, prepared.CodeSnippetsIncluded),
                ScenarioCount = chunks[i].Count
            });
        }
        _logger.LogInformation("Prepared {Count} prompt chunk(s).", chunks.Count);
        return prepared;
    }

    /// <summary>
    /// Step B — parses the raw Copilot Chat response(s) you pasted back, dedupes, ranks, and
    /// applies a deterministic backstop: any scenario matched to a linked work item is
    /// force-included at HIGH confidence even if the LLM's JSON omitted or under-rated it.
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
            LinkedWorkItems = prepared.LinkedWorkItems,
            CodeSnippetsIncluded = prepared.CodeSnippetsIncluded,
            ContentFetchWarnings = prepared.ContentFetchWarnings,
        };

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

        var merged = allImpacted
            .GroupBy(s => (s.FeatureFile, s.ScenarioName))
            .Select(g =>
            {
                // Keep the highest-confidence duplicate as the base, but union MatchSources
                // across ALL duplicates for this scenario — otherwise a scenario matched by
                // one chunk as "Code" and independently as "WorkItem" in another response
                // would silently lose one of those sources when only the first is kept.
                var best = g.OrderByDescending(s => s.Confidence).First();
                best.MatchSources = g.SelectMany(s => s.MatchSources).Distinct().ToList();
                return best;
            })
            .ToDictionary(s => (s.FeatureFile, s.ScenarioName));

        // Task 1 backstop: force-include/upgrade every work-item-matched scenario to HIGH,
        // regardless of what the LLM returned — a confirmed tag-to-work-item link is a
        // deterministic fact, not something that should be left to LLM discretion.
        foreach (var wiScenario in GetWorkItemMatchedScenarios(prepared))
        {
            var key = (wiScenario.FeatureFile, wiScenario.ScenarioName);
            if (merged.TryGetValue(key, out var existing))
            {
                existing.Confidence = ConfidenceLevel.High;
                existing.MatchedWorkItemIds = wiScenario.MatchedWorkItemIds;
                if (!existing.MatchSources.Contains("WorkItemTag"))
                    existing.MatchSources.Add("WorkItemTag");
                if (!existing.Reason.Contains("work item", StringComparison.OrdinalIgnoreCase))
                    existing.Reason = $"Linked to work item #{string.Join(", #", wiScenario.MatchedWorkItemIds)}. {existing.Reason}".Trim();
            }
            else
            {
                merged[key] = new ImpactedScenario
                {
                    ScenarioName  = wiScenario.ScenarioName,
                    FeatureFile   = wiScenario.FeatureFile,
                    MatchedChange = "Work item traceability tag",
                    Confidence    = ConfidenceLevel.High,
                    Reason        = $"Linked to work item #{string.Join(", #", wiScenario.MatchedWorkItemIds)} associated with this PR.",
                    MatchedWorkItemIds = wiScenario.MatchedWorkItemIds,
                    MatchSources  = new List<string> { "WorkItemTag" },
                };
            }
        }

        // BM25 semantic evidence tagging — SOFT signal, deliberately no "else" branch: unlike
        // the work-item tag backstop above, we do NOT force-create an entry for a scenario the
        // LLM didn't confirm. A semantic hit only earned this scenario a spot in the candidate
        // pool; the LLM's own verification is what determines whether it's actually impacted.
        // We only annotate entries the LLM ALREADY included, so a reviewer can see one more
        // piece of "why this surfaced" context on scenarios that turned out to be genuine.
        foreach (var semScenario in prepared.SemanticMatchedScenarios)
        {
            var key = (semScenario.FeatureFile, semScenario.ScenarioName);
            if (merged.TryGetValue(key, out var existing) && !existing.MatchSources.Contains("Semantic"))
                existing.MatchSources.Add("Semantic");
        }

        result.ImpactedScenarios = merged.Values
            .OrderByDescending(s => s.Confidence)
            .ThenBy(s => s.FeatureFile)
            .ToList();

        _logger.LogInformation("Finalized — {Count} impacted scenarios.", result.ImpactedScenarios.Count);
        return result;
    }

    /// <summary>Re-derives which scenarios matched a work item — cheap enough to recompute
    /// from the prompt chunks' scenario data rather than persisting a parallel list.</summary>
    private static List<ScenarioRecord> GetWorkItemMatchedScenarios(PreparedAnalysis prepared) =>
        prepared.WorkItemMatchedScenarios;

    private static int? TryExtractWorkItemId(string tag)
    {
        var m = WorkItemTagPattern.Match(tag.Trim());
        return m.Success && int.TryParse(m.Groups[1].Value, out var id) ? id : null;
    }

    /// <summary>
    /// Task 2: builds a compact, capped set of actual code-change snippets (real +/- lines,
    /// not just extracted symbol names) so the LLM has genuine code context as an extra clue
    /// for correlating changes with feature files. Bounded per-file and in total to protect
    /// the token budget the rest of the prompt already works hard to minimize.
    /// </summary>
    private static string BuildCodeSnippets(List<FileDiff> files, int maxLinesPerFile = 12, int maxTotalLines = 120)
    {
        var sb = new System.Text.StringBuilder();
        int totalLines = 0;

        foreach (var file in files)
        {
            if (totalLines >= maxTotalLines) break;

            var changedLines = file.Hunks
                .SelectMany(h => h.Lines)
                .Where(l => l.Type != DiffLineType.Context)
                .Take(maxLinesPerFile)
                .ToList();

            if (changedLines.Count == 0) continue;

            sb.AppendLine($"[{file.FilePath}]");
            foreach (var line in changedLines)
            {
                if (totalLines >= maxTotalLines) break;
                var marker = line.Type == DiffLineType.Added ? "+" : "-";
                var text = line.Content.Length > 140 ? line.Content[..140] + "…" : line.Content;
                sb.AppendLine($"{marker}{text.TrimEnd()}");
                totalLines++;
            }
        }

        return sb.ToString();
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

    /// <summary>Task 1: work items linked to the PR, used to enrich the prompt and match
    /// against feature file tags.</summary>
    public List<WorkItemInfo> LinkedWorkItems { get; set; } = new();

    /// <summary>Task 2: the actual code-change snippet text included in the prompt.</summary>
    public string CodeSnippetsIncluded { get; set; } = string.Empty;

    /// <summary>Scenarios that matched a linked work item via tags — persisted here so
    /// Finalize() can apply the deterministic HIGH-confidence backstop even after a
    /// round-trip through state.json (tags/matches don't need re-parsing the test repo).</summary>
    public List<ScenarioRecord> WorkItemMatchedScenarios { get; set; } = new();

    /// <summary>Scenarios surfaced as candidates via BM25 text similarity — persisted so
    /// Finalize() can tag the final impacted result with "Semantic" evidence for any of
    /// these the LLM actually confirmed. Unlike WorkItemMatchedScenarios, this is NOT a
    /// force-include backstop — a semantic hit only earns the scenario a spot in the
    /// candidate pool sent to the LLM; the LLM's own verification still decides confidence.</summary>
    public List<ScenarioRecord> SemanticMatchedScenarios { get; set; } = new();

    /// <summary>Non-fatal warnings if some files' content couldn't be fetched.</summary>
    public List<string> ContentFetchWarnings { get; set; } = new();
}

public class PromptChunk
{
    public int ChunkIndex { get; set; }
    public int TotalChunks { get; set; }
    public string PromptText { get; set; } = string.Empty;
    public int ScenarioCount { get; set; }
}
