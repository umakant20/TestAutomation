using System.Text.Json;
using System.Text.Json.Serialization;

namespace PRImpactAnalyzer.Extension;

/// <summary>
/// Config file shape for the extension. Lives at ~/.pr-impact/config.json (user's home
/// directory) — set once, works for every PR. Only the PR number changes per invocation.
///
/// Example:
/// {
///   "prBaseUrl": "https://dev.azure.com/yourorg/yourproject/_git/yourrepo",
///   "testRepoPath": "C:\\source\\MyApp.Tests",
///   "azureDevOpsPat": "your-pat-here",
///   "reportOutputDir": "C:\\reports"
/// }
/// </summary>
public class ExtensionConfig
{
    [JsonPropertyName("prBaseUrl")]       public string? PrBaseUrl       { get; set; }
    [JsonPropertyName("testRepoPath")]    public string? TestRepoPath    { get; set; }
    [JsonPropertyName("azureDevOpsPat")]  public string? AzureDevOpsPat  { get; set; }
    [JsonPropertyName("reportOutputDir")] public string? ReportOutputDir { get; set; }

    public static string DefaultConfigPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".pr-impact", "config.json");

    public static ExtensionConfig Load(string? path = null)
    {
        path ??= DefaultConfigPath;
        if (!File.Exists(path))
            throw new FileNotFoundException(
                $"Config file not found at '{path}'.\n\n" +
                "Create it with this content:\n" +
                "{\n" +
                "  \"prBaseUrl\": \"https://dev.azure.com/yourorg/yourproject/_git/yourrepo\",\n" +
                "  \"testRepoPath\": \"C:\\\\source\\\\MyApp.Tests\",\n" +
                "  \"azureDevOpsPat\": \"your-pat-here\",\n" +
                "  \"reportOutputDir\": \"C:\\\\reports\"\n" +
                "}");

        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<ExtensionConfig>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidOperationException($"Could not parse config file at '{path}'.");
    }
}
