using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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
using RedCompute.PluginSdk.Hosting.Local;
using Xunit;

namespace RedCompute.App.Tests;

/// <summary>
/// Placement by reference, Layer 2 (Phase 2 design, section 6), recorded on the code before any
/// placement support exists: every caller's legacy request shape (one per creation path, section 1,
/// taken from the callers' Layer 1 request goldens) goes through the real <c>/ai-session/sessions</c>
/// and <c>/ai-session/execute</c> endpoints, and what reaches the provider is recorded: project path,
/// scratch directory, repository id, the scratch environment the provider builds, the one-shot's
/// working directory, environment and provider parameters, and the job input. A subset runs the real
/// Claude Code provider on a capturing execution host and records the launch itself (file, argv, cwd,
/// environment overlay, containment) in the Phase 1 Layer A form.
/// <para>
/// When placement arrives, the placement-only body of each fixture must produce exactly these
/// captures; until then they pin today's behaviour. Goldens live in tests/Goldens/Placement; set
/// REDCOMPUTE_GOLDEN_RECORD=1 to (re)write them on unchanged code.
/// </para>
/// <para>
/// Not covered: <c>qualityTier</c> (it chooses the model, not the place, and needs the suite
/// catalog; a fixed <c>model</c> stands in where a caller sends one) and conversation-only sessions
/// (no path data; their workspace is allocated under the live compute data root).
/// </para>
/// </summary>
[Collection("Unified session endpoint mapping")]
public sealed class PlacementEquivalenceTests : IDisposable
{
    internal const string RepositoryId = "7e1e0000-0000-4000-8000-000000000001";
    private const string SessionId = "placement-session";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "redcompute-placement", Guid.NewGuid().ToString("N"));

    public PlacementEquivalenceTests()
    {
        foreach (var folder in new[] { "workspace", "scratch", Path.Combine("agents", "nova"), Path.Combine("roleplay", "opencode") })
            Directory.CreateDirectory(Path.Combine(_root, folder));
        var repo = Path.Combine(_root, "songes");
        Directory.CreateDirectory(repo);
        Git(repo, "init", "-q", "-b", "main");
        File.WriteAllText(Path.Combine(repo, "a.txt"), "a");
        Git(repo, "add", ".");
        Git(repo, "commit", "-q", "-m", "init");
        Git(repo, "worktree", "add", "-q", "-b", "laurent/x", Path.Combine(_root, "songes.worktrees", "laurent-x"));
    }

    // ── the creation paths (design section 1), legacy bodies as each caller sends them today ─────

    /// <summary>
    /// One legacy request per creation-path shape. <c>&lt;root&gt;</c> is this fixture's folder with
    /// backslashes, <c>&lt;root/&gt;</c> with forward slashes (the browser sends its picker's form).
    /// </summary>
    public static TheoryData<string> Cases() => new(Fixtures.Keys);

    internal static readonly IReadOnlyDictionary<string, PlacementCase> Fixtures = new Dictionary<string, PlacementCase>
    {
        // #1 nova MessagePipeline: mounted Agent workspace, kernel scratch (addDirs is ignored by /sessions).
        ["nova-discussion-mounted-workspace"] = Session("""
            {"addDirs":["<root>\\scratch"],"confidential":false,"projectPath":"<root>\\workspace","qualityTier":null,"scratchDir":"<root>\\scratch"}
            """),
        // #1 an Agent without a mounted workspace: the disposable workspace lives in the scratch folder.
        ["nova-discussion-disposable-workspace"] = Session("""
            {"addDirs":["<root>\\scratch"],"confidential":false,"projectPath":"<root>\\scratch","qualityTier":null,"scratchDir":"<root>\\scratch"}
            """),
        // #1 Heartbeat: resident and unattended.
        ["nova-heartbeat-resident-unattended"] = Session("""
            {"addDirs":["<root>\\scratch"],"confidential":false,"keepResident":true,"projectPath":"<root>\\workspace","qualityTier":null,"scratchDir":"<root>\\scratch","unattended":true}
            """),
        // #3 nova delegate: repository checkout, a linked worktree, an Agent workspace, another provider.
        ["nova-delegate-repository"] = Session("""
            {"projectPath":"<root>\\songes","repositoryId":"7e1e0000-0000-4000-8000-000000000001"}
            """),
        ["nova-delegate-branch-worktree"] = Session("""
            {"projectPath":"<root>\\songes.worktrees\\laurent-x","repositoryId":"7e1e0000-0000-4000-8000-000000000001"}
            """),
        ["nova-delegate-agent-workspace"] = Session("""
            {"projectPath":"<root>\\workspace"}
            """),
        ["nova-delegate-repository-codex"] = Session("""
            {"model":"gpt-5","projectPath":"<root>\\songes","provider":"codex","repositoryId":"7e1e0000-0000-4000-8000-000000000001"}
            """),
        // #7 Code's browser picker: forward slashes, checkout and worktree.
        ["code-session-checkout"] = Session("""
            {"projectPath":"<root/>/songes","repositoryId":"7e1e0000-0000-4000-8000-000000000001"}
            """),
        ["code-session-worktree"] = Session("""
            {"projectPath":"<root/>/songes.worktrees/laurent-x","repositoryId":"7e1e0000-0000-4000-8000-000000000001"}
            """),
        // #10 cap-ai-inference IAiInference.CreateSessionAsync: the old per-Agent folder.
        ["inference-session-agent-folder"] = Session("""
            {"projectPath":"<root>\\agents\\nova"}
            """),
        // #2 nova automation nova-session action, #4 redleaf AutomationService ai-session (same shape).
        ["nova-automation-session"] = Execute("""
            {"addDirs":["<root>\\scratch"],"allowedTools":["Read","Write","Edit","Glob","Grep","Bash","WebFetch","WebSearch","TodoWrite"],"confidential":false,"env":{"REDLEAF_SCRATCH_DIR":"<root>\\scratch","REDLEAF_SCRATCH_ROOT":"<root>\\scratch","TEMP":"<root>\\scratch","TMP":"<root>\\scratch","TMPDIR":"<root>\\scratch"},"maxTurns":50,"networkAccess":false,"prompt":"# Automation: Nightly review","provider":null,"sandbox":null,"timeout":3600,"unattended":true,"workingDir":"<root>\\workspace"}
            """, idempotencyKey: "automation:7e1e00000000400080000000000000b1:nova-session", jobName: "Nova: Nightly review"),
        // #8 Code executors (tackle, merge, review, issue): the repository found under the checkout.
        ["code-executor-tackle"] = Execute("""
            {"maxTurns":50,"prompt":"You are tackling GitHub issue #42","rationale":"Tackle issue #42: Crash on start","timeout":1800,"workingDir":"<root>\\songes"}
            """),
        // #6 redleaf setup probe, #12 cap-sentinel, #13 roleplay: the scratch folder is the cwd.
        ["redleaf-setup-probe"] = Execute("""
            {"env":{"REDLEAF_SCRATCH_DIR":"<root>\\scratch","REDLEAF_SCRATCH_ROOT":"<root>\\scratch","TEMP":"<root>\\scratch","TMP":"<root>\\scratch","TMPDIR":"<root>\\scratch"},"prompt":"Reply with OK.","timeout":120,"workingDir":"<root>\\scratch"}
            """),
        ["sentinel-review"] = Execute("""
            {"addDirs":[],"prompt":"Review the attached media.","timeout":300,"workingDir":"<root>\\scratch"}
            """),
        ["roleplay-turn"] = Execute("""
            {"env":{"OPENCODE_CONFIG_DIR":"<root>\\roleplay\\opencode","REDLEAF_SCRATCH_DIR":"<root>\\scratch","REDLEAF_SCRATCH_ROOT":"<root>\\scratch","TEMP":"<root>\\scratch","TMP":"<root>\\scratch","TMPDIR":"<root>\\scratch"},"prompt":"Continue the scene.","provider":"opencode","timeout":600,"workingDir":"<root>\\scratch"}
            """),
        // #11 cap-ai-inference flow node, agent mode: the old per-Agent folder, no scratch.
        ["inference-flow-agent"] = Execute("""
            {"prompt":"Summarise the day.","timeout":900,"workingDir":"<root>\\agents\\nova"}
            """),
    };

    private static PlacementCase Session(string body) => new("/ai-session/sessions", body.Trim());

    private static PlacementCase Execute(string body, string? idempotencyKey = null, string? jobName = null)
        => new("/ai-session/execute", body.Trim(), idempotencyKey, jobName);

    // ── Layer 2a: what reaches the provider ─────────────────────────────────────────────────────

    [WindowsTheory("Placement goldens are recorded on Windows (path bytes, separators included)")]
    [MemberData(nameof(Cases))]
    public async Task Legacy_request_reaches_the_provider_as_recorded(string name)
    {
        var fixture = Fixtures[name];
        var recorder = new RecordingProvider();
        var result = await InvokeAsync(fixture, jobs => recorder.Jobs = jobs,
            new Dictionary<string, IBackendProvider>
            {
                ["claude-code"] = recorder.As("claude-code"),
                ["codex"] = recorder.As("codex"),
                ["opencode"] = recorder.As("opencode"),
            });

        Assert.True(result.Status is 200 or 202, $"{name}: {result.Status} {result.Response}");
        var capture = new JsonObject
        {
            ["request"] = JsonNode.Parse(fixture.Body),
            ["route"] = fixture.Route,
            ["status"] = result.Status,
            ["provider"] = Assert.Single(recorder.Calls).DeepClone(),
            ["jobInput"] = result.JobInput is null ? null : JsonNode.Parse(result.JobInput),
        };
        PlacementGoldens.Verify(name, Normalizer().Apply(capture)!);
    }

    // ── Layer 2b: the launch itself, real Claude Code provider on a capturing host ─────────────

    [WindowsTheory("Placement goldens are recorded on Windows (path bytes, separators included)")]
    [InlineData("nova-discussion-mounted-workspace")]
    [InlineData("nova-delegate-branch-worktree")]
    [InlineData("nova-automation-session")]
    public async Task Legacy_request_launches_claude_as_recorded(string name)
    {
        var fixture = Fixtures[name];
        var hosted = new List<(ProcessStartInfo Start, string Containment)>();
        var host = new LocalExecutionHost("placement-node")
        {
            StartProcess = (start, mode, owner) =>
            {
                lock (hosted) hosted.Add((start, LocalExecutionHost.DescribeContainment(mode, owner)));
                return null;
            },
        };
        var claudePath = Path.Combine(_root, "cli", "claude.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(claudePath)!);
        File.WriteAllText(claudePath, "launch capture only, never executed");

        await InvokeAsync(fixture, _ => { }, providers: null, claude: jobs =>
        {
            var store = new ClaudeDeveloperInstructionsTests.MemoryStore();
            var service = new ClaudeSessionService(new ClaudeConfig { ClaudePath = claudePath }, jobs, store,
                new ClaudeDeveloperInstructionsTests.NullQualityModes(), () => null, (_, _) => { }, host: host);
            return new ClaudeCodeProvider("ai-session", service, store, jobs, (_, _) => { });
        });

        (ProcessStartInfo Start, string Containment) launch;
        lock (hosted) launch = Assert.Single(hosted);
        var normalizer = Normalizer();
        if (launch.Start.Environment.TryGetValue("REDLEAF_EXECUTION_TOKEN", out var token))
            normalizer.Add(token, "<execution-token>");
        // A one-shot's tool list or prompt may name its own isolated folder; a session's never does.
        var seam = fixture.Route == "/ai-session/execute" ? "claude.LaunchAgentProcess" : "claude.LaunchSessionProcess";
        PlacementGoldens.Verify("launch-" + name, LaunchGoldens.FromStartInfo(launch.Start, seam, launch.Containment, normalizer));
    }

    // ── harness ──────────────────────────────────────────────────────────────────────────────────

    private Normalizer Normalizer() => new Normalizer()
        .Add(_root, "<root>")
        .Add(SessionId, "<session-id>");

    /// <summary>The fixture's placeholders filled with this run's folder.</summary>
    private string Fill(string body)
        => body.Replace("<root/>", _root.Replace('\\', '/'))
            .Replace("<root>", _root.Replace("\\", "\\\\"));

    private async Task<(int Status, string Response, string? JobInput)> InvokeAsync(PlacementCase fixture,
        Action<IJobTracker> jobsReady, Dictionary<string, IBackendProvider>? providers,
        Func<IJobTracker, ClaudeCodeProvider>? claude = null)
    {
        using var f = new SessionRecoveryTests.Fixture("maintenance_restart");
        var dbPath = Path.Combine(f.Root, "jobs.db");
        RedComputeDbContext Db() => new(dbPath);
        using (var db = Db()) db.Database.EnsureCreated();
        var jobs = new JobTrackingService(Db);
        f.Jobs = jobs;
        jobsReady(jobs);
        providers ??= new Dictionary<string, IBackendProvider> { ["claude-code"] = claude!(jobs) };

        var registry = new CapabilityRegistry();
        registry.Register("ai-session", new CapabilityDefinition { Slug = "ai-session", DisplayName = "AI Session" },
            new CapabilityConfig(), providers, "claude-code");

        var options = new JwtOptions { SigningKey = "placement-equivalence-endpoint-secret-1234567890123456" };
        var jwt = new JwtService(options);
        var builder = WebApplication.CreateSlimBuilder();
        builder.Services.AddSingleton(options).AddSingleton<IExecutionTokenIssuer>(new ExecutionTokenIssuer(jwt, options));
        await using var app = builder.Build();
        var repositories = new RepositoryReferenceValidator(new HttpClient(new RepositoryEntity(Path.Combine(_root, "songes")))
        {
            BaseAddress = new Uri("http://redleaf/"),
        });
        UnifiedSessionEndpoints.Map(new EndpointRegistry(app), registry, jobs, (_, _) => { }, f.Config,
            attachmentStore: f.Attachments, inputQueue: f.Service(), repositoryValidator: repositories);
        var endpoint = ((IEndpointRouteBuilder)app).DataSources.SelectMany(d => d.Endpoints).OfType<RouteEndpoint>()
            .Single(e => e.RoutePattern.RawText == fixture.Route
                && e.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods.Contains("POST"));

        var ctx = new DefaultHttpContext
        {
            RequestServices = app.Services,
            User = jwt.ValidateToken(jwt.GenerateAccessToken("owner", "owner@test", "Owner", ["operator"]))!,
        };
        ctx.SetEndpoint(endpoint);
        ctx.Request.Method = "POST";
        ctx.Request.ContentType = "application/json";
        if (fixture.IdempotencyKey is not null) ctx.Request.Headers["X-Idempotency-Key"] = fixture.IdempotencyKey;
        if (fixture.JobName is not null) ctx.Request.Headers["X-Job-Name"] = fixture.JobName;
        var body = Encoding.UTF8.GetBytes(Fill(fixture.Body));
        ctx.Request.Body = new MemoryStream(body);
        ctx.Request.ContentLength = body.Length;
        ctx.Features.Set<Microsoft.AspNetCore.Http.Features.IHttpRequestBodyDetectionFeature>(
            new ClaudeDeveloperInstructionsTests.BodyFeature());
        ctx.Response.Body = new MemoryStream();

        await endpoint.RequestDelegate!(ctx);
        ctx.Response.Body.Position = 0;
        var response = await new StreamReader(ctx.Response.Body).ReadToEndAsync();
        var jobInput = ctx.Response.Headers.TryGetValue("X-Job-Id", out var jobId)
            ? jobs.GetJob(Guid.Parse(jobId.ToString()))?.InputJson
            : null;
        return (ctx.Response.StatusCode, response, jobInput);
    }

    private static void Git(string cwd, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = cwd, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
        };
        foreach (var a in new[] { "-c", "user.name=t", "-c", "user.email=t@t", "-c", "commit.gpgsign=false" }.Concat(args))
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        p.StandardOutput.ReadToEnd();
        var error = p.StandardError.ReadToEnd();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, $"git {string.Join(' ', args)} failed: {error}");
    }

    public void Dispose()
    {
        try
        {
            foreach (var file in new DirectoryInfo(_root).EnumerateFiles("*", SearchOption.AllDirectories))
                file.Attributes = FileAttributes.Normal;
            Directory.Delete(_root, recursive: true);
        }
        catch { }
    }

    internal sealed record PlacementCase(string Route, string Body, string? IdempotencyKey = null, string? JobName = null);

    /// <summary>The Repository entity RedLeaf answers for <see cref="RepositoryId"/>: active, branch work allowed.</summary>
    private sealed class RepositoryEntity(string localPath) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = new
            {
                id = RepositoryId,
                typeSlug = "repository",
                name = "songes",
                data = JsonSerializer.Serialize(new { status = "active", local_path = localPath, branch_work = "allowed" }),
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
            });
        }
    }

    public interface ITestProvider : ISessionProvider, IBackendProvider { }

    /// <summary>
    /// Records the exact arguments the endpoints hand a provider, plus the scratch environment a real
    /// provider builds from them (<see cref="SessionScratch.Environment"/>, the execution token
    /// replaced by a placeholder). One recorder answers under every provider id.
    /// </summary>
    public class RecordingProvider : DispatchProxy
    {
        public string ProviderId { get; set; } = "claude-code";
        public List<JsonObject> Calls { get; set; } = [];
        public IJobTracker? Jobs { get; set; }
        private RecordingProvider? _shared;

        public ITestProvider As(string providerId)
        {
            var proxy = DispatchProxy.Create<ITestProvider, RecordingProvider>();
            var inner = (RecordingProvider)(object)proxy;
            inner.ProviderId = providerId;
            inner._shared = this;
            return proxy;
        }

        private RecordingProvider Shared => _shared ?? this;

        protected override object? Invoke(MethodInfo? method, object?[]? args)
            => SessionProviderProxy.Route(method, args, InvokeProvider);

        private object? InvokeProvider(MethodInfo? method, object?[]? args) => method!.Name switch
        {
            "get_ProviderId" => ProviderId,
            "get_ProviderDisplayName" => ProviderId,
            "get_Capabilities" => SessionCapabilities.PersistentSessions | SessionCapabilities.StatelessExecution,
            "get_Name" => ProviderId,
            "get_CapabilitySlug" => "ai-session",
            "get_HealthCheckInterval" => TimeSpan.FromSeconds(10),
            "get_LastStartError" => null,
            "get_SupportedAccessLevels" => (IReadOnlyList<string>)[.. SessionAccessLevels.All],
            nameof(ISessionProvider.GetAccessState) => null,
            nameof(ISessionProvider.SetAccessState) => false,
            nameof(ISessionProvider.GetAvailableModels) => new List<ModelInfo>(),
            nameof(ISessionProvider.GetSession) => ((UnifiedSessionInfo?)null, new List<UnifiedMessageRecord>()),
            nameof(ISessionProvider.GetBackgroundTasks) => (IReadOnlyList<BackgroundTaskInfo>)[],
            nameof(ISessionProvider.StartSessionAsync) => Start(args!),
            nameof(ISessionProvider.ExecuteAsync) => Execute(args!),
            _ => throw new NotSupportedException(method.Name),
        };

        private Task<UnifiedSessionInfo?> Start(object?[] args)
        {
            var scratch = (string?)args[13];
            Shared.Calls.Add(new JsonObject
            {
                ["method"] = "StartSessionAsync",
                ["providerId"] = ProviderId,
                ["projectPath"] = (string)args[0]!,
                ["model"] = (string?)args[1],
                ["effort"] = (string?)args[5],
                ["repositoryId"] = ((Guid?)args[11])?.ToString("D"),
                ["scratchDirectory"] = scratch,
                ["confidential"] = args.Length > 14 && args[14] is true,
                ["developerInstructions"] = args.Length > 15 ? (string?)args[15] : null,
                ["environment"] = Environment(SessionScratch.Environment(scratch, sessionId: SessionId)),
                ["deliveredAsOneMessage"] = SessionDeliveryScope.DeliveredAsOneMessage,
                ["accessLevel"] = SessionAccessScope.Current?.AccessLevel,
            });
            var provenance = (JobProvenance)args[12]!;
            var job = Shared.Jobs!.CreateJob(new JobSubmission("ai-session", ProviderId, "{}", provenance));
            return Task.FromResult<UnifiedSessionInfo?>(new UnifiedSessionInfo
            {
                Id = SessionId, Provider = ProviderId, ProjectName = "placement", ProjectPath = (string)args[0]!,
                Status = RedCompute.Core.Sessions.SessionStatus.Idle, UserId = "owner", JobId = job.Id,
            });
        }

        private Task<SessionExecuteResult> Execute(object?[] args)
        {
            var env = (Dictionary<string, string>?)args[6];
            var providerParams = (Dictionary<string, object?>?)args[7];
            Shared.Calls.Add(new JsonObject
            {
                ["method"] = "ExecuteAsync",
                ["providerId"] = ProviderId,
                ["workingDir"] = (string?)args[1],
                ["model"] = (string?)args[2],
                ["timeout"] = (int)args[3]!,
                ["environment"] = Environment(env),
                ["providerParams"] = providerParams is null ? null
                    : new JsonObject(providerParams.OrderBy(p => p.Key, StringComparer.Ordinal)
                        .Select(p => KeyValuePair.Create(p.Key, JsonSerializer.SerializeToNode(p.Value)))),
            });
            return Task.FromResult(new SessionExecuteResult(true, "ok", null, (string?)args[2], 0, 0, null, null));
        }

        /// <summary>The environment with the signed execution token reduced to its presence.</summary>
        private static JsonObject? Environment(IReadOnlyDictionary<string, string>? env)
        {
            if (env is null) return null;
            var result = new JsonObject();
            foreach (var (key, value) in env.OrderBy(p => p.Key, StringComparer.Ordinal))
                result[key] = key == "REDLEAF_EXECUTION_TOKEN" && value.Length > 0 ? "<execution-token>" : value;
            return result;
        }
    }
}

/// <summary>Placement goldens: the Phase 1 canonical form, in their own folder (tests/Goldens/Placement).</summary>
internal static class PlacementGoldens
{
    public static string Directory => Path.Combine(RepoRoot(), "tests", "Goldens", "Placement");

    public static void Verify(string name, JsonNode actual)
    {
        var text = LaunchGoldens.Canonical(actual);
        var token = Environment.GetEnvironmentVariable("REDLEAF_EXECUTION_TOKEN");
        if (!string.IsNullOrEmpty(token) && token.Length >= 16)
            Assert.False(text.Contains(token, StringComparison.Ordinal), "A placement golden would contain the ambient execution token");
        var path = Path.Combine(Directory, name + ".json");
        if (LaunchGoldens.Recording)
        {
            System.IO.Directory.CreateDirectory(Directory);
            File.WriteAllText(path, text, new UTF8Encoding(false));
            return;
        }
        Assert.True(File.Exists(path), $"Missing placement golden {name}.json; record with {LaunchGoldens.RecordVariable}=1 on the unchanged code");
        var expected = File.ReadAllText(path).Replace("\r\n", "\n");
        if (expected == text) return;
        var dump = Path.Combine(Path.GetTempPath(), "redcompute-placement-goldens-actual", name + ".json");
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(dump)!);
        File.WriteAllText(dump, text, new UTF8Encoding(false));
        Assert.Fail($"Placement golden {name} changed. Expected {path}, actual written to {dump}.");
    }

    private static string RepoRoot([CallerFilePath] string file = "")
    {
        var directory = new DirectoryInfo(Path.GetDirectoryName(file)!);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "RedCompute.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("RedCompute.sln not found above " + file);
    }
}
