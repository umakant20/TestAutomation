# PR Test Impact Analyzer

Maps an Azure DevOps pull request's code changes to the impacted C# SpecFlow / Reqnroll test
scenarios. The LLM reasoning step is **manual**: you paste a generated prompt into Copilot
Chat yourself and paste the JSON reply back in. There is no programmatic Copilot API/SDK
access available for an individual subscription without an explicit GitHub token grant that
isn't obtainable for this use case — so this tool does everything else automatically around
that one manual step, rather than depending on auth that doesn't exist for this plan.

## Projects

| Project | Role |
|---|---|
| `PRImpactAnalyzer.Core` | Engine: domain models, pipeline (`PrepareAsync` / `Finalize`), prompt builder, response parser, HTML report writer, and the `PrImpactAnalyzerFacade` entry point. |
| `PRImpactAnalyzer.Infrastructure` | Azure DevOps diff provider and the `AddPrImpactAnalyzer()` DI registration helper. No LLM/API package of any kind. |
| `PRImpactAnalyzer.Plugins` | Code analyzers (.NET via Roslyn, ColdFusion, SOAP, Node.js, Markup selectors, scoped XML config) and the SpecFlow/Reqnroll parser. |
| `PRImpactAnalyzer.Cli` | `pr-impact` executable with two commands: `prepare` and `report`. |

## Prerequisites

- .NET 8 SDK
- An Azure DevOps PAT with **Code (Read)** scope.
- Access to GitHub Copilot Chat (Visual Studio, VS Code, or copilot.com) — used manually, no API key.

## Workflow

```bash
# Step 1 — prepare: fetches the diff, extracts symbols, scans the test repo, pre-filters,
# and writes ONE combined prompt file + a state file. No LLM call happens here.
pr-impact prepare \
  --pr "https://dev.azure.com/yourorg/yourproject/_git/yourrepo/pullrequest/482" \
  --test-repo "C:\source\MyApp.Tests" \
  --pat $env:ADO_PAT

# → writes prompt.txt and state.json, and prints next steps

# Step 2 — paste prompt.txt into Copilot Chat (one chunk at a time if there's more than one —
# usually there's just one after the local pre-filter). Save the JSON reply to a file, e.g. response.txt.

# Step 3 — report: parses the response(s) and builds the HTML report automatically.
pr-impact report --state state.json --response response.txt

# → writes pr-impact-report-<timestamp>.html and prints a summary
```

If `prepare` produced more than one chunk, paste each chunk into its **own fresh** Copilot
Chat thread (not a continuation of the previous chunk's thread — a fresh thread avoids
re-billing earlier chunks' tokens as context), save each chunk's reply to its own file, then
pass them all to `report` in order:

```bash
pr-impact report --state state.json --response chunk1-response.txt --response chunk2-response.txt
```

## Use as a library (from your test framework)

```csharp
using PRImpactAnalyzer.Core;
using PRImpactAnalyzer.Core.Models;
using PRImpactAnalyzer.Infrastructure;

await using var analyzer = PrImpactAnalyzerFacade.Create(services => services.AddPrImpactAnalyzer());

// Step A — local only, no LLM call:
var prepared = await analyzer.PrepareAndWriteFilesAsync(
    new AnalysisRequest
    {
        DevRepoPrUrl      = "https://dev.azure.com/yourorg/yourproject/_git/yourrepo/pullrequest/482",
        AzureDevOpsPat    = Environment.GetEnvironmentVariable("ADO_PAT")!,
        TestRepoLocalPath = @"C:\source\MyApp.Tests",
    },
    promptFilePath: "prompt.txt",
    stateFilePath:  "state.json");

// ... you paste prompt.txt into Copilot Chat, save the reply to response.txt ...

// Step B — parses the response(s) and writes the HTML report:
var result = await analyzer.FinalizeFromFilesAsync("state.json", new[] { "response.txt" }, "report.html");

foreach (var s in result.ImpactedScenarios)
    Console.WriteLine($"{s.Confidence} | {s.FeatureFile} | {s.ScenarioName} | {s.Reason}");
```

## The HTML report

Every `report` run produces a self-contained `.html` file (no external assets) showing:

- PR id, title, source → target branches, description
- Summary counts (impacted total + HIGH / MEDIUM / VERIFY breakdown, scenarios scanned, changed symbols)
- The impacted-scenarios table with confidence badges and a "Why Impacted" reason per row
- The changed-symbols table
- The raw PR diff (color-coded additions/removals)
- Every prompt you sent to Copilot and the raw JSON response you pasted back, per chunk

## Configuration notes

- **Scoped XML config**: `XmlConfigAnalyzer.FileNamePatterns` is intentionally narrow — add only
  the config files your team knows are test-relevant. See that file's comments.
- **No Copilot API key, SDK, or token of any kind** is used or stored anywhere in this solution.
