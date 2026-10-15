using System.Text.Json;
using RedCompute.Plugin.ClaudeCode;
using RedCompute.PluginSdk;
using Xunit;

namespace RedCompute.App.Tests;

public class ClaudeAccountUsageTests
{
    [Fact]
    public void ParseSnapshot_MapsFiveHourAndSevenDayWindows()
    {
        using var doc = JsonDocument.Parse("""
        {
          "type": "rate_limit_event",
          "rate_limit_info": {
            "status": "allowed",
            "resetsAt": 1788465600,
            "rateLimitType": "five_hour",
            "unifiedWindows": {
              "five_hour": { "utilization": 0.24, "resetsAt": 1788465600 },
              "seven_day": { "utilization": 0.13, "resetsAt": 1789005600 }
            }
          }
        }
        """);

        var snapshot = ClaudeAccountUsageService.ParseSnapshot(doc.RootElement);

        Assert.NotNull(snapshot);
        Assert.Equal("claude-code", snapshot.Provider);
        Assert.Equal("Claude Code", snapshot.DisplayName);
        var bucket = Assert.Single(snapshot.Buckets);
        Assert.Equal("claude", bucket.Id);
        Assert.Collection(
            bucket.Windows,
            fiveHour =>
            {
                Assert.Equal("five_hour", fiveHour.Id);
                Assert.Equal(24, fiveHour.UsedPercent);
                Assert.Equal(300, fiveHour.WindowDurationMinutes);
                Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1788465600), fiveHour.ResetsAt);
            },
            sevenDay =>
            {
                Assert.Equal("seven_day", sevenDay.Id);
                Assert.Equal(13, sevenDay.UsedPercent);
                Assert.Equal(10_080, sevenDay.WindowDurationMinutes);
                Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1789005600), sevenDay.ResetsAt);
            });
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"rate_limit_info\":{}}")]
    [InlineData("{\"rate_limit_info\":{\"unifiedWindows\":{\"five_hour\":{\"utilization\":-1}}}}")]
    public void ParseSnapshot_IgnoresMissingOrInvalidUsage(string json)
    {
        using var doc = JsonDocument.Parse(json);

        Assert.Null(ClaudeAccountUsageService.ParseSnapshot(doc.RootElement));
    }

    [Fact]
    public void Provider_ExposesSharedUsageContract()
    {
        Assert.True(typeof(IProviderUsageSource).IsAssignableFrom(typeof(ClaudeCodeProvider)));
    }

    [Fact]
    public async Task ApplyEvent_PublishesLatestPassiveSnapshot()
    {
        var usage = new ClaudeAccountUsageService();
        Assert.Null(await usage.GetAsync(forceRefresh: true, CancellationToken.None));
        using var doc = JsonDocument.Parse("""
        {
          "rate_limit_info": {
            "unifiedWindows": {
              "five_hour": { "utilization": 0.625, "resetsAt": 1788465600 }
            }
          }
        }
        """);

        usage.ApplyEvent(doc.RootElement);

        var snapshot = await usage.GetAsync(forceRefresh: true, CancellationToken.None);
        Assert.Equal(62.5, Assert.Single(Assert.Single(snapshot!.Buckets).Windows).UsedPercent);
    }
}
