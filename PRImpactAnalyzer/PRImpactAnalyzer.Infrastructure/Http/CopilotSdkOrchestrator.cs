using GitHub.Copilot.SDK;
using Microsoft.Extensions.Logging;
using PRImpactAnalyzer.Core.Interfaces;

namespace PRImpactAnalyzer.Infrastructure.Http;

/// <summary>
/// Real, programmatic LLM orchestrator backed by the GitHub Copilot SDK.
///
/// This replaces the old ManualCopilotBridgeOrchestrator (which required a human to paste
/// the prompt into Copilot Chat and paste the JSON reply back). The Copilot SDK, generally
/// available since 2026, exposes the same agent runtime that powers the Copilot CLI as a
/// .NET library, so the impact analysis now runs fully headless — exactly what's needed for
/// an automation-framework / CI usage.
///
/// LIFECYCLE NOTES (per the SDK docs):
///   - CopilotClient is expensive: it spawns a Copilot CLI process. Create ONE per
///     application lifetime and reuse it. This class is therefore registered as a singleton
///     and starts the client lazily on first use.
///   - Sessions are cheap: one is created per GetCompletionAsync call (i.e. per prompt chunk)
///     and disposed immediately after, giving each chunk an isolated context — which also
///     matches the token-isolation guidance from earlier (no chunk re-bills another's tokens).
///   - OnPermissionRequest is REQUIRED. Since our prompt only asks the model to read provided
///     text and return JSON — it never needs file writes, shell, or network tools — we deny
///     all tool calls rather than approve them. The model doesn't need any tools to answer,
///     and denying is the safe default for an automated context.
///
/// AUTH: uses your existing GitHub Copilot subscription via the credentials the Copilot CLI
/// already has (from `copilot login` / `gh auth login`). No API key is stored in this app.
/// </summary>
public sealed class CopilotSdkOrchestrator : ILlmOrchestrator, IAsyncDisposable
{
    private readonly ILogger<CopilotSdkOrchestrator> _logger;
    private readonly string _model;
    private readonly SemaphoreSlim _startLock = new(1, 1);
    private CopilotClient? _client;
    private bool _started;

    public CopilotSdkOrchestrator(ILogger<CopilotSdkOrchestrator> logger, string model = "claude-haiku-4.5")
    {
        _logger = logger;
        _model = model;
    }

    private async Task EnsureStartedAsync(CancellationToken ct)
    {
        if (_started) return;
        await _startLock.WaitAsync(ct);
        try
        {
            if (_started) return;
            _logger.LogInformation("Starting Copilot SDK client (spawns the bundled Copilot CLI process)…");
            _client = new CopilotClient();
            await _client.StartAsync();
            _started = true;
            _logger.LogInformation("Copilot SDK client started.");
        }
        finally
        {
            _startLock.Release();
        }
    }

    public async Task<string> GetCompletionAsync(string prompt, CancellationToken cancellationToken = default)
    {
        await EnsureStartedAsync(cancellationToken);

        // One session per prompt chunk — cheap, and gives each chunk an isolated context.
        await using var session = await _client!.CreateSessionAsync(new SessionConfig
        {
            Model = _model,
            // The analysis prompt never needs tools — it's pure read-the-text-and-return-JSON.
            // PermissionDecision is a discriminated union (factory methods, not a plain enum);
            // Reject(...) is the correct call to deny every tool request. The model doesn't
            // need any tool to answer our prompt, so this is a safe no-op in practice.
            OnPermissionRequest = (request, invocation) =>
                Task.FromResult(PermissionDecision.Reject("This automated analysis run does not grant any tool permissions.")),
        });

        var responseBuilder = new System.Text.StringBuilder();
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        session.On(evt =>
        {
            switch (evt)
            {
                case AssistantMessageEvent msg:
                    responseBuilder.Append(msg.Data.Content);
                    break;
                case SessionIdleEvent:
                    done.TrySetResult(true);
                    break;
                case SessionErrorEvent err:
                    _logger.LogError("Copilot session error: {Message}", err.Data.Message);
                    done.TrySetException(new InvalidOperationException($"Copilot session error: {err.Data.Message}"));
                    break;
            }
        });

        _logger.LogInformation("Sending prompt to Copilot ({Length} chars, model {Model})…", prompt.Length, _model);
        await session.SendAsync(new MessageOptions { Prompt = prompt });

        // Guard against a session that never goes idle (e.g. silent runtime stall).
        using (cancellationToken.Register(() => done.TrySetCanceled(cancellationToken)))
        {
            var completed = await Task.WhenAny(done.Task, Task.Delay(TimeSpan.FromMinutes(3), cancellationToken));
            if (completed != done.Task)
                throw new TimeoutException("Copilot did not return a completed response within 3 minutes.");
            await done.Task; // surface any exception captured above
        }

        var raw = responseBuilder.ToString();
        _logger.LogInformation("Received Copilot response ({Length} chars).", raw.Length);
        return raw;
    }

    public async ValueTask DisposeAsync()
    {
        if (_client is not null)
            await _client.DisposeAsync();
        _startLock.Dispose();
    }
}
