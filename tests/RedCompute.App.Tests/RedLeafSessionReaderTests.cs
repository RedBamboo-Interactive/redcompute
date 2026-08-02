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
}
