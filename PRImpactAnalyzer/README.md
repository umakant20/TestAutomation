# PR Test Impact Analyzer

Maps an Azure DevOps pull request's code changes to the impacted C# SpecFlow / Reqnroll test
scenarios, using the **GitHub Copilot SDK** for the reasoning step. Runs fully headless — no UI,
no manual paste. Designed to be consumed as a **NuGet library** from your automation framework,
or run as a **CLI** in scripts / CI.

## Projects

| Project | Role |
|---|---|
| `PRImpactAnalyzer.Core` | Engine: domain models, pipeline, prompt builder, response parser, plus the `PrImpactAnalyzerFacade` one-call entry point. Packable as NuGet. |
| `PRImpactAnalyzer.Infrastructure` | Azure DevOps diff provider, **Copilot SDK orchestrator**, and the `AddPrImpactAnalyzer()` DI registration helper. |
| `PRImpactAnalyzer.Plugins` | Code analyzers (.NET via Roslyn, ColdFusion, SOAP, Node.js, Markup selectors, scoped XML config) and the SpecFlow/Reqnroll parser. |
| `PRImpactAnalyzer.Cli` | Headless `pr-impact` executable for scripts / CI. |

## Prerequisites

- .NET 8 SDK
- GitHub Copilot CLI installed and logged in (`copilot login`) — the SDK uses its credentials and your existing Copilot subscription.
- An Azure DevOps PAT with **Code (Read)** scope.

## Use as a library (from your test framework)

```csharp
using PRImpactAnalyzer.Core;
using PRImpactAnalyzer.Core.Models;
using PRImpactAnalyzer.Infrastructure;

await using var analyzer = PrImpactAnalyzerFacade.Create(
    services => services.AddPrImpactAnalyzer(model: "claude-haiku-4.5"));

// Runs the analysis AND writes an HTML report of the run:
var (result, reportPath) = await analyzer.AnalyzeAndReportAsync(new AnalysisRequest
{
    DevRepoPrUrl      = "https://dev.azure.com/org/proj/_git/repo/pullrequest/482",
    AzureDevOpsPat    = Environment.GetEnvironmentVariable("ADO_PAT")!,
    TestRepoLocalPath = @"C:\source\MyApp.Tests",
});

Console.WriteLine($"Report: {reportPath}");
foreach (var s in result.ImpactedScenarios)
    Console.WriteLine($"{s.Confidence} | {s.FeatureFile} | {s.ScenarioName} | {s.Reason}");
```

Use `AnalyzeAsync(...)` instead of `AnalyzeAndReportAsync(...)` if you only want the result
object without writing a report file.

### The HTML report

Every run can produce a self-contained `.html` report (no external assets — archivable as a
CI artifact) showing, in one page:

- PR id, title, source → target branches, description
- Summary counts (impacted total + HIGH / MEDIUM / VERIFY breakdown, scenarios scanned, changed symbols)
- The impacted-scenarios table with confidence badges and a "Why Impacted" reason per row
- The changed-symbols table
- The raw PR diff (color-coded additions/removals)
- Every prompt sent to Copilot and the raw JSON response it returned, per chunk

> Note: with the Copilot SDK, you no longer parse anything by hand. `CopilotSdkOrchestrator`
> calls Copilot and `LlmResponseParser` converts the JSON to objects automatically. The HTML
> report simply *displays* everything the run produced.

## Use as a CLI (scripts / CI)

```bash
# one-time: authenticate the Copilot CLI
copilot login

# run an analysis — writes an HTML report every run
pr-impact \
  --pr https://dev.azure.com/org/proj/_git/repo/pullrequest/482 \
  --test-repo C:\source\MyApp.Tests \
  --pat $ADO_PAT \
  --report ./reports/pr-482.html

# CI gate: non-zero exit if anything is impacted
pr-impact --pr <url> --test-repo <path> --fail-on-impact --json
```

Exit codes: `0` ok · `2` impacted scenarios found with `--fail-on-impact` · `1` error.
If `--report` is omitted, the report is written to `./pr-impact-report-<timestamp>.html`.

## What changed from the earlier version

The earlier build used a manual "paste prompt into Copilot Chat, paste JSON back" bridge,
because no programmatic Copilot access existed for individual/office subscriptions. The GitHub
Copilot SDK (GA 2026) removed that constraint, so the manual bridge and the Blazor web UI have
been **deleted entirely** and replaced with a real, headless `CopilotSdkOrchestrator`. The whole
analysis is now a single `AnalyzeAsync` / `RunAsync` call.

## Configuration notes

- **Model**: default `claude-haiku-4.5` (cheapest adequate option for this structured task).
  Override via `AddPrImpactAnalyzer(model: "...")` or `--model`.
- **Scoped XML config**: `XmlConfigAnalyzer.FileNamePatterns` is intentionally narrow — add only
  the config files your team knows are test-relevant. See that file's comments.
