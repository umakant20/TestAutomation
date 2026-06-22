using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using PRImpactAnalyzer.Core.Interfaces;

namespace PRImpactAnalyzer.Infrastructure.Http;

/// <summary>
/// Calls the GitHub Copilot chat completions API.
///
/// COPILOT API KEY SETUP:
///   1. Your GitHub Copilot subscription gives you access to the Copilot API.
///   2. Generate a token at: https://github.com/settings/tokens
///      Scopes needed: copilot (read)  — or use a GitHub App installation token.
///   3. Paste the token into the Web UI's "Copilot API Key" field or appsettings.json.
///   4. The default endpoint is https://api.githubcopilot.com/chat/completions
///      Override via AnalysisRequest.CopilotApiEndpoint if your org routes through a proxy.
/// </summary>
public class GitHubCopilotOrchestrator : ILlmOrchestrator
{
    private readonly ILogger<GitHubCopilotOrchestrator> _logger;
    private readonly string _apiKey;
    private readonly string _endpoint;
    private readonly string _model;

    public GitHubCopilotOrchestrator(
        string apiKey,
        string endpoint,
        string model,
        ILogger<GitHubCopilotOrchestrator> logger)
    {
        _apiKey = apiKey;
        _endpoint = endpoint;
        _model = model;
        _logger = logger;
    }

    public async Task<string> GetCompletionAsync(string prompt, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_apiKey))
            throw new InvalidOperationException(
                "GitHub Copilot API key is not set. " +
                "Enter it in the Web UI or add CopilotApiKey to appsettings.json.");

        using var http = new HttpClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        http.DefaultRequestHeaders.Add("Copilot-Integration-Id", "pr-test-impact-analyzer");

        var requestBody = new
        {
            model = _model,
            messages = new[]
            {
                new { role = "system", content = "You are an expert software test impact analyzer. Always respond with valid JSON only." },
                new { role = "user",   content = prompt }
            },
            temperature = 0.1,   // low temperature for deterministic, structured output
            max_tokens = 4096
        };

        var json = JsonSerializer.Serialize(requestBody);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        _logger.LogInformation("Calling Copilot API at {Endpoint} with model {Model}", _endpoint, _model);

        var response = await http.PostAsync(_endpoint, content, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException(
                $"Copilot API returned {(int)response.StatusCode} {response.ReasonPhrase}. " +
                $"Check your API key and endpoint. Details: {errorBody[..Math.Min(500, errorBody.Length)]}");
        }

        var responseJson = await response.Content.ReadAsStringAsync(cancellationToken);
        var doc = JsonDocument.Parse(responseJson);

        var text = doc.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString() ?? string.Empty;

        _logger.LogDebug("Copilot response length: {Len} chars", text.Length);
        return text;
    }
}
