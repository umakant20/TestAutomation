using System.Text.Json;
using System.Text.Json.Serialization;

namespace PRImpactAnalyzer.McpServer;

/// <summary>
/// Config file shape — identical to the CLI's pr-impact-config.json so both tools can share
/// one config file if you want. Looked up, in order: path passed explicitly to a tool call,
/// then PR_IMPACT_CONFIG env var, then ./pr-impact-config.json in the server's working directory.
/// </summary>
public class PrImpactConfig
{
    [JsonPropertyName("pr")]             public string?       Pr             { get; set; }
    [JsonPropertyName("testRepoPath")]   public string?       TestRepoPath   { get; set; }
    [JsonPropertyName("devRepoPath")]    public string?       DevRepoPath    { get; set; }
    [JsonPropertyName("azureDevOpsPat")] public string?       AzureDevOpsPat { get; set; }
    [JsonPropertyName("promptOutput")]   public string?       PromptOutput   { get; set; }
    [JsonPropertyName("stateOutput")]    public string?       StateOutput    { get; set; }
    [JsonPropertyName("responseFiles")]  public List<string>? ResponseFiles  { get; set; }
    [JsonPropertyName("reportOutput")]   public string?       ReportOutput   { get; set; }
    [JsonPropertyName("failOnImpact")]   public bool          FailOnImpact   { get; set; }

    // prBaseUrl lets a single config work across multiple PR IDs — the MCP tool builds the
    // full PR URL by appending "/pullrequest/{prId}" to this, same convention as the old
    // VSIX extension used.
    [JsonPropertyName("prBaseUrl")]      public string?       PrBaseUrl      { get; set; }

    public static string ResolveConfigPath(string? explicitPath)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath)) return explicitPath;

        var envPath = Environment.GetEnvironmentVariable("PR_IMPACT_CONFIG");
        if (!string.IsNullOrWhiteSpace(envPath)) return envPath;

        return Path.Combine(Directory.GetCurrentDirectory(), "pr-impact-config.json");
    }

    public static PrImpactConfig Load(string? explicitPath = null)
    {
        var path = ResolveConfigPath(explicitPath);
        if (!File.Exists(path))
            throw new FileNotFoundException(
                $"Config file not found at '{path}'. Create it with 'pr', 'testRepoPath', and " +
                "'azureDevOpsPat' fields (or set azureDevOpsPat to null and use the ADO_PAT env var).");

        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<PrImpactConfig>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidOperationException($"Could not parse config file at '{path}'.");
    }
}
