using PRImpactAnalyzer.Core.Interfaces;
using PRImpactAnalyzer.Core.Pipeline;
using PRImpactAnalyzer.Infrastructure.Git;
using PRImpactAnalyzer.Infrastructure.Http;
using PRImpactAnalyzer.Plugins.ColdFusion;
using PRImpactAnalyzer.Plugins.DotNet;
using PRImpactAnalyzer.Plugins.Soap;
using PRImpactAnalyzer.Plugins.SpecFlow;
using PRImpactAnalyzer.Web.Components;

var builder = WebApplication.CreateBuilder(args);

// ── Blazor Server ────────────────────────────────────────────────────────────
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// ── Infrastructure ───────────────────────────────────────────────────────────
builder.Services.AddSingleton<IPrDiffProvider, AzureDevOpsPrDiffProvider>();

// Manual Copilot bridge — holds per-session "waiting for pasted response" state.
// Scoped so each browser tab/session has its own independent paste workflow.
// NOTE: this is a UI-facing helper, not part of AnalysisPipeline's constructor —
// the pipeline now builds prompts directly and never calls any LLM endpoint itself.
builder.Services.AddScoped<ManualCopilotBridgeOrchestrator>();

// ── Plugins ──────────────────────────────────────────────────────────────────
builder.Services.AddSingleton<ICodeAnalyzer, SoapWsdlAnalyzer>();
builder.Services.AddSingleton<ICodeAnalyzer, DotNetAnalyzer>();
builder.Services.AddSingleton<ICodeAnalyzer, ColdFusionAnalyzer>();
builder.Services.AddSingleton<ITestParser,   SpecFlowParser>();

// ── Pipeline ─────────────────────────────────────────────────────────────────
builder.Services.AddSingleton<PromptBuilder>();
builder.Services.AddSingleton<LlmResponseParser>();
builder.Services.AddScoped<AnalysisPipeline>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseAntiforgery();

app.MapRazorComponents<App>()
   .AddInteractiveServerRenderMode();

app.Run();
