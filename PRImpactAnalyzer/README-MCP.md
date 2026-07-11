# PR Test Impact Analyzer — MCP Server Setup

The analyzer is exposed to GitHub Copilot Chat's **Agent Mode** as an MCP (Model Context
Protocol) server — a documented, GA feature in Visual Studio 2022 17.14+.

## What you get

Type this in Copilot Chat (Agent mode):
```
Analyze test impact for PR 16773
```
Copilot will call your `pr-impact-mcp` server, receive the analysis prompt, reason over it
itself, produce the impacted-scenario JSON, call the server again to generate the HTML
report, and open it — all within one conversation turn (with a one-time tool-confirmation
click per tool call).

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

### 3. Build the MCP server once
```
cd PRImpactAnalyzer.McpServer
dotnet build
```
(`.mcp.json` runs it with `--no-build` for faster startup, so build it explicitly whenever
you change the code.)

### 4. Let Visual Studio detect `.mcp.json`
Open the solution — Visual Studio auto-detects `.mcp.json` at the solution root. Open
Copilot Chat, switch to **Agent** mode, click the **Tools** icon (wrench), and confirm
`pr-impact-mcp` appears. **Tool checkboxes are unchecked by default — enable them**
(`prepare_pr_analysis`, `generate_impact_report`).

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
| `PRImpactAnalyzer.McpServer` | Wraps the pipeline as two MCP tools: `prepare_pr_analysis`, `generate_impact_report` |

## The two MCP tools

- **`prepare_pr_analysis(prId)`** — runs the entire pipeline (diff fetch, symbol extraction,
  test scan, pre-filter, prompt build), returns the prompt text directly in the tool response
  so Copilot can read and reason over it in the same turn.
- **`generate_impact_report(analysisJson)`** — takes Copilot's own JSON verdict, reuses
  `FinalizeFromFilesAsync` unchanged, writes the HTML report, opens it in your browser.

The tool descriptions explicitly instruct Copilot to call `generate_impact_report`
immediately after producing its JSON verdict — that's what chains the two calls together
without you typing anything in between.
