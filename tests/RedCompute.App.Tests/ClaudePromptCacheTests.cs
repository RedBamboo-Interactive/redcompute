using System.Text.Json;
using RedCompute.Core.Sessions;
using RedCompute.Plugin.ClaudeCode;
using Xunit;

namespace RedCompute.App.Tests;

public sealed class ClaudePromptCacheTests
{
    [Fact]
    public void AccumulatePromptCache_WeightsByInputAndCountsWritesOnlyInDenominator()
    {
        using var first = JsonDocument.Parse("""{"input_tokens":1,"cache_read_input_tokens":0,"cache_creation_input_tokens":99}""");
        using var second = JsonDocument.Parse("""{"input_tokens":10,"cache_read_input_tokens":190,"cache_creation_input_tokens":0}""");

        var miss = ClaudeSessionService.AccumulatePromptCache(null, first.RootElement);
        Assert.Equal(new PromptCacheCounts(100, 0), miss.Session);
        Assert.False(miss.Partial);
        var both = ClaudeSessionService.AccumulatePromptCache(miss, second.RootElement);
        Assert.Equal(new PromptCacheCounts(300, 190), both.Session);
        Assert.False(both.Partial);
    }

    [Fact]
    public void AccumulatePromptCache_DoesNotAssumeCoverageForMissingOrLegacyCounts()
    {
        using var missing = JsonDocument.Parse("""{"input_tokens":200,"cache_creation_input_tokens":100}""");
        using var valid = JsonDocument.Parse("""{"input_tokens":10,"cache_read_input_tokens":50,"cache_creation_input_tokens":0}""");

        var unavailable = ClaudeSessionService.AccumulatePromptCache(null, missing.RootElement);
        Assert.Null(unavailable.Session);
        Assert.True(unavailable.Partial);
        var resumed = ClaudeSessionService.AccumulatePromptCache(unavailable, valid.RootElement);
        Assert.Equal(new PromptCacheCounts(60, 50), resumed.Session);
        Assert.True(resumed.Partial);
    }
}
