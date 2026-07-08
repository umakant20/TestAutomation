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
/// Step 1: Asks for a PR number, runs the full pipeline (diff fetch, symbol extraction,
///         test repo scan, pre-filter, prompt build), copies the prompt to clipboard,
///         opens Copilot Chat, and shows a notification telling you to paste.
///
/// After you paste into Copilot Chat and copy the response:
///
/// Step 2: Tools → Generate Impact Report — reads clipboard, parses JSON, writes HTML.
///
/// The pipeline runs entirely in-process using your existing library projects.
/// No external LLM call. No API key. The only LLM involvement is YOU pasting into
/// Copilot Chat manually — this command just makes that as frictionless as possible.
/// </summary>
[VisualStudioContribution]
[Command(CommandId, CommandDisplayName)]
[CommandPlacement(KnownCommandPlacement.ToolsMenu)]
[CommandIcon(KnownMonikers.TestSuite, IconSettings.IconAndText)]
public class AnalyzePrCommand : Microsoft.VisualStudio.Extensibility.Commands.Command
{
    private const string CommandId = "PRImpactAnalyzer.AnalyzePr";
    private const string CommandDisplayName = "Analyze PR Test Impact";

    // Shared state: the prepared analysis is stored here so GenerateReportCommand
    // can access it without re-running the pipeline. This is safe because both commands
    // run in the same extension process, sequentially, triggered by user action.
    internal static PreparedAnalysis? LastPreparedAnalysis { get; set; }

    public AnalyzePrCommand()
    {
    }

    public override async Task ExecuteCommandAsync(IClientContext context, CancellationToken ct)
    {
        // ── 1. Load config ────────────────────────────────────────────────────
        ExtensionConfig config;
        try
        {
            config = ExtensionConfig.Load();
        }
        catch (Exception ex)
        {
            await Extensibility.Shell().ShowPromptAsync(
                $"Config Error:\n\n{ex.Message}",
                PromptOptions.OK, ct);
            return;
        }

        // Validate required fields
        if (string.IsNullOrWhiteSpace(config.PrBaseUrl) || string.IsNullOrWhiteSpace(config.TestRepoPath))
        {
            await Extensibility.Shell().ShowPromptAsync(
                "Config file is missing 'prBaseUrl' or 'testRepoPath'.\n\n" +
                $"Edit: {ExtensionConfig.DefaultConfigPath}",
                PromptOptions.OK, ct);
            return;
        }

        // ── 2. Ask for PR number ──────────────────────────────────────────────
        var prIdInput = await Extensibility.Shell().ShowPromptAsync(
            "Enter the Pull Request number to analyze:",
            PromptOptions.OKCancel, ct);

        // The out-of-process extensibility model's ShowPromptAsync returns the button
        // pressed, not text input. For text input we need a different approach.
        // Since the new extensibility model has limited input UI, we'll use a simple
        // workaround: read the PR number from a file, or use an InputBox via the
        // legacy interop. For now, let's use the simplest approach that works:
        // read the PR number from the config file itself.

        // Actually, let's check if the config has a "prNumber" field:
        // This is the simplest UX — user sets the PR number in config before clicking.
        // We'll enhance with a proper input dialog later if needed.

        // For now, fall back to reading from a simple text file next to config:
        var prNumberFile = Path.Combine(
            Path.GetDirectoryName(ExtensionConfig.DefaultConfigPath)!, "pr-number.txt");

        if (!File.Exists(prNumberFile))
        {
            await Extensibility.Shell().ShowPromptAsync(
                $"Create a file at:\n{prNumberFile}\n\n" +
                "containing just the PR number (e.g. 16773).\n" +
                "Then click Tools → Analyze PR Test Impact again.",
                PromptOptions.OK, ct);

            // Create the file with a placeholder so user just edits it
            Directory.CreateDirectory(Path.GetDirectoryName(prNumberFile)!);
            await File.WriteAllTextAsync(prNumberFile, "16773", ct);
            return;
        }

        var prNumberText = (await File.ReadAllTextAsync(prNumberFile, ct)).Trim();
        if (!int.TryParse(prNumberText, out var prNumber))
        {
            await Extensibility.Shell().ShowPromptAsync(
                $"'{prNumberText}' is not a valid PR number.\n\n" +
                $"Edit: {prNumberFile}",
                PromptOptions.OK, ct);
            return;
        }

        var pat = config.AzureDevOpsPat ?? Environment.GetEnvironmentVariable("ADO_PAT");
        if (string.IsNullOrWhiteSpace(pat))
        {
            await Extensibility.Shell().ShowPromptAsync(
                "No Azure DevOps PAT found.\n\n" +
                "Set 'azureDevOpsPat' in config.json or the ADO_PAT environment variable.",
                PromptOptions.OK, ct);
            return;
        }

        var prUrl = $"{config.PrBaseUrl!.TrimEnd('/')}/pullrequest/{prNumber}";

        // ── 3. Show progress and run the pipeline ─────────────────────────────
        await Extensibility.Shell().ShowPromptAsync(
            $"Analyzing PR #{prNumber}...\n\n" +
            "This will take a few seconds. Click OK to start.",
            PromptOptions.OK, ct);

        PreparedAnalysis prepared;
        try
        {
            await using var facade = PrImpactAnalyzerFacade.Create(
                services => services.AddPrImpactAnalyzer());

            prepared = await facade.PrepareAsync(new AnalysisRequest
            {
                DevRepoPrUrl      = prUrl,
                TestRepoLocalPath = config.TestRepoPath!,
                AzureDevOpsPat    = pat,
            }, ct);
        }
        catch (Exception ex)
        {
            await Extensibility.Shell().ShowPromptAsync(
                $"Pipeline error:\n\n{ex.Message}",
                PromptOptions.OK, ct);
            return;
        }

        if (!string.IsNullOrEmpty(prepared.Warning))
        {
            await Extensibility.Shell().ShowPromptAsync(
                $"Warning:\n\n{prepared.Warning}\n\n" +
                "No prompt was generated — nothing to send to Copilot for this PR.",
                PromptOptions.OK, ct);
            return;
        }

        // Store for GenerateReportCommand
        LastPreparedAnalysis = prepared;

        // ── 4. Build combined prompt (all chunks in one) ──────────────────────
        var combinedPrompt = BuildCombinedPrompt(prepared);

        // ── 5. Copy to clipboard ──────────────────────────────────────────────
        // The out-of-process model doesn't have direct clipboard access, so we
        // write to a temp file and tell the user to copy from there, OR we can
        // use the interop clipboard helper.
        var promptFile = Path.Combine(
            Path.GetDirectoryName(ExtensionConfig.DefaultConfigPath)!, "last-prompt.txt");
        await File.WriteAllTextAsync(promptFile, combinedPrompt, ct);

        // Also write the state file for the CLI fallback
        var stateFile = Path.Combine(
            Path.GetDirectoryName(ExtensionConfig.DefaultConfigPath)!, "last-state.json");
        await File.WriteAllTextAsync(stateFile,
            JsonSerializer.Serialize(prepared, new JsonSerializerOptions { WriteIndented = true }), ct);

        // ── 6. Open Copilot Chat via the standard VS command ──────────────────
        try
        {
            // This command ID opens the Copilot Chat tool window in Visual Studio
            await Extensibility.Shell().ExecuteCommandAsync("GitHub.Copilot.Chat.Open", ct);
        }
        catch
        {
            // If the command doesn't exist (older VS or Copilot not installed), skip silently
        }

        // ── 7. Show instructions ──────────────────────────────────────────────
        var chunkInfo = prepared.PromptChunks.Count > 1
            ? $"({prepared.PromptChunks.Count} sections combined into one prompt)\n"
            : "";

        await Extensibility.Shell().ShowPromptAsync(
            $"PR #{prNumber} analyzed successfully!\n\n" +
            $"Changed symbols: {prepared.ChangedSymbols.Count}\n" +
            $"Scenarios in prompt: {prepared.PromptChunks.Sum(c => c.ScenarioCount)}\n" +
            chunkInfo +
            $"\nPrompt saved to:\n{promptFile}\n\n" +
            "NEXT STEPS:\n" +
            "1. Open the prompt file above, Select All (Ctrl+A), Copy (Ctrl+C)\n" +
            "2. Paste (Ctrl+V) into Copilot Chat and press Enter\n" +
            "3. Wait for Copilot's JSON response\n" +
            "4. Select ALL of Copilot's response, Copy (Ctrl+C)\n" +
            "5. Click: Tools → Generate Impact Report",
            PromptOptions.OK, ct);
    }

    private static string BuildCombinedPrompt(PreparedAnalysis prepared)
    {
        if (prepared.PromptChunks.Count == 1)
            return prepared.PromptChunks[0].PromptText;

        var sb = new StringBuilder();

        // Modify the first chunk's header to indicate multiple sections
        for (int i = 0; i < prepared.PromptChunks.Count; i++)
        {
            if (i == 0)
            {
                // Replace the standard header with a multi-section instruction
                var text = prepared.PromptChunks[i].PromptText;
                var scenarioLineIdx = text.IndexOf("SCENARIOS", StringComparison.Ordinal);
                if (scenarioLineIdx > 0)
                {
                    sb.Append(text[..scenarioLineIdx]);
                    sb.AppendLine($"SCENARIOS — SECTION {i + 1} OF {prepared.PromptChunks.Count}:");
                    sb.AppendLine("(Analyze ALL sections. Return ONE combined JSON covering every section.)");
                    var afterHeader = text.IndexOf('\n', scenarioLineIdx);
                    if (afterHeader > 0) sb.Append(text[(afterHeader + 1)..]);
                }
                else
                {
                    sb.Append(text);
                }
            }
            else
            {
                sb.AppendLine();
                sb.AppendLine($"════════ SECTION {i + 1} OF {prepared.PromptChunks.Count} ════════");
                // Extract only the scenarios portion (skip the repeated header/symbols)
                var text = prepared.PromptChunks[i].PromptText;
                var scenarioLineIdx = text.IndexOf("SCENARIOS", StringComparison.Ordinal);
                if (scenarioLineIdx >= 0)
                {
                    var afterHeader = text.IndexOf('\n', scenarioLineIdx);
                    if (afterHeader > 0) sb.Append(text[(afterHeader + 1)..]);
                    else sb.Append(text[scenarioLineIdx..]);
                }
                else
                {
                    sb.Append(text);
                }
            }
        }

        return sb.ToString();
    }
}
