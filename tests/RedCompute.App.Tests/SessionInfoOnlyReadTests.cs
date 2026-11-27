using System.Net;
using System.Reflection;
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
using RedCompute.Core.Sessions;
using RedCompute.PluginSdk;
using Xunit;

namespace RedCompute.App.Tests;

// GET /ai-session/sessions/{id}?tail=0 is the info-only read status probes use: the entity alone,
// no session-messages records, every field except messages and transcript unchanged.
[Collection("Unified session endpoint mapping")]
public sealed class SessionInfoOnlyReadTests
{
    [Fact]
    public async Task Tail_zero_reads_no_records_and_keeps_every_status_field()
    {
        var provider = DispatchProxy.Create<SessionWorkStateTests.ITestProvider, SessionWorkStateTests.LiveProvider>();
        var live = (SessionWorkStateTests.LiveProvider)(object)provider;
        live.Status = SessionStatus.Active;
        live.Pending.Add(SessionQuestionCallbackTests.Question("req-1"));
        var redLeaf = new RecordingRedLeaf();

        var info = await InvokeAsync(provider, redLeaf, "?tail=0");

        Assert.DoesNotContain(redLeaf.Requests, IsRecordsRead);
        Assert.Single(redLeaf.Requests, path => path.StartsWith("/api/entities", StringComparison.Ordinal));
        Assert.Empty(info.GetProperty("messages").EnumerateArray());
        Assert.False(info.TryGetProperty("transcript", out _));
        var session = info.GetProperty("session");
        Assert.Equal((int)SessionStatus.Active, session.GetProperty("status").GetInt32());
        Assert.Equal("awaiting_answer", session.GetProperty("workState").GetString());
        Assert.Equal("stopped-for-test", session.GetProperty("stopReason").GetString());
        Assert.Equal("provider-thread-1", session.GetProperty("providerSessionId").GetString());
        Assert.Equal("9c7d4f1e-0a52-4e1b-9b7c-3f1d2e6a8b90", session.GetProperty("jobId").GetString());
        Assert.True(session.TryGetProperty("accessLevel", out _));
        Assert.True(session.TryGetProperty("maxAccessLevel", out _));
        Assert.True(session.TryGetProperty("unattended", out _));
        Assert.Equal("req-1", Assert.Single(info.GetProperty("pendingQuestions").EnumerateArray()).GetProperty("requestId").GetString());
        Assert.Equal(JsonValueKind.Object, info.GetProperty("inputQueue").ValueKind);
    }

    [Fact]
    public async Task Tail_zero_differs_from_tail_one_only_in_messages_and_transcript()
    {
        var provider = DispatchProxy.Create<SessionWorkStateTests.ITestProvider, SessionWorkStateTests.LiveProvider>();
        ((SessionWorkStateTests.LiveProvider)(object)provider).Status = SessionStatus.Idle;

        var oneRedLeaf = new RecordingRedLeaf();
        var one = await InvokeAsync(provider, oneRedLeaf, "?tail=1");
        var zero = await InvokeAsync(provider, new RecordingRedLeaf(), "?tail=0");

        // tail=1 is the newest-record read it always was: one desc records read of one record.
        Assert.Contains(oneRedLeaf.Requests, path => IsRecordsRead(path) && path.Contains("order=desc") && path.Contains("limit=1"));
        Assert.Equal(["session", "messages", "inputQueue", "transcript", "pendingQuestions"], one.EnumerateObject().Select(p => p.Name));
        Assert.Equal("hello", Assert.Single(one.GetProperty("messages").EnumerateArray()).GetProperty("content").GetString());
        Assert.Equal(7, one.GetProperty("transcript").GetProperty("throughSequence").GetInt64());

        Assert.Equal(["session", "messages", "inputQueue", "pendingQuestions"], zero.EnumerateObject().Select(p => p.Name));
        foreach (var name in new[] { "session", "inputQueue", "pendingQuestions" })
            Assert.Equal(one.GetProperty(name).GetRawText(), zero.GetProperty(name).GetRawText());
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("?tail=-3", true)]
    [InlineData("?tail=abc", false)]
    public async Task Other_tail_values_keep_their_record_read(string? query, bool newestOne)
    {
        var provider = DispatchProxy.Create<SessionWorkStateTests.ITestProvider, SessionWorkStateTests.LiveProvider>();
        var redLeaf = new RecordingRedLeaf();

        var read = await InvokeAsync(provider, redLeaf, query);

        Assert.Contains(redLeaf.Requests, path => IsRecordsRead(path)
            && (newestOne ? path.Contains("order=desc") && path.Contains("limit=1") : path.Contains("order=asc")));
        Assert.True(read.TryGetProperty("transcript", out _));
        Assert.Single(read.GetProperty("messages").EnumerateArray());
    }

    [Fact]
    public async Task Reader_tail_zero_skips_the_checkpoint_and_the_records()
    {
        var checkpoints = new TranscriptCheckpointCoordinator();
        Assert.Throws<IOException>(() => checkpoints.Write("session-1", () => throw new IOException("write failed")));
        var redLeaf = new RecordingRedLeaf();
        var reader = Reader(redLeaf, checkpoints);

        var (info, history) = await reader.GetSessionAsync("session-1", tail: 0);

        Assert.Equal("session-1", info?.Id);
        Assert.Empty(history);
        Assert.DoesNotContain(redLeaf.Requests, IsRecordsRead);
        // Any read that does need records still refuses the incomplete checkpoint.
        await Assert.ThrowsAsync<InvalidOperationException>(() => reader.GetSessionAsync("session-1", tail: 1));
    }

    private static bool IsRecordsRead(string path) => path.StartsWith("/api/streams/session-messages/records", StringComparison.Ordinal);

    private static async Task<JsonElement> InvokeAsync(SessionWorkStateTests.ITestProvider provider, RecordingRedLeaf redLeaf, string? query)
    {
        using var f = new SessionRecoveryTests.Fixture("maintenance_restart");
        var registry = new CapabilityRegistry();
        registry.Register("ai-session", new CapabilityDefinition { Slug = "ai-session", DisplayName = "AI Session" },
            new CapabilityConfig(), new Dictionary<string, IBackendProvider> { ["test"] = provider }, "test");
        var builder = WebApplication.CreateSlimBuilder();
        await using var app = builder.Build();
        UnifiedSessionEndpoints.Map(new EndpointRegistry(app), registry, new NullJobs(), (_, _) => { }, f.Config,
            redLeafReader: Reader(redLeaf), attachmentStore: f.Attachments, inputQueue: f.Service());
        var endpoint = ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints).OfType<RouteEndpoint>()
            .Single(item => item.RoutePattern.RawText == "/ai-session/sessions/{id}"
                && item.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods.Contains("GET"));
        var context = new DefaultHttpContext
        {
            RequestServices = app.Services,
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "owner")], "signed")),
        };
        context.SetEndpoint(endpoint);
        context.Request.Method = "GET";
        if (query is not null) context.Request.QueryString = new QueryString(query);
        context.Request.RouteValues["id"] = "session-1";
        context.Response.Body = new MemoryStream();
        await endpoint.RequestDelegate!(context);
        Assert.Equal(200, context.Response.StatusCode);
        context.Response.Body.Position = 0;
        return JsonSerializer.Deserialize<JsonElement>(await new StreamReader(context.Response.Body).ReadToEndAsync());
    }

    private static RedLeafSessionReader Reader(RecordingRedLeaf redLeaf, TranscriptCheckpointCoordinator? checkpoints = null)
    {
        var config = new RedComputeConfig { RedLeafUrl = "http://redleaf" };
        var log = new Action<string, Guid?>((_, _) => { });
        var quality = new QualityModeService(config, log, new ProviderConfigService(config, log),
            new HttpClient(new RecordingRedLeaf()),
            Path.Combine(Path.GetTempPath(), $"redcompute-info-only-{Guid.NewGuid():N}.json"),
            TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(2));
        return new RedLeafSessionReader(new HttpClient(redLeaf) { BaseAddress = new Uri("http://redleaf/") }, quality)
        {
            Checkpoints = checkpoints,
        };
    }

    private sealed class RecordingRedLeaf : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.PathAndQuery;
            lock (Requests) Requests.Add(path);
            object body = path.StartsWith("/api/entities", StringComparison.Ordinal)
                ? new
                {
                    items = new[]
                    {
                        new
                        {
                            id = "entity-1", name = "Worker",
                            data = JsonSerializer.Serialize(new
                            {
                                session_id = "session-1", provider = "test", status = "Active", user_id = "owner",
                                started_at = "2026-10-24T10:00:00Z", stop_reason = "stopped-for-test",
                                external_session_id = "provider-thread-1", job_id = "9c7d4f1e-0a52-4e1b-9b7c-3f1d2e6a8b90",
                                message_count = 1,
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
                            id = 41L,
                            createdAt = "2026-10-24T10:00:01Z",
                            data = JsonSerializer.Serialize(new
                            {
                                session_id = "session-1", role = "assistant", event_type = "text", content = "hello",
                                epoch = TranscriptOrdering.Epoch("test", "session-1"), sequence = 7L,
                                timestamp = "2026-10-24T10:00:01Z",
                            }),
                        },
                    },
                };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
            });
        }
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
