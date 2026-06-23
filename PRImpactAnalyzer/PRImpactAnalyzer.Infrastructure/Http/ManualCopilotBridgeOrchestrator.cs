using Microsoft.Extensions.Logging;

namespace PRImpactAnalyzer.Infrastructure.Http;

/// <summary>
/// Holds the per-session "manual paste" state for the Copilot Chat workflow.
///
/// There is no programmatic Copilot chat-completions API available on individual or
/// office subscriptions, so this tool never calls Copilot over the network. Instead:
///
///   1. AnalysisPipeline.PrepareAsync builds one or more prompt chunks (no LLM call).
///   2. The Blazor UI shows each prompt and a "Save to file" / "Copy" action.
///   3. You paste the prompt into Visual Studio's Copilot Chat window (View → GitHub
///      Copilot Chat) and send it.
///   4. You paste Copilot's JSON reply back into the UI's textbox for that chunk.
///   5. AnalysisPipeline.ParseChunkResponse / FinalizeResult take over from there —
///      identical code path to what a real API response would have produced.
///
/// This class exists mainly to optionally persist prompt chunks to disk so very large
/// prompts can be opened in a text editor instead of scrolled through in a textarea.
/// </summary>
public class ManualCopilotBridgeOrchestrator
{
    private readonly ILogger<ManualCopilotBridgeOrchestrator> _logger;

    public ManualCopilotBridgeOrchestrator(ILogger<ManualCopilotBridgeOrchestrator> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Writes a prompt chunk to a temp file and returns the path, so the UI can offer
    /// "Open file" as an alternative to copying out of a textarea (useful for very
    /// large prompts that are awkward to select-all inside a browser textbox).
    /// </summary>
    public async Task<string> SavePromptToFileAsync(string promptText, int chunkIndex)
    {
        var fileName = $"copilot-prompt-chunk{chunkIndex + 1}-{DateTime.Now:yyyyMMdd-HHmmss}.txt";
        var dir = Path.Combine(Path.GetTempPath(), "PRImpactAnalyzer");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, fileName);

        await File.WriteAllTextAsync(path, promptText);
        _logger.LogInformation("Prompt chunk {Index} written to {Path} ({Length} chars)", chunkIndex + 1, path, promptText.Length);
        return path;
    }
}
