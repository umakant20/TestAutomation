using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PRImpactAnalyzer.Core.Models;
using PRImpactAnalyzer.Core.Pipeline;

namespace PRImpactAnalyzer.Core;

/// <summary>
/// One-call entry point for the manual-paste workflow:
///
///   1. await facade.PrepareAndWritePromptAsync(request, "prompt.txt", "state.json")
///      → writes ONE combined prompt file (all chunks, clearly separated if more than one)
///        and a state file capturing everything needed to finalize later.
///   2. You paste prompt.txt into Copilot Chat, and paste the JSON reply back into a file
///      (one file per chunk if there's more than one — usually there's just one).
///   3. await facade.FinalizeFromFilesAsync("state.json", new[] { "response.txt" }, "report.html")
///      → parses the response(s), builds the report, writes report.html.
/// </summary>
public sealed class PrImpactAnalyzerFacade : IAsyncDisposable
{
    private readonly ServiceProvider _provider;
    private readonly AnalysisPipeline _pipeline;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

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
        var provider = services.BuildServiceProvider();
        return new PrImpactAnalyzerFacade(provider);
    }

    /// <summary>Step A — runs the free/local phases and returns the prepared analysis (no file I/O).</summary>
    public Task<PreparedAnalysis> PrepareAsync(AnalysisRequest request, CancellationToken cancellationToken = default)
        => _pipeline.PrepareAsync(request, cancellationToken);

    /// <summary>
    /// Step A, convenience version — runs PrepareAsync AND writes the combined prompt .txt
    /// file plus a JSON state file you'll pass to FinalizeFromFilesAsync later.
    /// Returns the prepared analysis in case you want to inspect it (e.g. check .Warning).
    /// </summary>
    public async Task<PreparedAnalysis> PrepareAndWriteFilesAsync(
        AnalysisRequest request, string promptFilePath, string stateFilePath, CancellationToken cancellationToken = default)
    {
        var prepared = await _pipeline.PrepareAsync(request, cancellationToken);

        if (!string.IsNullOrEmpty(prepared.Warning))
            return prepared; // nothing to write — no chunks were generated

        File.WriteAllText(promptFilePath, RenderCombinedPromptFile(prepared), Encoding.UTF8);
        File.WriteAllText(stateFilePath, JsonSerializer.Serialize(prepared, JsonOptions), Encoding.UTF8);

        return prepared;
    }

    /// <summary>
    /// Step B — reads the state file written by PrepareAndWriteFilesAsync, parses the response
    /// file(s) you pasted Copilot's reply into (one per chunk, in order), builds the final
    /// AnalysisResult, and writes the HTML report. Returns the result too in case you need it.
    /// </summary>
    public async Task<AnalysisResult> FinalizeFromFilesAsync(
        string stateFilePath, IReadOnlyList<string> responseFilePaths, string reportFilePath)
    {
        var stateJson = await File.ReadAllTextAsync(stateFilePath);
        var prepared = JsonSerializer.Deserialize<PreparedAnalysis>(stateJson, JsonOptions)
            ?? throw new InvalidOperationException($"Could not deserialize state file at '{stateFilePath}'.");

        var responses = new List<string>();
        foreach (var path in responseFilePaths)
            responses.Add(await File.ReadAllTextAsync(path));

        var result = _pipeline.Finalize(prepared, responses);
        HtmlReportWriter.Write(result, reportFilePath);
        return result;
    }

    /// <summary>
    /// Renders every prompt chunk into ONE combined .txt file. If there's more than one chunk,
    /// each is clearly separated with instructions to paste it into its OWN fresh Copilot Chat
    /// thread (continuing the same thread re-sends prior chunks as context, which costs tokens
    /// for no benefit) and save that chunk's reply to its own response file.
    /// </summary>
    private static string RenderCombinedPromptFile(PreparedAnalysis prepared)
    {
        var sb = new StringBuilder();
        var chunks = prepared.PromptChunks;

        if (chunks.Count == 1)
        {
            sb.AppendLine("# Paste everything below this line into a single Copilot Chat message.");
            sb.AppendLine("# Save Copilot's JSON reply to a file and pass it to the `report` step.");
            sb.AppendLine();
            sb.Append(chunks[0].PromptText);
            return sb.ToString();
        }

        sb.AppendLine($"# This PR produced {chunks.Count} prompt chunks. Paste each one into its own FRESH");
        sb.AppendLine("# Copilot Chat thread (not a continuation of the previous chunk's thread — a fresh");
        sb.AppendLine("# thread avoids re-billing earlier chunks' tokens as context). Save each chunk's JSON");
        sb.AppendLine("# reply to its own file, then pass all the response files to the `report` step in order.");
        sb.AppendLine();

        for (int i = 0; i < chunks.Count; i++)
        {
            sb.AppendLine("================================================================================");
            sb.AppendLine($"CHUNK {i + 1} OF {chunks.Count} — paste only the text below this header, up to the next");
            sb.AppendLine("'CHUNK' header, into a fresh Copilot Chat thread.");
            sb.AppendLine("================================================================================");
            sb.AppendLine();
            sb.AppendLine(chunks[i].PromptText);
            sb.AppendLine();
        }

        return sb.ToString();
    }

    public async ValueTask DisposeAsync() => await _provider.DisposeAsync();
}
