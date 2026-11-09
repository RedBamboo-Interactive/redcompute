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
using RedCompute.Core.Jobs;
using RedCompute.Core.Providers;
using RedCompute.Core.Sessions;
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
            [.. DiscordOpenContext, new("session", sessionId), new("ai-session-provider", "claude-code", Name: "Claude Code"),
                new(JobCauses.ContextKind, JobCauses.Agent)],
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
                new("ai-session-provider", "claude-code", Name: "Claude Code"),
                new(JobCauses.ContextKind, JobCauses.Agent)],
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
        Assert.Equal(caller.Where(c => c.Kind != "session").Take(13), context.Take(13));
        Assert.Equal(new ExecutionContextReference("session", "own-session"), context[13]);
        Assert.Equal("ai-session-provider", context[14].Kind);
        Assert.Equal(new ExecutionContextReference(JobCauses.ContextKind, JobCauses.Agent), context[15]);
        // Without a session id (stateless execution) the caller context is untouched.
        var stateless = SessionExecutionToken.ChildContext(caller, "codex", "Codex", null);
        Assert.Equal(16, stateless.Count);
        Assert.Equal(caller.Take(14), stateless.Take(14));
    }

    [Theory]
    [InlineData("own-session")]
    [InlineData(null)]
    public void What_a_session_process_starts_is_the_Agents_even_when_a_person_started_the_session(string? sessionId)
    {
        ExecutionContextReference[] started =
        [
            new("discussion", "discussion-1"),
            new(JobCauses.ContextKind, JobCauses.Beneficiary),
        ];

        var context = SessionExecutionToken.ChildContext(started, "codex", "Codex", sessionId);

        Assert.Equal(new ExecutionContextReference(JobCauses.ContextKind, JobCauses.Agent),
            Assert.Single(context, item => item.Kind == JobCauses.ContextKind));
    }

    // ---- a delegated session inherits its parent's level, which is also its cap ----
    // The parent is read from the start identity's signed context: a session process's token names
    // its own session, so whatever that process starts is delegated by it.

    private static readonly SessionAccessPolicy FullEnabled = new()
    {
        DefaultLevel = SessionAccessLevels.Ask,
        FullAccess = new FullAccessAcknowledgement(true, 1),
    };

    [Theory]
    [InlineData(SessionAccessLevels.Full, "bypassPermissions")]
    [InlineData(SessionAccessLevels.Auto, "auto")]
    [InlineData(SessionAccessLevels.Ask, "acceptEdits")]
    public async Task A_delegated_session_inherits_its_parents_level_as_level_and_cap(string level, string mode)
    {
        await using var host = Host.Create(this, FullEnabled);
        var parentId = await StartParentAsync(host, level);

        var (status, body) = await host.InvokeAsync("/ai-session/sessions",
            Parent([.. DiscordOpenContext, new("session", parentId)]), new { projectPath = _root, provider = "claude-code" });

        Assert.True(status == 200, $"{status}: {body}");
        var child = JsonDocument.Parse(body).RootElement;
        Assert.Equal(level, child.GetProperty("accessLevel").GetString());
        Assert.Equal(level, child.GetProperty("maxAccessLevel").GetString());
        Assert.Equal(SessionAccessLevelSources.Parent, child.GetProperty("accessLevelSource").GetString());
        var args = _launches[^1].ArgumentList.ToList();
        Assert.Equal(mode, args[args.IndexOf("--permission-mode") + 1]);
    }

    [Fact]
    public async Task A_child_of_an_auto_parent_cannot_be_raised_above_auto_even_by_a_person()
    {
        await using var host = Host.Create(this, FullEnabled);
        var parentId = await StartParentAsync(host, SessionAccessLevels.Auto);
        var (_, body) = await host.InvokeAsync("/ai-session/sessions",
            Parent([new("session", parentId)]), new { projectPath = _root, provider = "claude-code" });
        var childId = JsonDocument.Parse(body).RootElement.GetProperty("id").GetString()!;
        // Stopped, so a change is persisted without a CLI to confirm it (this fake never answers).
        await host.Claude.ForceKill(childId);
        var child = host.Provider.GetSession(childId).Info!;

        var raise = await host.Access.ChangeAsync(host.Provider, child, SessionAccessLevels.Full, personPresent: true);
        var lower = await host.Access.ChangeAsync(host.Provider, child, SessionAccessLevels.Ask, personPresent: true);
        var back = await host.Access.ChangeAsync(host.Provider, child, SessionAccessLevels.Auto, personPresent: true);

        Assert.Equal(403, raise.Status);
        Assert.Equal(SessionAccessResolver.AboveSessionCap, raise.Error);
        Assert.Equal(200, lower.Status);
        Assert.Equal(200, back.Status);
        Assert.Equal(new SessionAccessState(SessionAccessLevels.Auto, SessionAccessLevels.Auto,
            AccessLevelSource: SessionAccessLevelSources.Explicit), host.Provider.GetAccessState(childId));
    }

    [Fact]
    public async Task A_request_body_naming_a_parent_is_ignored()
    {
        await using var host = Host.Create(this, FullEnabled);
        var parentId = await StartParentAsync(host, SessionAccessLevels.Full);

        var (status, body) = await host.InvokeAsync("/ai-session/sessions", Parent(DiscordOpenContext), new
        {
            projectPath = _root, provider = "claude-code",
            parentSessionId = parentId, parentSession = parentId, session = parentId, accessLevelSource = "parent",
        });

        Assert.True(status == 200, $"{status}: {body}");
        var child = JsonDocument.Parse(body).RootElement;
        Assert.Equal(SessionAccessLevels.Ask, child.GetProperty("accessLevel").GetString());
        Assert.Equal(JsonValueKind.Null, child.GetProperty("maxAccessLevel").ValueKind);
        Assert.Equal(SessionAccessLevelSources.Policy, child.GetProperty("accessLevelSource").GetString());
    }

    [Fact]
    public async Task An_unknown_parent_starts_at_ask_never_the_policy_default()
    {
        // The signed context names a session that cannot be established: falling back to the
        // policy default would hand a capped or unattended caller Full on an owner's install.
        await using var host = Host.Create(this, FullEnabled with { DefaultLevel = SessionAccessLevels.Full });

        var (status, body) = await host.InvokeAsync("/ai-session/sessions",
            Parent([.. DiscordOpenContext, new("session", "no-such-session")]), new { projectPath = _root, provider = "claude-code" });

        Assert.True(status == 200, $"{status}: {body}");
        var child = JsonDocument.Parse(body).RootElement;
        Assert.Equal(SessionAccessLevels.Ask, child.GetProperty("accessLevel").GetString());
        Assert.Equal(SessionAccessLevels.Ask, child.GetProperty("maxAccessLevel").GetString());
        Assert.Equal(SessionAccessLevelSources.Parent, child.GetProperty("accessLevelSource").GetString());
    }

    /// <summary>A person starts the delegating session at an explicit level.</summary>
    private async Task<string> StartParentAsync(Host host, string level)
    {
        var (status, body) = await host.InvokeAsync("/ai-session/sessions", parent: null,
            new { projectPath = _root, provider = "claude-code", accessLevel = level });
        Assert.True(status == 200, $"{status}: {body}");
        var root = JsonDocument.Parse(body).RootElement;
        Assert.Equal(level, root.GetProperty("accessLevel").GetString());
        return root.GetProperty("id").GetString()!;
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
            FileName = TestProcesses.StdinSink,
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
        public ClaudeCodeProvider Provider { get; private set; } = null!;
        public SessionAccessService Access { get; private set; } = null!;

        public static Host Create(SessionProcessIdentityTests test, SessionAccessPolicy? policy = null)
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
            var access = new SessionAccessService(new StaticSessionAccessPolicySource(policy ?? SessionAccessPolicy.NoRecord),
                () => registry.FindProviders<ISessionProvider>(), jobs.GetJob, (_, _) => { });
            UnifiedSessionEndpoints.Map(new EndpointRegistry(app), registry, jobs, (_, _) => { }, fixture.Config,
                attachmentStore: fixture.Attachments, inputQueue: fixture.Service(), access: access);
            return new Host(fixture, app, jwt, options, claude) { Provider = provider, Access = access };
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
            // As AuthMiddleware does for a verified execution token.
            if (parent is not null) ctx.Items[ExecutionIdentityClaims.HttpContextItemKey] = parent;
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
