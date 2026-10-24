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

// workState is derived in one place (SessionWorkStates) from the turn status, parked questions and
// background tasks, and exposed on the session read and list. status stays the turn state.
[Collection("Unified session endpoint mapping")]
public sealed class SessionWorkStateTests
{
    [Theory]
    [InlineData(SessionStatus.Active, 0, null, "working")]
    [InlineData(SessionStatus.Starting, 0, null, "working")]
    [InlineData(SessionStatus.Active, 1, null, "awaiting_answer")]
    [InlineData(SessionStatus.Active, 1, "running", "awaiting_answer")]
    [InlineData(SessionStatus.Active, 0, "running", "working")]
    [InlineData(SessionStatus.Idle, 0, "running", "background")]
    [InlineData(SessionStatus.Idle, 0, "completed", "idle")]
    [InlineData(SessionStatus.Idle, 0, "lost", "idle")]
    [InlineData(SessionStatus.Idle, 0, null, "idle")]
    [InlineData(SessionStatus.Stopped, 0, "running", "idle")]
    [InlineData(SessionStatus.Error, 0, null, "idle")]
    public void Derivation_table(SessionStatus status, int pending, string? taskStatus, string expected)
        => Assert.Equal(expected, SessionWorkStates.Derive(status, pending,
            taskStatus is null ? [] : [new BackgroundTaskInfo { TaskId = "t", Kind = "shell", Status = taskStatus }]));

    [Fact]
    public async Task Read_and_list_follow_every_transition_without_changing_status()
    {
        var provider = DispatchProxy.Create<ITestProvider, LiveProvider>();
        var live = (LiveProvider)(object)provider;

        async Task Expect(SessionStatus status, string workState)
        {
            live.Status = status;
            var read = await InvokeAsync(provider, "/ai-session/sessions/{id}");
            Assert.Equal((int)status, read.GetProperty("session").GetProperty("status").GetInt32());
            Assert.Equal(workState, read.GetProperty("session").GetProperty("workState").GetString());
            var listed = Assert.Single((await InvokeAsync(provider, "/ai-session/sessions")).EnumerateArray());
            Assert.Equal(workState, listed.GetProperty("workState").GetString());
        }

        await Expect(SessionStatus.Active, "working");
        live.Pending.Add(SessionQuestionCallbackTests.Question("req-1"));
        await Expect(SessionStatus.Active, "awaiting_answer");
        live.Pending.Clear();
        await Expect(SessionStatus.Active, "working");
        live.Tasks = [new BackgroundTaskInfo { TaskId = "b1", Kind = "agent", Status = "running" }];
        await Expect(SessionStatus.Idle, "background");
        await Expect(SessionStatus.Active, "working"); // the background work woke the session
        live.Tasks = [new BackgroundTaskInfo { TaskId = "b1", Kind = "agent", Status = "completed" }];
        await Expect(SessionStatus.Idle, "idle");
        await Expect(SessionStatus.Stopped, "idle");
    }

    private static async Task<JsonElement> InvokeAsync(ITestProvider provider, string pattern)
    {
        using var f = new SessionRecoveryTests.Fixture("maintenance_restart");
        var registry = new CapabilityRegistry();
        registry.Register("ai-session", new CapabilityDefinition { Slug = "ai-session", DisplayName = "AI Session" },
            new CapabilityConfig(), new Dictionary<string, IBackendProvider> { ["test"] = provider }, "test");
        var builder = WebApplication.CreateSlimBuilder();
        await using var app = builder.Build();
        UnifiedSessionEndpoints.Map(new EndpointRegistry(app), registry, new NullJobs(), (_, _) => { }, f.Config,
            redLeafReader: Reader((LiveProvider)(object)provider), attachmentStore: f.Attachments, inputQueue: f.Service());
        var endpoint = ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints).OfType<RouteEndpoint>()
            .Single(item => item.RoutePattern.RawText == pattern
                && item.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods.Contains("GET"));
        var context = new DefaultHttpContext
        {
            RequestServices = app.Services,
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "owner")], "signed")),
        };
        context.SetEndpoint(endpoint);
        context.Request.Method = "GET";
        context.Request.RouteValues["id"] = "session-1";
        context.Response.Body = new MemoryStream();
        await endpoint.RequestDelegate!(context);
        Assert.Equal(200, context.Response.StatusCode);
        context.Response.Body.Position = 0;
        return JsonSerializer.Deserialize<JsonElement>(await new StreamReader(context.Response.Body).ReadToEndAsync());
    }

    private static RedLeafSessionReader Reader(LiveProvider live)
    {
        var config = new RedComputeConfig { RedLeafUrl = "http://redleaf" };
        var log = new Action<string, Guid?>((_, _) => { });
        var quality = new QualityModeService(config, log, new ProviderConfigService(config, log),
            new HttpClient(new Handler(_ => new HttpResponseMessage(HttpStatusCode.NotFound))),
            Path.Combine(Path.GetTempPath(), $"redcompute-workstate-{Guid.NewGuid():N}.json"),
            TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(2));
        return new RedLeafSessionReader(new HttpClient(new Handler(request =>
        {
            object body = request.RequestUri!.PathAndQuery.StartsWith("/api/entities", StringComparison.Ordinal)
                ? new
                {
                    items = new[]
                    {
                        new
                        {
                            id = "entity-1", name = "Worker",
                            data = JsonSerializer.Serialize(new
                            {
                                session_id = "session-1", provider = "test", status = live.Status.ToString(), user_id = "owner",
                                started_at = "2026-10-24T10:00:00Z",
                            }),
                        },
                    },
                }
                : new { items = Array.Empty<object>() };
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
            };
        })) { BaseAddress = new Uri("http://redleaf/") }, quality);
    }

    public interface ITestProvider : ISessionProvider, IBackendProvider { }

    public class LiveProvider : DispatchProxy
    {
        public SessionStatus Status { get; set; } = SessionStatus.Active;
        public List<PendingSessionQuestion> Pending { get; } = [];
        public IReadOnlyList<BackgroundTaskInfo> Tasks { get; set; } = [];

        protected override object? Invoke(MethodInfo? method, object?[]? args) => method!.Name switch
        {
            "get_ProviderId" => "test",
            "get_ProviderDisplayName" => "Test",
            "get_Capabilities" => SessionCapabilities.PersistentSessions,
            "get_Name" => "Test",
            "get_CapabilitySlug" => "ai-session",
            "get_HealthCheckInterval" => TimeSpan.FromSeconds(10),
            nameof(ISessionProvider.GetAvailableModels) => new List<ModelInfo>(),
            nameof(ISessionProvider.GetBackgroundTasks) => Tasks,
            nameof(ISessionProvider.GetPendingQuestions) => (IReadOnlyList<PendingSessionQuestion>)[.. Pending],
            _ => throw new NotSupportedException(method.Name),
        };
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

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(send(request));
    }
}
