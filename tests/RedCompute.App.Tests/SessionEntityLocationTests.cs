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

// The reader remembers where a session's ai-session entity is (session id -> entity id) and reads it by id,
// never what it says: RedLeaf's entity API and relocation also write it, so every read is fresh and the
// caller's access is decided on the entity as it is now.
[Collection("Unified session endpoint mapping")]
public sealed class SessionEntityLocationTests
{
    [Fact]
    public async Task The_second_read_fetches_the_entity_by_id_instead_of_the_json_query()
    {
        var redLeaf = new MutableRedLeaf();
        var reader = Reader(redLeaf);

        var (first, firstId) = await reader.GetSessionInfoAsync("session-1");
        var (second, secondId) = await reader.GetSessionInfoAsync("session-1");

        Assert.Equal(("session-1", "entity-1"), (first?.Id, firstId));
        Assert.Equal(("session-1", "entity-1"), (second?.Id, secondId));
        Assert.Equal(["query", "by-id"], redLeaf.Reads);
        Assert.Equal(1, reader.KnownEntityIds);
    }

    [Fact]
    public async Task Every_read_sees_the_entity_as_it_is_now()
    {
        var redLeaf = new MutableRedLeaf();
        var reader = Reader(redLeaf);
        await reader.GetSessionInfoAsync("session-1");

        redLeaf.Data["project_path"] = @"D:\moved\project";
        redLeaf.Data["status"] = "Stopped";
        var (info, _) = await reader.GetSessionInfoAsync("session-1");

        Assert.Equal(@"D:\moved\project", info?.ProjectPath);
        Assert.Equal(SessionStatus.Stopped, info?.Status);
    }

    [Fact]
    public async Task A_deleted_entity_is_forgotten_and_found_again_by_the_query()
    {
        var redLeaf = new MutableRedLeaf();
        var reader = Reader(redLeaf);
        await reader.GetSessionInfoAsync("session-1");

        redLeaf.EntityId = "entity-2"; // deleted, then recreated by the mirror under a new id
        var (info, entityId) = await reader.GetSessionInfoAsync("session-1");
        Assert.Equal(("session-1", "entity-2"), (info?.Id, entityId));
        Assert.Equal(["query", "by-id:404", "query"], redLeaf.Reads);

        redLeaf.Deleted = true;
        Assert.Equal((null, null), await reader.GetSessionInfoAsync("session-1"));
        Assert.Equal(0, reader.KnownEntityIds);
    }

    [Fact]
    public async Task An_id_that_answers_with_another_session_is_forgotten()
    {
        var redLeaf = new MutableRedLeaf();
        var reader = Reader(redLeaf);
        await reader.GetSessionInfoAsync("session-1");

        redLeaf.ByIdSessionOverride = "session-other";
        var (info, _) = await reader.GetSessionInfoAsync("session-1");

        Assert.Equal("session-1", info?.Id);
        Assert.Equal(["query", "by-id", "query"], redLeaf.Reads);
    }

    [Fact]
    public async Task The_map_is_bounded()
    {
        var redLeaf = new MutableRedLeaf { AnySession = true };
        var reader = Reader(redLeaf);
        for (var i = 0; i <= RedLeafSessionReader.MaxKnownEntityIds; i++)
            await reader.GetSessionInfoAsync($"session-{i}");
        Assert.InRange(reader.KnownEntityIds, 1, RedLeafSessionReader.MaxKnownEntityIds);
    }

    [Fact]
    public async Task A_caller_loses_the_session_the_moment_its_owner_changes()
    {
        var redLeaf = new MutableRedLeaf();
        var app = await SessionApp.CreateAsync(Reader(redLeaf));

        Assert.Equal(200, await app.ReadAsync("owner"));
        redLeaf.Data["user_id"] = "someone-else";
        Assert.NotEqual(200, await app.ReadAsync("owner"));
        Assert.Equal(200, await app.ReadAsync("someone-else"));
        Assert.Contains("by-id", redLeaf.Reads);
        await app.DisposeAsync();
    }

    [Fact]
    public async Task A_caller_loses_the_session_the_moment_it_becomes_confidential()
    {
        var redLeaf = new MutableRedLeaf();
        redLeaf.Data.Remove("user_id"); // ownerless: every caller may read it
        var app = await SessionApp.CreateAsync(Reader(redLeaf));

        Assert.Equal(200, await app.ReadAsync("visitor"));
        redLeaf.Data["confidential"] = true;
        redLeaf.Data["user_id"] = "owner";
        redLeaf.Data["owner_agent_id"] = "agent-1";
        Assert.NotEqual(200, await app.ReadAsync("visitor"));
        Assert.Equal(["query", "by-id"], redLeaf.Reads.Where(read => !read.StartsWith("records", StringComparison.Ordinal)));
        await app.DisposeAsync();
    }

    private static RedLeafSessionReader Reader(MutableRedLeaf redLeaf)
    {
        var config = new RedComputeConfig { RedLeafUrl = "http://redleaf" };
        var log = new Action<string, Guid?>((_, _) => { });
        var quality = new QualityModeService(config, log, new ProviderConfigService(config, log),
            new HttpClient(new MutableRedLeaf()),
            Path.Combine(Path.GetTempPath(), $"redcompute-entity-location-{Guid.NewGuid():N}.json"),
            TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(2));
        return new RedLeafSessionReader(new HttpClient(redLeaf) { BaseAddress = new Uri("http://redleaf/") }, quality);
    }

    /// <summary>One RedLeaf holding one ai-session entity the test can change, delete or move between reads.</summary>
    private sealed class MutableRedLeaf : HttpMessageHandler
    {
        public Dictionary<string, object?> Data { get; } = new()
        {
            ["session_id"] = "session-1", ["provider"] = "test", ["status"] = "Idle", ["user_id"] = "owner",
            ["started_at"] = "2026-11-28T10:00:00Z", ["project_path"] = @"C:\work\project",
        };
        public string EntityId { get; set; } = "entity-1";
        public bool Deleted { get; set; }
        public bool AnySession { get; init; }
        public string? ByIdSessionOverride { get; set; }
        public List<string> Reads { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            var query = request.RequestUri.Query;
            if (path.StartsWith("/api/streams/session-messages/records", StringComparison.Ordinal))
            {
                lock (Reads) Reads.Add("records");
                return Json(new { items = Array.Empty<object>() });
            }
            if (path == "/api/entities")
            {
                lock (Reads) Reads.Add("query");
                var requested = Uri.UnescapeDataString(query.Split("data.session_id=")[1].Split('&')[0]);
                if (Deleted || (!AnySession && requested != (string)Data["session_id"]!))
                    return Json(new { items = Array.Empty<object>() });
                return Json(new { items = new[] { Entity(AnySession ? requested : null) } });
            }
            if (path.StartsWith("/api/entities/", StringComparison.Ordinal))
            {
                var id = Uri.UnescapeDataString(path["/api/entities/".Length..]);
                if (Deleted || id != EntityId)
                {
                    lock (Reads) Reads.Add("by-id:404");
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
                }
                lock (Reads) Reads.Add("by-id");
                return Json(Entity(ByIdSessionOverride));
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private object Entity(string? sessionId)
        {
            var data = new Dictionary<string, object?>(Data);
            if (sessionId is not null) data["session_id"] = sessionId;
            var id = sessionId is not null && AnySession ? $"entity-{sessionId}" : EntityId;
            return new { id, slug = $"ai-session-test-{data["session_id"]}", typeSlug = "ai-session", name = "Worker", data = JsonSerializer.Serialize(data) };
        }

        private static Task<HttpResponseMessage> Json(object body) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
        });
    }

    /// <summary>GET /ai-session/sessions/{id} mapped once, with one reader shared by every request.</summary>
    private sealed class SessionApp : IAsyncDisposable
    {
        private readonly SessionRecoveryTests.Fixture _fixture;
        private readonly WebApplication _app;
        private readonly RouteEndpoint _endpoint;

        private SessionApp(SessionRecoveryTests.Fixture fixture, WebApplication app, RouteEndpoint endpoint)
            => (_fixture, _app, _endpoint) = (fixture, app, endpoint);

        public static Task<SessionApp> CreateAsync(RedLeafSessionReader reader)
        {
            var fixture = new SessionRecoveryTests.Fixture("maintenance_restart");
            var provider = DispatchProxy.Create<SessionWorkStateTests.ITestProvider, SessionWorkStateTests.LiveProvider>();
            var registry = new CapabilityRegistry();
            registry.Register("ai-session", new CapabilityDefinition { Slug = "ai-session", DisplayName = "AI Session" },
                new CapabilityConfig(), new Dictionary<string, IBackendProvider> { ["test"] = provider }, "test");
            var app = WebApplication.CreateSlimBuilder().Build();
            UnifiedSessionEndpoints.Map(new EndpointRegistry(app), registry, new NullJobs(), (_, _) => { }, fixture.Config,
                redLeafReader: reader, attachmentStore: fixture.Attachments, inputQueue: fixture.Service());
            var endpoint = ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints).OfType<RouteEndpoint>()
                .Single(item => item.RoutePattern.RawText == "/ai-session/sessions/{id}"
                    && item.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods.Contains("GET"));
            return Task.FromResult(new SessionApp(fixture, app, endpoint));
        }

        public async Task<int> ReadAsync(string subject)
        {
            var context = new DefaultHttpContext
            {
                RequestServices = _app.Services,
                User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", subject)], "signed")),
            };
            context.SetEndpoint(_endpoint);
            context.Request.Method = "GET";
            context.Request.QueryString = new QueryString("?tail=0");
            context.Request.RouteValues["id"] = "session-1";
            context.Response.Body = new MemoryStream();
            await _endpoint.RequestDelegate!(context);
            return context.Response.StatusCode;
        }

        public async ValueTask DisposeAsync()
        {
            await _app.DisposeAsync();
            _fixture.Dispose();
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
