using PRImpactAnalyzer.Core.Models;

namespace PRImpactAnalyzer.Core.Interfaces;

public interface ICodeAnalyzer
{
    string Name { get; }
    bool CanAnalyze(string filePath);
    IEnumerable<ChangedSymbol> ExtractSymbols(FileDiff fileDiff);
}

public interface ITestParser
{
    string Name { get; }
    bool CanParse(string testRepoPath);
    IEnumerable<ScenarioRecord> ParseScenarios(string testRepoPath);
}

public interface IPrDiffProvider
{
    Task<PrDiff> GetDiffAsync(AnalysisRequest request, CancellationToken cancellationToken = default);
}

/// <summary>Marker interface — reserved for a future automated LLM provider if one becomes available.</summary>
public interface ILlmOrchestrator
{
    Task<string> GetCompletionAsync(string prompt, CancellationToken cancellationToken = default);
}
