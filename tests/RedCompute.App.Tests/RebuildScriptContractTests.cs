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

    [Fact]
    public void DesktopDeploymentReclaimsOnlyTheExactCanonicalComputeListener()
    {
        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "deploy-staged.ps1"));

        Assert.Contains(
            "$listeners = @(Get-NetTCPConnection -LocalPort 18800 -State Listen",
            source, StringComparison.Ordinal);
        Assert.Contains("$listeners.Count -ne 1", source, StringComparison.Ordinal);
        Assert.Contains(
            "[string]::Equals($actualPath, $expectedPath, [StringComparison]::OrdinalIgnoreCase)",
            source, StringComparison.Ordinal);
        Assert.Contains(
            "Stop-Process -Id $listener.OwningProcess -Force -ErrorAction Stop",
            source, StringComparison.Ordinal);
        Assert.Contains(
            "Stop-CanonicalComputeListener -Reason 'RedLeaf retained an adopted canonical child'",
            source, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Get-Process -Name RedCompute -ErrorAction SilentlyContinue |" + Environment.NewLine +
            "            Stop-Process -Force",
            source, StringComparison.Ordinal);
    }
}
