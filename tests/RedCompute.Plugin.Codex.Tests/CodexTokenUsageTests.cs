using System.Text.Json;
using RedCompute.Core.Sessions;
using Xunit;

namespace RedCompute.Plugin.Codex.Tests;

public class CodexTokenUsageTests
{
    [Fact]
    public void ApplyUsage_MapsCurrentContextSeparatelyFromCumulativeBillingTokens()
    {
        var info = Session();
        using var doc = JsonDocument.Parse("""
            {
              "tokenUsage": {
                "total": {
                  "totalTokens": 6842013,
                  "inputTokens": 6674321,
                  "cachedInputTokens": 6012345,
                  "outputTokens": 167692
                },
                "last": {
                  "totalTokens": 224917,
                  "inputTokens": 221103,
                  "cachedInputTokens": 198442,
                  "outputTokens": 3814
                },
                "modelContextWindow": 258400
              }
            }
            """);

        CodexInteractiveService.ApplyUsage(info, doc.RootElement);

        Assert.Equal(6674321, info.InputTokens);
        Assert.Equal(167692, info.OutputTokens);
        Assert.Equal(6012345, info.CachedInputTokens);
        Assert.Equal(224917, info.ContextTokens);
        Assert.Equal(258400, info.ContextWindow);
    }

    [Fact]
    public void ApplyUsage_DoesNotReplaceKnownContextWhenLastUsageIsAbsent()
    {
        var info = Session();
        info.ContextTokens = 123456;
        using var doc = JsonDocument.Parse("""
            { "tokenUsage": { "total": { "inputTokens": 456 }, "modelContextWindow": 258400 } }
            """);

        CodexInteractiveService.ApplyUsage(info, doc.RootElement);

        Assert.Equal(123456, info.ContextTokens);
        Assert.Equal(258400, info.ContextWindow);
    }

    [Fact]
    public void ApplyUsage_KeepsSessionAndLastRequestSeparateAndPreservesMeasuredZero()
    {
        var info = Session();
        using var full = JsonDocument.Parse("""
            {"tokenUsage": {
              "total": {"inputTokens": 7797030, "cachedInputTokens": 7606144},
              "last": {"inputTokens": 1024, "cachedInputTokens": 0, "cacheWriteInputTokens": 512}
            }}
            """);
        CodexInteractiveService.ApplyUsage(info, full.RootElement);
        Assert.Equal(new PromptCacheUsage(new(7_797_030, 7_606_144), LatestRequest: new(1024, 0)), info.PromptCache);

        using var partial = JsonDocument.Parse("""
            {"tokenUsage": {"total": {"inputTokens": 7800000}, "last": {"inputTokens": 10}}}
            """);
        CodexInteractiveService.ApplyUsage(info, partial.RootElement);
        Assert.Null(info.PromptCache!.Session);
        Assert.Null(info.PromptCache.LatestRequest);
    }

    [Fact]
    public void ModelTokenPricing_PricesCachedInputSeparatelyFromUncachedInput()
    {
        var pricing = new ModelTokenPricing(
            InputUsdPerMillion: 5.00,
            CachedInputUsdPerMillion: 0.50,
            OutputUsdPerMillion: 30.00);

        var cost = pricing.EstimateUsd(
            inputTokens: 12_125_640,
            cachedInputTokens: 11_526_400,
            outputTokens: 36_839);

        Assert.Equal(9.86457, cost, precision: 5);
    }

    private static CodexSessionInfo Session() => new()
    {
        Id = "session-1",
        ProjectName = "Nova",
        ProjectPath = "T:/Projects/nova",
        StartedAt = DateTimeOffset.UtcNow,
    };
}
