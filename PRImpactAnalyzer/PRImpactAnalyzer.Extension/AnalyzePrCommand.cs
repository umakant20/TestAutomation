using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Commands;
using Microsoft.VisualStudio.Extensibility.Shell;
using PRImpactAnalyzer.Core;
using PRImpactAnalyzer.Core.Models;
using PRImpactAnalyzer.Core.Pipeline;
using PRImpactAnalyzer.Infrastructure;

namespace PRImpactAnalyzer.Extension;

/// <summary>
/// Tools → Analyze PR Test Impact
///
/// Reads config, runs the full pipeline, writes prompt to a file, opens Copilot Chat,
/// and shows instructions. The user pastes the prompt, copies Copilot's response to
/// response.txt, then clicks Tools → Generate Impact Report.
/// </summary>
[VisualStudioContribution]
internal class AnalyzePrCommand : Command
{
    /// <summary>Stores the prepared analysis so GenerateReportCommand can access it.</summary>
    internal static PreparedAnalysis? LastPreparedAnalysis { get; set; }

    public override CommandConfiguration CommandConfiguration => new("Analyze PR Test Impact")
    {
        Placements = new[] { CommandPlacement.KnownPlacements.ToolsMenu },
        Icon = new CommandIconConfiguration(ImageMoniker.KnownValues.TestSuite, IconSettings.IconAndText),
    };

    public AnalyzePrCommand(VisualStudioExtensibility extensibility)
        : base(extensibility)
    {
    }

    public override async Task ExecuteCommandAsync(IClientContext context, CancellationToken ct)
    {
        var configDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".pr-impact");

        // ── 1. Load config ────────────────────────────────────────────────────
        ExtensionConfig config;
        try
        {
            config = ExtensionConfig.Load();
        }
        catch (Exception ex)
        {
            await context.ShowPromptAsync(
                $"Config Error:\n\n{ex.Message}",
                PromptOptions.OK, ct);
            return;
        }

        if (string.IsNullOrWhiteSpace(config.PrBaseUrl) || string.IsNullOrWhiteSpace(config.TestRepoPath))
        {
            await context.ShowPromptAsync(
                "Config file is missing 'prBaseUrl' or 'testRepoPath'.\n\n" +
                $"Edit: {ExtensionConfig.DefaultConfigPath}",
                PromptOptions.OK, ct);
            return;
        }

        // ── 2. Read PR number from file ───────────────────────────────────────
        var prNumberFile = Path.Combine(configDir, "pr-number.txt");

        if (!File.Exists(prNumberFile))
        {
            Directory.CreateDirectory(configDir);
            await File.WriteAllTextAsync(prNumberFile, "16773", ct);

            await context.ShowPromptAsync(
                $"Created: {prNumberFile}\n\n" +
                "Edit that file with your PR number, then click\n" +
                "Tools → Analyze PR Test Impact again.",
                PromptOptions.OK, ct);
            return;
        }

        var prNumberText = (await File.ReadAllTextAsync(prNumberFile, ct)).Trim();
        if (!int.TryParse(prNumberText, out var prNumber))
        {
            await context.ShowPromptAsync(
                $"'{prNumberText}' is not a valid PR number.\n\nEdit: {prNumberFile}",
                PromptOptions.OK, ct);
            return;
        }

        var pat = config.AzureDevOpsPat ?? Environment.GetEnvironmentVariable("ADO_PAT");
        if (string.IsNullOrWhiteSpace(pat))
        {
            await context.ShowPromptAsync(
                "No Azure DevOps PAT found.\n\n" +
                "Set 'azureDevOpsPat' in config.json or the ADO_PAT env var.",
                PromptOptions.OK, ct);
            return;
        }

        var prUrl = $"{config.PrBaseUrl!.TrimEnd('/')}/pullrequest/{prNumber}";

        // ── 3. Run the pipeline ───────────────────────────────────────────────
        PreparedAnalysis prepared;
        try
        {
            await using var facade = PrImpactAnalyzerFacade.Create(
                services => PRImpactAnalyzer.Infrastructure.PrImpactAnalyzerRegistration.AddPrImpactAnalyzer(services));

            prepared = await facade.PrepareAsync(new AnalysisRequest
            {
                DevRepoPrUrl      = prUrl,
                TestRepoLocalPath = config.TestRepoPath!,
                AzureDevOpsPat    = pat,
            }, ct);
        }
        catch (Exception ex)
        {
            await context.ShowPromptAsync(
                $"Pipeline error:\n\n{ex.Message}",
                PromptOptions.OK, ct);
            return;
        }

        if (!string.IsNullOrEmpty(prepared.Warning))
        {
            await context.ShowPromptAsync(
                $"Warning:\n\n{prepared.Warning}\n\n" +
                "No prompt generated for this PR.",
                PromptOptions.OK, ct);
            return;
        }

        LastPreparedAnalysis = prepared;

        // ── 4. Write prompt file + state file ─────────────────────────────────
        var promptFile = Path.Combine(configDir, "last-prompt.txt");
        var stateFile  = Path.Combine(configDir, "last-state.json");

        var combinedPrompt = BuildCombinedPrompt(prepared);
        await File.WriteAllTextAsync(promptFile, combinedPrompt, ct);
        await File.WriteAllTextAsync(stateFile,
            JsonSerializer.Serialize(prepared, new JsonSerializerOptions { WriteIndented = true }), ct);

        // ── 5. Open the prompt file in Notepad for easy Ctrl+A, Ctrl+C ────────
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "notepad.exe",
                Arguments = promptFile,
                UseShellExecute = true
            });
        }
        catch { /* ignore if notepad fails */ }

        // ── 6. Show instructions ──────────────────────────────────────────────
        var scenarioCount = prepared.PromptChunks.Sum(c => c.ScenarioCount);
        await context.ShowPromptAsync(
            $"PR #{prNumber} analyzed!\n\n" +
            $"Changed symbols: {prepared.ChangedSymbols.Count}\n" +
            $"Scenarios in prompt: {scenarioCount}\n\n" +
            $"Prompt opened in Notepad:\n{promptFile}\n\n" +
            "NEXT STEPS:\n" +
            "1. In Notepad: Ctrl+A, Ctrl+C (select all, copy)\n" +
            "2. In VS Copilot Chat: Ctrl+V, Enter (paste, send)\n" +
            "3. Wait for Copilot's JSON response\n" +
            "4. Select ALL of Copilot's response, Ctrl+C\n" +
            $"5. Paste into: {Path.Combine(configDir, "response.txt")}\n" +
            "6. Click: Tools → Generate Impact Report",
            PromptOptions.OK, ct);
    }

    private static string BuildCombinedPrompt(PreparedAnalysis prepared)
    {
        if (prepared.PromptChunks.Count == 1)
            return prepared.PromptChunks[0].PromptText;

        var sb = new StringBuilder();
        for (int i = 0; i < prepared.PromptChunks.Count; i++)
        {
            var text = prepared.PromptChunks[i].PromptText;
            if (i == 0)
            {
                // First chunk: keep the full header (instructions + symbols) and mark as multi-section
                var scenarioIdx = text.IndexOf("SCENARIOS", StringComparison.Ordinal);
                if (scenarioIdx > 0)
                {
                    sb.Append(text[..scenarioIdx]);
                    sb.AppendLine($"SCENARIOS — SECTION {i + 1} OF {prepared.PromptChunks.Count}:");
                    sb.AppendLine("(Analyze ALL sections. Return ONE combined JSON covering every section.)");
                    var afterLine = text.IndexOf('\n', scenarioIdx);
                    if (afterLine > 0) sb.Append(text[(afterLine + 1)..]);
                }
                else sb.Append(text);
            }
            else
            {
                // Subsequent chunks: skip the repeated header/symbols, just append scenarios
                sb.AppendLine();
                sb.AppendLine($"════════ SECTION {i + 1} OF {prepared.PromptChunks.Count} ════════");
                var scenarioIdx = text.IndexOf("SCENARIOS", StringComparison.Ordinal);
                if (scenarioIdx >= 0)
                {
                    var afterLine = text.IndexOf('\n', scenarioIdx);
                    sb.Append(afterLine > 0 ? text[(afterLine + 1)..] : text[scenarioIdx..]);
                }
                else sb.Append(text);
            }
        }
        return sb.ToString();
    }
}
