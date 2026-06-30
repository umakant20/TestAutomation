using System.Net;
using System.Text;
using PRImpactAnalyzer.Core.Models;

namespace PRImpactAnalyzer.Core.Pipeline;

/// <summary>
/// Renders an <see cref="AnalysisResult"/> to a self-contained, styled HTML report file.
/// One report is written per run so you can review everything together: PR metadata/id,
/// the raw diff, the changed symbols, every prompt sent to Copilot and the raw response it
/// returned, and the final parsed impacted-scenario table.
///
/// The output is a single .html file with inline CSS (no external assets), so it opens
/// anywhere and can be archived as a CI artifact.
/// </summary>
public static class HtmlReportWriter
{
    /// <summary>
    /// Writes the report to <paramref name="outputPath"/> (or an auto-named file in the
    /// current directory if null) and returns the full path written.
    /// </summary>
    public static string Write(AnalysisResult result, string? outputPath = null)
    {
        outputPath ??= Path.Combine(
            Directory.GetCurrentDirectory(),
            $"pr-impact-report-{DateTime.Now:yyyyMMdd-HHmmss}.html");

        var html = Render(result);
        File.WriteAllText(outputPath, html, Encoding.UTF8);
        return outputPath;
    }

    private static string Render(AnalysisResult r)
    {
        var sb = new StringBuilder();
        var pr = r.PrMetadata;

        sb.Append($@"<!DOCTYPE html>
<html lang=""en"">
<head>
<meta charset=""utf-8"">
<meta name=""viewport"" content=""width=device-width, initial-scale=1"">
<title>PR Test Impact Report</title>
<style>
  :root {{
    --bg:#0f1117; --card:#1a1d27; --card2:#212530; --border:#2c3140;
    --text:#e6e9ef; --muted:#9aa3b4; --accent:#5b8def;
    --high:#3fb950; --medium:#d29922; --verify:#8b949e;
    --add:#3fb950; --del:#f85149; --mono:'Cascadia Code','Consolas',monospace;
  }}
  * {{ box-sizing:border-box; }}
  body {{ margin:0; background:var(--bg); color:var(--text);
         font-family:-apple-system,Segoe UI,Roboto,Helvetica,Arial,sans-serif; line-height:1.5; }}
  .wrap {{ max-width:1100px; margin:0 auto; padding:32px 24px 80px; }}
  h1 {{ font-size:26px; margin:0 0 4px; }}
  h2 {{ font-size:18px; margin:32px 0 12px; padding-bottom:8px; border-bottom:1px solid var(--border); }}
  .sub {{ color:var(--muted); font-size:13px; margin-bottom:24px; }}
  .meta-grid {{ display:grid; grid-template-columns:repeat(auto-fit,minmax(200px,1fr)); gap:12px; margin-bottom:8px; }}
  .meta-card {{ background:var(--card); border:1px solid var(--border); border-radius:8px; padding:14px 16px; }}
  .meta-card .label {{ color:var(--muted); font-size:11px; text-transform:uppercase; letter-spacing:.04em; }}
  .meta-card .value {{ font-size:20px; font-weight:600; margin-top:4px; }}
  .value.high {{ color:var(--high); }} .value.medium {{ color:var(--medium); }} .value.verify {{ color:var(--verify); }}
  table {{ width:100%; border-collapse:collapse; background:var(--card); border-radius:8px; overflow:hidden; font-size:13px; }}
  th {{ background:var(--card2); text-align:left; padding:10px 12px; font-size:12px; color:var(--muted);
        text-transform:uppercase; letter-spacing:.03em; }}
  td {{ padding:10px 12px; border-top:1px solid var(--border); vertical-align:top; }}
  .badge {{ display:inline-block; padding:2px 9px; border-radius:11px; font-size:11px; font-weight:600; }}
  .badge.high {{ background:rgba(63,185,80,.16); color:var(--high); }}
  .badge.medium {{ background:rgba(210,153,34,.16); color:var(--medium); }}
  .badge.verify {{ background:rgba(139,148,158,.16); color:var(--verify); }}
  code, pre {{ font-family:var(--mono); font-size:12px; }}
  pre {{ background:var(--card); border:1px solid var(--border); border-radius:8px; padding:16px;
         overflow:auto; max-height:480px; white-space:pre-wrap; word-break:break-word; }}
  .diff .add {{ color:var(--add); }} .diff .del {{ color:var(--del); }} .diff .ctx {{ color:var(--muted); }}
  details {{ background:var(--card); border:1px solid var(--border); border-radius:8px; margin-bottom:12px; }}
  summary {{ cursor:pointer; padding:12px 16px; font-weight:600; font-size:14px; }}
  details[open] summary {{ border-bottom:1px solid var(--border); }}
  .details-body {{ padding:16px; }}
  .chunk-meta {{ color:var(--muted); font-size:12px; margin-bottom:10px; }}
  .empty {{ color:var(--muted); padding:16px; text-align:center; }}
  .err {{ background:rgba(248,81,73,.12); border:1px solid var(--del); color:#ffb4ae;
          border-radius:8px; padding:14px 16px; margin-bottom:16px; }}
  .file-tag {{ color:#79c0ff; }}
</style>
</head>
<body>
<div class=""wrap"">
  <h1>PR Test Impact Report</h1>
  <div class=""sub"">Generated {WebUtility.HtmlEncode(r.AnalyzedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"))}</div>
");

        if (!r.Success)
            sb.Append($@"  <div class=""err""><strong>Analysis failed:</strong> {WebUtility.HtmlEncode(r.ErrorMessage ?? "Unknown error")}</div>
");
        else if (!string.IsNullOrWhiteSpace(r.ErrorMessage))
            sb.Append($@"  <div class=""err"" style=""background:rgba(210,153,34,.12);border-color:var(--medium);color:#f0d58a;"">{WebUtility.HtmlEncode(r.ErrorMessage)}</div>
");

        // ── PR metadata ──────────────────────────────────────────────────────
        sb.Append(@"  <h2>Pull Request</h2>
  <div class=""meta-grid"">
");
        AppendMetaCard(sb, "PR ID", pr?.Id.ToString() ?? "—");
        AppendMetaCard(sb, "Title", pr?.Title ?? "—");
        AppendMetaCard(sb, "Source → Target", pr is null ? "—" : $"{pr.SourceBranch} → {pr.TargetBranch}");
        sb.Append("  </div>\n");
        if (!string.IsNullOrWhiteSpace(pr?.Description))
            sb.Append($@"  <div class=""meta-card"" style=""margin-top:12px""><div class=""label"">Description</div><div style=""margin-top:6px"">{WebUtility.HtmlEncode(pr!.Description)}</div></div>
");

        // ── Summary counts ───────────────────────────────────────────────────
        sb.Append(@"  <h2>Summary</h2>
  <div class=""meta-grid"">
");
        AppendMetaCard(sb, "Impacted Scenarios", r.ImpactedScenarios.Count.ToString());
        AppendMetaCard(sb, "HIGH", r.ImpactedScenarios.Count(s => s.Confidence == ConfidenceLevel.High).ToString(), "high");
        AppendMetaCard(sb, "MEDIUM", r.ImpactedScenarios.Count(s => s.Confidence == ConfidenceLevel.Medium).ToString(), "medium");
        AppendMetaCard(sb, "VERIFY", r.ImpactedScenarios.Count(s => s.Confidence == ConfidenceLevel.Verify).ToString(), "verify");
        AppendMetaCard(sb, "Scenarios Scanned", r.AllScenarioCount.ToString());
        AppendMetaCard(sb, "Changed Symbols", r.ChangedSymbols.Count.ToString());
        sb.Append("  </div>\n");

        // ── Impacted scenarios table ─────────────────────────────────────────
        sb.Append("  <h2>Impacted Scenarios</h2>\n");
        if (r.ImpactedScenarios.Count == 0)
        {
            sb.Append(@"  <div class=""empty"">No impacted scenarios found.</div>
");
        }
        else
        {
            sb.Append(@"  <table>
    <thead><tr><th>#</th><th>Scenario</th><th>Feature File</th><th>Matched Change</th><th>Confidence</th><th>Why Impacted</th></tr></thead>
    <tbody>
");
            int idx = 1;
            foreach (var s in r.ImpactedScenarios)
            {
                var c = s.Confidence.ToString().ToLower();
                sb.Append($@"      <tr>
        <td>{idx++}</td>
        <td><strong>{WebUtility.HtmlEncode(s.ScenarioName)}</strong></td>
        <td><code>{WebUtility.HtmlEncode(s.FeatureFile)}</code></td>
        <td>{WebUtility.HtmlEncode(s.MatchedChange)}</td>
        <td><span class=""badge {c}"">{s.Confidence.ToString().ToUpper()}</span></td>
        <td>{WebUtility.HtmlEncode(s.Reason)}</td>
      </tr>
");
            }
            sb.Append("    </tbody>\n  </table>\n");
        }

        // ── Changed symbols ──────────────────────────────────────────────────
        sb.Append("  <h2>Changed Symbols</h2>\n");
        if (r.ChangedSymbols.Count == 0)
            sb.Append(@"  <div class=""empty"">No changed symbols extracted.</div>
");
        else
        {
            sb.Append(@"  <table>
    <thead><tr><th>File</th><th>Symbol</th><th>Kind</th><th>Change</th><th>Context</th></tr></thead>
    <tbody>
");
            foreach (var sym in r.ChangedSymbols)
                sb.Append($@"      <tr>
        <td><code>{WebUtility.HtmlEncode(sym.File)}</code></td>
        <td><strong>{WebUtility.HtmlEncode(sym.Symbol)}</strong>{(sym.OldSymbol != null ? " <span style=\"color:var(--muted)\">(was: " + WebUtility.HtmlEncode(sym.OldSymbol) + ")</span>" : "")}</td>
        <td>{sym.Kind}</td>
        <td>{sym.Change}</td>
        <td>{WebUtility.HtmlEncode(sym.AdditionalContext ?? "")}</td>
      </tr>
");
            sb.Append("    </tbody>\n  </table>\n");
        }

        // ── Raw diff ─────────────────────────────────────────────────────────
        sb.Append("  <h2>PR Diff</h2>\n");
        if (string.IsNullOrWhiteSpace(r.RawDiffText))
            sb.Append(@"  <div class=""empty"">No diff text captured.</div>
");
        else
            sb.Append($@"  <details><summary>View raw diff ({r.RawDiffText.Split('\n').Length} lines)</summary>
    <div class=""details-body""><pre class=""diff"">{RenderDiff(r.RawDiffText)}</pre></div>
  </details>
");

        // ── LLM exchanges ────────────────────────────────────────────────────
        sb.Append("  <h2>LLM Prompts &amp; Responses</h2>\n");
        if (r.LlmExchanges.Count == 0)
            sb.Append(@"  <div class=""empty"">No LLM exchanges recorded.</div>
");
        else
        {
            foreach (var ex in r.LlmExchanges)
            {
                sb.Append($@"  <details>
    <summary>Chunk {ex.ChunkIndex + 1} of {ex.TotalChunks} — {ex.ScenarioCount} scenarios sent, {ex.ParsedImpactedCount} impacted returned</summary>
    <div class=""details-body"">
      <div class=""chunk-meta"">Prompt sent to Copilot:</div>
      <pre>{WebUtility.HtmlEncode(ex.Prompt)}</pre>
      <div class=""chunk-meta"" style=""margin-top:14px"">Raw response from Copilot:</div>
      <pre>{WebUtility.HtmlEncode(ex.RawResponse)}</pre>
    </div>
  </details>
");
            }
        }

        sb.Append(@"</div>
</body>
</html>");
        return sb.ToString();
    }

    private static void AppendMetaCard(StringBuilder sb, string label, string value, string? valueClass = null)
    {
        var cls = valueClass != null ? $" {valueClass}" : "";
        sb.Append($@"    <div class=""meta-card""><div class=""label"">{WebUtility.HtmlEncode(label)}</div><div class=""value{cls}"">{WebUtility.HtmlEncode(value)}</div></div>
");
    }

    private static string RenderDiff(string diff)
    {
        var sb = new StringBuilder();
        foreach (var line in diff.Split('\n'))
        {
            var encoded = WebUtility.HtmlEncode(line);
            if (line.StartsWith("+")) sb.Append($"<span class=\"add\">{encoded}</span>\n");
            else if (line.StartsWith("-")) sb.Append($"<span class=\"del\">{encoded}</span>\n");
            else sb.Append($"<span class=\"ctx\">{encoded}</span>\n");
        }
        return sb.ToString();
    }
}
