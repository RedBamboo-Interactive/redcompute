using System.Net;
using System.Text;
using System.Text.Json;
using RedCompute.App.Services;
using RedCompute.Core.Configuration;
using RedCompute.Core.Sessions;
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
        Assert.Equal(Guid.Parse("1910ac53-d68a-4ccc-883d-0541c0091d9b"), info?.RepositoryId);
        Assert.Equal([2L, 3L], history.Select(message => message.Id));
        Assert.Equal(["second", "third"], history.Select(message => message.Content));
        Assert.All(history, message => Assert.Equal("final_answer", message.Phase));
        Assert.All(history, message => Assert.Equal("epoch-1", message.Epoch));
        Assert.Equal([2L, 3L], history.Select(message => message.Sequence));
        // Background attribution survives the RedLeaf read path.
        Assert.Equal([null, "toolu_agent"], history.Select(message => message.ParentToolUseId));
        Assert.Null(history[0].TurnOrigin);
        Assert.Equal(("background-task", "task-1"), (history[1].TurnOrigin?.Kind, history[1].TurnOrigin?.TaskId));
        Assert.Equal(
            [
                DateTimeOffset.Parse("2026-08-03T18:00:02Z"),
                DateTimeOffset.Parse("2026-08-03T18:00:03Z"),
            ],
            history.Select(message => message.RecordCreatedAt));
        Assert.Contains(handler.Requests, path => path.Contains("order=desc") && path.Contains("limit=2"));
        Assert.DoesNotContain(handler.Requests, path => path.Contains("after_id="));
    }

    [Theory]
    [InlineData("opencode", 12_325, null, 12_325)]
    [InlineData("opencode", 12_325, 12_400, 12_400)]
    [InlineData("opencode", 12_325, 0, 0)]
    [InlineData("opencode", null, null, null)]
    [InlineData("claude", 12_325, null, null)]
    [InlineData("codex", 12_325, null, null)]
    public async Task GetSessionAsync_OnlyOpenCodeUsesLegacyInputTokensForContext(
        string provider, int? inputTokens, int? contextTokens, int? expectedContextTokens)
    {
        var data = new Dictionary<string, object?>
        {
            ["session_id"] = "session-1",
            ["provider"] = provider,
            ["input_tokens"] = inputTokens,
            ["context_window"] = 1_000_000,
        };
        if (contextTokens.HasValue)
            data["context_tokens"] = contextTokens.Value;

        using var http = new HttpClient(new SessionHandler(JsonSerializer.Serialize(data)))
            { BaseAddress = new Uri("http://redleaf/") };
        var config = new RedComputeConfig { RedLeafUrl = "http://redleaf" };
        var log = new Action<string, Guid?>((_, _) => { });
        var quality = new QualityModeService(config, log, new ProviderConfigService(config, log));
        var reader = new RedLeafSessionReader(http, quality);

        var (info, _) = await reader.GetSessionAsync("session-1");

        Assert.NotNull(info);
        Assert.Equal(inputTokens, info.InputTokens);
        Assert.Equal(expectedContextTokens, info.ContextTokens);
        Assert.Equal(1_000_000, info.ContextWindow);
    }

    [Theory]
    [InlineData("codex", null, 7_606_144L)]
    [InlineData("opencode", null, null)]
    [InlineData("claude", null, null)]
    [InlineData("opencode", """{"session":{"inputTokens":24552,"cachedInputTokens":24320},"partial":true,"latestRequest":{"inputTokens":12294,"cachedInputTokens":12160}}""", 24_320L)]
    [InlineData("codex", """{"session":null}""", null)]
    public async Task PromptCacheUsesStoredComparableCountsWithoutReadingTranscript(
        string provider, string? promptCache, long? cachedTokens)
    {
        var data = new Dictionary<string, object?>
        {
            ["session_id"] = "session-1",
            ["provider"] = provider,
            ["input_tokens"] = 7_797_030,
            ["cache_read_input_tokens"] = 7_606_144,
            ["cost_usd"] = 0.003,
            ["context_tokens"] = 12_325,
            ["context_window"] = 1_000_000,
        };
        using var cache = promptCache is null ? null : JsonDocument.Parse(promptCache);
        if (cache != null)
            data["prompt_cache"] = cache.RootElement;

        var handler = new SessionHandler(JsonSerializer.Serialize(data));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://redleaf/") };
        var config = new RedComputeConfig { RedLeafUrl = "http://redleaf" };
        var log = new Action<string, Guid?>((_, _) => { });
        var quality = new QualityModeService(config, log, new ProviderConfigService(config, log));
        var reader = new RedLeafSessionReader(http, quality);

        var (info, _) = await reader.GetSessionInfoAsync("session-1");

        Assert.NotNull(info);
        Assert.Equal(cachedTokens, info.PromptCache?.Session?.CachedInputTokens);
        if (provider == "codex" && cachedTokens != null)
            Assert.True(info.PromptCache!.Partial);
        Assert.Equal(0.003, info.CostUsd);
        Assert.False(info.CostEstimated);
        Assert.Equal(12_325, info.ContextTokens);
        Assert.Equal(1_000_000, info.ContextWindow);
        if (provider == "opencode" && cache != null)
        {
            Assert.True(info.PromptCache!.Partial);
            Assert.Equal(new PromptCacheCounts(24_552, 24_320), info.PromptCache.Session);
            Assert.Equal(new PromptCacheCounts(12_294, 12_160), info.PromptCache.LatestRequest);
        }
        Assert.All(handler.Requests, path => Assert.StartsWith("/api/entities", path));
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

    [Fact]
    public async Task GeneratedImageHistoryAndPayloadPreserveKindBytesAndContentType()
    {
        var handler = new ImagePayloadHandler();
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://redleaf/") };
        var config = new RedComputeConfig { RedLeafUrl = "http://redleaf" };
        var log = new Action<string, Guid?>((_, _) => { });
        var providerConfig = new ProviderConfigService(config, log);
        var quality = new QualityModeService(
            config, log, providerConfig, new HttpClient(new SessionHandler()),
            Path.Combine(Path.GetTempPath(), $"redcompute-image-payload-{Guid.NewGuid():N}.json"),
            TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(2));
        var reader = new RedLeafSessionReader(http, quality);

        var (_, history) = await reader.GetSessionAsync("session-image");
        var image = Assert.Single(history);
        Assert.Equal("image", image.EventType);
        Assert.Equal("assistant-image", image.PayloadRef?.Kind);
        Assert.Equal("image/png", image.PayloadRef?.ContentType);
        Assert.Equal("binary", image.PayloadRef?.Encoding);

        using var payload = await reader.OpenPayloadAsync("entity-image", 43, null, CancellationToken.None);
        Assert.Equal("image/png", payload.Content.Headers.ContentType?.MediaType);
        Assert.Equal(ImagePayloadHandler.Bytes, await payload.Content.ReadAsByteArrayAsync());
    }

    private sealed class SessionHandler(string? sessionData = null) : HttpMessageHandler
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
                            data = sessionData ?? JsonSerializer.Serialize(new
                            {
                                session_id = "session-1",
                                provider = "codex",
                                repository = "1910ac53-d68a-4ccc-883d-0541c0091d9b",
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
            createdAt = $"2026-08-03T18:00:0{id}Z",
            data = JsonSerializer.Serialize(new
            {
                session_id = "session-1",
                role = "assistant",
                event_type = "text",
                content,
                phase = "final_answer",
                epoch = "epoch-1",
                sequence = id,
                timestamp = $"2026-08-02T18:00:0{id}Z",
                parent_tool_use_id = id == 3 ? "toolu_agent" : null,
                turn_origin = id == 3 ? new { kind = "background-task", task_id = "task-1" } : null,
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

    private sealed class ImagePayloadHandler : HttpMessageHandler
    {
        public static readonly byte[] Bytes = [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.PathAndQuery;
            if (path.Contains("/records/43/payload", StringComparison.Ordinal))
            {
                var content = new ByteArrayContent(Bytes);
                content.Headers.ContentType = new("image/png");
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
            }

            object body = path.StartsWith("/api/entities", StringComparison.Ordinal)
                ? new
                {
                    items = new[]
                    {
                        new
                        {
                            id = "entity-image",
                            name = "Generated image",
                            data = JsonSerializer.Serialize(new
                            {
                                session_id = "session-image",
                                provider = "codex",
                                status = "Idle",
                                started_at = "2026-10-17T10:00:00Z",
                            }),
                        },
                    },
                }
                : new
                {
                    items = new[]
                    {
                        new
                        {
                            id = 43,
                            createdAt = "2026-10-17T10:01:00Z",
                            data = JsonSerializer.Serialize(new
                            {
                                session_id = "session-image",
                                role = "assistant",
                                event_type = "image",
                                content = "A smoke-lilac portrait",
                                message_uid = "turn-image",
                                payload_kind = "assistant-image",
                                timestamp = "2026-10-17T10:01:00Z",
                            }),
                            payload = new
                            {
                                available = true,
                                length = Bytes.Length,
                                contentType = "image/png",
                                encoding = "binary",
                                sha256 = "png-hash",
                            },
                        },
                    },
                };

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
