using System.Net;
using System.Text;
using PRImpactAnalyzer.Core.Models;

namespace PRImpactAnalyzer.Core.Pipeline;

public static class HtmlReportWriter
{
    public static string Write(AnalysisResult result, string? outputPath = null)
    {
        outputPath ??= Path.Combine(
            Directory.GetCurrentDirectory(),
            $"pr-impact-report-{DateTime.Now:yyyyMMdd-HHmmss}.html");
        File.WriteAllText(outputPath, Render(result), Encoding.UTF8);
        return outputPath;
    }

    private static string Render(AnalysisResult r)
    {
        var sb = new StringBuilder();
        var pr = r.PrMetadata;

        // Sort impacted: HIGH first, then MEDIUM, then VERIFY
        var sorted = r.ImpactedScenarios
            .OrderBy(s => s.Confidence switch { ConfidenceLevel.High => 0, ConfidenceLevel.Medium => 1, _ => 2 })
            .ThenBy(s => s.FeatureFile)
            .ToList();

        int countHigh   = sorted.Count(s => s.Confidence == ConfidenceLevel.High);
        int countMedium = sorted.Count(s => s.Confidence == ConfidenceLevel.Medium);
        int countVerify = sorted.Count(s => s.Confidence == ConfidenceLevel.Verify);

        sb.Append($@"<!DOCTYPE html>
<html lang=""en"">
<head>
<meta charset=""utf-8"">
<meta name=""viewport"" content=""width=device-width,initial-scale=1"">
<title>PR Test Impact Report</title>
<style>
/* ── Base ─────────────────────────────────────────────────────────────── */
:root{{
  --bg:#0d1117; --surface:#161b22; --surface2:#21262d; --border:#30363d;
  --text:#e6edf3; --muted:#8b949e; --mono:'Consolas','Courier New',monospace;
  --green:#2ea043; --green-bg:rgba(46,160,67,.12); --green-bd:rgba(46,160,67,.3);
  --amber:#d29922; --amber-bg:rgba(210,153,34,.12); --amber-bd:rgba(210,153,34,.3);
  --grey:#8b949e;  --grey-bg:rgba(139,148,158,.1);  --grey-bd:rgba(139,148,158,.25);
  --blue:#58a6ff;  --blue-bg:rgba(88,166,255,.1);   --blue-bd:rgba(88,166,255,.25);
  --del:#f85149;   --add:#3fb950;
}}
*{{box-sizing:border-box;margin:0;padding:0;}}
body{{background:var(--bg);color:var(--text);font-family:-apple-system,Segoe UI,Roboto,Helvetica,sans-serif;font-size:14px;line-height:1.5;}}
a{{color:var(--blue);}}

/* ── Layout ───────────────────────────────────────────────────────────── */
.wrap{{max-width:1280px;margin:0 auto;padding:28px 24px 80px;}}
.page-title{{font-size:22px;font-weight:700;margin-bottom:4px;}}
.page-sub{{color:var(--muted);font-size:12px;margin-bottom:28px;}}

/* ── Section headings ─────────────────────────────────────────────────── */
.section-head{{
  font-size:13px;font-weight:600;text-transform:uppercase;letter-spacing:.06em;
  color:var(--muted);padding-bottom:8px;border-bottom:1px solid var(--border);
  margin:28px 0 14px;
}}

/* ── PR metadata grid ─────────────────────────────────────────────────── */
.meta-grid{{
  display:flex;flex-wrap:wrap;gap:12px;
}}
.meta-grid .meta-card{{flex:1;min-width:120px;}}
.meta-card{{
  background:var(--surface);border:1px solid var(--border);border-radius:8px;
  padding:14px 16px;min-width:0;
}}
.meta-card .lbl{{
  font-size:10px;font-weight:600;text-transform:uppercase;letter-spacing:.06em;
  color:var(--muted);margin-bottom:6px;
}}
.meta-card .val{{
  font-size:14px;font-weight:600;line-height:1.4;
  word-break:break-word;overflow-wrap:anywhere;
}}
.meta-card .val.branch{{font-size:12px;font-family:var(--mono);font-weight:400;}}
.meta-desc{{
  background:var(--surface);border:1px solid var(--border);border-radius:8px;
  padding:12px 16px;margin-top:10px;color:var(--muted);font-size:13px;
}}

/* ── Summary stat cards ───────────────────────────────────────────────── */
.summary-grid{{
  display:flex;flex-wrap:wrap;gap:10px;margin-bottom:4px;
}}
.stat-card{{flex:1;min-width:120px;}}
.stat-card .lbl{{font-size:10px;font-weight:600;text-transform:uppercase;letter-spacing:.06em;margin-bottom:6px;}}
.stat-card .val{{font-size:26px;font-weight:700;line-height:1;}}

.stat-total{{border-color:var(--blue-bd);background:var(--blue-bg);}}
.stat-total .lbl{{color:var(--blue);}} .stat-total .val{{color:var(--blue);}}

.stat-high{{border-color:var(--green-bd);background:var(--green-bg);}}
.stat-high .lbl{{color:var(--green);}} .stat-high .val{{color:var(--green);}}

.stat-medium{{border-color:var(--amber-bd);background:var(--amber-bg);}}
.stat-medium .lbl{{color:var(--amber);}} .stat-medium .val{{color:var(--amber);}}

.stat-verify{{border-color:var(--grey-bd);background:var(--grey-bg);}}
.stat-verify .lbl{{color:var(--grey);}} .stat-verify .val{{color:var(--grey);}}

.stat-neutral{{border-color:var(--border);background:var(--surface2);}}
.stat-neutral .lbl{{color:var(--muted);}} .stat-neutral .val{{color:var(--text);}}

/* ── Filter bar ───────────────────────────────────────────────────────── */
.filter-bar{{display:flex;align-items:center;gap:8px;margin-bottom:14px;flex-wrap:wrap;}}
.filter-bar span{{font-size:12px;color:var(--muted);}}
.filter-btn{{
  border:1px solid var(--border);background:var(--surface2);color:var(--muted);
  border-radius:6px;padding:4px 12px;font-size:12px;font-weight:500;cursor:pointer;transition:.15s;
}}
.filter-btn:hover,.filter-btn.active{{background:var(--surface);color:var(--text);border-color:var(--blue);}}
.filter-btn.f-high.active{{border-color:var(--green);color:var(--green);background:var(--green-bg);}}
.filter-btn.f-medium.active{{border-color:var(--amber);color:var(--amber);background:var(--amber-bg);}}
.filter-btn.f-verify.active{{border-color:var(--grey);color:var(--grey);background:var(--grey-bg);}}

/* ── Impacted scenarios table ─────────────────────────────────────────── */
.tbl-wrap{{overflow-x:auto;border:1px solid var(--border);border-radius:8px;}}
table{{width:100%;border-collapse:collapse;font-size:13px;}}
thead th{{
  background:var(--surface2);text-align:left;
  padding:10px 12px;font-size:11px;font-weight:600;
  text-transform:uppercase;letter-spacing:.05em;color:var(--muted);
  white-space:nowrap;border-bottom:1px solid var(--border);
  position:sticky;top:0;
}}
tbody tr{{border-bottom:1px solid var(--border);transition:background .1s;}}
tbody tr:last-child{{border-bottom:none;}}
tbody tr:hover{{background:var(--surface2);}}
tbody tr.row-high{{border-left:3px solid var(--green);}}
tbody tr.row-medium{{border-left:3px solid var(--amber);}}
tbody tr.row-verify{{border-left:3px solid var(--grey-bd);}}
td{{padding:10px 12px;vertical-align:top;}}

/* Column widths */
.col-num{{width:40px;text-align:center;color:var(--muted);font-size:12px;}}
.col-scenario{{width:26%;min-width:180px;}}
.col-scenario strong{{display:block;font-weight:600;line-height:1.4;margin-bottom:2px;}}
.col-scenario .sc-tags{{display:flex;gap:4px;flex-wrap:wrap;margin-top:4px;}}
.col-file{{width:28%;min-width:160px;}}
.col-file code{{
  font-family:var(--mono);font-size:11px;color:#79c0ff;
  background:#1c2333;padding:2px 6px;border-radius:4px;
  word-break:break-all;display:inline-block;line-height:1.5;
}}
.col-match{{width:16%;min-width:120px;font-size:12px;color:var(--muted);word-break:break-word;}}
.col-conf{{width:80px;text-align:center;white-space:nowrap;}}
.col-why{{min-width:140px;font-size:12px;color:var(--muted);line-height:1.5;}}

/* ── Confidence badges ────────────────────────────────────────────────── */
.badge{{
  display:inline-block;padding:3px 10px;border-radius:12px;
  font-size:11px;font-weight:700;letter-spacing:.04em;
}}
.badge-high{{background:var(--green-bg);color:var(--green);border:1px solid var(--green-bd);}}
.badge-medium{{background:var(--amber-bg);color:var(--amber);border:1px solid var(--amber-bd);}}
.badge-verify{{background:var(--grey-bg);color:var(--grey);border:1px solid var(--grey-bd);}}
.badge-wi{{background:var(--blue-bg);color:var(--blue);border:1px solid var(--blue-bd);margin-left:6px;}}

/* ── Work items ───────────────────────────────────────────────────────── */
.wi-card{{
  background:var(--surface);border:1px solid var(--border);border-radius:8px;
  padding:12px 16px;margin-bottom:10px;
}}
.wi-card .wi-head{{display:flex;gap:8px;align-items:center;margin-bottom:6px;}}
.wi-card .wi-id{{color:var(--blue);font-weight:700;font-size:13px;}}
.wi-card .wi-type{{
  font-size:10px;font-weight:700;text-transform:uppercase;letter-spacing:.04em;
  background:var(--surface2);border:1px solid var(--border);border-radius:4px;padding:2px 6px;color:var(--muted);
}}
.wi-card .wi-title{{font-weight:600;font-size:13px;}}
.wi-card .wi-field{{font-size:12px;color:var(--muted);margin-top:4px;line-height:1.5;}}
.wi-card .wi-field strong{{color:var(--text);font-weight:600;}}

/* ── Legend ───────────────────────────────────────────────────────────── */
.legend{{
  background:var(--surface);border:1px solid var(--border);border-radius:8px;
  padding:12px 16px;margin-bottom:16px;font-size:12px;color:var(--muted);
}}
.legend ul{{list-style:none;display:flex;flex-direction:column;gap:4px;margin-top:6px;}}
.legend li{{line-height:1.5;}}

/* ── Changed symbols table ────────────────────────────────────────────── */
.sym-tbl td:first-child code{{color:#79c0ff;font-size:11px;}}
.sym-tbl td strong{{font-size:13px;}}
.sym-tbl .old-sym{{color:var(--muted);font-size:11px;}}
.change-badge{{
  display:inline-block;padding:1px 7px;border-radius:4px;font-size:10px;font-weight:600;
  background:var(--surface2);color:var(--muted);border:1px solid var(--border);
}}

/* ── Diff ─────────────────────────────────────────────────────────────── */
details{{border:1px solid var(--border);border-radius:8px;margin-bottom:12px;}}
summary{{
  cursor:pointer;padding:11px 16px;font-size:13px;font-weight:600;
  background:var(--surface);border-radius:8px;list-style:none;
  display:flex;align-items:center;gap:8px;
}}
summary::-webkit-details-marker{{display:none;}}
details[open] summary{{border-radius:8px 8px 0 0;border-bottom:1px solid var(--border);}}
.details-body{{padding:16px;background:var(--bg);border-radius:0 0 8px 8px;}}
pre{{
  font-family:var(--mono);font-size:12px;line-height:1.6;
  overflow:auto;max-height:440px;white-space:pre-wrap;word-break:break-word;
}}
.add{{color:var(--add);}} .del{{color:var(--del);}} .ctx{{color:var(--muted);}}
.cm-label{{font-size:11px;color:var(--muted);margin-bottom:8px;font-weight:500;}}

/* ── Empty / error ────────────────────────────────────────────────────── */
.empty{{text-align:center;padding:32px;color:var(--muted);font-size:13px;}}
.err-box{{
  background:rgba(248,81,73,.1);border:1px solid rgba(248,81,73,.3);
  color:#ffb4ae;border-radius:8px;padding:14px 16px;margin-bottom:16px;
}}
.no-reason{{color:var(--amber);font-style:italic;}}
</style>
</head>
<body>
<div class=""wrap"">

<div class=""page-title"">PR Test Impact Report</div>
<div class=""page-sub"">Generated {E(r.AnalyzedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"))}</div>
");

        // ── Error banner ─────────────────────────────────────────────────────────
        if (!r.Success)
            sb.Append($"<div class=\"err-box\"><strong>Analysis failed:</strong> {E(r.ErrorMessage ?? "Unknown error")}</div>\n");

        // ── Pull Request section ──────────────────────────────────────────────────
        sb.Append("<div class=\"section-head\">Pull Request</div>\n");
        sb.Append("<div class=\"meta-grid\">\n");
        sb.Append($"  <div class=\"meta-card\"><div class=\"lbl\">PR ID</div><div class=\"val\">{E(pr?.Id.ToString() ?? "—")}</div></div>\n");
        sb.Append($"  <div class=\"meta-card\"><div class=\"lbl\">Title</div><div class=\"val\">{E(pr?.Title ?? "—")}</div></div>\n");
        sb.Append($"  <div class=\"meta-card\"><div class=\"lbl\">Source → Target</div><div class=\"val branch\">{E(pr?.SourceBranch ?? "—")} → {E(pr?.TargetBranch ?? "—")}</div></div>\n");
        sb.Append("</div>\n");
        if (!string.IsNullOrWhiteSpace(pr?.Description))
            sb.Append($"<div class=\"meta-desc\">{E(pr!.Description)}</div>\n");

        // ── Task 1: Related Work Items ──────────────────────────────────────────────
        sb.Append("<div class=\"section-head\">Related Work Items</div>\n");
        if (r.LinkedWorkItems.Count == 0)
        {
            sb.Append("<div class=\"empty\">No work items linked to this PR.</div>\n");
        }
        else
        {
            sb.Append("<div class=\"legend\">These work items were pulled from Azure DevOps and used to enrich the analysis prompt. Scenarios tagged with a matching work item ID were automatically force-included at HIGH confidence (see the <span class=\"badge badge-wi\">WI #id</span> badge in the table below).</div>\n");
            foreach (var wi in r.LinkedWorkItems)
            {
                sb.Append($"<div class=\"wi-card\">\n");
                sb.Append($"  <div class=\"wi-head\"><span class=\"wi-id\">#{wi.Id}</span><span class=\"wi-type\">{E(wi.Type)}</span><span class=\"wi-title\">{E(wi.Title)}</span></div>\n");
                if (!string.IsNullOrWhiteSpace(wi.Description))
                    sb.Append($"  <div class=\"wi-field\"><strong>Description:</strong> {E(wi.Description)}</div>\n");
                if (!string.IsNullOrWhiteSpace(wi.ReproSteps))
                    sb.Append($"  <div class=\"wi-field\"><strong>Repro steps:</strong> {E(wi.ReproSteps)}</div>\n");
                if (!string.IsNullOrWhiteSpace(wi.AcceptanceCriteria))
                    sb.Append($"  <div class=\"wi-field\"><strong>Acceptance criteria:</strong> {E(wi.AcceptanceCriteria)}</div>\n");
                if (wi.Tags.Count > 0)
                    sb.Append($"  <div class=\"wi-field\"><strong>Tags:</strong> {E(string.Join(", ", wi.Tags))}</div>\n");
                if (wi.DiscussionComments.Count > 0)
                    sb.Append($"  <div class=\"wi-field\"><strong>Discussion:</strong> {E(string.Join(" — ", wi.DiscussionComments))}</div>\n");
                sb.Append("</div>\n");
            }
        }

        // ── Summary cards ─────────────────────────────────────────────────────────
        sb.Append("<div class=\"section-head\">Summary</div>\n");
        sb.Append("<div class=\"summary-grid\">\n");
        sb.Append($"  <div class=\"stat-card stat-total\"><div class=\"lbl\">Impacted</div><div class=\"val\">{sorted.Count}</div></div>\n");
        sb.Append($"  <div class=\"stat-card stat-high\"><div class=\"lbl\">High</div><div class=\"val\">{countHigh}</div></div>\n");
        sb.Append($"  <div class=\"stat-card stat-medium\"><div class=\"lbl\">Medium</div><div class=\"val\">{countMedium}</div></div>\n");
        sb.Append($"  <div class=\"stat-card stat-verify\"><div class=\"lbl\">Verify</div><div class=\"val\">{countVerify}</div></div>\n");
        sb.Append($"  <div class=\"stat-card stat-neutral\"><div class=\"lbl\">Scanned</div><div class=\"val\">{r.AllScenarioCount}</div></div>\n");
        sb.Append($"  <div class=\"stat-card stat-neutral\"><div class=\"lbl\">Symbols</div><div class=\"val\">{r.ChangedSymbols.Count}</div></div>\n");
        sb.Append("</div>\n");

        // ── Impacted scenarios ────────────────────────────────────────────────────
        sb.Append("<div class=\"section-head\">Impacted Scenarios</div>\n");

        if (sorted.Count == 0)
        {
            sb.Append("<div class=\"empty\">No impacted scenarios found for this PR.</div>\n");
        }
        else
        {
            // Legend
            sb.Append(@"<div class=""legend"">
  <strong>Confidence levels:</strong>
  <ul>
    <li><span class=""badge badge-high"">HIGH</span> — scenario's bound code directly references a changed symbol.</li>
    <li><span class=""badge badge-medium"">MEDIUM</span> — same business behaviour but no direct code-level link.</li>
    <li><span class=""badge badge-verify"">VERIFY</span> — plausible match — manually confirm before relying on it.</li>
  </ul>
</div>
");

            // Filter bar
            sb.Append(@"<div class=""filter-bar"">
  <span>Filter:</span>
  <button class=""filter-btn active"" onclick=""filterTable('ALL')"">All</button>
  <button class=""filter-btn f-high"" onclick=""filterTable('HIGH')"">HIGH</button>
  <button class=""filter-btn f-medium"" onclick=""filterTable('MEDIUM')"">MEDIUM</button>
  <button class=""filter-btn f-verify"" onclick=""filterTable('VERIFY')"">VERIFY</button>
</div>
");

            // Table — sorted HIGH → MEDIUM → VERIFY
            sb.Append("<div class=\"tbl-wrap\">\n<table id=\"results-table\">\n");
            sb.Append("<thead><tr><th class=\"col-num\">#</th><th class=\"col-scenario\">Scenario</th><th class=\"col-file\">Feature File</th><th class=\"col-match\">Matched Change</th><th class=\"col-conf\">Confidence</th><th class=\"col-why\">Why Impacted</th></tr></thead>\n");
            sb.Append("<tbody>\n");

            int idx = 1;
            foreach (var s in sorted)
            {
                var cl  = s.Confidence.ToString().ToLower();
                var rc  = $"row-{cl}";
                var bdg = $"badge-{cl}";
                var conf = s.Confidence.ToString().ToUpper();
                var reason = string.IsNullOrWhiteSpace(s.Reason)
                    ? "<span class=\"no-reason\">— no reason provided</span>"
                    : E(s.Reason);
                var wiBadge = s.MatchedWorkItemIds.Count > 0
                    ? $"<span class=\"badge badge-wi\">WI #{string.Join(", #", s.MatchedWorkItemIds)}</span>"
                    : "";

                sb.Append($@"<tr class=""{rc}"" data-conf=""{conf}"">
  <td class=""col-num"">{idx++}</td>
  <td class=""col-scenario""><strong>{E(s.ScenarioName)}</strong>{wiBadge}</td>
  <td class=""col-file""><code>{E(s.FeatureFile)}</code></td>
  <td class=""col-match"">{E(s.MatchedChange)}</td>
  <td class=""col-conf""><span class=""badge {bdg}"">{conf}</span></td>
  <td class=""col-why"">{reason}</td>
</tr>
");
            }
            sb.Append("</tbody>\n</table>\n</div>\n");
        }

        // ── Changed symbols ────────────────────────────────────────────────────────
        sb.Append("<div class=\"section-head\">Changed Symbols</div>\n");
        if (r.ChangedSymbols.Count == 0)
        {
            sb.Append("<div class=\"empty\">No changed symbols extracted.</div>\n");
        }
        else
        {
            sb.Append("<div class=\"tbl-wrap\">\n<table class=\"sym-tbl\">\n");
            sb.Append("<thead><tr><th style=\"width:28%\">File</th><th style=\"width:24%\">Symbol</th><th style=\"width:12%\">Kind</th><th style=\"width:10%\">Change</th><th>Context</th></tr></thead>\n<tbody>\n");
            foreach (var sym in r.ChangedSymbols)
            {
                var oldPart = sym.OldSymbol != null ? $" <span class=\"old-sym\">(was: {E(sym.OldSymbol)})</span>" : "";
                sb.Append($@"<tr>
  <td><code>{E(sym.File)}</code></td>
  <td><strong>{E(sym.Symbol)}</strong>{oldPart}</td>
  <td>{sym.Kind}</td>
  <td><span class=""change-badge"">{sym.Change}</span></td>
  <td style=""font-size:12px;color:var(--muted)"">{E(sym.AdditionalContext ?? "")}</td>
</tr>
");
            }
            sb.Append("</tbody>\n</table>\n</div>\n");
        }

        // ── PR Diff ────────────────────────────────────────────────────────────────
        sb.Append("<div class=\"section-head\">PR Diff</div>\n");
        if (string.IsNullOrWhiteSpace(r.RawDiffText))
            sb.Append("<div class=\"empty\">No diff text captured.</div>\n");
        else
            sb.Append($"<details><summary>▶ View raw diff <span style=\"color:var(--muted);font-weight:400;font-size:12px\">({r.RawDiffText.Split('\n').Length} lines)</span></summary><div class=\"details-body\"><pre>{RenderDiff(r.RawDiffText)}</pre></div></details>\n");

        // ── Task 2: Code snippets included in the analysis ────────────────────────
        sb.Append("<div class=\"section-head\">Code Change Snippets Sent to LLM</div>\n");
        if (string.IsNullOrWhiteSpace(r.CodeSnippetsIncluded))
            sb.Append("<div class=\"empty\">No code snippets were included (either no changes qualified, or this report predates this feature).</div>\n");
        else
            sb.Append($"<details><summary>▶ View code snippets <span style=\"color:var(--muted);font-weight:400;font-size:12px\">(actual +/- lines given to the LLM as extra context)</span></summary><div class=\"details-body\"><pre>{RenderDiff(r.CodeSnippetsIncluded)}</pre></div></details>\n");

        // ── LLM Exchanges ──────────────────────────────────────────────────────────
        sb.Append("<div class=\"section-head\">LLM Prompts &amp; Responses</div>\n");
        if (r.LlmExchanges.Count == 0)
            sb.Append("<div class=\"empty\">No LLM exchanges recorded.</div>\n");
        else
            foreach (var ex in r.LlmExchanges)
                sb.Append($@"<details>
<summary>▶ Chunk {ex.ChunkIndex + 1} of {ex.TotalChunks} <span style=""color:var(--muted);font-weight:400;font-size:12px"">— {ex.ScenarioCount} scenarios sent · {ex.ParsedImpactedCount} impacted returned</span></summary>
<div class=""details-body"">
  <div class=""cm-label"">Prompt sent to Copilot:</div>
  <pre>{E(ex.Prompt)}</pre>
  <div class=""cm-label"" style=""margin-top:14px"">Raw response from Copilot:</div>
  <pre>{E(ex.RawResponse)}</pre>
</div></details>
");

        // ── Filter JS ──────────────────────────────────────────────────────────────
        sb.Append(@"
<script>
function filterTable(conf) {
  document.querySelectorAll('.filter-btn').forEach(b => b.classList.remove('active'));
  event.target.classList.add('active');
  const rows = document.querySelectorAll('#results-table tbody tr');
  rows.forEach(row => {
    row.style.display = (conf === 'ALL' || row.dataset.conf === conf) ? '' : 'none';
  });
}
</script>
");
        sb.Append("</div></body></html>");
        return sb.ToString();
    }

    private static string RenderDiff(string diff)
    {
        var sb = new StringBuilder();
        foreach (var line in diff.Split('\n'))
        {
            var enc = WebUtility.HtmlEncode(line);
            if (line.StartsWith("+"))      sb.Append($"<span class=\"add\">{enc}</span>\n");
            else if (line.StartsWith("-")) sb.Append($"<span class=\"del\">{enc}</span>\n");
            else                           sb.Append($"<span class=\"ctx\">{enc}</span>\n");
        }
        return sb.ToString();
    }

    private static string E(string? s) => WebUtility.HtmlEncode(s ?? string.Empty);
}
