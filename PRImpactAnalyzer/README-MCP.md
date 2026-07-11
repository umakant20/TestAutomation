# PR Test Impact Analyzer — MCP Server Setup

This replaces the earlier VSIX extension approach. Instead of a Tools-menu command, the
analyzer is now exposed to GitHub Copilot Chat's **Agent Mode** as MCP (Model Context
Protocol) tools — a documented, GA feature in Visual Studio 2022 17.14+.

## What you get

Type this in Copilot Chat (Agent mode):
```
Analyze test impact for PR 16773
```
Copilot will call your `pr-impact-mcp` server, receive the analysis prompt, reason over it
itself, produce the impacted-scenario JSON, call the server again to generate the HTML
report, and open it — all within one conversation turn (with a one-time tool-confirmation
click per tool call).

A second, independent Python MCP server (`pr-sentiment-mcp`) is also registered, so you can
additionally ask things like:
```
Also analyze the sentiment and urgency of this PR's description
```
and Copilot will pull in that server's tools alongside the .NET one.

## One-time setup

### 1. Enable Agent Mode in Visual Studio
**Tools → Options → GitHub → Copilot → Copilot Chat → check "Enable Agent mode in the chat pane."**

> If your Copilot subscription is provisioned through a company's Business/Enterprise plan,
> an admin may need to enable the **"MCP servers in Copilot"** policy first. If MCP is
> blocked by policy, Visual Studio will show an explicit error saying so — it won't fail
> silently.

### 2. Configure `pr-impact-config.json`
Edit the file at the solution root:
```json
{
  "prBaseUrl": "https://dev.azure.com/yourorg/yourproject/_git/yourrepo",
  "testRepoPath": "C:\\source\\MyApp.Tests",
  "azureDevOpsPat": "your-ado-pat-here",
  "promptOutput": "prompt.txt",
  "stateOutput": "state.json",
  "reportOutput": "report.html"
}
```
`azureDevOpsPat` can be left `null` if you set the `ADO_PAT` environment variable instead.

### 3. Build the .NET MCP server once
```
cd PRImpactAnalyzer.McpServer
dotnet build
```
(`.mcp.json` runs it with `--no-build` for faster startup, so build it explicitly whenever
you change the code.)

### 4. Install the Python MCP server's one dependency
```
cd pr-sentiment-mcp
pip install -r requirements.txt
```

### 5. Let Visual Studio detect `.mcp.json`
Open the solution — Visual Studio auto-detects `.mcp.json` at the solution root. Open
Copilot Chat, switch to **Agent** mode, click the **Tools** icon (wrench), and confirm both
`pr-impact-mcp` and `pr-sentiment-mcp` appear. **Tool checkboxes are unchecked by default —
enable the ones you want** (`prepare_pr_analysis`, `generate_impact_report`,
`analyze_pr_sentiment`, `analyze_workflow_complexity`).

## Usage

In Copilot Chat, Agent mode:
```
Analyze test impact for PR 16773
```
The first tool call each session will prompt for confirmation — you can set it to
auto-confirm for the session, the solution, or permanently, via the Confirm dropdown that
appears.

## Project structure

| Project | Role |
|---|---|
| `PRImpactAnalyzer.Core` | Engine — unchanged from all earlier work |
| `PRImpactAnalyzer.Infrastructure` | ADO diff provider, DI registration — unchanged |
| `PRImpactAnalyzer.Plugins` | Analyzers + SpecFlow parser — unchanged |
| `PRImpactAnalyzer.Cli` | Still usable standalone (`pr-impact prepare` / `report`) if you ever want to run outside Copilot |
| `PRImpactAnalyzer.McpServer` | **New.** Wraps the pipeline as two MCP tools: `prepare_pr_analysis`, `generate_impact_report` |
| `pr-sentiment-mcp` | **New, Python.** Independent MCP server: `analyze_pr_sentiment`, `analyze_workflow_complexity` |

## Extending the Python server

`pr-sentiment-mcp/server.py` uses simple lexicon-based heuristics deliberately, so it runs
instantly with no model download. To upgrade to real NLP:
- Swap `_score_sentiment` for a `transformers` sentiment-analysis pipeline, or
- Add a `spaCy` pipeline for deeper linguistic analysis (entities, dependency parsing, etc.)

The tool signatures Copilot already knows (`analyze_pr_sentiment`, `analyze_workflow_complexity`)
don't need to change — only the internals.

## The VSIX extension is gone

The earlier `PRImpactAnalyzer.Extension` (Tools-menu VSIX) project has been removed. MCP +
Agent Mode is the documented, supported mechanism for this use case and replaces it entirely.
