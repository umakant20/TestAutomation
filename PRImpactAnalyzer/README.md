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


prompt
i have an automation framework build in c# selenium , reqnroll used across multiple projects.  last year we have developed result repository for project where in each execution results will be stored in database and that can be use to show execution result on dashboard using powerbi. That approach of storing results are not getting used across all projects only few projects uses that data. In that approach  we are storing data into database directly by connecting to db using ADO.net into sql server. Now i want to standardise the result repository storing process by implementing REST API to store all results. and build custom dashboard in node js or any other ui/ux  trending technology may be you can suggest me.There will be 2 custom dashboard one which will tell about daily regressing execution will all detail reports and stats and other one will be organization wide which have summary of data for each project which are using that automation framework. Currently project is using extent report API to store the data which is then getting sent via email after each test scenario execution.  I don't think powerbi is a good option because it don't store and retrive all the result due to storage limit on import mode.   let me know your openion. so i have build small data base model where in there are multiple tables TestRun , TestFeatures, TestScenarios,  TestSteps,TestActions. TestRun stores daily execution result start time and end time which have one unique TestRunid and it will be referenced in TestFeatureTable as one Features have multiple Scenarios, one scenario have multiple steps, one step can have multiple actions. each table is stored with referential integrity constraints. because we are running tests on daily basis so each table stores corresponding information. Like TestFeature table have unique guid, Feature name , start time, end time, pass fail status and TestRunId, similarly TestScenario Table table have unique guid, star and end time, pass/fail status, and guid ref or Feature to which the scenario belongs, similarly test step and test actions  action table is the most basic which will have information about each ui action, pass fail status, error logs/ stack trace and screenshot path. there is one more table we have implemented where we are storing failures screen shots in base64 encoded format. with ref of action guid. this is quick summary of DB model. In Power bi report we have different pi chart, high level test details, feature wise pass fail count. Also all the features are divided into workflow/modules which we have mapped into TestFeature Table. When i run regression for 5-6 hours or full one day then all the test will have only one run id called as TestRunID. If one features fails due to sync issue or data issue during regression then  we rerun the feature and if it pass the application should consider pass feature on that day. Also if we got to workflow and from there we should be able to drill down into features > scenarios > Steps > Action..etc like powerbi feature. i will tell other dashboard feature later but tell me that plan , best , optimised  approach. how it can be implemented and also will be correct to implement or keep it as it is i mean run in powerbi. Tell me also multiple approaches and which could be best approach and why.? will focus on Backend  implementation for now
