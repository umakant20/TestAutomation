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
/// One-stop DI registration for library/CLI consumers. Registers every analyzer, the test
/// parser, the Azure DevOps diff provider, and the pipeline.
///
/// NOTE: there is deliberately no LLM orchestrator registered here. The LLM step is manual —
/// you paste the generated prompt into Copilot Chat yourself and paste the JSON reply back in.
/// See AnalysisPipeline.PrepareAsync / Finalize, and the CLI's `prepare` / `report` commands.
/// </summary>
public static class PrImpactAnalyzerRegistration
{
    public static IServiceCollection AddPrImpactAnalyzer(this IServiceCollection services)
    {
        services.AddSingleton<IPrDiffProvider, AzureDevOpsPrDiffProvider>();

        // Code analyzers — all matching analyzers run per file (not just the first match),
        // so e.g. a .cs SOAP service file reaches both the SOAP analyzer and Roslyn DotNet analyzer.
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
