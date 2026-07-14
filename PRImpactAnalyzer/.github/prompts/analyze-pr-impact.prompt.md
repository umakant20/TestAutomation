---
mode: agent
description: Run the PR Test Impact Analyzer end-to-end for a given PR number, then execute the impacted tests
---

Run this multi-step task fully — every step is a real terminal/file action, not a chat reply.
Do not stop until Step 6 is delivered. No clarifying questions.

1. [run] `dotnet run --project ./PRImpactAnalyzer.Cli/PRImpactAnalyzer.Cli.csproj -- prepare pr-impact-config.json ${input:prId}`
   Note the "Run folder" path it prints. Stop and report if it errors.

2. [read] Open `prompt.txt` inside that run folder.

3. Analyze it per its own embedded format instructions. If it has multiple "CHUNK N OF M"
   sections, analyze all of them and merge into ONE JSON object.

4. [write] Create a real file `response.txt` inside that SAME run folder containing only
   your JSON (no fences, no prose). Verify the file exists before continuing.

5. [run] `dotnet run --project ./PRImpactAnalyzer.Cli/PRImpactAnalyzer.Cli.csproj -- report pr-impact-config.json`
   Report: impacted count, HIGH/MEDIUM/VERIFY breakdown, and the report file path.

6. [run] `dotnet run --project ./PRImpactAnalyzer.Cli/PRImpactAnalyzer.Cli.csproj -- execute pr-impact-config.json`
   This runs exactly the impacted scenarios (scoped by testExecutionScope in the config)
   against the Selenium/Reqnroll/NUnit test project. Report the pass/fail summary.

Analyze PR #${input:prId}.
