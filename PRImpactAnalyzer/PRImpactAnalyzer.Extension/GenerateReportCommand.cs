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
/// Reads Copilot's JSON response from ~/.pr-impact/response.txt, parses it,
/// builds the HTML report, and opens it in the browser.
/// </summary>
[VisualStudioContribution]
internal class GenerateReportCommand : Command
{
    public override CommandConfiguration CommandConfiguration => new("Generate Impact Report")
    {
        Placements = new[] { CommandPlacement.KnownPlacements.ToolsMenu },
        Icon = new CommandIconConfiguration(ImageMoniker.KnownValues.GenerateFile, IconSettings.IconAndText),
    };

    public GenerateReportCommand(VisualStudioExtensibility extensibility)
        : base(extensibility)
    {
    }

    public override async Task ExecuteCommandAsync(IClientContext context, CancellationToken ct)
    {
        var configDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".pr-impact");

        // ── 1. Load prepared analysis ─────────────────────────────────────────
        var prepared = AnalyzePrCommand.LastPreparedAnalysis;
        var stateFile = Path.Combine(configDir, "last-state.json");

        if (prepared is null && File.Exists(stateFile))
        {
            try
            {
                var json = await File.ReadAllTextAsync(stateFile, ct);
                prepared = JsonSerializer.Deserialize<PreparedAnalysis>(json,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }
            catch { /* fall through */ }
        }

        if (prepared is null)
        {
            await context.ShowPromptAsync(
                "No analysis found.\n\nRun 'Tools → Analyze PR Test Impact' first.",
                PromptOptions.OK, ct);
            return;
        }

        // ── 2. Read response file ─────────────────────────────────────────────
        var responseFile = Path.Combine(configDir, "response.txt");

        if (!File.Exists(responseFile))
        {
            await context.ShowPromptAsync(
                $"Response file not found:\n{responseFile}\n\n" +
                "After Copilot responds:\n" +
                "1. Select ALL of Copilot's response\n" +
                "2. Ctrl+C to copy\n" +
                $"3. Create {responseFile} and paste (Ctrl+V), save\n" +
                "4. Click Tools → Generate Impact Report again",
                PromptOptions.OK, ct);
            return;
        }

        var rawResponse = await File.ReadAllTextAsync(responseFile, ct);
        if (string.IsNullOrWhiteSpace(rawResponse))
        {
            await context.ShowPromptAsync(
                $"Response file is empty:\n{responseFile}\n\n" +
                "Paste Copilot's complete response and save.",
                PromptOptions.OK, ct);
            return;
        }

        // ── 3. Split if multi-chunk ───────────────────────────────────────────
        List<string> responses;
        if (prepared.PromptChunks.Count > 1)
        {
            responses = SplitCombinedResponses(rawResponse);
            if (responses.Count == 0) responses = new List<string> { rawResponse };
        }
        else
        {
            responses = new List<string> { rawResponse };
        }

        // ── 4. Parse and finalize ─────────────────────────────────────────────
        var parser = new LlmResponseParser();
        var allImpacted = new List<ImpactedScenario>();
        var exchanges = new List<LlmExchange>();

        for (int i = 0; i < responses.Count; i++)
        {
            var chunk = i < prepared.PromptChunks.Count
                ? prepared.PromptChunks[i]
                : new PromptChunk { ChunkIndex = i, TotalChunks = prepared.PromptChunks.Count };

            var parsed = parser.Parse(responses[i]);
            allImpacted.AddRange(parsed);

            exchanges.Add(new LlmExchange
            {
                ChunkIndex          = chunk.ChunkIndex,
                TotalChunks         = chunk.TotalChunks,
                ScenarioCount       = chunk.ScenarioCount,
                Prompt              = chunk.PromptText,
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
            LlmExchanges     = exchanges,
            ImpactedScenarios = allImpacted
                .GroupBy(s => (s.FeatureFile, s.ScenarioName))
                .Select(g => g.OrderByDescending(s => s.Confidence).First())
                .OrderBy(s => s.Confidence switch
                {
                    ConfidenceLevel.High => 0,
                    ConfidenceLevel.Medium => 1,
                    _ => 2
                })
                .ThenBy(s => s.FeatureFile)
                .ToList()
        };

        // ── 5. Write HTML report ──────────────────────────────────────────────
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
            Process.Start(new ProcessStartInfo { FileName = reportPath, UseShellExecute = true });
        }
        catch { /* user can open manually */ }

        // ── 7. Summary ────────────────────────────────────────────────────────
        int high   = result.ImpactedScenarios.Count(s => s.Confidence == ConfidenceLevel.High);
        int medium = result.ImpactedScenarios.Count(s => s.Confidence == ConfidenceLevel.Medium);
        int verify = result.ImpactedScenarios.Count(s => s.Confidence == ConfidenceLevel.Verify);

        await context.ShowPromptAsync(
            $"Report generated!\n\n" +
            $"PR #{prId}: {prepared.PrMetadata?.Title}\n" +
            $"Impacted: {result.ImpactedScenarios.Count} scenarios\n" +
            $"  HIGH: {high}  |  MEDIUM: {medium}  |  VERIFY: {verify}\n\n" +
            $"Report opened:\n{reportPath}",
            PromptOptions.OK, ct);
    }

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
