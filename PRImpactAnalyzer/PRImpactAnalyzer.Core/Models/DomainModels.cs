namespace PRImpactAnalyzer.Core.Models;

// ── PR diff models ────────────────────────────────────────────────────────────

public class PrDiff
{
    public PrMetadata Metadata { get; set; } = new(0, "", "", "", "", "");
    public List<FileDiff> Files { get; set; } = new();
    public string RawDiffText { get; set; } = string.Empty;
    public List<WorkItemInfo> LinkedWorkItems { get; set; } = new();

    /// <summary>Non-fatal warnings from fetching individual file contents (e.g. a file's old/new
    /// content couldn't be retrieved) — surfaced so degraded symbol extraction is visible
    /// instead of silently producing an empty "Changed Symbols" section.</summary>
    public List<string> ContentFetchWarnings { get; set; } = new();
}

/// <summary>A work item (User Story/Bug/Task) linked to the PR in Azure DevOps.</summary>
public class WorkItemInfo
{
    public int Id { get; set; }
    public string Type { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string ReproSteps { get; set; } = string.Empty;
    public string AcceptanceCriteria { get; set; } = string.Empty;
    public List<string> Tags { get; set; } = new();
    public List<string> DiscussionComments { get; set; } = new();
}

public record PrMetadata(
    int Id,
    string Title,
    string Description,
    string SourceBranch,
    string TargetBranch,
    string Author);

public class FileDiff
{
    public string FilePath { get; set; } = string.Empty;
    public FileChangeType ChangeType { get; set; }
    public List<HunkDiff> Hunks { get; set; } = new();
    public string OldContent { get; set; } = string.Empty;
    public string NewContent { get; set; } = string.Empty;
}

public enum FileChangeType { Added, Modified, Deleted, Renamed }

public class HunkDiff
{
    public List<DiffLine> Lines { get; set; } = new();
}

public class DiffLine
{
    public string Content { get; set; } = string.Empty;
    public DiffLineType Type { get; set; }
}

public enum DiffLineType { Added, Removed, Context }

// ── Symbol models ─────────────────────────────────────────────────────────────

public enum SymbolKind
{
    DotNetMethod,
    DotNetClass,
    HttpRoute,
    HttpVerb,
    SoapOperation,
    ColdFusionFunction,
    ColdFusionField,
    ColdFusionPage,
    JsFunction,
    MarkupSelector,
    ConfigValue
}

public enum ChangeType
{
    Added,
    Removed,
    RenameFrom,
    RenameTo,
    SignatureChanged,
    BodyChanged,
    Stable
}

public class ChangedSymbol
{
    public string File { get; set; } = string.Empty;
    public string Symbol { get; set; } = string.Empty;
    public string? OldSymbol { get; set; }
    public SymbolKind Kind { get; set; }
    public ChangeType Change { get; set; }
    public string? AdditionalContext { get; set; }
}

// ── Test scenario models ──────────────────────────────────────────────────────

public class ScenarioRecord
{
    public string ScenarioName { get; set; } = string.Empty;
    public string FeatureFile { get; set; } = string.Empty;
    public string FeatureTitle { get; set; } = string.Empty;
    public List<string> Tags { get; set; } = new();
    public List<string> Steps { get; set; } = new();
    public List<string> BoundEndpoints { get; set; } = new();
    public List<string> BoundPageObjects { get; set; } = new();
    public List<string> BoundSoapProxies { get; set; } = new();
    public List<string> BoundColdFusionPages { get; set; } = new();
    public List<string> BoundSelectors { get; set; } = new();
    public bool IsOutline { get; set; }

    /// <summary>Work item IDs (from the PR's linked work items) found in this scenario's tags
    /// or feature/scenario text — a deterministic, non-LLM signal used to force-prioritize
    /// scenarios that are explicitly traceable to the same requirement as the PR.</summary>
    public List<int> MatchedWorkItemIds { get; set; } = new();
}

// ── Result models ─────────────────────────────────────────────────────────────

public enum ConfidenceLevel { High, Medium, Verify }

public class ImpactedScenario
{
    public string ScenarioName { get; set; } = string.Empty;
    public string FeatureFile { get; set; } = string.Empty;
    public string MatchedChange { get; set; } = string.Empty;
    public ConfidenceLevel Confidence { get; set; }
    public string Reason { get; set; } = string.Empty;

    /// <summary>Non-null when this scenario was force-included/upgraded because it's tagged
    /// with a work item ID linked to this PR — a deterministic signal, not an LLM guess.</summary>
    public List<int> MatchedWorkItemIds { get; set; } = new();
}

public class AnalysisResult
{
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
    public PrMetadata? PrMetadata { get; set; }
    public List<ChangedSymbol> ChangedSymbols { get; set; } = new();
    public int AllScenarioCount { get; set; }
    public List<ImpactedScenario> ImpactedScenarios { get; set; } = new();
    public string RawDiffText { get; set; } = string.Empty;
    public List<LlmExchange> LlmExchanges { get; set; } = new();
    public DateTime AnalyzedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Work items linked to the PR — surfaced in the report for transparency
    /// (Task 1: work item context used to build the prompt).</summary>
    public List<WorkItemInfo> LinkedWorkItems { get; set; } = new();

    /// <summary>The exact code-change snippet text that was included in the prompt sent to
    /// the LLM — surfaced in the report for transparency (Task 2: code changes as a clue).</summary>
    public string CodeSnippetsIncluded { get; set; } = string.Empty;

    /// <summary>Non-fatal warnings if some files' content couldn't be fetched — a likely cause
    /// of thin/empty Changed Symbols or Code Snippets sections if this is non-empty.</summary>
    public List<string> ContentFetchWarnings { get; set; } = new();
}

public class LlmExchange
{
    public int ChunkIndex { get; set; }
    public int TotalChunks { get; set; }
    public int ScenarioCount { get; set; }
    public string Prompt { get; set; } = string.Empty;
    public string RawResponse { get; set; } = string.Empty;
    public int ParsedImpactedCount { get; set; }
}

// ── Request model ─────────────────────────────────────────────────────────────

public class AnalysisRequest
{
    public string DevRepoPrUrl { get; set; } = string.Empty;
    public string AzureDevOpsPat { get; set; } = string.Empty;
    public string DevRepoLocalPath { get; set; } = string.Empty;
    public string TestRepoLocalPath { get; set; } = string.Empty;
}
