using System.Net;
using System.Text;
using System.Text.Json;
using RedCompute.App.Services;
using RedCompute.Core.Configuration;
using Xunit;

namespace RedCompute.App.Tests;

public sealed class RedLeafSessionReaderTests
{
    [Fact]
    public async Task GetSessionAsync_TailReadsNewestPageAndReturnsChronologicalHistory()
    {
        var handler = new SessionHandler();
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://redleaf/") };
        var config = new RedComputeConfig { RedLeafUrl = "http://redleaf" };
        var log = new Action<string, Guid?>((_, _) => { });
        var providerConfig = new ProviderConfigService(config, log);
        var cachePath = Path.Combine(Path.GetTempPath(), $"redcompute-tail-test-{Guid.NewGuid():N}.json");
        var quality = new QualityModeService(
            config,
            log,
            providerConfig,
            new HttpClient(new SessionHandler()),
            cachePath,
            TimeSpan.FromMilliseconds(1),
            TimeSpan.FromMilliseconds(2));

        var reader = new RedLeafSessionReader(http, quality);
        var (info, history) = await reader.GetSessionAsync("session-1", tail: 2);

        Assert.Equal("session-1", info?.Id);
        Assert.Equal([2L, 3L], history.Select(message => message.Id));
        Assert.Equal(["second", "third"], history.Select(message => message.Content));
        Assert.Contains(handler.Requests, path => path.Contains("order=desc") && path.Contains("limit=2"));
        Assert.DoesNotContain(handler.Requests, path => path.Contains("after_id="));
    }

    [Fact]
    public async Task GetSessionAsync_CodexSubscriptionZeroUsesApiEquivalentEstimate()
    {
        var cachePath = Path.Combine(Path.GetTempPath(), $"redcompute-cost-test-{Guid.NewGuid():N}.json");
        try
        {
            var config = new RedComputeConfig { RedLeafUrl = "http://redleaf" };
            var log = new Action<string, Guid?>((_, _) => { });
            var providerConfig = new ProviderConfigService(config, log);
            var quality = new QualityModeService(
                config,
                log,
                providerConfig,
                new HttpClient(new PricingCatalogHandler()),
                cachePath,
                TimeSpan.FromMilliseconds(1),
                TimeSpan.FromMilliseconds(2));
            Assert.True(await quality.InitialSyncAsync());

            var http = new HttpClient(new SubscriptionSessionHandler()) { BaseAddress = new Uri("http://redleaf/") };
            var reader = new RedLeafSessionReader(http, quality);
            var (info, _) = await reader.GetSessionAsync("session-1");

            Assert.NotNull(info);
            Assert.True(info.CostEstimated);
            Assert.Equal(17.804905, info.CostUsd!.Value, precision: 6);
        }
        finally
        {
            if (File.Exists(cachePath)) File.Delete(cachePath);
            if (File.Exists(cachePath + ".tmp")) File.Delete(cachePath + ".tmp");
        }
    }

    private sealed class SessionHandler : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.PathAndQuery;
            Requests.Add(path);

            object body = path.StartsWith("/api/entities", StringComparison.Ordinal)
                ? new
                {
                    items = new[]
                    {
                        new
                        {
                            id = "entity-1",
                            name = "Tail test",
                            data = JsonSerializer.Serialize(new
                            {
                                session_id = "session-1",
                                provider = "codex",
                                status = "Idle",
                                started_at = "2026-08-02T18:00:00Z",
                            }),
                        },
                    },
                }
                : new
                {
                    // RedLeaf returns descending records for the tail query.
                    items = new[] { Record(3, "third"), Record(2, "second") },
                };

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
            });
        }

        private static object Record(long id, string content) => new
        {
            id,
            data = JsonSerializer.Serialize(new
            {
                session_id = "session-1",
                role = "assistant",
                event_type = "text",
                content,
                timestamp = $"2026-08-02T18:00:0{id}Z",
            }),
        };
    }

    private sealed class SubscriptionSessionHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            object body = request.RequestUri!.PathAndQuery.StartsWith("/api/entities", StringComparison.Ordinal)
                ? new
                {
                    items = new[]
                    {
                        new
                        {
                            id = "entity-1",
                            name = "Subscription cost test",
                            data = JsonSerializer.Serialize(new
                            {
                                session_id = "session-1",
                                provider = "codex",
                                model = "gpt-5.6-sol",
                                status = "Active",
                                started_at = "2026-08-04T13:40:15Z",
                                cost_usd = 0,
                                input_tokens = 23_258_687,
                                cache_read_input_tokens = 22_346_240,
                                output_tokens = 68_985,
                            }),
                        },
                    },
                }
                : new { items = Array.Empty<object>() };

            return Task.FromResult(Json(body));
        }
    }

    private sealed class PricingCatalogHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var query = request.RequestUri?.Query ?? "";
            var json = query switch
            {
                var q when q.Contains("type=quality-tier", StringComparison.Ordinal) =>
                    "{\"items\":[{\"id\":\"tier-deep\",\"slug\":\"deep\",\"name\":\"Deep\",\"data\":{\"label\":\"Deep\",\"sort_order\":1}}]}",
                var q when q.Contains("type=quality-mode", StringComparison.Ordinal) =>
                    "{\"items\":[{\"id\":\"mode-deep-codex\",\"slug\":\"deep-codex\",\"data\":{\"quality_tier\":\"tier-deep\",\"provider\":\"codex-default\",\"model\":\"gpt-5.6-sol\",\"effort\":\"high\",\"is_default\":true}}]}",
                var q when q.Contains("type=inference-model", StringComparison.Ordinal) =>
                    "{\"items\":[{\"id\":\"sol\",\"slug\":\"gpt-5.6-sol\",\"data\":{\"model_id\":\"gpt-5.6-sol\",\"cost_input\":5.0,\"cost_output\":30.0}}]}",
                var q when q.Contains("type=suite-config", StringComparison.Ordinal) =>
                    "{\"items\":[{\"id\":\"suite\",\"slug\":\"suite-config\",\"data\":{\"default_quality_tier\":\"tier-deep\"}}]}",
                _ => "{\"items\":[]}",
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            });
        }
    }

    private static HttpResponseMessage Json(object body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
    };
}
