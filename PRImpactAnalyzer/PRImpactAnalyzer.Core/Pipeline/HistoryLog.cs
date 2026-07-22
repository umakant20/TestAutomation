using System.Text.Json;
using PRImpactAnalyzer.Core.Models;

namespace PRImpactAnalyzer.Core.Pipeline;

/// <summary>
/// Lightweight, append-only cross-run history log (Reports/history.jsonl — one JSON object
/// per line, one line per completed 'report' run). Used to compute how often each scenario
/// has been flagged as impacted over recent PRs, surfaced in the HTML report as a "how hot
/// is this area of the suite" signal that a single-run report can't show on its own.
/// </summary>
public static class HistoryLog
{
    private const string FileName = "history.jsonl";

    public static async Task AppendAsync(string reportsBaseDir, HistoryEntry entry)
    {
        Directory.CreateDirectory(reportsBaseDir);
        var path = Path.Combine(reportsBaseDir, FileName);
        var line = JsonSerializer.Serialize(entry) + "\n";
        await File.AppendAllTextAsync(path, line);
    }

    public static List<HistoryEntry> ReadAll(string reportsBaseDir)
    {
        var path = Path.Combine(reportsBaseDir, FileName);
        if (!File.Exists(path)) return new List<HistoryEntry>();

        var entries = new List<HistoryEntry>();
        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var entry = JsonSerializer.Deserialize<HistoryEntry>(line);
                if (entry is not null) entries.Add(entry);
            }
            catch { /* skip a malformed line rather than fail the whole read */ }
        }
        return entries;
    }

    /// <summary>
    /// Computes, for each scenario appearing in the CURRENT run's impacted list, how many of
    /// the last `lastNRuns` logged runs also flagged it — a simple "flagged in 4 of the last
    /// 10 runs" signal. Only scenarios relevant to the current run are returned (not the
    /// full historical universe), so this is meant to be cross-referenced against the
    /// current report, not read as a standalone dashboard.
    /// </summary>
    public static List<ScenarioFrequency> ComputeFrequency(
        List<HistoryEntry> history, List<ImpactedScenario> currentImpacted, int lastNRuns = 20)
    {
        var recentRuns = history
            .OrderByDescending(h => h.Timestamp)
            .Take(lastNRuns)
            .ToList();

        if (recentRuns.Count == 0) return new List<ScenarioFrequency>();

        return currentImpacted
            .Select(cur => new ScenarioFrequency
            {
                ScenarioName = cur.ScenarioName,
                FeatureFile  = cur.FeatureFile,
                FlaggedCount = recentRuns.Count(run => run.Scenarios.Any(s =>
                    s.FeatureFile == cur.FeatureFile && s.ScenarioName == cur.ScenarioName)),
                TotalRuns = recentRuns.Count,
            })
            .Where(f => f.FlaggedCount > 0)
            .OrderByDescending(f => f.FlaggedCount)
            .ToList();
    }
}
