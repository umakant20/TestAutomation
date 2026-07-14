using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using PRImpactAnalyzer.Core.Interfaces;
using PRImpactAnalyzer.Core.Models;

namespace PRImpactAnalyzer.Infrastructure.Git;

public class AzureDevOpsPrDiffProvider : IPrDiffProvider
{
    public async Task<PrDiff> GetDiffAsync(AnalysisRequest request, CancellationToken cancellationToken = default)
    {
        var (orgUrl, project, repo, prId) = ParsePrUrl(request.DevRepoPrUrl);
        using var http = CreateHttpClient(request.AzureDevOpsPat);

        var prMeta  = await FetchPrMetadataAsync(http, orgUrl, project, repo, prId, cancellationToken);
        var items   = await FetchChangedItemsAsync(http, orgUrl, project, repo, prId, cancellationToken);
        var workItems = await FetchLinkedWorkItemsAsync(http, orgUrl, project, repo, prId, cancellationToken);

        var fileDiffs = new List<FileDiff>();
        foreach (var item in items)
        {
            var fd = await FetchFileDiffAsync(http, orgUrl, project, repo, item, prMeta, cancellationToken);
            if (fd != null) fileDiffs.Add(fd);
        }

        return new PrDiff
        {
            Metadata = prMeta,
            Files = fileDiffs,
            LinkedWorkItems = workItems,
            RawDiffText = string.Join("\n\n", fileDiffs.Select(f =>
                $"=== {f.FilePath} ({f.ChangeType}) ===\n" +
                string.Join("\n", f.Hunks.SelectMany(h => h.Lines.Select(l =>
                    l.Type switch
                    {
                        DiffLineType.Added   => "+ " + l.Content,
                        DiffLineType.Removed => "- " + l.Content,
                        _                    => "  " + l.Content
                    })))))
        };
    }

    private (string OrgUrl, string Project, string Repo, int PrId) ParsePrUrl(string url)
    {
        var uri = new Uri(url);
        var segments = uri.AbsolutePath.TrimStart('/').Split('/');

        // dev.azure.com/{org}/{project}/_git/{repo}/pullrequest/{id}
        if (uri.Host.Equals("dev.azure.com", StringComparison.OrdinalIgnoreCase))
        {
            var org = segments[0]; var proj = segments[1]; var repoName = segments[3]; var id = int.Parse(segments[5]);
            return ($"https://dev.azure.com/{org}", proj, repoName, id);
        }

        // {org}.visualstudio.com/{project}/_git/{repo}/pullrequest/{id}
        var vsOrg = uri.Host.Split('.')[0];
        return ($"https://{vsOrg}.visualstudio.com", segments[0], segments[2], int.Parse(segments[4]));
    }

    private HttpClient CreateHttpClient(string pat)
    {
        var http = new HttpClient();
        var token = Convert.ToBase64String(Encoding.ASCII.GetBytes($":{pat}"));
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", token);
        return http;
    }

    private async Task<PrMetadata> FetchPrMetadataAsync(HttpClient http, string orgUrl, string project, string repo, int prId, CancellationToken ct)
    {
        var url = $"{orgUrl}/{project}/_apis/git/repositories/{repo}/pullrequests/{prId}?api-version=7.0";
        var resp = await http.GetStringAsync(url, ct);
        var doc = JsonDocument.Parse(resp);
        var root = doc.RootElement;

        return new PrMetadata(
            prId,
            root.GetProperty("title").GetString() ?? string.Empty,
            root.TryGetProperty("description", out var d) ? d.GetString() ?? string.Empty : string.Empty,
            root.GetProperty("sourceRefName").GetString()?.Replace("refs/heads/", "") ?? string.Empty,
            root.GetProperty("targetRefName").GetString()?.Replace("refs/heads/", "") ?? string.Empty,
            root.TryGetProperty("createdBy", out var cb) && cb.TryGetProperty("displayName", out var dn) ? dn.GetString() ?? string.Empty : string.Empty);
    }

    private async Task<List<JsonElement>> FetchChangedItemsAsync(HttpClient http, string orgUrl, string project, string repo, int prId, CancellationToken ct)
    {
        var iterUrl = $"{orgUrl}/{project}/_apis/git/repositories/{repo}/pullrequests/{prId}/iterations?api-version=7.0";
        var iterResp = await http.GetStringAsync(iterUrl, ct);
        var iterDoc = JsonDocument.Parse(iterResp);
        var iterations = iterDoc.RootElement.GetProperty("value").EnumerateArray().ToList();
        if (iterations.Count == 0) return new List<JsonElement>();

        var lastId = iterations.Last().GetProperty("id").GetInt32();
        var changesUrl = $"{orgUrl}/{project}/_apis/git/repositories/{repo}/pullrequests/{prId}/iterations/{lastId}/changes?api-version=7.0";
        var changesResp = await http.GetStringAsync(changesUrl, ct);
        var changesDoc = JsonDocument.Parse(changesResp);

        return changesDoc.RootElement.GetProperty("changeEntries").EnumerateArray()
            .Where(e => e.TryGetProperty("item", out var item) && item.TryGetProperty("isFolder", out var isFolder) && !isFolder.GetBoolean())
            .ToList();
    }

    private async Task<FileDiff?> FetchFileDiffAsync(HttpClient http, string orgUrl, string project, string repo, JsonElement item, PrMetadata prMeta, CancellationToken ct)
    {
        try
        {
            var path = item.GetProperty("item").GetProperty("path").GetString() ?? string.Empty;
            var changeTypeStr = item.TryGetProperty("changeType", out var ct2) ? ct2.GetString() ?? "edit" : "edit";
            var changeType = changeTypeStr.ToLower() switch
            {
                "add"    => FileChangeType.Added,
                "delete" => FileChangeType.Deleted,
                "rename" => FileChangeType.Renamed,
                _        => FileChangeType.Modified
            };

            var oldContent = await FetchFileContentAsync(http, orgUrl, project, repo, path, prMeta.TargetBranch, ct);
            var newContent = await FetchFileContentAsync(http, orgUrl, project, repo, path, prMeta.SourceBranch, ct);
            var hunks = BuildHunks(oldContent, newContent);

            return new FileDiff
            {
                FilePath = path.TrimStart('/'),
                ChangeType = changeType,
                Hunks = hunks,
                OldContent = oldContent,
                NewContent = newContent
            };
        }
        catch { return null; }
    }

    /// <summary>
    /// Task 1: fetches work items (User Story/Bug/Task) linked to this PR in Azure DevOps,
    /// pulling Title, Description, Repro Steps (Bugs), Acceptance Criteria, Tags, and a few
    /// recent discussion comments — used to enrich the analysis prompt and to force-prioritize
    /// feature-file scenarios that are traceable to the same work item (via tags).
    /// Failures here are non-fatal — an empty list is returned so PR analysis still proceeds
    /// even if the PR has no linked work items or the API call fails.
    /// </summary>
    private async Task<List<WorkItemInfo>> FetchLinkedWorkItemsAsync(HttpClient http, string orgUrl, string project, string repo, int prId, CancellationToken ct)
    {
        var result = new List<WorkItemInfo>();
        try
        {
            var linkUrl = $"{orgUrl}/{project}/_apis/git/repositories/{repo}/pullrequests/{prId}/workitems?api-version=7.0";
            var linkResp = await http.GetStringAsync(linkUrl, ct);
            var linkDoc = JsonDocument.Parse(linkResp);

            var ids = linkDoc.RootElement.TryGetProperty("value", out var vals)
                ? vals.EnumerateArray()
                    .Select(v => v.TryGetProperty("id", out var idProp) && int.TryParse(idProp.GetString(), out var id) ? id : 0)
                    .Where(id => id > 0)
                    .ToList()
                : new List<int>();

            foreach (var id in ids)
            {
                var wi = await FetchWorkItemDetailAsync(http, orgUrl, project, id, ct);
                if (wi != null) result.Add(wi);
            }
        }
        catch { /* non-fatal — proceed with no linked work items */ }

        return result;
    }

    private async Task<WorkItemInfo?> FetchWorkItemDetailAsync(HttpClient http, string orgUrl, string project, int id, CancellationToken ct)
    {
        try
        {
            var url = $"{orgUrl}/{project}/_apis/wit/workitems/{id}?$expand=all&api-version=7.0";
            var resp = await http.GetStringAsync(url, ct);
            var doc = JsonDocument.Parse(resp);
            var fields = doc.RootElement.GetProperty("fields");

            string GetField(string name) =>
                fields.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
                    ? StripHtml(v.GetString() ?? string.Empty)
                    : string.Empty;

            var tags = GetField("System.Tags");
            var tagList = tags.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

            var wi = new WorkItemInfo
            {
                Id                 = id,
                Type               = GetField("System.WorkItemType"),
                Title              = GetField("System.Title"),
                Description        = Truncate(GetField("System.Description"), 600),
                ReproSteps         = Truncate(GetField("Microsoft.VSTS.TCM.ReproSteps"), 600),
                AcceptanceCriteria = Truncate(GetField("Microsoft.VSTS.Common.AcceptanceCriteria"), 400),
                Tags               = tagList,
            };

            wi.DiscussionComments = await FetchWorkItemCommentsAsync(http, orgUrl, project, id, ct);
            return wi;
        }
        catch { return null; }
    }

    private async Task<List<string>> FetchWorkItemCommentsAsync(HttpClient http, string orgUrl, string project, int id, CancellationToken ct)
    {
        try
        {
            var url = $"{orgUrl}/{project}/_apis/wit/workItems/{id}/comments?api-version=7.0-preview.3";
            var resp = await http.GetStringAsync(url, ct);
            var doc = JsonDocument.Parse(resp);

            if (!doc.RootElement.TryGetProperty("comments", out var comments)) return new List<string>();

            // Cap to the most recent 3 comments, each truncated — comments are useful context
            // (e.g. "repro only happens when order has a hold") but must stay token-cheap.
            return comments.EnumerateArray()
                .Select(c => c.TryGetProperty("text", out var t) ? StripHtml(t.GetString() ?? string.Empty) : string.Empty)
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .TakeLast(3)
                .Select(t => Truncate(t, 250))
                .ToList();
        }
        catch { return new List<string>(); }
    }

    private static string StripHtml(string html) =>
        System.Text.RegularExpressions.Regex.Replace(html, "<[^>]+>", " ")
            .Replace("&nbsp;", " ").Replace("&amp;", "&").Replace("&lt;", "<").Replace("&gt;", ">")
            .Replace("\r\n", " ").Replace("\n", " ")
            .Trim();

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max] + "…";

    private async Task<string> FetchFileContentAsync(HttpClient http, string orgUrl, string project, string repo, string path, string branch, CancellationToken ct)
    {
        try
        {
            var url = $"{orgUrl}/{project}/_apis/git/repositories/{repo}/items?path={Uri.EscapeDataString(path)}&versionDescriptor.version={Uri.EscapeDataString(branch)}&versionDescriptor.versionType=branch&api-version=7.0";
            return await http.GetStringAsync(url, ct);
        }
        catch { return string.Empty; }
    }

    private List<HunkDiff> BuildHunks(string oldContent, string newContent)
    {
        var hunk = new HunkDiff();
        var oldLines = oldContent.Split('\n');
        var newLines = newContent.Split('\n');
        var maxLen = Math.Max(oldLines.Length, newLines.Length);

        for (int i = 0; i < maxLen; i++)
        {
            if (i >= oldLines.Length)
                hunk.Lines.Add(new DiffLine { Content = newLines[i], Type = DiffLineType.Added });
            else if (i >= newLines.Length)
                hunk.Lines.Add(new DiffLine { Content = oldLines[i], Type = DiffLineType.Removed });
            else if (oldLines[i] != newLines[i])
            {
                hunk.Lines.Add(new DiffLine { Content = oldLines[i], Type = DiffLineType.Removed });
                hunk.Lines.Add(new DiffLine { Content = newLines[i], Type = DiffLineType.Added });
            }
            else
                hunk.Lines.Add(new DiffLine { Content = oldLines[i], Type = DiffLineType.Context });
        }

        return new List<HunkDiff> { hunk };
    }
}
