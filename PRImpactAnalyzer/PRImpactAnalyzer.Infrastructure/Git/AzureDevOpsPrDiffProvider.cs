using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using PRImpactAnalyzer.Core.Interfaces;
using PRImpactAnalyzer.Core.Models;

namespace PRImpactAnalyzer.Infrastructure.Git;

/// <summary>
/// Fetches PR metadata and diff from Azure DevOps using a Personal Access Token.
/// Parses the browser PR URL — no manual org/project/repo entry required.
/// 
/// PAT SETUP:
///   1. Go to https://dev.azure.com/{yourorg} → User Settings → Personal Access Tokens
///   2. Create a token with scope: Code (Read)
///   3. Paste it into the Web UI's "Azure DevOps PAT" field or appsettings.json
/// </summary>
public class AzureDevOpsPrDiffProvider : IPrDiffProvider
{
    // Matches both old and new ADO PR URL formats:
    //   https://dev.azure.com/org/project/_git/repo/pullrequest/123
    //   https://org.visualstudio.com/project/_git/repo/pullrequest/123
    private static readonly Regex AdoPrUrlPattern = new(
        @"(?:dev\.azure\.com/(?<org>[^/]+)|(?<org2>[^.]+)\.visualstudio\.com)/(?<project>[^/]+)/_git/(?<repo>[^/]+)/pullrequest/(?<id>\d+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly ILogger<AzureDevOpsPrDiffProvider> _logger;

    public AzureDevOpsPrDiffProvider(ILogger<AzureDevOpsPrDiffProvider> logger)
    {
        _logger = logger;
    }

    public async Task<PrDiff> GetDiffAsync(AnalysisRequest request, CancellationToken cancellationToken = default)
    {
        var (orgUrl, project, repo, prId) = ParsePrUrl(request.DevRepoPrUrl);

        using var http = BuildHttpClient(request.AzureDevOpsPat);

        // 1. Fetch PR metadata
        var prMeta = await FetchPrMetadataAsync(http, orgUrl, project, repo, prId, cancellationToken);

        // 2. Fetch diff items (changed files)
        var diffItems = await FetchDiffItemsAsync(http, orgUrl, project, repo, prId, cancellationToken);

        // 3. For each changed file fetch the raw content changes as a unified diff
        var fileDiffs = new List<FileDiff>();
        foreach (var item in diffItems)
        {
            var fd = await FetchFileDiffAsync(http, orgUrl, project, repo, prMeta, item, cancellationToken);
            if (fd != null) fileDiffs.Add(fd);
        }

        return new PrDiff
        {
            Metadata = prMeta,
            Files = fileDiffs,
            RawDiffText = string.Join("\n", fileDiffs.SelectMany(f =>
                f.Hunks.SelectMany(h => h.Lines.Select(l =>
                    l.Type switch
                    {
                        DiffLineType.Added   => "+ " + l.Content,
                        DiffLineType.Removed => "- " + l.Content,
                        _                    => "  " + l.Content
                    }))))
        };
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private (string OrgUrl, string Project, string Repo, int PrId) ParsePrUrl(string url)
    {
        var m = AdoPrUrlPattern.Match(url);
        if (!m.Success)
            throw new ArgumentException(
                $"Cannot parse an Azure DevOps PR URL from '{url}'. " +
                "Expected: https://dev.azure.com/org/project/_git/repo/pullrequest/123");

        var org = m.Groups["org"].Success ? m.Groups["org"].Value : m.Groups["org2"].Value;
        var orgUrl = url.Contains("visualstudio.com")
            ? $"https://{org}.visualstudio.com"
            : $"https://dev.azure.com/{org}";

        return (orgUrl, m.Groups["project"].Value, m.Groups["repo"].Value, int.Parse(m.Groups["id"].Value));
    }

    private HttpClient BuildHttpClient(string pat)
    {
        var http = new HttpClient();
        // ADO uses Basic auth with an empty username and the PAT as password
        var encoded = Convert.ToBase64String(Encoding.ASCII.GetBytes($":{pat}"));
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", encoded);
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return http;
    }

    private async Task<PrMetadata> FetchPrMetadataAsync(
        HttpClient http, string orgUrl, string project, string repo, int prId,
        CancellationToken ct)
    {
        var url = $"{orgUrl}/{Uri.EscapeDataString(project)}/_apis/git/repositories/{Uri.EscapeDataString(repo)}/pullrequests/{prId}?api-version=7.1";
        _logger.LogDebug("GET {Url}", url);

        var response = await http.GetAsync(url, ct);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(ct);
        var doc = JsonDocument.Parse(json).RootElement;

        return new PrMetadata(
            Id: prId,
            Title: doc.GetProperty("title").GetString() ?? "",
            Description: doc.TryGetProperty("description", out var desc) ? desc.GetString() ?? "" : "",
            SourceBranch: doc.GetProperty("sourceRefName").GetString() ?? "",
            TargetBranch: doc.GetProperty("targetRefName").GetString() ?? "",
            Author: doc.GetProperty("createdBy").GetProperty("displayName").GetString() ?? ""
        );
    }

    private async Task<List<JsonElement>> FetchDiffItemsAsync(
        HttpClient http, string orgUrl, string project, string repo, int prId,
        CancellationToken ct)
    {
        // Get the list of changed files via the PR iterations endpoint
        var iterUrl = $"{orgUrl}/{Uri.EscapeDataString(project)}/_apis/git/repositories/{Uri.EscapeDataString(repo)}/pullrequests/{prId}/iterations?api-version=7.1";
        var iterResp = await http.GetAsync(iterUrl, ct);
        iterResp.EnsureSuccessStatusCode();

        var iterJson = JsonDocument.Parse(await iterResp.Content.ReadAsStringAsync(ct)).RootElement;
        var iterations = iterJson.GetProperty("value").EnumerateArray().ToList();
        if (!iterations.Any()) return new List<JsonElement>();

        var lastIter = iterations.Last().GetProperty("id").GetInt32();

        var changesUrl = $"{orgUrl}/{Uri.EscapeDataString(project)}/_apis/git/repositories/{Uri.EscapeDataString(repo)}/pullrequests/{prId}/iterations/{lastIter}/changes?api-version=7.1";
        var changesResp = await http.GetAsync(changesUrl, ct);
        changesResp.EnsureSuccessStatusCode();

        var changesJson = JsonDocument.Parse(await changesResp.Content.ReadAsStringAsync(ct)).RootElement;
        return changesJson.GetProperty("changeEntries").EnumerateArray().ToList();
    }

    private async Task<FileDiff?> FetchFileDiffAsync(
        HttpClient http, string orgUrl, string project, string repo,
        PrMetadata prMeta, JsonElement changeEntry, CancellationToken ct)
    {
        var item = changeEntry.GetProperty("item");
        var path = item.TryGetProperty("path", out var p) ? p.GetString() ?? "" : "";
        if (string.IsNullOrEmpty(path)) return null;

        var changeTypeRaw = changeEntry.TryGetProperty("changeType", out var ct2) ? ct2.GetString() ?? "" : "";
        var changeType = changeTypeRaw.ToLowerInvariant() switch
        {
            "add"    => FileChangeType.Added,
            "delete" => FileChangeType.Deleted,
            "rename" => FileChangeType.Renamed,
            _        => FileChangeType.Modified
        };

        // Fetch the new content at source branch tip and old at target branch tip
        var newContent = await FetchFileContentAsync(http, orgUrl, project, repo, path, prMeta.SourceBranch, ct);
        var oldContent = await FetchFileContentAsync(http, orgUrl, project, repo, path, prMeta.TargetBranch, ct);

        // Build a simple line-by-line diff in-process
        var hunks = BuildHunks(oldContent, newContent);

        return new FileDiff
        {
            FilePath = path.TrimStart('/'),
            ChangeType = changeType,
            Hunks = hunks
        };
    }

    private async Task<string> FetchFileContentAsync(
        HttpClient http, string orgUrl, string project, string repo,
        string path, string branch, CancellationToken ct)
    {
        try
        {
            var encodedPath = Uri.EscapeDataString(path);
            var encodedBranch = Uri.EscapeDataString(branch);
            var url = $"{orgUrl}/{Uri.EscapeDataString(project)}/_apis/git/repositories/{Uri.EscapeDataString(repo)}/items?path={encodedPath}&versionDescriptor.version={encodedBranch}&versionDescriptor.versionType=branch&api-version=7.1";
            var resp = await http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode) return string.Empty;
            return await resp.Content.ReadAsStringAsync(ct);
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// Produces a minimal unified diff structure by comparing old vs new file lines.
    /// Good enough for symbol extraction — no external diff library needed.
    /// </summary>
    private List<HunkDiff> BuildHunks(string oldContent, string newContent)
    {
        var oldLines = oldContent.Split('\n');
        var newLines = newContent.Split('\n');

        // Simple approach: record all removed lines from old and all added lines from new.
        // For symbol extraction purposes (we only care about +/- method signatures),
        // this is sufficient without implementing a full Myers diff algorithm.
        var hunk = new HunkDiff();

        var oldSet = new HashSet<string>(oldLines.Select(l => l.TrimEnd()));
        var newSet = new HashSet<string>(newLines.Select(l => l.TrimEnd()));

        foreach (var line in oldLines.Select(l => l.TrimEnd()))
        {
            if (!newSet.Contains(line) && !string.IsNullOrWhiteSpace(line))
                hunk.Lines.Add(new DiffLine { Content = line, Type = DiffLineType.Removed });
        }

        foreach (var line in newLines.Select(l => l.TrimEnd()))
        {
            if (!oldSet.Contains(line) && !string.IsNullOrWhiteSpace(line))
                hunk.Lines.Add(new DiffLine { Content = line, Type = DiffLineType.Added });
        }

        // Add a small context sample so the LLM has file/class context
        foreach (var line in newLines.Take(10).Select(l => l.TrimEnd()).Where(l => !string.IsNullOrWhiteSpace(l)))
        {
            if (!hunk.Lines.Any(x => x.Content == line))
                hunk.Lines.Add(new DiffLine { Content = line, Type = DiffLineType.Context });
        }

        return new List<HunkDiff> { hunk };
    }
}
