using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PRImpactAnalyzer.Core.Interfaces;
using PRImpactAnalyzer.Core.Pipeline;
using PRImpactAnalyzer.Infrastructure.Git;
using PRImpactAnalyzer.Infrastructure.Http;
using PRImpactAnalyzer.Plugins.ColdFusion;
using PRImpactAnalyzer.Plugins.Config;
using PRImpactAnalyzer.Plugins.DotNet;
using PRImpactAnalyzer.Plugins.Markup;
using PRImpactAnalyzer.Plugins.NodeJs;
using PRImpactAnalyzer.Plugins.Soap;
using PRImpactAnalyzer.Plugins.SpecFlow;

namespace PRImpactAnalyzer.Infrastructure;

/// <summary>
/// One-stop DI registration for library/CI/CLI consumers. Registers every analyzer, the
/// test parser, the Azure DevOps diff provider, the Copilot SDK orchestrator, and the pipeline.
///
/// Usage with the facade:
///   var analyzer = PrImpactAnalyzerFacade.Create(services =>
///       services.AddPrImpactAnalyzer(model: "claude-haiku-4.5"));
/// </summary>
public static class PrImpactAnalyzerRegistration
{
    public static IServiceCollection AddPrImpactAnalyzer(this IServiceCollection services, string model = "claude-haiku-4.5")
    {
        // Source control + LLM
        services.AddSingleton<IPrDiffProvider, AzureDevOpsPrDiffProvider>();

        // CopilotClient is expensive (spawns the CLI) — register the orchestrator as a singleton
        // so exactly one client/process exists for the whole run.
        services.AddSingleton<ILlmOrchestrator>(sp =>
            new CopilotSdkOrchestrator(sp.GetRequiredService<ILogger<CopilotSdkOrchestrator>>(), model));

        // Code analyzers — SoapWsdl before DotNet so a "Service" .cs file reaches the SOAP
        // analyzer too; all matching analyzers run per file regardless of order now.
        services.AddSingleton<ICodeAnalyzer, SoapWsdlAnalyzer>();
        services.AddSingleton<ICodeAnalyzer, DotNetAnalyzer>();
        services.AddSingleton<ICodeAnalyzer, ColdFusionAnalyzer>();
        services.AddSingleton<ICodeAnalyzer, NodeJsAnalyzer>();
        services.AddSingleton<ICodeAnalyzer, MarkupSelectorAnalyzer>();
        services.AddSingleton<ICodeAnalyzer, XmlConfigAnalyzer>();

        // Test parser
        services.AddSingleton<ITestParser, SpecFlowParser>();

        // Pipeline components
        services.AddSingleton<PromptBuilder>();
        services.AddSingleton<LlmResponseParser>();
        services.AddSingleton<AnalysisPipeline>();

        return services;
    }
}
