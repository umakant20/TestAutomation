---
mode: agent
description: Run the PR Test Impact Analyzer end-to-end for a given PR number
---

You are running the PR Test Impact Analyzer end-to-end. Follow these steps in exact order.
Do not skip steps, do not ask clarifying questions, and confirm each step's output before
moving to the next.

STEP 1: Run this terminal command from the solution root:
pr-impact prepare pr-impact-config.json

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

STEP 6: Run this terminal command from the solution root:
pr-impact report pr-impact-config.json

STEP 7: Report back:
- Total impacted scenario count
- HIGH / MEDIUM / VERIFY breakdown
- Full path to the generated HTML report

Analyze PR #${input:prId}.