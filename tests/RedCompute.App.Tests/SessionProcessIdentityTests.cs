using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using RedBamboo.AppHost.Auth;
using RedBamboo.AppHost.Discovery;
using RedCompute.App.Api.Endpoints;
using RedCompute.App.Data;
using RedCompute.App.Services;
using RedCompute.App.Services.Jobs;
using RedCompute.Core.Capabilities;
using RedCompute.Core.Configuration;
using RedCompute.Core.Providers;
using RedCompute.Plugin.ClaudeCode;
using RedCompute.PluginSdk;
using Xunit;
using Fakes = RedCompute.App.Tests.ClaudeDeveloperInstructionsTests;

namespace RedCompute.App.Tests;

// leaf-discord binds guild reads, reactions and DM outreach to the `session` entry of the signed
// process identity. A new session's id is allocated inside the provider, so the process token must
// be issued once that id exists, before the first turn, and never name a second session on resume.
[Collection("Unified session endpoint mapping")]
public sealed class SessionProcessIdentityTests : IDisposable
{
    private static readonly ExecutionContextReference[] DiscordOpenContext =
    [
        new("external-conversation", "discord:1546583557567287306:1562922009216950343"),
        new("external-generation", "1"),
        new("external-application", "1546583557567287306"),
        new("thread", "1562922009216950343"),
        new("discord-generation", "1"),
        new("discord-conversation", "1562922009216950343"),
        new("origin-entrypoint", Route: "/api/apps/nova/external-conversations"),
    ];

    private readonly string _root = Path.Combine(
        Environment.GetEnvironmentVariable("REDLEAF_SCRATCH_DIR") ?? Path.GetTempPath(),
        "redcompute-session-identity", Guid.NewGuid().ToString("N"));
    private readonly List<Process> _processes = [];
    private readonly List<ProcessStartInfo> _launches = [];

    public SessionProcessIdentityTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task New_session_process_token_names_its_own_session_from_the_first_turn()
    {
        await using var host = Host.Create(this);
        var parent = Parent(DiscordOpenContext);

        var (status, response) = await host.InvokeAsync("/ai-session/sessions", parent,
            new { projectPath = _root, provider = "claude-code" });

        Assert.True(status == 200, $"{status}: {response}");
        var sessionId = JsonDocument.Parse(response).RootElement.GetProperty("id").GetString();
        var child = host.ReadProcessIdentity(Assert.Single(_launches));
        Assert.Equal(parent.ExecutionId, child.ParentExecutionId);
        Assert.Equal(sessionId, Assert.Single(child.Context, c => c.Kind == "session").Id);
        Assert.Equal(
            [.. DiscordOpenContext, new("session", sessionId), new("ai-session-provider", "claude-code", Name: "Claude Code")],
            child.Context);
        Assert.Null(SessionScratch.Environment(null, sessionId: sessionId));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Resumed_session_process_token_has_exactly_one_session_entry(bool callerNamesAnotherSession)
    {
        await using var host = Host.Create(this);
        var (_, created) = await host.InvokeAsync("/ai-session/sessions", Parent(DiscordOpenContext),
            new { projectPath = _root, provider = "claude-code" });
        var sessionId = JsonDocument.Parse(created).RootElement.GetProperty("id").GetString()!;
        await host.Claude.ForceKill(sessionId);

        // Nova's resume provenance already names this session; a delegating parent names its own.
        ExecutionContextReference[] resumeContext =
        [
            new("external-conversation", "discord:1546583557567287306:1562922009216950343"),
            new("external-conversation-generation", "1"),
            new("session", callerNamesAnotherSession ? "delegating-parent" : sessionId),
        ];
        var (status, response) = await host.InvokeAsync($"/ai-session/sessions/{sessionId}/resume",
            Parent(resumeContext), body: null, routeId: sessionId);

        Assert.True(status == 200, $"{status}: {response}");
        Assert.Equal(2, _launches.Count);
        var child = host.ReadProcessIdentity(_launches[1]);
        Assert.Equal(sessionId, Assert.Single(child.Context, c => c.Kind == "session").Id);
        Assert.Equal(
            [resumeContext[0], resumeContext[1], new("session", sessionId),
                new("ai-session-provider", "claude-code", Name: "Claude Code")],
            child.Context);
    }

    [Fact]
    public async Task Session_started_without_suite_identity_receives_no_token()
    {
        await using var host = Host.Create(this);

        var (status, response) = await host.InvokeAsync("/ai-session/sessions", parent: null,
            new { projectPath = _root, provider = "claude-code" });

        Assert.True(status == 200, $"{status}: {response}");
        // Only an inherited ambient value may be present; RedCompute must not mint one.
        var launched = Assert.Single(_launches).Environment;
        Assert.Equal(Environment.GetEnvironmentVariable("REDLEAF_EXECUTION_TOKEN"),
            launched.TryGetValue("REDLEAF_EXECUTION_TOKEN", out var token) ? token : null);
    }

    [Fact]
    public void Child_context_keeps_caller_context_within_the_identity_limit()
    {
        var caller = Enumerable.Range(0, 16)
            .Select(i => new ExecutionContextReference(i == 3 ? "session" : $"kind-{i}", $"id-{i}"))
            .ToArray();

        var context = SessionExecutionToken.ChildContext(caller, "codex", "Codex", "own-session");

        Assert.Equal(16, context.Count);
        Assert.Equal(caller.Where(c => c.Kind != "session").Take(14), context.Take(14));
        Assert.Equal(new ExecutionContextReference("session", "own-session"), context[14]);
        Assert.Equal("ai-session-provider", context[15].Kind);
        // Without a session id (stateless execution) the caller context is untouched.
        Assert.Equal([.. caller.Take(15), context[15]],
            SessionExecutionToken.ChildContext(caller, "codex", "Codex", null));
    }

    private static ExecutionIdentity Parent(IReadOnlyList<ExecutionContextReference> context) => new(
        ExecutionIdentity.CurrentSchemaVersion,
        Guid.NewGuid().ToString(),
        new ExecutionAppIdentity("nova", "Nova"),
        new ExecutionActorIdentity("agent", "nova", "Nova", "nova-agent-entity"),
        new ExecutionBeneficiaryIdentity("user", "owner", "Owner"),
        context);

    private Process? Launch(ProcessStartInfo requested)
    {
        _launches.Add(requested);
        var process = Process.Start(new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "more.com"),
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        _processes.Add(process);
        return process;
    }

    public void Dispose()
    {
        foreach (var process in _processes)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            process.Dispose();
        }
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { }
    }

    private sealed class Host : IAsyncDisposable
    {
        private readonly SessionRecoveryTests.Fixture _fixture;
        private readonly WebApplication _app;
        private readonly JwtService _jwt;
        private readonly JwtOptions _options;

        private Host(SessionRecoveryTests.Fixture fixture, WebApplication app, JwtService jwt, JwtOptions options,
            ClaudeSessionService claude)
        {
            _fixture = fixture;
            _app = app;
            _jwt = jwt;
            _options = options;
            Claude = claude;
        }

        public ClaudeSessionService Claude { get; }

        public static Host Create(SessionProcessIdentityTests test)
        {
            var fixture = new SessionRecoveryTests.Fixture("maintenance_restart");
            var dbPath = Path.Combine(fixture.Root, "jobs.db");
            RedComputeDbContext Db() => new(dbPath);
            using (var db = Db()) db.Database.EnsureCreated();
            var jobs = new JobTrackingService(Db);
            fixture.Jobs = jobs;
            var store = new Fakes.MemoryStore();
            var claudePath = Path.Combine(test._root, "claude.exe");
            File.WriteAllText(claudePath, "");
            var claude = new ClaudeSessionService(new ClaudeConfig { ClaudePath = claudePath }, jobs, store,
                new Fakes.NullQualityModes(), () => null, (_, _) => { })
            {
                LaunchSessionProcess = test.Launch,
            };
            var provider = new ClaudeCodeProvider("ai-session", claude, store, jobs, (_, _) => { });
            var registry = new CapabilityRegistry();
            registry.Register("ai-session", new CapabilityDefinition { Slug = "ai-session", DisplayName = "AI Session" },
                new CapabilityConfig(), new Dictionary<string, IBackendProvider> { ["claude-code"] = provider }, "claude-code");

            var options = new JwtOptions { SigningKey = "session-process-identity-secret-123456789012345678901" };
            var jwt = new JwtService(options);
            var builder = WebApplication.CreateSlimBuilder();
            builder.Services.AddSingleton(options).AddSingleton<IExecutionTokenIssuer>(new ExecutionTokenIssuer(jwt, options));
            var app = builder.Build();
            UnifiedSessionEndpoints.Map(new EndpointRegistry(app), registry, jobs, (_, _) => { }, fixture.Config,
                attachmentStore: fixture.Attachments, inputQueue: fixture.Service());
            return new Host(fixture, app, jwt, options, claude);
        }

        public async Task<(int Status, string Body)> InvokeAsync(string route, ExecutionIdentity? parent,
            object? body, string? routeId = null)
        {
            var pattern = routeId is null ? route : route.Replace(routeId, "{id}");
            var endpoint = ((IEndpointRouteBuilder)_app).DataSources.SelectMany(d => d.Endpoints).OfType<RouteEndpoint>()
                .Single(e => e.RoutePattern.RawText == pattern
                    && e.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods.Contains("POST"));
            var principal = new ExecutionPrincipal("owner", "owner@test", "Owner", ["operator"]);
            var ctx = new DefaultHttpContext
            {
                RequestServices = _app.Services,
                User = _jwt.ValidateToken(parent is null
                    ? _jwt.GenerateAccessToken("owner", "owner@test", "Owner", ["operator"])
                    : new ExecutionTokenIssuer(_jwt, _options).Issue(parent, principal).AccessToken)!,
            };
            ctx.SetEndpoint(endpoint);
            if (routeId is not null) ctx.Request.RouteValues["id"] = routeId;
            ctx.Request.Method = "POST";
            ctx.Request.ContentType = "application/json";
            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(body ?? new { }));
            ctx.Request.Body = new MemoryStream(bytes);
            ctx.Request.ContentLength = bytes.Length;
            ctx.Features.Set<Microsoft.AspNetCore.Http.Features.IHttpRequestBodyDetectionFeature>(new Fakes.BodyFeature());
            ctx.Response.Body = new MemoryStream();

            using (parent is null ? null : ExecutionContextScope.Push(parent))
                await endpoint.RequestDelegate!(ctx);

            ctx.Response.Body.Position = 0;
            return (ctx.Response.StatusCode, await new StreamReader(ctx.Response.Body).ReadToEndAsync());
        }

        public ExecutionIdentity ReadProcessIdentity(ProcessStartInfo launch)
        {
            var principal = _jwt.ValidateToken(launch.Environment["REDLEAF_EXECUTION_TOKEN"]!);
            Assert.NotNull(principal);
            Assert.True(ExecutionIdentityClaims.TryRead(principal!, out var identity, out var error), error);
            return identity!;
        }

        public async ValueTask DisposeAsync()
        {
            await _app.DisposeAsync();
            _fixture.Dispose();
        }
    }
}
