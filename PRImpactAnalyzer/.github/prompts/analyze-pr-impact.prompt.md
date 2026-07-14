---
mode: agent
description: Run the PR Test Impact Analyzer end-to-end for a given PR number
---

Run this multi-step task fully — every step is a real terminal/file action, not a chat reply.
Do not stop until Step 5 is delivered. No clarifying questions.

1. [run] `dotnet run --project ./PRImpactAnalyzer.Cli/PRImpactAnalyzer.Cli.csproj -- prepare pr-impact-config.json ${input:prId}`
   Note the "Run folder" path it prints. Stop and report if it errors.

2. [read] Open `prompt.txt` inside that run folder.

3. Analyze it per its own embedded format instructions. If it has multiple "CHUNK N OF M"
   sections, analyze all of them and merge into ONE JSON object.

4. [write] Create a real file `response.txt` inside that SAME run folder containing only
   your JSON (no fences, no prose). Verify the file exists before continuing.

5. [run] `dotnet run --project ./PRImpactAnalyzer.Cli/PRImpactAnalyzer.Cli.csproj -- report pr-impact-config.json`
   Then report: impacted count, HIGH/MEDIUM/VERIFY breakdown, and the report file path.

Analyze PR #${input:prId}.
