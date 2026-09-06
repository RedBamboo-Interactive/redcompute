using System.Text.Json;
using Xunit;

namespace RedCompute.Plugin.Codex.Tests;

public class CodexAccountUsageTests
{
    [Fact]
    public void ParseSnapshot_MapsAccountQuotaWithoutSparkOrResetCreditIds()
    {
        using var doc = JsonDocument.Parse("""
            {
              "rateLimits": {
                "limitId": "codex",
                "primary": { "usedPercent": 39, "windowDurationMins": 10080, "resetsAt": 1788747933 },
                "planType": "pro"
              },
              "rateLimitsByLimitId": {
                "codex_bengalfox": {
                  "limitId": "codex_bengalfox",
                  "limitName": "GPT-5.3-Codex-Spark",
                  "primary": { "usedPercent": 12.5, "windowDurationMins": 300, "resetsAt": 1788635137 },
                  "secondary": { "usedPercent": 4, "windowDurationMins": 10080, "resetsAt": 1789221937 },
                  "planType": "pro"
                },
                "codex": {
                  "limitId": "codex",
                  "limitName": null,
                  "primary": { "usedPercent": 39, "windowDurationMins": 10080, "resetsAt": 1788747933 },
                  "credits": { "hasCredits": false, "unlimited": false, "balance": "0" },
                  "planType": "pro"
                }
              },
              "rateLimitResetCredits": {
                "availableCount": 3,
                "credits": [{ "id": "opaque-secret-ish-id" }]
              }
            }
            """);

        var snapshot = CodexAccountUsageService.ParseSnapshot(doc.RootElement);

        Assert.Equal("codex", snapshot.Provider);
        Assert.Equal("Codex", snapshot.DisplayName);
        Assert.Equal("pro", snapshot.PlanType);
        Assert.Equal(3, snapshot.ResetCreditsAvailable);
        var bucket = Assert.Single(snapshot.Buckets);
        Assert.Equal("codex", bucket.Id);
        Assert.Equal("Codex", bucket.Name);
        Assert.Equal(39, bucket.Windows.Single().UsedPercent);
        Assert.DoesNotContain("GPT-5.3-Codex-Spark", JsonSerializer.Serialize(snapshot));
        Assert.DoesNotContain("opaque-secret-ish-id", JsonSerializer.Serialize(snapshot));
    }

    [Fact]
    public void ParseSnapshot_FallsBackToLegacySingleBucket()
    {
        using var doc = JsonDocument.Parse("""
            {
              "rateLimits": {
                "limitId": "codex",
                "primary": { "usedPercent": 90, "windowDurationMins": 300, "resetsAt": 1788635137 },
                "rateLimitReachedType": null,
                "planType": "plus"
              }
            }
            """);

        var snapshot = CodexAccountUsageService.ParseSnapshot(doc.RootElement);

        var bucket = Assert.Single(snapshot.Buckets);
        Assert.Equal("plus", snapshot.PlanType);
        Assert.Equal(90, Assert.Single(bucket.Windows).UsedPercent);
    }
}
