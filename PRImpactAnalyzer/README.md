# PR Test Impact Analyzer

Maps an Azure DevOps pull request's code changes (+ linked work items) to impacted C#
SpecFlow/Reqnroll test scenarios, then optionally runs exactly those scenarios.

## Quick start — one command per PR

1. Fill in `pr-impact-config.json` at the solution root.
2. Open the solution in VS 2022 17.14+, open Copilot Chat (Agent mode).
3. Type `/analyze-pr-impact`, enter the PR number when prompted.

This runs `prepare` → analyze → `report` → `execute`, all in one turn:
```
Reports/
  16773_20260714-153000/
    prompt.txt
    state.json
    response.txt
    impacted-tests.json
    pr-16773-impact-report.html
```

## Manual way

```bash
dotnet run --project PRImpactAnalyzer.Cli -- prepare pr-impact-config.json 16773
# paste prompt.txt into Copilot Chat, save reply as response.txt in the same run folder

dotnet run --project PRImpactAnalyzer.Cli -- report pr-impact-config.json
# writes the HTML report + impacted-tests.json manifest

dotnet run --project PRImpactAnalyzer.Cli -- execute pr-impact-config.json
# runs exactly the impacted scenarios via `dotnet test --filter`
```

## Test execution scope

Controlled by `testExecutionScope` in config:

| Value | Runs |
|---|---|
| `HighOnly` (default) | Only HIGH confidence scenarios — safest, smallest set |
| `HighAndMedium` | HIGH + MEDIUM |
| `All` | Everything in the report, including VERIFY |

Requires `testProjectPath` in config — the `.csproj` (or built `.dll`) of your Selenium/
Reqnroll/NUnit test project.

**How matching works:** Reqnroll generates one NUnit test method per scenario, named from
the scenario title. `execute` builds a `dotnet test --filter "FullyQualifiedName~..."`
expression per scenario using the `~` (contains) operator — tolerant of minor naming
variations, but not a byte-exact guarantee for every possible scenario title (Scenario
Outline examples in particular can get suffixed differently). Spot-check the filtered test
count against what you expect on first use with a given test project.

## Config reference (`pr-impact-config.json`)

| Field | Required | Purpose |
|---|---|---|
| `prBaseUrl` | Yes (or use `pr`) | e.g. `https://dev.azure.com/org/project/_git/repo` |
| `pr` | Alternative to `prBaseUrl` | A full, static PR URL |
| `testRepoPath` | Yes | Local path to your SpecFlow/Reqnroll `.feature` files |
| `azureDevOpsPat` | Yes (or `ADO_PAT` env var) | Azure DevOps PAT, Code (Read) + Work Items (Read) scope |
| `devRepoPath` | No | Optional extra context |
| `reportsBaseDir` | No | Default `Reports` |
| `failOnImpact` | No | `report` exits code 2 if any scenario impacted (CI gate) |
| `testProjectPath` | Yes, for `execute` | Path to your test `.csproj` or `.dll` |
| `testExecutionScope` | No | `HighOnly` (default) / `HighAndMedium` / `All` |

## Project structure

| Project | Role |
|---|---|
| `PRImpactAnalyzer.Core` | Engine: models, pipeline, prompt builder, response parser, HTML report writer |
| `PRImpactAnalyzer.Infrastructure` | Azure DevOps diff + work item provider, DI registration |
| `PRImpactAnalyzer.Plugins` | Code analyzers + SpecFlow/Reqnroll parser |
| `PRImpactAnalyzer.Cli` | `pr-impact` console tool — `prepare` / `report` / `execute` |
| `.github/prompts/analyze-pr-impact.prompt.md` | One-command Copilot Agent entry point |

## Report contents

- **Related Work Items** — linked User Stories/Bugs/Tasks with description, repro steps, tags
- **Summary → By Evidence Source** — how many scenarios were identified via Code Changes,
  Work Item Description, or Work Item Tag Match (a scenario can appear in more than one)
- **Impacted Scenarios** — sorted HIGH→MEDIUM→VERIFY, badged by evidence source, filterable
- **Changed Symbols**, **PR Diff**, **Code Change Snippets Sent to LLM** — full transparency
  into what evidence fed the analysis
- **Content fetch warnings** (if any) — flags files whose content couldn't be retrieved,
  which would otherwise silently degrade code-based matching
