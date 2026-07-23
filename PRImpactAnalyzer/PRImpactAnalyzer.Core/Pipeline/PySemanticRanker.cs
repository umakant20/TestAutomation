using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PRImpactAnalyzer.Core.Models;

namespace PRImpactAnalyzer.Core.Pipeline;

/// <summary>
/// Option A2 from the earlier discussion: TF-IDF + Truncated SVD semantic candidate search,
/// via a Python subprocess (scikit-learn). Unlike a pretrained neural embedding model, this
/// "model" is trained fresh, every run, purely from the current PR's scenario corpus and
/// text — no external model download, no Hugging Face, no pretrained weights of any kind.
/// Nothing here ever leaves the machine; only PyPI's `scikit-learn`/`numpy` packages need to
/// be installed once via `pip install -r python-semantic-rank/requirements.txt`.
///
/// Same soft-signal contract as Bm25Ranker/OnnxEmbedder: a hit only earns a scenario a spot
/// in the candidate pool sent to the LLM — it never force-includes anything.
///
/// Gracefully skipped (never throws) if Python isn't found, the script is missing, the
/// process times out, or scikit-learn isn't installed — BM25 and everything else continue
/// working exactly as before.
/// </summary>
public static class PySemanticRanker
{
    public static async Task<List<(ScenarioRecord Scenario, double Score)>> FindTopMatchesAsync(
        List<ScenarioRecord> scenarios, string queryText, int topK,
        string pythonExecutable, string scriptPath, string workingDir,
        ILogger? logger = null, int timeoutSeconds = 30)
    {
        if (scenarios.Count == 0 || string.IsNullOrWhiteSpace(queryText))
            return new List<(ScenarioRecord, double)>();

        if (!File.Exists(scriptPath))
        {
            logger?.LogInformation("Python semantic ranker script not found at {Path} — skipping.", scriptPath);
            return new List<(ScenarioRecord, double)>();
        }

        Directory.CreateDirectory(workingDir);
        var inputPath  = Path.Combine(workingDir, $".py-semantic-input-{Guid.NewGuid():N}.json");
        var outputPath = Path.Combine(workingDir, $".py-semantic-output-{Guid.NewGuid():N}.json");

        try
        {
            var docs = scenarios.Select((s, i) => new
            {
                id = i,
                text = string.Join(' ', new[] { s.ScenarioName, s.FeatureTitle }
                    .Concat(s.Tags).Concat(s.Steps)
                    .Where(t => !string.IsNullOrWhiteSpace(t)))
            }).ToList();

            var input = new { query = queryText, scenarios = docs, topK };
            await File.WriteAllTextAsync(inputPath, JsonSerializer.Serialize(input));

            var psi = new ProcessStartInfo
            {
                FileName = pythonExecutable,
                Arguments = $"\"{scriptPath}\" \"{inputPath}\" \"{outputPath}\"",
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
            };

            using var process = Process.Start(psi);
            if (process is null)
            {
                logger?.LogWarning("Failed to start Python process for semantic ranking — skipping.");
                return new List<(ScenarioRecord, double)>();
            }

            var completed = await WaitForExitAsync(process, timeoutSeconds);
            if (!completed)
            {
                TryKill(process);
                logger?.LogWarning("Python semantic ranker timed out after {Seconds}s — skipping.", timeoutSeconds);
                return new List<(ScenarioRecord, double)>();
            }

            if (!File.Exists(outputPath))
            {
                var stderr = await process.StandardError.ReadToEndAsync();
                logger?.LogWarning("Python semantic ranker produced no output file. Stderr: {Stderr}", stderr);
                return new List<(ScenarioRecord, double)>();
            }

            var outputJson = await File.ReadAllTextAsync(outputPath);
            var output = JsonSerializer.Deserialize<PySemanticOutput>(outputJson,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (!string.IsNullOrEmpty(output?.Error))
            {
                logger?.LogWarning("Python semantic ranker reported an error: {Error}", output.Error);
                return new List<(ScenarioRecord, double)>();
            }

            if (output?.TopMatches is null) return new List<(ScenarioRecord, double)>();

            return output.TopMatches
                .Where(m => m.Id >= 0 && m.Id < scenarios.Count)
                .Select(m => (scenarios[m.Id], m.Score))
                .ToList();
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Python semantic ranker failed — skipping.");
            return new List<(ScenarioRecord, double)>();
        }
        finally
        {
            TryDelete(inputPath);
            TryDelete(outputPath);
        }
    }

    private static async Task<bool> WaitForExitAsync(Process process, int timeoutSeconds)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        try
        {
            await process.WaitForExitAsync(cts.Token);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private static void TryKill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { /* best effort */ }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort cleanup */ }
    }

    private class PySemanticOutput
    {
        public string? Error { get; set; }
        public List<PySemanticMatch>? TopMatches { get; set; }
    }

    private class PySemanticMatch
    {
        public int Id { get; set; }
        public double Score { get; set; }
    }
}
