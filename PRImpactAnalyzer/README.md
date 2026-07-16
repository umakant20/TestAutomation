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

**Build your test project first.** `execute` runs `dotnet test --no-build` — it does NOT
build your test project itself. Build it normally in Visual Studio (or via classic
`MSBuild.exe`) before running `execute`. This is intentional: if your test project (or any
of its dependencies) has a `<COMReference>` — common in older Selenium/automation projects —
the modern .NET SDK's MSBuild (which `dotnet build`/`dotnet test` uses by default) cannot
resolve it and fails with `"The task 'ResolveComReference' is not supported on the .NET Core
version of MSBuild."` Classic MSBuild.exe (bundled with Visual Studio) handles COM references
fine, so building there first and skipping the build in `execute` sidesteps this entirely.

**How matching works:** Reqnroll generates one NUnit test method per scenario, named from
the scenario title. `execute` builds a `dotnet test --filter "FullyQualifiedName~..."`
expression per scenario using the `~` (contains) operator — tolerant of minor naming
variations, but not a byte-exact guarantee for every possible scenario title (Scenario
Outline examples in particular can get suffixed differently). Spot-check the filtered test
count against what you expect on first use with a given test project.

## Triggering an Azure DevOps pipeline instead of local execution

If you already have a pipeline set up and a request body you've already validated works
(e.g. tested via Postman or curl), `trigger-pipeline` uses that EXACT body — no pipeline
YAML changes needed. You supply it as a template file with placeholder tokens; our code
substitutes the dynamically-identified impacted scenarios into it before sending.

**Step 1 — Copy your already-working request body into a template file.**

Create `pipeline-request-template.json` (path referenced by `pipelineRequestBodyTemplateFile`
in config) containing your exact known-working body, with tokens wherever the test list
should go. Example — adjust the key names to match whatever YOUR pipeline already expects:

```json
{
  "resources": {
    "repositories": {
      "self": { "refName": "refs/heads/main" }
    }
  },
  "templateParameters": {
    "testFilter": "{{TEST_FILTER}}"
  }
}
```

Two example templates are included:
- `pipeline-request-template.sample.json` — minimal example using `{{TEST_FILTER}}`
- `pipeline-request-template.bullet-list-example.json` — matches a payload shape with
  `stagesToSkip`, `templateParameters` (`EnvironmentToTest`, `TestNames`, etc.), and `variables`
  — using `{{FEATURE_NAMES_BULLET_LIST}}` for a `TestNames` field expecting one **feature file
  name** per line, dash-prefixed

Copy whichever fits closer, rename to `pipeline-request-template.json`, and edit the key names
to match your pipeline's actual parameters/variables (whatever you already use today — we
don't assume any particular shape).

**Available tokens** (use whichever fit your existing body's structure):

| Token | Expands to |
|---|---|
| `{{TEST_FILTER}}` | `dotnet test --filter` expression, e.g. `FullyQualifiedName~Scenario1|FullyQualifiedName~Scenario2` — put inside a quoted JSON string |
| `{{SCENARIO_NAMES_CSV}}` | Comma-separated scenario names as plain text — put inside a quoted JSON string |
| `{{SCENARIO_NAMES_JSON_ARRAY}}` | A real JSON array literal, e.g. `["Scenario1","Scenario2"]` — do NOT wrap in quotes, it expands to the array itself |
| `{{SCENARIO_NAMES_BULLET_LIST}}` | Dash-bulleted, newline-separated list, e.g. `"- Scenario1\n- Scenario2\n"` — put inside a quoted JSON string. Matches pipelines expecting a YAML-list-shaped string parameter (e.g. a `TestNames` field parsed as one name per line) |
| `{{FEATURE_NAMES_CSV}}` | Comma-separated, deduplicated **feature file names** (not scenario names, not full paths — just e.g. `CreateOrder.feature`) — put inside a quoted JSON string |
| `{{FEATURE_NAMES_JSON_ARRAY}}` | Deduplicated feature file names as a real JSON array, e.g. `["CreateOrder.feature","CancelOrder.feature"]` — do NOT wrap in quotes |
| `{{FEATURE_NAMES_BULLET_LIST}}` | Deduplicated feature file names as a dash-bulleted, newline-separated list, e.g. `"- CreateOrder.feature\n- CancelOrder.feature\n"` — put inside a quoted JSON string. Use this instead of the scenario-name equivalents when your pipeline/test runner executes at the feature-file level rather than the individual-scenario level (one feature file often contains several impacted scenarios, so this list is typically shorter and avoids re-running the same file multiple times) |
| `{{FEATURE_FILES_CSV}}` | Comma-separated feature file paths — put inside a quoted JSON string |
| `{{PR_ID}}` | The PR number — put inside or outside quotes depending on whether your field is a string or number |
| `{{SCENARIO_COUNT}}` | Count of scenarios being triggered |

**Step 2 — Config fields required:**

| Field | Purpose |
|---|---|
| `pipelineOrgUrl` | e.g. `https://dev.azure.com/yourorg` |
| `pipelineProject` | Project name containing the pipeline |
| `pipelineId` | Numeric ID of the pipeline (visible in its URL: `.../_build?definitionId=123`) |
| `pipelineRequestBodyTemplateFile` | Path to your template file from Step 1 |

`trigger-pipeline` reads the template, substitutes tokens, validates the result is still
well-formed JSON (catching a misplaced token locally instead of a confusing error from Azure
DevOps), then POSTs it to `{pipelineOrgUrl}/{pipelineProject}/_apis/pipelines/{pipelineId}/runs`
using your existing `azureDevOpsPat`. It prints the exact substituted body before sending, so
you can see precisely what went out.

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
| `pipelineOrgUrl` / `pipelineProject` / `pipelineId` | Yes, for `trigger-pipeline` | Identify which Azure DevOps pipeline to trigger |
| `pipelineRequestBodyTemplateFile` | Yes, for `trigger-pipeline` | Path to your own working request body template with placeholder tokens |

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
