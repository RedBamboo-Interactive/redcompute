using RedCompute.App.Services;
using RedCompute.Core.Capabilities;
using RedCompute.Core.Configuration;
using RedCompute.Core.Providers;
using Xunit;

namespace RedCompute.App.Tests;

public sealed class CapabilityRegistryTests
{
    [Fact]
    public async Task External_capability_is_running_without_a_provider()
    {
        var registry = new CapabilityRegistry();
        registry.Register("workflow", new CapabilityDefinition
        {
            Slug = "workflow",
            DisplayName = "Workflow",
            ExecutionMode = CapabilityExecutionMode.External,
            WorkerDisplayName = "RedLeaf Workflow Engine",
        }, new CapabilityConfig(), [], null);

        var entry = Assert.IsType<CapabilityEntry>(registry.Get("workflow"));
        Assert.True(entry.IsExternal);
        Assert.Null(entry.ActiveProvider);
        Assert.Equal(BackendStatus.Running, await registry.GetStatus("workflow"));
    }

    [Fact]
    public async Task Provider_capability_without_a_provider_remains_stopped()
    {
        var registry = new CapabilityRegistry();
        registry.Register("image-gen", new CapabilityDefinition
        {
            Slug = "image-gen",
            DisplayName = "Image Generation",
        }, new CapabilityConfig(), [], null);

        Assert.Equal(BackendStatus.Stopped, await registry.GetStatus("image-gen"));
    }
}
