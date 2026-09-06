using RedCompute.App.Api.Endpoints;
using RedCompute.PluginSdk;
using Xunit;

namespace RedCompute.App.Tests;

public class ProviderUsageResolutionTests
{
    [Fact]
    public void ConfiguredEntitySlugResolvesToProviderUsageBackend()
    {
        var source = new UsageSource("codex");

        var resolved = UnifiedSessionEndpoints.FindProviderUsageSource(
            [source], "codex-default", "codex");

        Assert.Same(source, resolved);
    }

    [Fact]
    public void DirectProviderIdDoesNotRequireConfiguredAlias()
    {
        var source = new UsageSource("codex");

        var resolved = UnifiedSessionEndpoints.FindProviderUsageSource(
            [source], "codex", null);

        Assert.Same(source, resolved);
    }

    private sealed class UsageSource(string providerId) : IProviderUsageSource
    {
        public string ProviderId { get; } = providerId;

        public Task<ProviderUsageSnapshot?> GetProviderUsageAsync(
            bool forceRefresh = false,
            CancellationToken ct = default)
            => Task.FromResult<ProviderUsageSnapshot?>(null);
    }
}
