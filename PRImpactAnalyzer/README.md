# PR Test Impact Analyzer

Maps an Azure DevOps pull request's code changes to the impacted C# SpecFlow/Reqnroll test
scenarios. The LLM reasoning step uses GitHub Copilot Chat — either manually (paste a prompt,
paste the reply back) or automatically via a Visual Studio **Copilot prompt file** that drives
the whole thing from one slash command, using Agent mode's built-in terminal tool (no MCP
required — works even if your org has MCP servers disabled).

## Quick start — the one-command way (recommended)

1. Fill in `pr-impact-config.json` at the solution root:
   ```json
   {
     "prBaseUrl": "https://dev.azure.com/yourorg/yourproject/_git/yourrepo",
     "testRepoPath": "C:\\source\\MyApp.Tests",
     "azureDevOpsPat": "your-ado-pat-here"
   }
   ```
2. Open the solution in Visual Studio 2022 17.14+, open Copilot Chat.
3. Type: `/analyze-pr-impact` — Visual Studio prompts you for a PR number.
4. Enter the PR number. Everything else — fetching the diff, analyzing, generating the
   report — runs automatically (with a one-time approval click per terminal command).

The prompt file lives at `.github/prompts/analyze-pr-impact.prompt.md` and is checked into
the repo, so it's shared with your whole team automatically.

## Manual way (no Copilot Agent mode needed)

```bash
# Step 1 — prepare: fetch diff, extract symbols, scan tests, build the prompt
dotnet run --project PRImpactAnalyzer.Cli -- prepare pr-impact-config.json 16773

# Step 2 — paste prompt.txt into Copilot Chat, save its JSON reply to response.txt,
# then add "response.txt" to responseFiles in pr-impact-config.json

# Step 3 — report: parse the JSON, generate the HTML report
dotnet run --project PRImpactAnalyzer.Cli -- report pr-impact-config.json
```

The `16773` in Step 1 is the PR number — combined with `prBaseUrl` in the config to build
the full PR URL. You can alternatively skip this argument and set a full, static `"pr"` URL
in the config instead if you're always analyzing the same PR (less common).

## Project structure

| Project | Role |
|---|---|
| `PRImpactAnalyzer.Core` | Engine: models, pipeline, prompt builder, response parser, HTML report writer |
| `PRImpactAnalyzer.Infrastructure` | Azure DevOps diff provider, DI registration |
| `PRImpactAnalyzer.Plugins` | Code analyzers (.NET/Roslyn, ColdFusion, SOAP, Node.js, Markup selectors, scoped XML config) + SpecFlow/Reqnroll parser |
| `PRImpactAnalyzer.Cli` | `pr-impact` console tool — `prepare` / `report` commands |
| `.github/prompts/analyze-pr-impact.prompt.md` | Reusable Copilot prompt file — the one-command entry point |

## Config reference (`pr-impact-config.json`)

| Field | Required | Purpose |
|---|---|---|
| `prBaseUrl` | Yes, if passing PR ID as an argument | e.g. `https://dev.azure.com/org/project/_git/repo` — combined with a PR number to build the full URL |
| `pr` | Alternative to `prBaseUrl` | A full, static PR URL — use only if you're not passing a PR ID dynamically |
| `testRepoPath` | Yes | Local path to your SpecFlow/Reqnroll test repo |
| `azureDevOpsPat` | Yes (or set `ADO_PAT` env var) | Azure DevOps PAT, Code (Read) scope |
| `devRepoPath` | No | Optional extra context |
| `promptOutput` / `stateOutput` | No | Default `prompt.txt` / `state.json` |
| `responseFiles` | Needed for `report` | List of files containing Copilot's pasted JSON reply — all chunk replies can go in ONE file |
| `reportOutput` | No | Default `pr-impact-report-<timestamp>.html` |
| `failOnImpact` | No | If true, `report` exits with code 2 when any scenario is impacted (useful as a CI gate) |
