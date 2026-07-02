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
