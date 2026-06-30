using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PRImpactAnalyzer.Core.Interfaces;
using PRImpactAnalyzer.Core.Models;
using PRImpactAnalyzer.Core.Pipeline;

namespace PRImpactAnalyzer.Core;

/// <summary>
/// One-call entry point for consuming the analyzer as a library from a test framework,
/// build script, or CI step. Wraps the DI-resolved AnalysisPipeline so callers don't have
/// to wire up the container themselves for the common case.
///
/// Typical usage from a test framework:
///
///   var analyzer = PrImpactAnalyzerFacade.CreateDefault();
///   var result = await analyzer.AnalyzeAsync(new AnalysisRequest {
///       DevRepoPrUrl      = "https://dev.azure.com/org/proj/_git/repo/pullrequest/482",
///       AzureDevOpsPat    = Environment.GetEnvironmentVariable("ADO_PAT")!,
///       TestRepoLocalPath = @"C:\source\MyApp.Tests",
///   });
///   foreach (var s in result.ImpactedScenarios)
///       Console.WriteLine($"{s.Confidence} | {s.FeatureFile} | {s.ScenarioName} | {s.Reason}");
///
/// CreateDefault() requires the consuming project to also reference the Infrastructure and
/// Plugins assemblies (so the analyzers, ADO provider, and Copilot SDK orchestrator can be
/// registered). The RegisterServices hook below is called via reflection-free delegate so
/// Core itself stays dependency-free — see PrImpactAnalyzerRegistration in Infrastructure.
/// </summary>
public sealed class PrImpactAnalyzerFacade : IAsyncDisposable
{
    private readonly ServiceProvider _provider;
    private readonly AnalysisPipeline _pipeline;

    private PrImpactAnalyzerFacade(ServiceProvider provider)
    {
        _provider = provider;
        _pipeline = provider.GetRequiredService<AnalysisPipeline>();
    }

    /// <summary>
    /// Builds a facade with all default services registered via the supplied registration
    /// delegate (which lives in Infrastructure so Core has no dependency on it).
    /// </summary>
    public static PrImpactAnalyzerFacade Create(Action<IServiceCollection> registerServices, Action<ILoggingBuilder>? configureLogging = null)
    {
        var services = new ServiceCollection();
        services.AddLogging(lb =>
        {
            configureLogging?.Invoke(lb);
            // Fully-qualified call (rather than relying on extension-method resolution via
            // `using`) so this compiles even if a stale obj/bin cache or partial restore
            // hasn't picked up the Microsoft.Extensions.Logging.Console package reference yet.
            // If this line itself fails to resolve, it confirms the package truly isn't
            // restored — run "Restore NuGet Packages" (or `dotnet restore`) on the solution.
            if (configureLogging is null) Microsoft.Extensions.Logging.ConsoleLoggerExtensions.AddConsole(lb);
        });
        registerServices(services);
        var provider = services.BuildServiceProvider();
        return new PrImpactAnalyzerFacade(provider);
    }

    public Task<AnalysisResult> AnalyzeAsync(AnalysisRequest request, CancellationToken cancellationToken = default)
        => _pipeline.RunAsync(request, cancellationToken);

    /// <summary>
    /// Runs the analysis AND writes a self-contained HTML report of the run, returning both
    /// the result and the path to the report file. This is the convenient entry point when
    /// you want the visual report every time (the common case from a framework or CI step).
    /// </summary>
    public async Task<(AnalysisResult Result, string ReportPath)> AnalyzeAndReportAsync(
        AnalysisRequest request, string? reportPath = null, CancellationToken cancellationToken = default)
    {
        var result = await _pipeline.RunAsync(request, cancellationToken);
        var path = HtmlReportWriter.Write(result, reportPath);
        return (result, path);
    }

    public async ValueTask DisposeAsync()
    {
        await _provider.DisposeAsync();
    }
}
