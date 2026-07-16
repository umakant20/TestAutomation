---
mode: agent
description: Run the PR Test Impact Analyzer end-to-end for a given PR number, then trigger the impacted tests in Azure DevOps
---

This is a SIX-STEP AUTONOMOUS TASK. You must physically perform every step using your tools
— running terminal commands and creating/editing real files on disk. Reporting a summary
after Step 5 is NOT the end of the task and does NOT satisfy this request — Step 6 is a
separate, mandatory, real terminal command that you must also execute before you are done.
Do not stop, do not ask clarifying questions, and do not treat any intermediate summary as
a final answer until Step 6 has actually run and its output has been reported.

1. [terminal command — must actually execute]:
   `dotnet run --project ./PRImpactAnalyzer.Cli/PRImpactAnalyzer.Cli.csproj -- prepare pr-impact-config.json ${input:prId}`
   Note the "Run folder" path it prints. Stop and report if it errors.

2. [file read]: Open `prompt.txt` inside that run folder.

3. [reasoning]: Analyze it per its own embedded format instructions. If it has multiple
   "CHUNK N OF M" sections, analyze all of them and merge into ONE JSON object.

4. [file WRITE — mandatory, this is a disk operation, not a chat reply]:
   Create a real file `response.txt` inside that SAME run folder containing only your JSON
   (no fences, no prose). Verify the file exists before continuing.

5. [terminal command — must actually execute]:
   `dotnet run --project ./PRImpactAnalyzer.Cli/PRImpactAnalyzer.Cli.csproj -- report pr-impact-config.json`
   Note its output. Do NOT present this as your final answer — Step 6 still remains.

6. [terminal command — MANDATORY, must actually execute, this is the actual deliverable of
   this task, not optional]:
   `dotnet run --project ./PRImpactAnalyzer.Cli/PRImpactAnalyzer.Cli.csproj -- trigger-pipeline pr-impact-config.json`
   This calls the Azure DevOps Pipelines REST API to trigger a remote pipeline run. You MUST
   run this command yourself right now, in this same turn, using your terminal tool — do not
   describe it, do not summarize what it would do, do not stop after Step 5's report and
   consider the task complete. Only after this command has actually executed should you give
   your final reply, reporting: impacted count, HIGH/MEDIUM/VERIFY breakdown, report file
   path, and the pipeline run ID/URL from Step 6's output.

Analyze PR #${input:prId}.
