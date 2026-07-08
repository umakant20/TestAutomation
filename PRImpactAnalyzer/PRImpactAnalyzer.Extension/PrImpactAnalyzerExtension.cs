using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.Extensibility;

namespace PRImpactAnalyzer.Extension;

[VisualStudioContribution]
internal class PrImpactAnalyzerExtension : Microsoft.VisualStudio.Extensibility.Extension
{
    protected override void InitializeServices(IServiceCollection serviceCollection)
    {
        base.InitializeServices(serviceCollection);
    }
}
