using Microsoft.VisualStudio.Extensibility;

namespace PRImpactAnalyzer.Extension;

/// <summary>
/// Extension entry point. The VisualStudio.Extensibility SDK discovers this class
/// automatically and uses it to register all commands, tool windows, etc.
/// </summary>
[VisualStudioContribution]
public class PrImpactAnalyzerExtension : Microsoft.VisualStudio.Extensibility.Extension
{
    public override ExtensionConfiguration ExtensionConfiguration => new()
    {
        Metadata = new ExtensionMetadata(
            id: "PRImpactAnalyzer.Extension",
            version: this.ExtensionAssemblyVersion,
            publisherName: "QA Automation",
            displayName: "PR Test Impact Analyzer",
            description: "Analyzes which test scenarios are impacted by an Azure DevOps PR. " +
                         "Works with your existing GitHub Copilot Chat subscription — no API key needed.")
    };
}
