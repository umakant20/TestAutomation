using Microsoft.Extensions.DependencyInjection;
using PRImpactAnalyzer.Core.Interfaces;
using PRImpactAnalyzer.Core.Pipeline;
using PRImpactAnalyzer.Infrastructure.Git;
using PRImpactAnalyzer.Plugins.ColdFusion;
using PRImpactAnalyzer.Plugins.Config;
using PRImpactAnalyzer.Plugins.DotNet;
using PRImpactAnalyzer.Plugins.Markup;
using PRImpactAnalyzer.Plugins.NodeJs;
using PRImpactAnalyzer.Plugins.Soap;
using PRImpactAnalyzer.Plugins.SpecFlow;

namespace PRImpactAnalyzer.Infrastructure;

/// <summary>
/// Registers all analyzers, the test parser, the ADO diff provider, and the pipeline.
/// No LLM/API/token dependency — the LLM step is manual (paste into Copilot Chat, save reply, run report).
/// </summary>
public static class PrImpactAnalyzerRegistration
{
    public static IServiceCollection AddPrImpactAnalyzer(this IServiceCollection services)
    {
        services.AddSingleton<IPrDiffProvider, AzureDevOpsPrDiffProvider>();

        services.AddSingleton<ICodeAnalyzer, SoapWsdlAnalyzer>();
        services.AddSingleton<ICodeAnalyzer, DotNetAnalyzer>();
        services.AddSingleton<ICodeAnalyzer, ColdFusionAnalyzer>();
        services.AddSingleton<ICodeAnalyzer, NodeJsAnalyzer>();
        services.AddSingleton<ICodeAnalyzer, MarkupSelectorAnalyzer>();
        services.AddSingleton<ICodeAnalyzer, XmlConfigAnalyzer>();

        services.AddSingleton<ITestParser, SpecFlowParser>();

        services.AddSingleton<PromptBuilder>();
        services.AddSingleton<LlmResponseParser>();
        services.AddSingleton<AnalysisPipeline>();

        return services;
    }
}
