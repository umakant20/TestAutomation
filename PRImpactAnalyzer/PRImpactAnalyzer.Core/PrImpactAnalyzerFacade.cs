using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PRImpactAnalyzer.Core.Models;
using PRImpactAnalyzer.Core.Pipeline;

namespace PRImpactAnalyzer.Core;

public sealed class PrImpactAnalyzerFacade : IAsyncDisposable
{
    private readonly ServiceProvider _provider;
    private readonly AnalysisPipeline _pipeline;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>Name of the small pointer file written into baseReportsFolder after each
    /// prepare run, recording where that run's files live — lets the report step find them
    /// automatically without any config editing.</summary>
    private const string CurrentRunPointerFileName = "current-run.json";

    private PrImpactAnalyzerFacade(ServiceProvider provider)
    {
        _provider = provider;
        _pipeline = provider.GetRequiredService<AnalysisPipeline>();
    }

    public static PrImpactAnalyzerFacade Create(Action<IServiceCollection> registerServices, Action<ILoggingBuilder>? configureLogging = null)
    {
        var services = new ServiceCollection();
        services.AddLogging(lb =>
        {
            configureLogging?.Invoke(lb);
            if (configureLogging is null) Microsoft.Extensions.Logging.ConsoleLoggerExtensions.AddConsole(lb);
        });
        registerServices(services);
        return new PrImpactAnalyzerFacade(services.BuildServiceProvider());
    }

    public Task<PreparedAnalysis> PrepareAsync(AnalysisRequest request, CancellationToken ct = default)
        => _pipeline.PrepareAsync(request, ct);

    /// <summary>
    /// Runs PrepareAsync AND writes the combined prompt .txt + state.json to disk.
    /// </summary>
    public async Task<PreparedAnalysis> PrepareAndWriteFilesAsync(
        AnalysisRequest request, string promptFilePath, string stateFilePath, CancellationToken ct = default)
    {
        var prepared = await _pipeline.PrepareAsync(request, ct);
        if (!string.IsNullOrEmpty(prepared.Warning)) return prepared;

        File.WriteAllText(promptFilePath, RenderCombinedPromptFile(prepared), Encoding.UTF8);
        File.WriteAllText(stateFilePath, JsonSerializer.Serialize(prepared, JsonOptions), Encoding.UTF8);
        return prepared;
    }

    /// <summary>
    /// Same as PrepareAndWriteFilesAsync, but automatically computes a dedicated run folder
    /// named "{prId}_{yyyyMMdd-HHmmss}" under baseReportsFolder, using the PR's ACTUAL ID as
    /// returned by Azure DevOps (not whatever string the caller passed in) — so the folder
    /// name is always correct even if the CLI argument and the real PR number ever diverge.
    /// Every run gets its own folder, so past runs are never overwritten.
    /// </summary>
    public async Task<RunResult> PrepareAndWriteFilesAutoAsync(
        AnalysisRequest request, string baseReportsFolder, CancellationToken ct = default)
    {
        var prepared = await _pipeline.PrepareAsync(request, ct);
        if (!string.IsNullOrEmpty(prepared.Warning))
            return new RunResult { Prepared = prepared };

        var prId = prepared.PrMetadata?.Id ?? 0;
        var runFolder = Path.Combine(baseReportsFolder, $"{prId}_{DateTime.Now:yyyyMMdd-HHmmss}");
        Directory.CreateDirectory(runFolder);

        var promptPath = Path.Combine(runFolder, "prompt.txt");
        var statePath  = Path.Combine(runFolder, "state.json");

        File.WriteAllText(promptPath, RenderCombinedPromptFile(prepared), Encoding.UTF8);
        File.WriteAllText(statePath, JsonSerializer.Serialize(prepared, JsonOptions), Encoding.UTF8);

        // Write/update the pointer file so the report step can find this run automatically —
        // no manual config editing needed.
        Directory.CreateDirectory(baseReportsFolder);
        var pointer = new CurrentRunPointer { RunFolder = runFolder, PrId = prId, StatePath = statePath };
        File.WriteAllText(
            Path.Combine(baseReportsFolder, CurrentRunPointerFileName),
            JsonSerializer.Serialize(pointer, JsonOptions), Encoding.UTF8);

        return new RunResult
        {
            Prepared   = prepared,
            RunFolder  = runFolder,
            PromptPath = promptPath,
            StatePath  = statePath,
            PrId       = prId,
        };
    }

    /// <summary>
    /// Reads the pointer file written by the most recent PrepareAndWriteFilesAutoAsync call,
    /// so the report step can locate that run's folder without any argument or config field.
    /// Returns null if no pointer file exists (e.g. prepare was never run, or used the older
    /// PrepareAndWriteFilesAsync overload with explicit fixed paths instead).
    /// </summary>
    public static CurrentRunPointer? ReadCurrentRunPointer(string baseReportsFolder)
    {
        var path = Path.Combine(baseReportsFolder, CurrentRunPointerFileName);
        if (!File.Exists(path)) return null;
        try
        {
            return JsonSerializer.Deserialize<CurrentRunPointer>(File.ReadAllText(path), JsonOptions);
        }
        catch { return null; }
    }

    /// <summary>
    /// Reads the state file + response file(s), parses and finalizes the result — does NOT
    /// write the HTML report. Lets a caller enrich the result (e.g. attach historical
    /// frequency data) before writing, via WriteReport(). Use FinalizeFromFilesAsync instead
    /// if you don't need that — it does both steps in one call, unchanged for existing callers.
    /// </summary>
    public async Task<AnalysisResult> FinalizeOnlyAsync(
        string stateFilePath, IReadOnlyList<string> responseFilePaths)
    {
        var prepared = JsonSerializer.Deserialize<PreparedAnalysis>(
            await File.ReadAllTextAsync(stateFilePath), JsonOptions)
            ?? throw new InvalidOperationException($"Could not deserialize state file at '{stateFilePath}'.");

        List<string> responses;

        if (responseFilePaths.Count == 1 && prepared.PromptChunks.Count > 1)
        {
            // User pasted all chunk replies into a single file — extract each JSON block.
            var combined = await File.ReadAllTextAsync(responseFilePaths[0]);
            responses = SplitCombinedResponses(combined);

            // If splitting found the right number of blocks, great. Otherwise fall back to
            // passing the whole file as-is (Finalize will process what it gets).
            if (responses.Count == 0) responses = new List<string> { combined };
        }
        else
        {
            responses = new List<string>();
            foreach (var path in responseFilePaths)
                responses.Add(await File.ReadAllTextAsync(path));
        }

        return _pipeline.Finalize(prepared, responses);
    }

    /// <summary>Writes the HTML report for an already-computed result — the second half of
    /// what FinalizeFromFilesAsync does in one call, split out so callers can enrich the
    /// result (e.g. historical frequency) in between.</summary>
    public static void WriteReport(AnalysisResult result, string reportFilePath) =>
        HtmlReportWriter.Write(result, reportFilePath);

    /// <summary>
    /// Reads the state file + response file(s), automatically handles the combined-file case
    /// (all chunk replies pasted into one file), builds the HTML report. Unchanged behavior
    /// for existing callers — internally just calls FinalizeOnlyAsync + WriteReport.
    /// </summary>
    public async Task<AnalysisResult> FinalizeFromFilesAsync(
        string stateFilePath, IReadOnlyList<string> responseFilePaths, string reportFilePath)
    {
        var result = await FinalizeOnlyAsync(stateFilePath, responseFilePaths);
        WriteReport(result, reportFilePath);
        return result;
    }

    /// <summary>
    /// Scans a combined response file for individual {"impacted":[...]} JSON blocks,
    /// one per chunk. Handles raw JSON and markdown-fenced JSON alike.
    /// </summary>
    private static List<string> SplitCombinedResponses(string combined)
    {
        var results = new List<string>();
        var remaining = combined;

        while (true)
        {
            var start = remaining.IndexOf('{');
            if (start < 0) break;

            // Walk forward tracking brace depth to find the matching closing brace
            int depth = 0, end = -1;
            for (int i = start; i < remaining.Length; i++)
            {
                if (remaining[i] == '{') depth++;
                else if (remaining[i] == '}') { depth--; if (depth == 0) { end = i; break; } }
            }
            if (end < 0) break;

            var block = remaining[start..(end + 1)].Trim();
            if (block.Contains("impacted", StringComparison.OrdinalIgnoreCase))
                results.Add(block);

            remaining = remaining[(end + 1)..];
        }

        return results;
    }

    private static string RenderCombinedPromptFile(PreparedAnalysis prepared)
    {
        var sb = new StringBuilder();
        var chunks = prepared.PromptChunks;

        if (chunks.Count == 1)
        {
            sb.AppendLine("# Paste everything below this line into Copilot Chat.");
            sb.AppendLine("# Save Copilot's JSON reply to a file (e.g. response.txt) then run: pr-impact report");
            sb.AppendLine();
            sb.Append(chunks[0].PromptText);
            return sb.ToString();
        }

        sb.AppendLine($"# This PR produced {chunks.Count} prompt chunks.");
        sb.AppendLine("# You can paste ALL chunks into ONE response.txt file (one reply after another).");
        sb.AppendLine("# The tool automatically splits the combined file and parses each JSON block.");
        sb.AppendLine("# Alternatively, paste each chunk into its own fresh Copilot Chat thread and");
        sb.AppendLine("# save each reply to a separate file (response1.txt, response2.txt, ...).");
        sb.AppendLine();

        for (int i = 0; i < chunks.Count; i++)
        {
            sb.AppendLine("================================================================================");
            sb.AppendLine($"CHUNK {i + 1} OF {chunks.Count}");
            sb.AppendLine("================================================================================");
            sb.AppendLine();
            sb.AppendLine(chunks[i].PromptText);
            sb.AppendLine();
        }

        return sb.ToString();
    }

    public async ValueTask DisposeAsync() => await _provider.DisposeAsync();
}

/// <summary>Result of PrepareAndWriteFilesAutoAsync — tells the caller exactly where
/// everything for this run was written.</summary>
public class RunResult
{
    public PreparedAnalysis Prepared { get; set; } = new();
    public string RunFolder  { get; set; } = string.Empty;
    public string PromptPath { get; set; } = string.Empty;
    public string StatePath  { get; set; } = string.Empty;
    public int    PrId       { get; set; }
}

/// <summary>Small pointer record written to baseReportsFolder/current-run.json after each
/// prepare run, so the report step can find the run's files automatically.</summary>
public class CurrentRunPointer
{
    public string RunFolder { get; set; } = string.Empty;
    public int    PrId      { get; set; }
    public string StatePath { get; set; } = string.Empty;
}
