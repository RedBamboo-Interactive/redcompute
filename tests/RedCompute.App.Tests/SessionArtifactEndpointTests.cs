using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using RedBamboo.AppHost.Discovery;
using RedCompute.App.Api.Endpoints;
using RedCompute.App.Services;
using RedCompute.Core.Capabilities;
using RedCompute.Core.Configuration;
using RedCompute.Core.Jobs;
using RedCompute.Core.Providers;
using RedCompute.PluginSdk;
using Xunit;

namespace RedCompute.App.Tests;

[Collection("Unified session endpoint mapping")]
public sealed class SessionArtifactEndpointTests
{
    private const string SessionId = "session-1";

    private sealed record Row(long Id, string Role, string EventType, string? Content, string? AttachmentsJson = null);

    private sealed class Harness : IAsyncDisposable
    {
        private readonly SessionRecoveryTests.Fixture _fixture = new("maintenance_restart");
        private readonly WebApplication _app;
        private readonly RouteEndpoint _endpoint;
        public List<Row> Rows { get; } = [];
        public bool Exists { get; set; } = true;
        public bool Confidential { get; set; }
        public int StreamRequests { get; private set; }

        public Harness()
        {
            var builder = WebApplication.CreateSlimBuilder();
            _app = builder.Build();
            UnifiedSessionEndpoints.Map(new EndpointRegistry(_app), new CapabilityRegistry(), new NullJobs(),
                (_, _) => { }, _fixture.Config, redLeafReader: Reader(), attachmentStore: _fixture.Attachments,
                inputQueue: _fixture.Service());
            _endpoint = ((IEndpointRouteBuilder)_app).DataSources.SelectMany(source => source.Endpoints)
                .OfType<RouteEndpoint>()
                .Single(item => item.RoutePattern.RawText == "/ai-session/sessions/{id}/artifacts"
                    && item.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods.Contains("GET"));
        }

        public async Task<(int Status, JsonElement Body)> GetAsync(string user, string query = "", string id = SessionId)
        {
            var context = new DefaultHttpContext
            {
                RequestServices = _app.Services,
                User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", user)], "signed")),
            };
            context.SetEndpoint(_endpoint);
            context.Request.Method = "GET";
            context.Request.QueryString = new QueryString(query);
            context.Request.RouteValues["id"] = id;
            context.Response.Body = new MemoryStream();
            await _endpoint.RequestDelegate!(context);
            context.Response.Body.Position = 0;
            var text = await new StreamReader(context.Response.Body).ReadToEndAsync();
            return (context.Response.StatusCode, text.Length == 0 ? default : JsonSerializer.Deserialize<JsonElement>(text));
        }

        private RedLeafSessionReader Reader()
        {
            var config = new RedComputeConfig { RedLeafUrl = "http://redleaf" };
            var log = new Action<string, Guid?>((_, _) => { });
            var quality = new QualityModeService(config, log, new ProviderConfigService(config, log),
                new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound))),
                Path.Combine(Path.GetTempPath(), $"redcompute-artifacts-{Guid.NewGuid():N}.json"),
                TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(2));
            return new RedLeafSessionReader(new HttpClient(new StubHandler(Respond))
            { BaseAddress = new Uri("http://redleaf/") }, quality);
        }

        private HttpResponseMessage Respond(HttpRequestMessage request)
        {
            var path = request.RequestUri!.AbsolutePath;
            object body;
            if (path.StartsWith("/api/entities", StringComparison.Ordinal))
            {
                body = new
                {
                    items = Exists
                        ? (object)new[]
                        {
                            new
                            {
                                id = "entity-1", name = "Worker",
                                data = JsonSerializer.Serialize(new
                                {
                                    session_id = SessionId, provider = "test", status = "Idle", user_id = "owner",
                                    confidential = Confidential, owner_agent_id = Confidential ? "agent-1" : null,
                                    started_at = "2026-10-24T10:00:00Z",
                                }),
                            },
                        }
                        : Array.Empty<object>(),
                };
            }
            else
            {
                StreamRequests++;
                var query = request.RequestUri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
                    .Select(part => part.Split('=', 2)).ToDictionary(p => p[0], p => Uri.UnescapeDataString(p[1]));
                IEnumerable<Row> rows = Rows;
                if (query.TryGetValue("before_id", out var before)) rows = rows.Where(r => r.Id < long.Parse(before));
                if (query.TryGetValue("after_id", out var after)) rows = rows.Where(r => r.Id > long.Parse(after));
                rows = query["order"] == "asc" ? rows.OrderBy(r => r.Id) : rows.OrderByDescending(r => r.Id);
                body = new
                {
                    items = rows.Take(int.Parse(query["limit"])).Select(row => new
                    {
                        id = row.Id,
                        createdAt = DateTimeOffset.UnixEpoch.AddDays(1).AddSeconds(row.Id).ToString("O"),
                        data = JsonSerializer.Serialize(new
                        {
                            session_id = SessionId, role = row.Role, event_type = row.EventType, content = row.Content,
                            message_uid = $"uid-{row.Id}", epoch = TranscriptOrdering.Epoch("test", SessionId), sequence = row.Id,
                            timestamp = DateTimeOffset.UnixEpoch.AddSeconds(row.Id).ToString("O"),
                            attachments_json = row.AttachmentsJson,
                        }),
                        payload = (object?)null,
                    }),
                };
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
            };
        }

        public async ValueTask DisposeAsync()
        {
            await _app.DisposeAsync();
            _fixture.Dispose();
        }
    }

    private static Harness WithTranscript()
    {
        var harness = new Harness();
        harness.Rows.AddRange(
        [
            new Row(1, "user", "text", "look at [spec](https://spec.example.com/) please", AttachmentsJson:
                "{\"attachments\":[{\"id\":\"a1\",\"kind\":\"image\",\"name\":\"p.png\",\"mediaType\":\"image/png\",\"size\":9,\"downloadUrl\":\"/ai-session/input-attachments/a1\"}]}"),
            new Row(2, "assistant", "thinking", "![hidden](/api/assets/hidden.png)"),
            new Row(3, "assistant", "text", "Done: ![chart](/api/assets/chart1.png) and [spec](https://spec.example.com/)"),
        ]);
        return harness;
    }

    [Fact]
    public async Task Unknown_session_is_404()
    {
        await using var harness = new Harness { Exists = false };
        var (status, body) = await harness.GetAsync("owner");
        Assert.Equal(404, status);
        Assert.Equal("not_found", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task A_caller_who_may_not_read_the_session_gets_403_never_an_empty_list()
    {
        await using var harness = WithTranscript();
        var (status, body) = await harness.GetAsync("intruder");
        Assert.Equal(403, status);
        Assert.Equal("forbidden", body.GetProperty("error").GetString());
        Assert.False(body.TryGetProperty("items", out _));
        Assert.Equal(0, harness.StreamRequests); // refused before any record was read
    }

    [Fact]
    public async Task Confidential_session_is_refused_to_non_owners_exactly_like_the_transcript_and_open_to_its_owner()
    {
        await using var harness = WithTranscript();
        harness.Confidential = true;

        var (status, body) = await harness.GetAsync("intruder");
        Assert.Equal(404, status); // ComputeResourceAccess.SessionDenied: confidential sessions do not reveal themselves
        Assert.False(body.TryGetProperty("items", out _));
        Assert.Equal(0, harness.StreamRequests);

        // A cursor must not turn the refusal into an oracle either.
        var (withCursor, _) = await harness.GetAsync("intruder", "?cursor=garbage");
        Assert.Equal(404, withCursor);

        // The explicit human owner reads it, as the transcript does.
        var (ownerStatus, owned) = await harness.GetAsync("owner");
        Assert.Equal(200, ownerStatus);
        Assert.Equal(3, owned.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task Happy_path_returns_the_deduplicated_artifacts_of_the_current_epoch()
    {
        await using var harness = WithTranscript();
        var (status, body) = await harness.GetAsync("owner");

        Assert.Equal(200, status);
        Assert.Equal(SessionId, body.GetProperty("sessionId").GetString());
        Assert.Equal(TranscriptOrdering.Epoch("test", SessionId), body.GetProperty("epoch").GetString());
        Assert.Equal(2, body.GetProperty("extractorVersion").GetInt32());
        Assert.Equal(3, body.GetProperty("throughSequence").GetInt64());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("page").GetProperty("nextCursor").ValueKind);

        var items = body.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(
            ["asset:chart1.png", "url:https://spec.example.com/", "url:/ai-session/input-attachments/a1"],
            items.Select(item => item.GetProperty("key").GetString()));

        var link = items[1];
        Assert.Equal("link", link.GetProperty("kind").GetString());
        Assert.Equal(2, link.GetProperty("occurrences").GetInt32());
        Assert.Equal(1, link.GetProperty("first").GetProperty("sequence").GetInt64());
        Assert.Equal("uid-3", link.GetProperty("last").GetProperty("messageUid").GetString());
        Assert.Equal("assistant-text", link.GetProperty("origin").GetString());

        var attachment = items[2];
        Assert.Equal(("image", "user-attachment", "p.png", 9), (
            attachment.GetProperty("kind").GetString(), attachment.GetProperty("origin").GetString(),
            attachment.GetProperty("name").GetString(), attachment.GetProperty("size").GetInt32()));
        Assert.DoesNotContain("hidden.png", body.GetRawText()); // thinking is never an artifact
    }

    [Fact]
    public async Task Filters_delta_limit_and_cursor_work_and_a_warm_request_extends_the_index()
    {
        await using var harness = WithTranscript();

        var (_, images) = await harness.GetAsync("owner", "?kinds=image&origins=user-attachment,assistant-text");
        Assert.Equal(["asset:chart1.png", "url:/ai-session/input-attachments/a1"],
            images.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("key").GetString()));

        var (_, delta) = await harness.GetAsync("owner", "?sinceSequence=1");
        Assert.Equal(2, delta.GetProperty("items").GetArrayLength());

        var (_, firstPage) = await harness.GetAsync("owner", "?limit=1");
        var cursor = firstPage.GetProperty("page").GetProperty("nextCursor").GetString();
        Assert.NotNull(cursor);
        var (_, secondPage) = await harness.GetAsync("owner", $"?limit=1&cursor={Uri.EscapeDataString(cursor!)}");
        Assert.Equal("url:https://spec.example.com/",
            secondPage.GetProperty("items")[0].GetProperty("key").GetString());

        var requestsBefore = harness.StreamRequests;
        harness.Rows.Add(new Row(4, "assistant", "text", "new [doc](/api/assets/doc.pdf)"));
        var (_, grown) = await harness.GetAsync("owner");
        Assert.Equal(4, grown.GetProperty("throughSequence").GetInt64());
        Assert.Equal("asset:doc.pdf", grown.GetProperty("items")[0].GetProperty("key").GetString());
        Assert.True(harness.StreamRequests - requestsBefore < 10); // tail read, not a rescan
    }

    [Theory]
    [InlineData("?kinds=image,banana", "invalid_filter")]
    [InlineData("?origins=nowhere", "invalid_filter")]
    [InlineData("?limit=many", "invalid_pagination")]
    [InlineData("?sinceSequence=-3", "invalid_pagination")]
    [InlineData("?cursor=%2A%2A", "invalid_cursor")]
    public async Task Bad_parameters_are_400_with_a_code(string query, string code)
    {
        await using var harness = WithTranscript();
        var (status, body) = await harness.GetAsync("owner", query);
        Assert.Equal(400, status);
        Assert.Equal(code, body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Limit_is_clamped_to_the_documented_range()
    {
        await using var harness = WithTranscript();
        var (zero, low) = await harness.GetAsync("owner", "?limit=0");
        Assert.Equal(200, zero);
        Assert.Equal(1, low.GetProperty("items").GetArrayLength());
        var (huge, all) = await harness.GetAsync("owner", "?limit=100000");
        Assert.Equal(200, huge);
        Assert.Equal(3, all.GetProperty("items").GetArrayLength());
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(send(request));
    }

    private sealed class NullJobs : IJobTracker
    {
        public JobRecord CreateJob(JobSubmission submission) => new()
            { CapabilitySlug = submission.CapabilitySlug, ProviderName = submission.ProviderName };
        public void MarkRunning(Guid jobId) { }
        public void MarkCompleted(Guid jobId, string? outputLocation = null, long? outputSizeBytes = null,
            string? contentType = null, string? resultJson = null, double? costUsd = null) { }
        public void MarkFailed(Guid jobId, string errorMessage, string? errorDetails = null, string? resultJson = null) { }
        public void MarkCancelled(Guid jobId) { }
        public void UpdateName(Guid jobId, string name) { }
    }
}
