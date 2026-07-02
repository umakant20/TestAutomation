namespace PRImpactAnalyzer.Core.Models;

// ── PR diff models ────────────────────────────────────────────────────────────

public class PrDiff
{
    public PrMetadata Metadata { get; set; } = new(0, "", "", "", "", "");
    public List<FileDiff> Files { get; set; } = new();
    public string RawDiffText { get; set; } = string.Empty;
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
