using System.Diagnostics;
using System.Text.Json;
using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Commands;
using Microsoft.VisualStudio.Extensibility.Shell;
using PRImpactAnalyzer.Core.Models;
using PRImpactAnalyzer.Core.Pipeline;

namespace PRImpactAnalyzer.Extension;

/// <summary>
/// Tools → Generate Impact Report
///
/// Called AFTER you've pasted the prompt into Copilot Chat and copied the response.
/// This command:
///   1. Reads Copilot's JSON response from a file (response.txt next to config)
///   2. Parses it using the existing LlmResponseParser (handles markdown fences,
///      long keys, bare arrays, multiple JSON blocks — all the formats Copilot uses)
///   3. Deduplicates and ranks by confidence
///   4. Writes the HTML report
///   5. Opens it in your default browser
///
/// WHY NOT CLIPBOARD: The out-of-process extensibility model (VisualStudio.Extensibility)
/// runs in a separate process and does not have direct clipboard access. Instead, the user
/// saves Copilot's response to a file — same file, same location every time, so it becomes
/// muscle memory.
/// </summary>
[VisualStudioContribution]
[Command(CommandId, CommandDisplayName)]
[CommandPlacement(KnownCommandPlacement.ToolsMenu)]
[CommandIcon(KnownMonikers.GenerateReport, IconSettings.IconAndText)]
public class GenerateReportCommand : Microsoft.VisualStudio.Extensibility.Commands.Command
{
    private const string CommandId = "PRImpactAnalyzer.GenerateReport";
    private const string CommandDisplayName = "Generate Impact Report";

    public GenerateReportCommand()
    {
    }

    public override async Task ExecuteCommandAsync(IClientContext context, CancellationToken ct)
    {
        // ── 1. Check we have a prepared analysis from the previous step ───────
        var prepared = AnalyzePrCommand.LastPreparedAnalysis;

        // If the in-memory state is gone (VS restarted between steps), try loading
        // from the state file
        var configDir = Path.GetDirectoryName(ExtensionConfig.DefaultConfigPath)!;
        var stateFile = Path.Combine(configDir, "last-state.json");

        if (prepared is null)
        {
            if (File.Exists(stateFile))
            {
                try
                {
                    var stateJson = await File.ReadAllTextAsync(stateFile, ct);
                    prepared = JsonSerializer.Deserialize<PreparedAnalysis>(stateJson,
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                }
                catch { /* fall through to error below */ }
            }

            if (prepared is null)
            {
                await Extensibility.Shell().ShowPromptAsync(
                    "No analysis found.\n\n" +
                    "Run 'Tools → Analyze PR Test Impact' first.",
                    PromptOptions.OK, ct);
                return;
            }
        }

        // ── 2. Read the response file ─────────────────────────────────────────
        var responseFile = Path.Combine(configDir, "response.txt");

        if (!File.Exists(responseFile))
        {
            await Extensibility.Shell().ShowPromptAsync(
                $"Response file not found at:\n{responseFile}\n\n" +
                "After Copilot Chat responds to your prompt:\n" +
                "1. Select ALL of Copilot's response text\n" +
                "2. Copy it (Ctrl+C)\n" +
                $"3. Paste it into a new file and save as:\n{responseFile}\n" +
                "4. Then click 'Tools → Generate Impact Report' again.",
                PromptOptions.OK, ct);

            return;
        }

        var rawResponse = await File.ReadAllTextAsync(responseFile, ct);
        if (string.IsNullOrWhiteSpace(rawResponse))
        {
            await Extensibility.Shell().ShowPromptAsync(
                $"Response file is empty:\n{responseFile}\n\n" +
                "Paste Copilot's complete JSON response into this file and try again.",
                PromptOptions.OK, ct);
            return;
        }

        // ── 3. Split combined responses if needed ─────────────────────────────
        List<string> responses;
        if (prepared.PromptChunks.Count > 1)
        {
            responses = SplitCombinedResponses(rawResponse);
            if (responses.Count == 0)
                responses = new List<string> { rawResponse };
        }
        else
        {
            responses = new List<string> { rawResponse };
        }

        // ── 4. Finalize using the existing pipeline ───────────────────────────
        var responseParser = new LlmResponseParser();
        var allImpacted = new List<ImpactedScenario>();
        var exchanges = new List<LlmExchange>();

        for (int i = 0; i < responses.Count; i++)
        {
            var chunkMeta = i < prepared.PromptChunks.Count
                ? prepared.PromptChunks[i]
                : new PromptChunk { ChunkIndex = i, TotalChunks = prepared.PromptChunks.Count };

            var parsed = responseParser.Parse(responses[i]);
            allImpacted.AddRange(parsed);

            exchanges.Add(new LlmExchange
            {
                ChunkIndex          = chunkMeta.ChunkIndex,
                TotalChunks         = chunkMeta.TotalChunks,
                ScenarioCount       = chunkMeta.ScenarioCount,
                Prompt              = chunkMeta.PromptText,
                RawResponse         = responses[i],
                ParsedImpactedCount = parsed.Count(p => !p.ScenarioName.StartsWith("["))
            });
        }

        var result = new AnalysisResult
        {
            Success          = true,
            PrMetadata       = prepared.PrMetadata,
            ChangedSymbols   = prepared.ChangedSymbols,
            AllScenarioCount = prepared.AllScenarioCount,
            RawDiffText      = prepared.RawDiffText,
            LlmExchanges    = exchanges,
            ImpactedScenarios = allImpacted
                .GroupBy(s => (s.FeatureFile, s.ScenarioName))
                .Select(g => g.OrderByDescending(s => s.Confidence).First())
                .OrderByDescending(s => s.Confidence)
                .ThenBy(s => s.FeatureFile)
                .ToList()
        };

        // ── 5. Write the HTML report ──────────────────────────────────────────
        ExtensionConfig config;
        try { config = ExtensionConfig.Load(); }
        catch { config = new ExtensionConfig(); }

        var reportDir = config.ReportOutputDir ?? configDir;
        Directory.CreateDirectory(reportDir);

        var prId = prepared.PrMetadata?.Id ?? 0;
        var reportPath = Path.Combine(reportDir,
            $"pr-{prId}-impact-{DateTime.Now:yyyyMMdd-HHmmss}.html");

        HtmlReportWriter.Write(result, reportPath);

        // ── 6. Open in browser ────────────────────────────────────────────────
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = reportPath,
                UseShellExecute = true
            });
        }
        catch { /* browser open failed — user can open manually */ }

        // ── 7. Show summary ───────────────────────────────────────────────────
        int high   = result.ImpactedScenarios.Count(s => s.Confidence == ConfidenceLevel.High);
        int medium = result.ImpactedScenarios.Count(s => s.Confidence == ConfidenceLevel.Medium);
        int verify = result.ImpactedScenarios.Count(s => s.Confidence == ConfidenceLevel.Verify);

        await Extensibility.Shell().ShowPromptAsync(
            $"Report generated!\n\n" +
            $"PR #{prId}: {prepared.PrMetadata?.Title}\n" +
            $"Impacted: {result.ImpactedScenarios.Count} scenarios\n" +
            $"  HIGH: {high}  |  MEDIUM: {medium}  |  VERIFY: {verify}\n\n" +
            $"Report: {reportPath}\n\n" +
            "(Opened in your default browser)",
            PromptOptions.OK, ct);

        // Clean up: optionally delete the response file so the next run
        // doesn't accidentally re-use old data. Leave it for now — user may
        // want to re-run the report.
    }

    /// <summary>
    /// Same splitter as PrImpactAnalyzerFacade — extracts individual {"impacted":[...]}
    /// JSON blocks from a combined response file.
    /// </summary>
    private static List<string> SplitCombinedResponses(string combined)
    {
        var results = new List<string>();
        var remaining = combined;

        while (true)
        {
            var start = remaining.IndexOf('{');
            if (start < 0) break;

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
}
