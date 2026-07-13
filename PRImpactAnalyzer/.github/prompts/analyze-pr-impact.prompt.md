---
mode: agent
description: Run the PR Test Impact Analyzer end-to-end for a given PR number
---

You are running the PR Test Impact Analyzer end-to-end. Follow these steps in exact order.
Do not skip steps, do not ask clarifying questions, and confirm each step's output before
moving to the next.

STEP 0: Determine the solution root directory — the folder containing PRImpactAnalyzer.sln
and pr-impact-config.json. All following commands must be run FROM that directory (cd there
first if your terminal's current directory is anywhere else).

STEP 1: Run this terminal command from the solution root (relative paths only — do not
hardcode any absolute path or drive letter). The PR number is passed as the 3rd argument,
which the CLI combines with "prBaseUrl" already set in pr-impact-config.json to build the
full PR URL automatically — the config file itself never needs editing per PR:
dotnet run --project ./PRImpactAnalyzer.Cli/PRImpactAnalyzer.Cli.csproj -- prepare pr-impact-config.json ${input:prId}

Wait for it to complete. Confirm it created prompt.txt and state.json in the solution root.
If it reports a warning or error instead, stop and report that error — do not proceed.

STEP 2: Read the full contents of prompt.txt.

STEP 3: Analyze prompt.txt exactly as its own embedded instructions specify. It contains a
system-instruction section describing the required JSON output format. Follow that format
exactly. If prompt.txt contains multiple sections labeled "CHUNK 1 OF N", "CHUNK 2 OF N" etc.,
analyze ALL sections and produce ONE combined JSON object covering every section's scenarios.

STEP 4: Write your resulting JSON verdict to a new file named response.txt in the solution
root. Write ONLY the raw JSON — no markdown code fences, no explanation text before or after.

STEP 5: Open pr-impact-config.json and set the "responseFiles" field to exactly ["response.txt"]
(overwrite any existing value, don't append).

STEP 6: Run this terminal command from the solution root (no PR number needed here — the
state file from Step 1 already has everything):
dotnet run --project ./PRImpactAnalyzer.Cli/PRImpactAnalyzer.Cli.csproj -- report pr-impact-config.json

STEP 7: Report back:
- Total impacted scenario count
- HIGH / MEDIUM / VERIFY breakdown
- Full path to the generated HTML report

Analyze PR #${input:prId}.
