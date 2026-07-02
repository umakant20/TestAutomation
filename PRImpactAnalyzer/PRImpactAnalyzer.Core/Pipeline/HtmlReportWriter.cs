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

        sb.Append($@"<!DOCTYPE html>
<html lang=""en""><head><meta charset=""utf-8""><meta name=""viewport"" content=""width=device-width,initial-scale=1"">
<title>PR Test Impact Report</title>
<style>
:root{{--bg:#0f1117;--card:#1a1d27;--card2:#212530;--border:#2c3140;--text:#e6e9ef;--muted:#9aa3b4;
  --high:#3fb950;--medium:#d29922;--verify:#8b949e;--add:#3fb950;--del:#f85149;--mono:'Consolas',monospace;}}
*{{box-sizing:border-box;}}
body{{margin:0;background:var(--bg);color:var(--text);font-family:-apple-system,Segoe UI,Roboto,sans-serif;line-height:1.5;}}
.wrap{{max-width:1100px;margin:0 auto;padding:32px 24px 80px;}}
h1{{font-size:26px;margin:0 0 4px;}}
h2{{font-size:18px;margin:32px 0 12px;padding-bottom:8px;border-bottom:1px solid var(--border);}}
.sub{{color:var(--muted);font-size:13px;margin-bottom:24px;}}
.grid{{display:grid;grid-template-columns:repeat(auto-fit,minmax(170px,1fr));gap:12px;margin-bottom:8px;}}
.mc{{background:var(--card);border:1px solid var(--border);border-radius:8px;padding:14px 16px;}}
.mc .lbl{{color:var(--muted);font-size:11px;text-transform:uppercase;letter-spacing:.04em;}}
.mc .val{{font-size:22px;font-weight:600;margin-top:4px;}}
.val.high{{color:var(--high);}} .val.medium{{color:var(--medium);}} .val.verify{{color:var(--verify);}}
.badge{{display:inline-block;padding:2px 9px;border-radius:11px;font-size:11px;font-weight:600;}}
.badge.high{{background:rgba(63,185,80,.16);color:var(--high);}}
.badge.medium{{background:rgba(210,153,34,.16);color:var(--medium);}}
.badge.verify{{background:rgba(139,148,158,.16);color:var(--verify);}}
.sc-list{{display:flex;flex-direction:column;gap:10px;}}
.sc{{background:var(--card);border-radius:8px;padding:14px 16px;border-left:3px solid var(--border);}}
.sc.row-high{{border-left-color:var(--high);}} .sc.row-medium{{border-left-color:var(--medium);}} .sc.row-verify{{border-left-color:var(--verify);}}
.sc-hdr{{display:flex;align-items:center;gap:10px;margin-bottom:8px;}}
.sc-hdr strong{{flex:1;font-size:14px;}}
.sc-meta{{display:flex;gap:16px;flex-wrap:wrap;font-size:12px;color:var(--muted);margin-bottom:8px;}}
.sc-meta code{{font-family:var(--mono);font-size:11px;color:#79c0ff;background:#1a1d36;padding:1px 6px;border-radius:4px;}}
.sc-reason{{font-size:13px;background:rgba(255,255,255,.03);border-radius:6px;padding:8px 10px;}}
.sc-reason strong{{color:var(--muted);margin-right:6px;}}
.reason-missing{{color:var(--verify);font-style:italic;}}
table{{width:100%;border-collapse:collapse;background:var(--card);border-radius:8px;overflow:hidden;font-size:13px;}}
th{{background:var(--card2);text-align:left;padding:10px 12px;font-size:12px;color:var(--muted);text-transform:uppercase;letter-spacing:.03em;}}
td{{padding:10px 12px;border-top:1px solid var(--border);vertical-align:top;}}
code{{font-family:var(--mono);font-size:12px;}}
pre{{background:var(--card);border:1px solid var(--border);border-radius:8px;padding:16px;overflow:auto;max-height:480px;white-space:pre-wrap;word-break:break-word;font-family:var(--mono);font-size:12px;}}
.diff .add{{color:var(--add);}} .diff .del{{color:var(--del);}} .diff .ctx{{color:var(--muted);}}
details{{background:var(--card);border:1px solid var(--border);border-radius:8px;margin-bottom:12px;}}
summary{{cursor:pointer;padding:12px 16px;font-weight:600;font-size:14px;}}
details[open] summary{{border-bottom:1px solid var(--border);}}
.db{{padding:16px;}}
.cm{{color:var(--muted);font-size:12px;margin-bottom:10px;}}
.empty{{color:var(--muted);padding:16px;text-align:center;}}
.err{{background:rgba(248,81,73,.12);border:1px solid var(--del);color:#ffb4ae;border-radius:8px;padding:14px 16px;margin-bottom:16px;}}
.legend{{background:var(--card);border:1px solid var(--border);border-radius:8px;padding:12px 16px;margin-bottom:16px;font-size:12px;color:var(--muted);}}
.legend ul{{margin:8px 0 0;padding-left:18px;}} .legend li{{margin-bottom:6px;line-height:1.5;}}
</style></head><body><div class=""wrap"">
<h1>PR Test Impact Report</h1>
<div class=""sub"">Generated {E(r.AnalyzedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"))}</div>
");

        if (!r.Success)
            sb.Append($"<div class=\"err\"><strong>Analysis failed:</strong> {E(r.ErrorMessage ?? "Unknown error")}</div>\n");

        // PR metadata
        sb.Append("<h2>Pull Request</h2><div class=\"grid\">\n");
        MC(sb, "PR ID",            pr?.Id.ToString() ?? "—");
        MC(sb, "Title",            pr?.Title ?? "—");
        MC(sb, "Source → Target",  pr is null ? "—" : $"{pr.SourceBranch} → {pr.TargetBranch}");
        MC(sb, "Author",           pr?.Author ?? "—");
        sb.Append("</div>\n");
        if (!string.IsNullOrWhiteSpace(pr?.Description))
            sb.Append($"<div class=\"mc\" style=\"margin-top:12px\"><div class=\"lbl\">Description</div><div style=\"margin-top:6px\">{E(pr!.Description)}</div></div>\n");

        // Summary counts
        sb.Append("<h2>Summary</h2><div class=\"grid\">\n");
        MC(sb, "Impacted Scenarios", r.ImpactedScenarios.Count.ToString());
        MC(sb, "HIGH",    r.ImpactedScenarios.Count(s => s.Confidence == ConfidenceLevel.High).ToString(),   "high");
        MC(sb, "MEDIUM",  r.ImpactedScenarios.Count(s => s.Confidence == ConfidenceLevel.Medium).ToString(), "medium");
        MC(sb, "VERIFY",  r.ImpactedScenarios.Count(s => s.Confidence == ConfidenceLevel.Verify).ToString(), "verify");
        MC(sb, "Scenarios Scanned", r.AllScenarioCount.ToString());
        MC(sb, "Changed Symbols",   r.ChangedSymbols.Count.ToString());
        sb.Append("</div>\n");

        // Legend
        sb.Append(@"<div class=""legend""><strong>How to read the results:</strong><ul>
<li><strong>Matched Change</strong> — the specific changed symbol (method, route, ColdFusion page, selector) Copilot linked this scenario to.</li>
<li><strong>Confidence</strong> — <span class=""badge high"">HIGH</span> scenario's bound code directly references a changed symbol; <span class=""badge medium"">MEDIUM</span> same business behaviour but no direct code link; <span class=""badge verify"">VERIFY</span> plausible — manually confirm before relying on it.</li>
<li><strong>Why Impacted</strong> — Copilot's one-line explanation for this scenario being flagged.</li>
</ul></div>
");

        // Impacted scenarios
        sb.Append("<h2>Impacted Scenarios</h2>\n");
        if (r.ImpactedScenarios.Count == 0)
        {
            sb.Append("<div class=\"empty\">No impacted scenarios found.</div>\n");
        }
        else
        {
            sb.Append("<div class=\"sc-list\">\n");
            int idx = 1;
            foreach (var s in r.ImpactedScenarios)
            {
                var cl = s.Confidence.ToString().ToLower();
                var rc = $"row-{cl}";
                sb.Append($@"<div class=""sc {rc}"">
  <div class=""sc-hdr""><span style=""color:var(--muted);font-size:12px"">#{idx++}</span><strong>{E(s.ScenarioName)}</strong><span class=""badge {cl}"">{s.Confidence.ToString().ToUpper()}</span></div>
  <div class=""sc-meta""><span><strong>Feature:</strong> <code>{E(s.FeatureFile)}</code></span><span><strong>Matched change:</strong> {E(s.MatchedChange)}</span></div>
  <div class=""sc-reason""><strong>Why impacted:</strong> {(string.IsNullOrWhiteSpace(s.Reason) ? "<span class=\"reason-missing\">⚠️ Copilot did not provide a reason — treat as VERIFY.</span>" : E(s.Reason))}</div>
</div>
");
            }
            sb.Append("</div>\n");
        }

        // Changed symbols
        sb.Append("<h2>Changed Symbols</h2>\n");
        if (r.ChangedSymbols.Count == 0)
        {
            sb.Append("<div class=\"empty\">No changed symbols extracted.</div>\n");
        }
        else
        {
            sb.Append("<table><thead><tr><th>File</th><th>Symbol</th><th>Kind</th><th>Change</th><th>Context</th></tr></thead><tbody>\n");
            foreach (var sym in r.ChangedSymbols)
                sb.Append($"<tr><td><code>{E(sym.File)}</code></td><td><strong>{E(sym.Symbol)}</strong>{(sym.OldSymbol != null ? $" <span style=\"color:var(--muted)\">(was: {E(sym.OldSymbol)})</span>" : "")}</td><td>{sym.Kind}</td><td>{sym.Change}</td><td>{E(sym.AdditionalContext ?? "")}</td></tr>\n");
            sb.Append("</tbody></table>\n");
        }

        // PR diff
        sb.Append("<h2>PR Diff</h2>\n");
        if (string.IsNullOrWhiteSpace(r.RawDiffText))
            sb.Append("<div class=\"empty\">No diff text captured.</div>\n");
        else
            sb.Append($"<details><summary>View raw diff ({r.RawDiffText.Split('\n').Length} lines)</summary><div class=\"db\"><pre class=\"diff\">{RenderDiff(r.RawDiffText)}</pre></div></details>\n");

        // LLM exchanges
        sb.Append("<h2>LLM Prompts &amp; Responses</h2>\n");
        if (r.LlmExchanges.Count == 0)
        {
            sb.Append("<div class=\"empty\">No LLM exchanges recorded.</div>\n");
        }
        else
        {
            foreach (var ex in r.LlmExchanges)
                sb.Append($@"<details><summary>Chunk {ex.ChunkIndex + 1} of {ex.TotalChunks} — {ex.ScenarioCount} scenarios sent, {ex.ParsedImpactedCount} impacted returned</summary>
<div class=""db""><div class=""cm"">Prompt sent to Copilot:</div><pre>{E(ex.Prompt)}</pre>
<div class=""cm"" style=""margin-top:14px"">Raw response from Copilot:</div><pre>{E(ex.RawResponse)}</pre></div></details>
");
        }

        sb.Append("</div></body></html>");
        return sb.ToString();
    }

    private static void MC(StringBuilder sb, string label, string value, string? cls = null)
    {
        var c = cls != null ? $" {cls}" : "";
        sb.Append($"<div class=\"mc\"><div class=\"lbl\">{E(label)}</div><div class=\"val{c}\">{E(value)}</div></div>\n");
    }

    private static string RenderDiff(string diff)
    {
        var sb = new StringBuilder();
        foreach (var line in diff.Split('\n'))
        {
            var enc = WebUtility.HtmlEncode(line);
            if (line.StartsWith("+")) sb.Append($"<span class=\"add\">{enc}</span>\n");
            else if (line.StartsWith("-")) sb.Append($"<span class=\"del\">{enc}</span>\n");
            else sb.Append($"<span class=\"ctx\">{enc}</span>\n");
        }
        return sb.ToString();
    }

    private static string E(string? s) => WebUtility.HtmlEncode(s ?? string.Empty);
}
