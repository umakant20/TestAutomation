using PRImpactAnalyzer.Core.Interfaces;
using PRImpactAnalyzer.Core.Pipeline;
using PRImpactAnalyzer.Infrastructure.Git;
using PRImpactAnalyzer.Infrastructure.Http;
using PRImpactAnalyzer.Plugins.ColdFusion;
using PRImpactAnalyzer.Plugins.DotNet;
using PRImpactAnalyzer.Plugins.Soap;
using PRImpactAnalyzer.Plugins.SpecFlow;
using PRImpactAnalyzer.Web.Components;   // needed so App type resolves at line 57

var builder = WebApplication.CreateBuilder(args);

// ── Blazor Server ────────────────────────────────────────────────────────────
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// ── Configuration ────────────────────────────────────────────────────────────
var cfg = builder.Configuration.GetSection("PrImpactAnalyzer");

// ── Infrastructure ───────────────────────────────────────────────────────────
builder.Services.AddSingleton<IPrDiffProvider, AzureDevOpsPrDiffProvider>();

// ILlmOrchestrator is registered as a factory so it picks up the API key
// from appsettings / user-secrets at startup
builder.Services.AddTransient<ILlmOrchestrator>(sp =>
    new GitHubCopilotOrchestrator(
        apiKey:   cfg["CopilotApiKey"] ?? string.Empty,
        endpoint: cfg["CopilotApiEndpoint"] ?? "https://api.githubcopilot.com/chat/completions",
        model:    cfg["CopilotModel"] ?? "gpt-4o",
        logger:   sp.GetRequiredService<ILogger<GitHubCopilotOrchestrator>>()));

// ── Plugins ──────────────────────────────────────────────────────────────────
builder.Services.AddSingleton<ICodeAnalyzer, SoapWsdlAnalyzer>();
builder.Services.AddSingleton<ICodeAnalyzer, DotNetAnalyzer>();
builder.Services.AddSingleton<ICodeAnalyzer, ColdFusionAnalyzer>();
builder.Services.AddSingleton<ITestParser,   SpecFlowParser>();

// ── Pipeline ─────────────────────────────────────────────────────────────────
builder.Services.AddSingleton<PromptBuilder>();
builder.Services.AddSingleton<LlmResponseParser>();
builder.Services.AddTransient<AnalysisPipeline>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseAntiforgery();

// App is now resolvable via the `using PRImpactAnalyzer.Web.Components` above
app.MapRazorComponents<App>()
   .AddInteractiveServerRenderMode();

app.Run();
