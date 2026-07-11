using System.ComponentModel;
using ModelContextProtocol.Server;
using PRImpactAnalyzer.Core;
using PRImpactAnalyzer.Core.Models;
using PRImpactAnalyzer.Infrastructure;

namespace PRImpactAnalyzer.McpServer;

/// <summary>
/// MCP tools exposing the existing PR Test Impact Analyzer pipeline to Copilot Chat's agent
/// mode. Register this server via .mcp.json, enable its tools in the Tools panel, switch
/// Copilot Chat to Agent mode, then type something like:
///
///   "Analyze test impact for PR 16773"
///
/// Copilot will call prepare_pr_analysis(16773), read the returned prompt text, reason over
/// it using its own model, produce the impacted-scenario JSON as part of its response, and
/// then (guided by this tool's returned instructions) call generate_impact_report with that
/// JSON to produce the final HTML report.
///
/// WHY TWO SEPARATE TOOLS INSTEAD OF ONE: MCP tools are synchronous request/response — a tool
/// cannot itself call back into the LLM mid-execution. The LLM reasoning has to happen in
/// Copilot's own turn, between two tool calls: prepare (gets the prompt to Copilot) and
/// generate_impact_report (takes Copilot's own analysis and turns it into the report). This
/// mirrors exactly how the CLI's `prepare`/`report` split works, except Copilot is now the one
/// pasting the prompt and the response, invisibly, as part of one continuous conversation turn.
/// </summary>
[McpServerToolType]
public static class PrImpactTools
{
    [McpServerTool(Name = "prepare_pr_analysis")]
    [Description(
        "Fetches an Azure DevOps pull request's diff, extracts changed code symbols, scans the " +
        "local SpecFlow/Reqnroll test repository, and builds a test-impact-analysis prompt. " +
        "Returns the prompt text as a large block of text. " +
        "IMPORTANT NEXT STEP: after receiving this prompt text, read it carefully and reason " +
        "about which test scenarios are impacted by the changed symbols, exactly as instructed " +
        "in the prompt's own system instructions. Produce a JSON object in the exact format the " +
        "prompt specifies: {\"impacted\":[{\"s\":...,\"f\":...,\"m\":...,\"c\":...,\"r\":...}]}. " +
        "Then call the generate_impact_report tool, passing that JSON as the analysisJson argument, " +
        "to produce the final HTML report.")]
    public static async Task<string> PreparePrAnalysis(
        [Description("The Azure DevOps pull request number to analyze, e.g. 16773")] int prId,
        [Description("Optional path to pr-impact-config.json. If omitted, uses PR_IMPACT_CONFIG env var or ./pr-impact-config.json")] string? configPath = null)
    {
        PrImpactConfig config;
        try
        {
            config = PrImpactConfig.Load(configPath);
        }
        catch (Exception ex)
        {
            return $"ERROR: {ex.Message}";
        }

        if (string.IsNullOrWhiteSpace(config.PrBaseUrl) && string.IsNullOrWhiteSpace(config.Pr))
            return "ERROR: config must have either 'prBaseUrl' (to build the URL from prId) or a full 'pr' URL.";

        if (string.IsNullOrWhiteSpace(config.TestRepoPath))
            return "ERROR: config is missing 'testRepoPath'.";

        var pat = config.AzureDevOpsPat ?? Environment.GetEnvironmentVariable("ADO_PAT");
        if (string.IsNullOrWhiteSpace(pat))
            return "ERROR: no Azure DevOps PAT found. Set 'azureDevOpsPat' in config or the ADO_PAT env var.";

        var prUrl = !string.IsNullOrWhiteSpace(config.PrBaseUrl)
            ? $"{config.PrBaseUrl.TrimEnd('/')}/pullrequest/{prId}"
            : config.Pr!;

        var promptFile = config.PromptOutput ?? "prompt.txt";
        var stateFile  = config.StateOutput  ?? "state.json";

        try
        {
            await using var facade = PrImpactAnalyzerFacade.Create(
                services => PRImpactAnalyzer.Infrastructure.PrImpactAnalyzerRegistration.AddPrImpactAnalyzer(services));

            var prepared = await facade.PrepareAndWriteFilesAsync(
                new AnalysisRequest
                {
                    DevRepoPrUrl      = prUrl,
                    TestRepoLocalPath = config.TestRepoPath!,
                    DevRepoLocalPath  = config.DevRepoPath ?? string.Empty,
                    AzureDevOpsPat    = pat,
                },
                promptFile, stateFile);

            if (!string.IsNullOrEmpty(prepared.Warning))
                return $"WARNING: {prepared.Warning}\nNo prompt was generated — nothing to analyze for this PR.";

            // Return the actual prompt text as the tool result — this is what lets Copilot
            // "see" it and reason over it in its own next response.
            var promptText = await File.ReadAllTextAsync(promptFile);

            return
                $"PR #{prId} prepared successfully.\n" +
                $"Changed symbols: {prepared.ChangedSymbols.Count}\n" +
                $"Scenarios in prompt: {prepared.PromptChunks.Sum(c => c.ScenarioCount)}\n" +
                $"State file saved to: {Path.GetFullPath(stateFile)}\n\n" +
                "=== ANALYSIS PROMPT (analyze this yourself, then call generate_impact_report with your JSON verdict) ===\n\n" +
                promptText;
        }
        catch (Exception ex)
        {
            return $"ERROR during PR analysis: {ex.Message}";
        }
    }

    [McpServerTool(Name = "generate_impact_report")]
    [Description(
        "Takes your own test-impact-analysis JSON verdict (produced after reasoning over the " +
        "prompt returned by prepare_pr_analysis) and generates the final HTML report. The JSON " +
        "must match the format {\"impacted\":[{\"s\":...,\"f\":...,\"m\":...,\"c\":...,\"r\":...}]} " +
        "described in that prompt. Call this immediately after producing your JSON verdict — do " +
        "not ask the user to do anything manual.")]
    public static async Task<string> GenerateImpactReport(
        [Description("Your JSON verdict, exactly as produced after analyzing the prompt from prepare_pr_analysis")] string analysisJson,
        [Description("Optional path to pr-impact-config.json. If omitted, uses PR_IMPACT_CONFIG env var or ./pr-impact-config.json")] string? configPath = null)
    {
        PrImpactConfig config;
        try
        {
            config = PrImpactConfig.Load(configPath);
        }
        catch (Exception ex)
        {
            return $"ERROR: {ex.Message}";
        }

        var stateFile = config.StateOutput ?? "state.json";
        if (!File.Exists(stateFile))
            return $"ERROR: state file not found at '{Path.GetFullPath(stateFile)}'. Call prepare_pr_analysis first.";

        var reportFile = config.ReportOutput
            ?? $"pr-impact-report-{DateTime.Now:yyyyMMdd-HHmmss}.html";

        // Write the LLM's JSON verdict to a temp response file so we can reuse the existing,
        // already-tested FinalizeFromFilesAsync path unchanged.
        var tempResponseFile = Path.Combine(
            Path.GetDirectoryName(Path.GetFullPath(stateFile)) ?? ".",
            $".mcp-response-{Guid.NewGuid():N}.tmp");

        try
        {
            await File.WriteAllTextAsync(tempResponseFile, analysisJson);

            await using var facade = PrImpactAnalyzerFacade.Create(
                services => PRImpactAnalyzer.Infrastructure.PrImpactAnalyzerRegistration.AddPrImpactAnalyzer(services));

            var result = await facade.FinalizeFromFilesAsync(stateFile, new[] { tempResponseFile }, reportFile);

            if (!result.Success)
                return $"ERROR: {result.ErrorMessage}";

            int high   = result.ImpactedScenarios.Count(s => s.Confidence == ConfidenceLevel.High);
            int medium = result.ImpactedScenarios.Count(s => s.Confidence == ConfidenceLevel.Medium);
            int verify = result.ImpactedScenarios.Count(s => s.Confidence == ConfidenceLevel.Verify);

            // Best-effort: open the report in the default browser
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = reportFile,
                    UseShellExecute = true
                });
            }
            catch { /* user can open manually */ }

            return
                $"Report generated successfully.\n" +
                $"PR: {result.PrMetadata?.Title}\n" +
                $"Impacted scenarios: {result.ImpactedScenarios.Count} " +
                $"(HIGH: {high}, MEDIUM: {medium}, VERIFY: {verify})\n" +
                $"Report: {Path.GetFullPath(reportFile)}";
        }
        catch (Exception ex)
        {
            return $"ERROR generating report: {ex.Message}";
        }
        finally
        {
            try { if (File.Exists(tempResponseFile)) File.Delete(tempResponseFile); } catch { /* ignore cleanup failure */ }
        }
    }
}
