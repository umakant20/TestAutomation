# PR Test Impact Analyzer

Maps an Azure DevOps pull request's code changes to impacted C# SpecFlow/Reqnroll test
scenarios. LLM reasoning uses GitHub Copilot Chat — via a Visual Studio prompt file (Agent
mode's built-in terminal tool, no MCP required) or fully manually.

## Quick start — one command per PR

1. Fill in `pr-impact-config.json` at the solution root:
   ```json
   {
     "prBaseUrl": "https://dev.azure.com/yourorg/yourproject/_git/yourrepo",
     "testRepoPath": "C:\\source\\MyApp.Tests",
     "azureDevOpsPat": "your-ado-pat-here"
   }
   ```
2. Open the solution in VS 2022 17.14+, open Copilot Chat (Agent mode).
3. Type `/analyze-pr-impact`, enter the PR number when prompted.

Every run creates its own folder — nothing is ever overwritten:
```
Reports/
  16773_20260714-153000/
    prompt.txt
    state.json
    response.txt
    pr-16773-impact-report.html
  16774_20260715-091200/
    ...
```

## Manual way

```bash
dotnet run --project PRImpactAnalyzer.Cli -- prepare pr-impact-config.json 16773
# → prints the run folder, e.g. Reports/16773_20260714-153000/

# Paste prompt.txt (from that folder) into Copilot Chat, save its JSON reply as
# response.txt in that SAME folder (no config editing needed — auto-detected)

dotnet run --project PRImpactAnalyzer.Cli -- report pr-impact-config.json
# → finds the latest run automatically, writes pr-16773-impact-report.html into it
```

## Config reference (`pr-impact-config.json`)

| Field | Required | Purpose |
|---|---|---|
| `prBaseUrl` | Yes (or use `pr`) | e.g. `https://dev.azure.com/org/project/_git/repo` — combined with a PR number passed as the 3rd CLI arg |
| `pr` | Alternative to `prBaseUrl` | A full, static PR URL, for always analyzing one fixed PR |
| `testRepoPath` | Yes | Local path to your SpecFlow/Reqnroll test repo |
| `azureDevOpsPat` | Yes (or `ADO_PAT` env var) | Azure DevOps PAT, Code (Read) scope |
| `devRepoPath` | No | Optional extra context |
| `reportsBaseDir` | No | Default `Reports` — where dated run folders are created |
| `failOnImpact` | No | If true, `report` exits code 2 when any scenario is impacted (CI gate) |

## Project structure

| Project | Role |
|---|---|
| `PRImpactAnalyzer.Core` | Engine: models, pipeline, prompt builder, response parser, HTML report writer, run-folder management |
| `PRImpactAnalyzer.Infrastructure` | Azure DevOps diff provider, DI registration |
| `PRImpactAnalyzer.Plugins` | Code analyzers + SpecFlow/Reqnroll parser |
| `PRImpactAnalyzer.Cli` | `pr-impact` console tool — `prepare` / `report` |
| `.github/prompts/analyze-pr-impact.prompt.md` | One-command Copilot Agent entry point |
