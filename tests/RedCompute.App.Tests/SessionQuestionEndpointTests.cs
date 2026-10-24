using System.Net;
using System.Reflection;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
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

// The session read exposes what the session is parked on, under the read's own authorization;
// callback registration accepts an opt-in questionUrl.
[Collection("Unified session endpoint mapping")]
public sealed class SessionQuestionEndpointTests
{
    [Fact]
    public async Task Session_read_lists_pending_questions_beside_the_transcript_not_inside_it()
    {
        var provider = CreateProvider();
        ((QuestionProvider)(object)provider).Pending.Add(SessionQuestionCallbackTests.Question("req-1"));

        var (status, body) = await GetAsync(provider, "owner");

        Assert.Equal(200, status);
        var question = Assert.Single(body.GetProperty("pendingQuestions").EnumerateArray());
        Assert.Equal("req-1", question.GetProperty("requestId").GetString());
        Assert.Equal("AskUserQuestion", question.GetProperty("toolName").GetString());
        Assert.Equal("toolu_ask", question.GetProperty("toolUseId").GetString());
        Assert.Equal("Which database?", question.GetProperty("questions")[0].GetProperty("question").GetString());
        Assert.Equal(2, question.GetProperty("questions")[0].GetProperty("options").GetArrayLength());
        Assert.Equal(DateTimeOffset.Parse("2026-10-24T10:28:52Z"), question.GetProperty("askedAt").GetDateTimeOffset());
        Assert.Equal(DateTimeOffset.Parse("2026-10-24T10:58:52Z"), question.GetProperty("expiresAt").GetDateTimeOffset());
        Assert.Empty(body.GetProperty("messages").EnumerateArray());
        Assert.False(body.GetProperty("session").TryGetProperty("pendingQuestions", out _));
    }

    [Fact]
    public async Task Session_read_reports_an_empty_list_when_nothing_is_pending()
    {
        var (status, body) = await GetAsync(CreateProvider(), "owner");

        Assert.Equal(200, status);
        Assert.Empty(body.GetProperty("pendingQuestions").EnumerateArray());
    }

    [Fact]
    public async Task Other_users_cannot_read_pending_questions()
    {
        var provider = CreateProvider();
        ((QuestionProvider)(object)provider).Pending.Add(SessionQuestionCallbackTests.Question("req-1"));

        var (status, body) = await GetAsync(provider, "intruder");

        Assert.Equal(403, status);
        Assert.False(body.ValueKind == JsonValueKind.Object && body.TryGetProperty("pendingQuestions", out _));
    }

    [Theory]
    [InlineData("not a url")]
    [InlineData("ftp://localhost/question")]
    [InlineData(42)]
    public async Task Callback_registration_rejects_an_invalid_question_url(object questionUrl)
    {
        var (status, posts) = await RegisterAsync(new { url = "http://localhost/complete", questionUrl });

        Assert.Equal(422, status);
        Assert.False(posts.Reader.TryRead(out _));
    }

    [Fact]
    public async Task Callback_registration_with_a_question_url_while_parked_notifies_that_url()
    {
        var (status, posts) = await RegisterAsync(
            new { url = "http://localhost/complete", questionUrl = "http://localhost/question" }, parked: true);

        Assert.Equal(200, status);
        var (url, body) = await posts.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("http://localhost/question", url);
        Assert.Equal("req-1", body.GetProperty("requestId").GetString());
    }

    [Fact]
    public async Task Callback_registration_without_a_question_url_never_receives_one()
    {
        var (status, posts) = await RegisterAsync(new { url = "http://localhost/complete" }, parked: true);

        Assert.Equal(200, status);
        await Task.Delay(300);
        Assert.False(posts.Reader.TryRead(out _));
    }

    [Fact]
    public async Task Discovery_describes_pending_questions_and_the_question_url()
    {
        using var f = new SessionRecoveryTests.Fixture("maintenance_restart");
        var builder = WebApplication.CreateSlimBuilder();
        await using var app = builder.Build();
        var endpoints = new EndpointRegistry(app);
        UnifiedSessionEndpoints.Map(endpoints, Register(CreateProvider()), new MemoryJobTracker(), (_, _) => { }, f.Config,
            attachmentStore: f.Attachments, inputQueue: f.Service());

        var read = endpoints.GetEndpoints().Single(e => e.Method == "GET" && e.Path == "/ai-session/sessions/{id}");
        Assert.Contains("pendingQuestions", read.Description);
        // The response schema (only it says "waits indefinitely") reaches the discovery descriptor.
        Assert.Contains("waits indefinitely", JsonSerializer.Serialize<object>(read));
        var callback = endpoints.GetEndpoints().Single(e => e.Method == "POST" && e.Path == "/ai-session/sessions/{id}/callback");
        Assert.Contains(callback.Parameters!, p => p.Name == "questionUrl" && !p.Required);
    }

    private static async Task<(int Status, JsonElement Body)> GetAsync(ITestProvider provider, string user)
    {
        using var f = new SessionRecoveryTests.Fixture("maintenance_restart");
        var builder = WebApplication.CreateSlimBuilder();
        await using var app = builder.Build();
        UnifiedSessionEndpoints.Map(new EndpointRegistry(app), Register(provider), new MemoryJobTracker(), (_, _) => { }, f.Config,
            redLeafReader: Reader(), attachmentStore: f.Attachments, inputQueue: f.Service());
        var context = Context(app, "/ai-session/sessions/{id}", "GET", user);
        await Endpoint(app, "/ai-session/sessions/{id}", "GET").RequestDelegate!(context);
        context.Response.Body.Position = 0;
        var text = await new StreamReader(context.Response.Body).ReadToEndAsync();
        return (context.Response.StatusCode, text.Length == 0 ? default : JsonSerializer.Deserialize<JsonElement>(text));
    }

    private static async Task<(int Status, Channel<(string Url, JsonElement Body)> Posts)> RegisterAsync(object body, bool parked = false)
    {
        using var f = new SessionRecoveryTests.Fixture("maintenance_restart");
        var provider = CreateProvider();
        if (parked) ((QuestionProvider)(object)provider).Pending.Add(SessionQuestionCallbackTests.Question("req-1"));
        var posts = Channel.CreateUnbounded<(string Url, JsonElement Body)>();
        // Not disposed here: a notification fired by the registration may still be in flight.
        var http = new HttpClient(new Handler(async r => {
            await posts.Writer.WriteAsync((r.RequestUri!.ToString(), JsonSerializer.Deserialize<JsonElement>(await r.Content!.ReadAsStringAsync())));
            return new HttpResponseMessage(HttpStatusCode.OK); }));
        var callbacks = new SessionCallbackRegistry(http, (_, _) => { });
        callbacks.AttachQuestionSource(provider.GetPendingQuestions, id => provider.GetSession(id).Info);
        var builder = WebApplication.CreateSlimBuilder();
        await using var app = builder.Build();
        UnifiedSessionEndpoints.Map(new EndpointRegistry(app), Register(provider), new MemoryJobTracker(), (_, _) => { }, f.Config,
            callbacks: callbacks, attachmentStore: f.Attachments, inputQueue: f.Service());
        var context = Context(app, "/ai-session/sessions/{id}/callback", "POST", "owner");
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(body));
        context.Request.ContentType = "application/json";
        context.Request.Body = new MemoryStream(bytes); context.Request.ContentLength = bytes.Length;
        context.Features.Set<Microsoft.AspNetCore.Http.Features.IHttpRequestBodyDetectionFeature>(new BodyFeature());
        await Endpoint(app, "/ai-session/sessions/{id}/callback", "POST").RequestDelegate!(context);
        return (context.Response.StatusCode, posts);
    }

    private static RouteEndpoint Endpoint(WebApplication app, string pattern, string method)
        => ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints).OfType<RouteEndpoint>()
            .Single(item => item.RoutePattern.RawText == pattern
                && item.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods.Contains(method));

    private static DefaultHttpContext Context(WebApplication app, string pattern, string method, string user)
    {
        var context = new DefaultHttpContext
        {
            RequestServices = app.Services,
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", user)], "signed")),
        };
        context.SetEndpoint(Endpoint(app, pattern, method));
        context.Request.Method = method;
        context.Request.RouteValues["id"] = "session-1";
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static RedLeafSessionReader Reader()
    {
        var config = new RedComputeConfig { RedLeafUrl = "http://redleaf" };
        var log = new Action<string, Guid?>((_, _) => { });
        var quality = new QualityModeService(config, log, new ProviderConfigService(config, log),
            new HttpClient(new Handler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)))),
            Path.Combine(Path.GetTempPath(), $"redcompute-question-test-{Guid.NewGuid():N}.json"),
            TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(2));
        var redLeaf = new HttpClient(new Handler(request =>
        {
            object body = request.RequestUri!.PathAndQuery.StartsWith("/api/entities", StringComparison.Ordinal)
                ? new
                {
                    items = new[]
                    {
                        new
                        {
                            id = "entity-1",
                            name = "Relay",
                            data = JsonSerializer.Serialize(new
                            {
                                session_id = "session-1", provider = "test", status = "Active", user_id = "owner",
                                started_at = "2026-10-24T10:00:00Z",
                            }),
                        },
                    },
                }
                : new { items = Array.Empty<object>() };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
            });
        })) { BaseAddress = new Uri("http://redleaf/") };
        return new RedLeafSessionReader(redLeaf, quality);
    }

    private static ITestProvider CreateProvider() => DispatchProxy.Create<ITestProvider, QuestionProvider>();

    private static CapabilityRegistry Register(ITestProvider provider)
    {
        var registry = new CapabilityRegistry();
        registry.Register("ai-session", new CapabilityDefinition { Slug = "ai-session", DisplayName = "AI Session" },
            new CapabilityConfig(), new Dictionary<string, IBackendProvider> { ["test"] = provider }, "test");
        return registry;
    }

    public interface ITestProvider : ISessionProvider, IBackendProvider { }

    public class QuestionProvider : DispatchProxy
    {
        public List<PendingSessionQuestion> Pending { get; } = [];

        protected override object? Invoke(MethodInfo? method, object?[]? args) => method!.Name switch
        {
            "get_ProviderId" => "test",
            "get_ProviderDisplayName" => "Test",
            "get_Capabilities" => SessionCapabilities.PersistentSessions,
            "get_Name" => "Test",
            "get_CapabilitySlug" => "ai-session",
            "get_HealthCheckInterval" => TimeSpan.FromSeconds(10),
            nameof(ISessionProvider.GetAvailableModels) => new List<ModelInfo>(),
            nameof(ISessionProvider.GetSession) => ((string)args![0]!) == "session-1"
                ? (Session(), new List<UnifiedMessageRecord>())
                : ((UnifiedSessionInfo?)null, new List<UnifiedMessageRecord>()),
            nameof(ISessionProvider.GetBackgroundTasks) => (IReadOnlyList<BackgroundTaskInfo>)[],
            nameof(ISessionProvider.GetPendingQuestions) => ((string)args![0]!) == "session-1"
                ? (IReadOnlyList<PendingSessionQuestion>)[.. Pending]
                : (IReadOnlyList<PendingSessionQuestion>)[],
            _ => throw new NotSupportedException(method.Name),
        };

        private static UnifiedSessionInfo Session() => new()
        {
            Id = "session-1", Provider = "test", ProjectName = "Nova", ProjectPath = "C:\\nova",
            Status = SessionStatus.Active, UserId = "owner", Title = "Relay",
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

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request);
    }

    private sealed class BodyFeature : Microsoft.AspNetCore.Http.Features.IHttpRequestBodyDetectionFeature { public bool CanHaveBody => true; }
}
