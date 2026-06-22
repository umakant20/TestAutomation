using PRImpactAnalyzer.Core.Models;

namespace PRImpactAnalyzer.Core.Interfaces;

/// <summary>
/// Implemented by each technology-specific code analyzer plugin.
/// DotNetAnalyzer, ColdFusionAnalyzer, and SoapWsdlAnalyzer each implement this.
/// </summary>
public interface ICodeAnalyzer
{
    /// <summary>Human-readable name shown in logs and UI (e.g. ".NET / C# Analyzer").</summary>
    string Name { get; }

    /// <summary>
    /// Returns true if this analyzer can handle the given file path.
    /// Used to route each diff file to the right analyzer.
    /// </summary>
    bool CanAnalyze(string filePath);

    /// <summary>
    /// Extracts changed symbols from the diff lines of a single file.
    /// </summary>
    IEnumerable<ChangedSymbol> ExtractSymbols(FileDiff fileDiff);
}

/// <summary>
/// Implemented by each test framework parser plugin.
/// SpecFlowParser and ReqnrollParser both implement this.
/// </summary>
public interface ITestParser
{
    /// <summary>Human-readable name (e.g. "SpecFlow / Reqnroll Parser").</summary>
    string Name { get; }

    /// <summary>
    /// Returns true if this parser can handle the given test repo layout.
    /// e.g. checks for *.feature files + *Steps.cs files.
    /// </summary>
    bool CanParse(string testRepoPath);

    /// <summary>
    /// Walks the repo path and returns all extracted scenario records.
    /// </summary>
    IEnumerable<ScenarioRecord> ParseScenarios(string testRepoPath);
}

/// <summary>
/// Abstraction over the LLM call. Swap GitHub Copilot for any other provider
/// without touching the pipeline — just register a different implementation.
/// </summary>
public interface ILlmOrchestrator
{
    /// <summary>
    /// Sends the structured prompt and returns the raw LLM response text.
    /// </summary>
    Task<string> GetCompletionAsync(string prompt, CancellationToken cancellationToken = default);
}

/// <summary>
/// Fetches the PR diff and metadata from the source control system.
/// AzureDevOpsPrService implements this; swap for a GitHub implementation if needed.
/// </summary>
public interface IPrDiffProvider
{
    Task<PrDiff> GetDiffAsync(AnalysisRequest request, CancellationToken cancellationToken = default);
}
