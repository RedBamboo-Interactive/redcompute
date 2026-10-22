using System.Reflection;
using System.Security.Claims;
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

// Stopping a background task is a session control like interrupt, so it gets exactly the
// interrupt endpoint's authorization: the same session lookup and the same read policy.
[Collection("Unified session endpoint mapping")]
public sealed class BackgroundTaskStopEndpointTests
{
    private const SessionCapabilities Full = SessionCapabilities.PersistentSessions | SessionCapabilities.Interrupt
        | SessionCapabilities.SupportsBackgroundTaskStop;

    [Fact]
    public async Task Owner_stop_reaches_the_provider_with_the_exact_task()
    {
        var (status, calls) = await InvokeAsync("stop", user: "owner");

        Assert.Equal(200, status);
        Assert.Equal([("session-1", "task-1")], calls);
    }

    [Theory]
    [InlineData(false, 403)]
    [InlineData(true, 404)]
    public async Task Other_users_are_denied_exactly_as_interrupt_denies_them(bool confidential, int expected)
    {
        var stop = await InvokeAsync("stop", user: "intruder", confidential: confidential);
        var interrupt = await InvokeAsync("interrupt", user: "intruder", confidential: confidential);

        Assert.Equal(expected, stop.Status);
        Assert.Equal(interrupt.Status, stop.Status);
        Assert.Empty(stop.Calls);
    }

    [Fact]
    public async Task Confidential_session_owner_gets_the_same_answer_as_interrupt()
    {
        var stop = await InvokeAsync("stop", user: "owner", confidential: true);
        var interrupt = await InvokeAsync("interrupt", user: "owner", confidential: true);

        Assert.Equal(interrupt.Status, stop.Status);
        Assert.Equal(stop.Status == 200 ? 1 : 0, stop.Calls.Count);
    }

    [Fact]
    public async Task Provider_without_the_capability_is_not_asked()
    {
        var (status, calls) = await InvokeAsync("stop", user: "owner",
            capabilities: SessionCapabilities.PersistentSessions | SessionCapabilities.Interrupt);

        Assert.Equal(422, status);
        Assert.Empty(calls);
    }

    [Fact]
    public async Task Unknown_session_is_not_found()
    {
        var (status, calls) = await InvokeAsync("stop", user: "owner", sessionId: "missing");

        Assert.Equal(404, status);
        Assert.Empty(calls);
    }

    [Fact]
    public async Task Discovery_describes_the_stop_route()
    {
        using var f = new SessionRecoveryTests.Fixture("maintenance_restart");
        var builder = WebApplication.CreateSlimBuilder();
        await using var app = builder.Build();
        var endpoints = new EndpointRegistry(app);
        UnifiedSessionEndpoints.Map(endpoints, Register(CreateProvider(Full, confidential: false)), new MemoryJobTracker(),
            (_, _) => { }, f.Config, attachmentStore: f.Attachments, inputQueue: f.Service());

        var stop = endpoints.GetEndpoints().Single(endpoint => endpoint.Method == "POST"
            && endpoint.Path == "/ai-session/sessions/{id}/background-tasks/{taskId}/stop");
        Assert.Contains(stop.Parameters!, parameter => parameter.Name == "taskId" && parameter.Required);
        Assert.Contains("background task", stop.Description, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<(int Status, List<(string, string)> Calls)> InvokeAsync(
        string action, string user, bool confidential = false, string sessionId = "session-1",
        SessionCapabilities capabilities = Full)
    {
        using var f = new SessionRecoveryTests.Fixture("maintenance_restart");
        var provider = CreateProvider(capabilities, confidential);
        var builder = WebApplication.CreateSlimBuilder();
        await using var app = builder.Build();
        UnifiedSessionEndpoints.Map(new EndpointRegistry(app), Register(provider), new MemoryJobTracker(),
            (_, _) => { }, f.Config, attachmentStore: f.Attachments, inputQueue: f.Service());

        var pattern = action == "stop"
            ? "/ai-session/sessions/{id}/background-tasks/{taskId}/stop"
            : "/ai-session/sessions/{id}/interrupt";
        var endpoint = ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Single(item => item.RoutePattern.RawText == pattern
                && item.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods.Contains("POST"));
        var context = new DefaultHttpContext
        {
            RequestServices = app.Services,
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", user)], "signed")),
        };
        context.SetEndpoint(endpoint);
        context.Request.Method = "POST";
        context.Request.RouteValues["id"] = sessionId;
        context.Request.RouteValues["taskId"] = "task-1";
        context.Response.Body = new MemoryStream();

        await endpoint.RequestDelegate!(context);
        return (context.Response.StatusCode, ((StopProvider)(object)provider).Calls);
    }

    private static ITestProvider CreateProvider(SessionCapabilities capabilities, bool confidential)
    {
        var provider = DispatchProxy.Create<ITestProvider, StopProvider>();
        var proxy = (StopProvider)(object)provider;
        proxy.Capabilities = capabilities;
        proxy.Confidential = confidential;
        return provider;
    }

    private static CapabilityRegistry Register(ITestProvider provider)
    {
        var registry = new CapabilityRegistry();
        registry.Register("ai-session", new CapabilityDefinition { Slug = "ai-session", DisplayName = "AI Session" },
            new CapabilityConfig(), new Dictionary<string, IBackendProvider> { ["test"] = provider }, "test");
        return registry;
    }

    public interface ITestProvider : ISessionProvider, IBackendProvider { }

    public class StopProvider : DispatchProxy
    {
        public SessionCapabilities Capabilities { get; set; }
        public bool Confidential { get; set; }
        public List<(string, string)> Calls { get; } = [];

        protected override object? Invoke(MethodInfo? method, object?[]? args) => method!.Name switch
        {
            "get_ProviderId" => "test",
            "get_ProviderDisplayName" => "Test",
            "get_Capabilities" => Capabilities,
            "get_Name" => "Test",
            "get_CapabilitySlug" => "ai-session",
            "get_HealthCheckInterval" => TimeSpan.FromSeconds(10),
            nameof(ISessionProvider.GetAvailableModels) => new List<ModelInfo>(),
            nameof(ISessionProvider.GetSession) => ((string)args![0]!) == "session-1"
                ? (Session(), new List<UnifiedMessageRecord>())
                : ((UnifiedSessionInfo?)null, new List<UnifiedMessageRecord>()),
            nameof(ISessionProvider.InterruptSession) => InterruptResult.Interrupted,
            nameof(ISessionProvider.GetBackgroundTasks) => (IReadOnlyList<BackgroundTaskInfo>)[],
            nameof(ISessionProvider.TryStopBackgroundTaskAsync) => Stop((string)args![0]!, (string)args[1]!),
            _ => throw new NotSupportedException(method.Name),
        };

        private Task<BackgroundTaskStopResult> Stop(string sessionId, string taskId)
        {
            Calls.Add((sessionId, taskId));
            return Task.FromResult(BackgroundTaskStopResult.Accepted());
        }

        private UnifiedSessionInfo Session() => new()
        {
            Id = "session-1", Provider = "test", ProjectName = "Nova", ProjectPath = "C:\\nova",
            Status = SessionStatus.Idle, UserId = "owner", Confidential = Confidential,
            OwnerAgentId = Confidential ? "agent-1" : null,
        };
    }

    private sealed class MemoryJobTracker : IJobTracker
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
