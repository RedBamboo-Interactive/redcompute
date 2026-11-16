using RedCompute.PluginSdk;
using RedCompute.PluginSdk.Hosting.Local;
using Xunit;

namespace RedCompute.App.Tests;

/// <summary>
/// The execution host lives in RedCompute.ExecutionHost (no ASP.NET Core, no plugin SDK), so the node
/// agent can run it. RedCompute.PluginSdk forwards every public type it used to hold, so a plugin
/// built against the older SDK still loads.
/// </summary>
public sealed class ExecutionHostSplitTests
{
    [Fact]
    public void The_sdk_forwards_every_public_execution_host_type()
    {
        var host = typeof(LocalExecutionHost).Assembly;
        var exported = host.GetExportedTypes().Where(type => !type.IsNested)
            .Select(type => type.FullName!).Order(StringComparer.Ordinal).ToList();
        var forwarded = typeof(SessionScratch).Assembly.GetForwardedTypes()
            .Select(type => type.FullName!).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(exported, forwarded);
        Assert.Contains(typeof(PlatformSupport).FullName!, forwarded);
    }

    [Fact]
    public void The_execution_host_depends_on_core_only()
    {
        var references = typeof(LocalExecutionHost).Assembly.GetReferencedAssemblies()
            .Select(name => name.Name!)
            .Where(name => name.StartsWith("RedCompute", StringComparison.Ordinal)
                || name.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal))
            .ToList();
        Assert.Equal(["RedCompute.Core"], references);
        Assert.Equal(LocalExecutionHost.LocalNodeAlias, INodeRegistry.LocalAlias);
        Assert.Equal("local", LocalExecutionHost.Shared.NodeId);
    }
}
