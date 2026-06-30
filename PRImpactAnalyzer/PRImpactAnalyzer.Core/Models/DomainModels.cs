namespace PRImpactAnalyzer.Core.Models;

// ─────────────────────────────────────────────
// PR / Diff models
// ─────────────────────────────────────────────

public record PrReference(
    string OrganizationUrl,   // e.g. https://dev.azure.com/myorg
    string Project,
    string RepositoryName,
    int PrId);

public record PrMetadata(
    int Id,
    string Title,
    string Description,
    string SourceBranch,
    string TargetBranch,
    string Author);

public class PrDiff
{
    public PrMetadata Metadata { get; set; } = new(0, "", "", "", "", "");
    public List<FileDiff> Files { get; set; } = new();
    public string RawDiffText { get; set; } = string.Empty;
}

public class FileDiff
{
    public string FilePath { get; set; } = string.Empty;
    public FileChangeType ChangeType { get; set; }
    public List<HunkDiff> Hunks { get; set; } = new();

    /// <summary>
    /// Full file content at the PR's target branch (before the change) and source
    /// branch (after the change). Needed by any analyzer that requires a syntactically
    /// complete file to parse — e.g. Roslyn cannot parse a diff fragment of +/- lines,
    /// only a real, complete source file. Regex-based analyzers can ignore these and
    /// keep using Hunks; AST-based analyzers (DotNetAnalyzer) use these instead.
    /// </summary>
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

// ─────────────────────────────────────────────
// Symbol models (extracted from diff)
// ─────────────────────────────────────────────

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
    JsFunction,        // Node.js/JS/TS exported function or module export
    MarkupSelector,    // id / class / data-testid / name attribute from HTML, JSX, or any markup-producing template
    ConfigValue         // a scoped, test-relevant key from an explicitly-allowlisted XML/JSON config file
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
    public string? OldSymbol { get; set; }          // populated on renames
    public SymbolKind Kind { get; set; }
    public ChangeType Change { get; set; }
    public string? AdditionalContext { get; set; }  // e.g. HTTP verb for routes
}

// ─────────────────────────────────────────────
// Test scenario models
// ─────────────────────────────────────────────

public class ScenarioRecord
{
    public string ScenarioName { get; set; } = string.Empty;
    public string FeatureFile { get; set; } = string.Empty;      // relative path
    public string FeatureTitle { get; set; } = string.Empty;
    public List<string> Tags { get; set; } = new();
    public List<string> Steps { get; set; } = new();
    public List<string> BoundEndpoints { get; set; } = new();         // HTTP endpoint strings resolved from step defs
    public List<string> BoundPageObjects { get; set; } = new();       // Selenium page object class names
    public List<string> BoundSoapProxies { get; set; } = new();       // SOAP proxy/client class names
    public List<string> BoundColdFusionPages { get; set; } = new();   // .cfm page filenames navigated to/from step defs (URLs, Page.Url consts, driver.Navigate calls)
    public List<string> BoundSelectors { get; set; } = new();         // Selenium By.Id/By.ClassName/By.Name/CSS selector literal values referenced in step defs or page objects
    public bool IsOutline { get; set; }
}

// ─────────────────────────────────────────────
// Analysis pipeline models
// ─────────────────────────────────────────────

public class AnalysisRequest
{
    // Azure DevOps connection
    public string AzureDevOpsOrgUrl { get; set; } = string.Empty;    // https://dev.azure.com/yourorg
    public string AzureDevOpsProject { get; set; } = string.Empty;
    public string DevRepoPrUrl { get; set; } = string.Empty;         // full browser URL of the PR
    public string AzureDevOpsPat { get; set; } = string.Empty;       // *** PLACEHOLDER — set via UI or config ***

    // Local repo paths (already cloned on dev machine)
    public string DevRepoLocalPath { get; set; } = string.Empty;
    public string TestRepoLocalPath { get; set; } = string.Empty;
}

public class AnalysisResult
{
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
    public PrMetadata? PrMetadata { get; set; }
    public List<ChangedSymbol> ChangedSymbols { get; set; } = new();

    /// <summary>Total scenarios scanned in the test repo (count only — the full list isn't
    /// needed downstream and would otherwise round-trip a potentially large list through
    /// the on-disk state file between the prepare and report steps for no benefit).</summary>
    public int AllScenarioCount { get; set; }

    public List<ImpactedScenario> ImpactedScenarios { get; set; } = new();

    /// <summary>The raw unified diff text fetched from the PR (shown in the HTML report).</summary>
    public string RawDiffText { get; set; } = string.Empty;

    /// <summary>Every prompt sent to Copilot and the raw response pasted back, one entry per chunk.</summary>
    public List<LlmExchange> LlmExchanges { get; set; } = new();

    public DateTime AnalyzedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>One prompt-and-response pair for a single chunk, captured for the report.</summary>
public class LlmExchange
{
    public int ChunkIndex { get; set; }
    public int TotalChunks { get; set; }
    public int ScenarioCount { get; set; }
    public string Prompt { get; set; } = string.Empty;
    public string RawResponse { get; set; } = string.Empty;
    public int ParsedImpactedCount { get; set; }
}

public class ImpactedScenario
{
    public string ScenarioName { get; set; } = string.Empty;
    public string FeatureFile { get; set; } = string.Empty;
    public string MatchedChange { get; set; } = string.Empty;
    public ConfidenceLevel Confidence { get; set; }
    public string Reason { get; set; } = string.Empty;
}

public enum ConfidenceLevel { High, Medium, Verify }
