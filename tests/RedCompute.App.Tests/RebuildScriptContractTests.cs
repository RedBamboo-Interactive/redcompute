using System.IO;
using Xunit;

namespace RedCompute.App.Tests;

public sealed class RebuildScriptContractTests
{
    [Fact]
    public void LiveRebuildKeepsAgentBearerAndUsesRedLeafLocalOwnerFallback()
    {
        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "rebuild.ps1"));

        Assert.Contains("REDLEAF_EXECUTION_TOKEN", source, StringComparison.Ordinal);
        Assert.Contains("--local-maintenance-deploy", source, StringComparison.Ordinal);
        Assert.Contains("Get-Process -Name RedLeaf", source, StringComparison.Ordinal);
        Assert.DoesNotContain("A live RedCompute deployment requires REDLEAF_EXECUTION_TOKEN", source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void StageAssemblesProviderDllsFromTheCurrentIsolatedBuild()
    {
        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "rebuild.ps1"));

        Assert.Contains("$projectOutputRoot = Join-Path $artifactsDirectory \"bin\\$projectName\"", source,
            StringComparison.Ordinal);
        Assert.Contains("-Filter \"$projectName.dll\"", source, StringComparison.Ordinal);
        Assert.Contains(
            "Invoke-RedBambooMirrorTree -Source $isolatedPluginDirectory -Destination $stagedPluginDirectory",
            source, StringComparison.Ordinal);
        Assert.Contains("$expectedPluginCount = $pluginProjects.Count", source, StringComparison.Ordinal);
    }
}
