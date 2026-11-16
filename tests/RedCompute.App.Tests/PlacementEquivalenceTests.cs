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
using RedCompute.App.Services.Nodes;
using RedCompute.App.Services.Placement;
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

    internal const string AgentId = "7e1e0000-0000-4000-8000-0000000000a1";

    /// <summary>
    /// The <c>placement</c> each caller adds in stage A (design 8.1), beside its unchanged legacy fields.
    /// The two cap-ai-inference paths have none: their placement comes with the D2 behaviour change
    /// (the Agent's real workspace instead of the old per-Agent folder), so no body sent today matches it.
    /// </summary>
    internal static readonly IReadOnlyDictionary<string, string> StageAPlacements = new Dictionary<string, string>
    {
        ["nova-discussion-mounted-workspace"] = """{"nodeId":null,"workspace":{"agentId":"7e1e0000-0000-4000-8000-0000000000a1"},"scratch":{"owner":"nova","executionKey":"target"}}""",
        ["nova-discussion-disposable-workspace"] = """{"nodeId":null,"workspace":{"agentId":"7e1e0000-0000-4000-8000-0000000000a1"},"scratch":{"owner":"nova","executionKey":"target"}}""",
        ["nova-heartbeat-resident-unattended"] = """{"nodeId":null,"workspace":{"agentId":"7e1e0000-0000-4000-8000-0000000000a1"},"scratch":{"owner":"nova","executionKey":"target"}}""",
        ["nova-delegate-repository"] = """{"nodeId":null,"repository":{"repositoryId":"7e1e0000-0000-4000-8000-000000000001"}}""",
        ["nova-delegate-branch-worktree"] = """{"nodeId":null,"repository":{"repositoryId":"7e1e0000-0000-4000-8000-000000000001","branch":"laurent/x"}}""",
        ["nova-delegate-agent-workspace"] = """{"nodeId":null,"workspace":{"agentId":"7e1e0000-0000-4000-8000-0000000000a1"}}""",
        ["nova-delegate-repository-codex"] = """{"nodeId":null,"repository":{"repositoryId":"7e1e0000-0000-4000-8000-000000000001"}}""",
        ["code-session-checkout"] = """{"nodeId":null,"repository":{"repositoryId":"7e1e0000-0000-4000-8000-000000000001"}}""",
        ["code-session-worktree"] = """{"nodeId":null,"repository":{"repositoryId":"7e1e0000-0000-4000-8000-000000000001","branch":"laurent/x"}}""",
        ["nova-automation-session"] = """{"nodeId":null,"workspace":{"agentId":"7e1e0000-0000-4000-8000-0000000000a1"},"scratch":{"owner":"nova","executionKey":"attempt-1"},"scratchAs":"add-dir"}""",
        ["code-executor-tackle"] = """{"nodeId":null,"repository":{"repositoryId":"7e1e0000-0000-4000-8000-000000000001"}}""",
        ["redleaf-setup-probe"] = """{"nodeId":"local","scratch":{"owner":"owner","executionKey":"setup-probe"},"scratchAs":"cwd"}""",
        ["sentinel-review"] = """{"nodeId":"local","scratch":{"owner":"sentinel","executionKey":"review-1"},"scratchAs":"cwd"}""",
        ["roleplay-turn"] = """{"nodeId":"local","scratch":{"owner":"roleplay","executionKey":"turn-1"},"scratchAs":"cwd"}""",
    };

    public static TheoryData<string> StageACases() => new(StageAPlacements.Keys);

    /// <summary>The fixture's legacy body with a placement added as its last member.</summary>
    internal static string WithPlacement(PlacementCase fixture, string placement)
        => fixture.Body[..fixture.Body.LastIndexOf('}')] + ",\"placement\":" + placement + "}";

    // ── Layer 2a: what reaches the provider ─────────────────────────────────────────────────────

    [WindowsTheory("Placement goldens are recorded on Windows (path bytes, separators included)")]
    [MemberData(nameof(Cases))]
    public async Task Legacy_request_reaches_the_provider_as_recorded(string name)
        => PlacementGoldens.Verify(name, await CaptureAsync(Fixtures[name], Fixtures[name].Body));

    /// <summary>
    /// Stage A: the same legacy fields with the caller's placement beside them reach the provider exactly
    /// as the legacy fields alone do (the same golden), and the job input does not change.
    /// </summary>
    [WindowsTheory("Placement goldens are recorded on Windows (path bytes, separators included)")]
    [MemberData(nameof(StageACases))]
    public async Task Legacy_request_with_its_placement_reaches_the_provider_unchanged(string name)
        => PlacementGoldens.Verify(name, await CaptureAsync(Fixtures[name], WithPlacement(Fixtures[name], StageAPlacements[name])));

    /// <summary>What the provider was handed for <paramref name="body"/>, recorded under the fixture's legacy request.</summary>
    private async Task<JsonNode> CaptureAsync(PlacementCase fixture, string body, INodeRegistry? nodes = null,
        PlacementShadow? shadow = null)
    {
        var recorder = new RecordingProvider();
        var result = await InvokeAsync(fixture, jobs => recorder.Jobs = jobs, recorder.Providers(), body: body, nodes: nodes,
            shadow: shadow);

        Assert.True(result.Status is 200 or 202, $"{result.Status} {result.Response}");
        var capture = new JsonObject
        {
            ["request"] = JsonNode.Parse(fixture.Body),
            ["route"] = fixture.Route,
            ["status"] = result.Status,
            ["provider"] = Assert.Single(recorder.Calls).DeepClone(),
            ["jobInput"] = result.JobInput is null ? null : JsonNode.Parse(result.JobInput),
        };
        return Normalizer().Apply(capture)!;
    }

    // ── refused placements never reach the provider ────────────────────────────────────────────

    private const string NodeZero = "node_01JCZ0000000000000000PLAC";

    private static NodeRegistry Nodes() => new(
        new NodeIdentityResult(new NodeIdentity(NodeZero, DateTimeOffset.UtcNow.AddDays(-1), NodeZero), true, false, null),
        NodeRegistryTests.Platform(), "0.2.0", DateTimeOffset.UtcNow, () => DateTimeOffset.UtcNow);

    [Theory]
    [InlineData("nova-discussion-mounted-workspace", """{"nodeId":"node_01JCZ000000000000000OTHER"}""", 404, "node_not_found")]
    [InlineData("nova-automation-session", """{"nodeId":"node_01JCZ000000000000000OTHER"}""", 404, "node_not_found")]
    [InlineData("nova-discussion-mounted-workspace", """{"workspace":{"agentId":"nova"}}""", 422, "validation_failed")]
    [InlineData("nova-discussion-mounted-workspace", """{"scratch":{"owner":"nova","executionKey":"k"},"scratchAs":"add-dir"}""", 422, "validation_failed")]
    [InlineData("nova-automation-session", """{"workspace":{"agentId":"7e1e0000-0000-4000-8000-0000000000a1"},"repository":{"repositoryId":"7e1e0000-0000-4000-8000-000000000001"}}""", 422, "validation_failed")]
    [InlineData("code-executor-tackle", """{"repository":{"repositoryId":"7e1e0000-0000-4000-8000-000000000001","subPath":"../other"}}""", 422, "validation_failed")]
    [InlineData("code-executor-tackle", """ "local" """, 422, "validation_failed")]
    public async Task A_refused_placement_never_reaches_the_provider(string name, string placement, int status, string code)
    {
        var fixture = Fixtures[name];
        var recorder = new RecordingProvider();
        var result = await InvokeAsync(fixture, jobs => recorder.Jobs = jobs, recorder.Providers(),
            body: WithPlacement(fixture, placement.Trim()), nodes: Nodes());

        Assert.Equal(status, result.Status);
        using var response = JsonDocument.Parse(result.Response);
        Assert.Equal(code, response.RootElement.GetProperty("error").GetString());
        if (code == "node_not_found")
            Assert.Equal("node_01JCZ000000000000000OTHER", response.RootElement.GetProperty("nodeId").GetString());
        else
            Assert.StartsWith("placement", Assert.Single(response.RootElement.GetProperty("fields").EnumerateObject()).Name);
        Assert.DoesNotContain(_root, result.Response, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(recorder.Calls);
        Assert.Null(result.JobInput);
    }

    [WindowsTheory("Placement goldens are recorded on Windows (path bytes, separators included)")]
    [InlineData("local")]
    [InlineData(NodeZero)]
    public async Task A_known_node_with_a_registry_changes_nothing(string nodeId)
        => PlacementGoldens.Verify("nova-delegate-repository", await CaptureAsync(Fixtures["nova-delegate-repository"],
            WithPlacement(Fixtures["nova-delegate-repository"], $$$"""{"nodeId":"{{{nodeId}}}","repository":{"repositoryId":"{{{RepositoryId}}}"}}"""),
            Nodes()));

    [Fact]
    public async Task A_conversation_only_session_refuses_placement_references_before_allocating_its_workspace()
    {
        var recorder = new RecordingProvider();
        var result = await InvokeAsync(new PlacementCase("/ai-session/sessions", "{}"), jobs => recorder.Jobs = jobs,
            recorder.Providers(), body: $$$$"""
                {"provider":"codex","executionProfile":"conversation-only","placement":{"nodeId":"local","workspace":{"agentId":"{{{{AgentId}}}}"}}}
                """);

        Assert.Equal(422, result.Status);
        Assert.Contains("placement.workspace", result.Response);
        Assert.Empty(recorder.Calls);
    }

    [Fact]
    public async Task Discovery_documents_placement_on_both_routes()
    {
        using var f = new SessionRecoveryTests.Fixture("maintenance_restart");
        await using var app = WebApplication.CreateSlimBuilder().Build();
        var endpoints = new EndpointRegistry(app);
        UnifiedSessionEndpoints.Map(endpoints, new CapabilityRegistry(), new ClaudeDeveloperInstructionsTests.MemoryJobTracker(),
            (_, _) => { }, f.Config, attachmentStore: f.Attachments, inputQueue: f.Service());

        var sessions = endpoints.GetEndpoints().Single(e => e.Method == "POST" && e.Path == "/ai-session/sessions");
        var parameter = Assert.Single(sessions.Parameters!, p => p.Name == "placement");
        Assert.Equal("object", parameter.Type);
        Assert.Contains("node_not_found", parameter.Description);
        var execute = endpoints.GetEndpoints().Single(e => e.Method == "POST" && e.Path == "/ai-session/execute");
        var schema = JsonSerializer.SerializeToElement(execute.RequestBody);
        Assert.Equal("object", schema.GetProperty("properties").GetProperty("placement").GetProperty("type").GetString());
    }

    // ── stage A: the shadow compare (R4) ─────────────────────────────────────────────────────────

    /// <summary>
    /// How each caller's stage-A placement compares with the legacy fields it is sent with, when the
    /// references resolve to where that caller's paths point today (field outcome, fields in order).
    /// Code's browser sends its picker's forward slashes, so its cwd is equivalent, not exact. The
    /// Sentinel sends <c>addDirs: []</c> and no scratch environment today, while its placement's scratch
    /// comes with one: the flip gives it the scratch variables (design 2.1, scratchAs cwd), so it shows a
    /// mismatch here.
    /// </summary>
    internal static readonly IReadOnlyDictionary<string, string> ShadowOutcomes = new Dictionary<string, string>
    {
        ["nova-discussion-mounted-workspace"] = "cwd exact, scratch exact",
        ["nova-discussion-disposable-workspace"] = "cwd exact, scratch exact",
        ["nova-heartbeat-resident-unattended"] = "cwd exact, scratch exact",
        ["nova-delegate-repository"] = "cwd exact, repositoryId exact",
        ["nova-delegate-branch-worktree"] = "cwd exact, repositoryId exact",
        ["nova-delegate-agent-workspace"] = "cwd exact",
        ["nova-delegate-repository-codex"] = "cwd exact, repositoryId exact",
        ["code-session-checkout"] = "cwd equivalent, repositoryId exact",
        ["code-session-worktree"] = "cwd equivalent, repositoryId exact",
        ["nova-automation-session"] = "addDirs exact, cwd exact, env exact",
        ["code-executor-tackle"] = "cwd exact",
        ["redleaf-setup-probe"] = "cwd exact, env exact",
        ["sentinel-review"] = "addDirs equivalent, cwd exact, env mismatch",
        ["roleplay-turn"] = "cwd exact, env exact",
    };

    /// <summary>
    /// Stage A end to end: with the shadow wired, every caller's body still reaches the provider exactly
    /// as its legacy fields alone do (the same golden), and its placement is resolved and compared once,
    /// with the outcome above. The workspace and scratch resolvers stand in for the kernel (K2) and answer
    /// where the fixture's paths are; repositories resolve through the real resolver and real git.
    /// </summary>
    [WindowsTheory("Placement goldens are recorded on Windows (path bytes, separators included)")]
    [MemberData(nameof(StageACases))]
    public async Task Each_stage_A_start_is_shadow_compared_and_still_runs_on_its_legacy_fields(string name)
    {
        var places = new FixturePlaces(_root, mounted: name != "nova-discussion-disposable-workspace");
        var repositories = new RepositoryPlacementResolver(new HttpClient(new RepositoryEntity(Path.Combine(_root, "songes")))
        {
            BaseAddress = new Uri("http://redleaf/"),
        });
        var logged = new List<string>();
        var shadow = new PlacementShadow(new PlacementResolver(null, places, places, repositories), null,
            (line, _) => { lock (logged) logged.Add(line); });

        PlacementGoldens.Verify(name, await CaptureAsync(Fixtures[name], WithPlacement(Fixtures[name], StageAPlacements[name]),
            shadow: shadow));
        await shadow.IdleAsync();

        var path = Assert.Single(shadow.Status().Paths);
        Assert.Equal(Fixtures[name].Route == "/ai-session/sessions" ? "sessions" : "execute", path.Route);
        Assert.Equal((1L, 1L), (path.Observed, path.Compared));
        Assert.Equal(ShadowOutcomes[name], string.Join(", ", path.Fields.Select(pair => pair.Key + " "
            + (pair.Value.Exact == 1 ? ShadowOutcome.Exact : pair.Value.Equivalent == 1 ? ShadowOutcome.Equivalent : ShadowOutcome.Mismatch))));
        Assert.All(logged, line => Assert.DoesNotContain(_root, line, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task A_start_without_placement_is_not_shadowed()
    {
        var places = new FixturePlaces(_root, mounted: true);
        var shadow = new PlacementShadow(new PlacementResolver(null, places, places, null), null, (_, _) => { });

        await CaptureAsync(Fixtures["nova-discussion-mounted-workspace"], Fixtures["nova-discussion-mounted-workspace"].Body,
            shadow: shadow);
        await CaptureAsync(Fixtures["nova-automation-session"], Fixtures["nova-automation-session"].Body, shadow: shadow);
        await shadow.IdleAsync();

        Assert.Empty(shadow.Status().Paths);
        Assert.Equal(0, places.Calls);
    }

    /// <summary>The kernel's answers for the fixtures: the Agent's workspace (or none) and the scratch folder with its environment.</summary>
    private sealed class FixturePlaces(string root, bool mounted) : IWorkspaceResolver, IHostScratch
    {
        public int Calls;

        public Task<Resolution<WorkspaceResolution>> ResolveAsync(WorkspacePlacement workspace, CancellationToken ct = default)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(Resolution<WorkspaceResolution>.Of(mounted
                ? new(WorkspaceResolution.Mounted, Path.Combine(root, "workspace"))
                : new(WorkspaceResolution.None, null)));
        }

        public Task<Resolution<ScratchAllocation>> PrepareAsync(ScratchPlacement scratch, CancellationToken ct = default)
        {
            Interlocked.Increment(ref Calls);
            var path = Path.Combine(root, "scratch");
            return Task.FromResult(Resolution<ScratchAllocation>.Of(new(path,
                PlacementShadow.ScratchVariables.ToDictionary(name => name, _ => path))));
        }
    }

    // ── the session keeps its references (R3) ─────────────────────────────────────────────────

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_started_session_reports_and_stores_its_placement_references_only(bool withPlacement)
    {
        var fixture = Fixtures["nova-delegate-repository"];
        var store = new ClaudeDeveloperInstructionsTests.MemoryStore();
        var launched = new List<Process>();
        var claudePath = Path.Combine(_root, "cli", "claude.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(claudePath)!);
        File.WriteAllText(claudePath, "launch capture only, never executed");
        try
        {
            var result = await InvokeAsync(fixture, _ => { }, providers: null, claude: jobs =>
            {
                var service = new ClaudeSessionService(new ClaudeConfig { ClaudePath = claudePath }, jobs, store,
                    new ClaudeDeveloperInstructionsTests.NullQualityModes(), () => null, (_, _) => { });
                service.LaunchSessionProcess = HostSeams.From(_ =>
                {
                    var process = Process.Start(new ProcessStartInfo
                    {
                        FileName = TestProcesses.StdinSink, CreateNoWindow = true, UseShellExecute = false,
                        RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                    })!;
                    lock (launched) launched.Add(process);
                    return process;
                });
                return new ClaudeCodeProvider("ai-session", service, store, jobs, (_, _) => { });
            }, body: withPlacement ? WithPlacement(fixture, StageAPlacements["nova-delegate-repository"]) : null);

            Assert.True(result.Status == 200, $"{result.Status}: {result.Response}");
            using var response = JsonDocument.Parse(result.Response);
            var session = store.FindSession(response.RootElement.GetProperty("id").GetString()!)!;
            if (!withPlacement)
            {
                Assert.Equal(JsonValueKind.Null, response.RootElement.GetProperty("placement").ValueKind);
                Assert.Null(session.Placement);
                return;
            }
            const string expected = """{"nodeId":null,"repository":{"repositoryId":"7e1e0000-0000-4000-8000-000000000001"}}""";
            Assert.Equal(expected, response.RootElement.GetProperty("placement").GetRawText());
            Assert.Equal(expected, session.Placement);
            // The legacy fields still decided where it runs.
            Assert.Equal(Path.Combine(_root, "songes"), session.ProjectPath);
        }
        finally
        {
            lock (launched)
                foreach (var process in launched)
                {
                    try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
                    process.Dispose();
                }
        }
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
        Func<IJobTracker, ClaudeCodeProvider>? claude = null, string? body = null, INodeRegistry? nodes = null,
        PlacementShadow? shadow = null)
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
            attachmentStore: f.Attachments, inputQueue: f.Service(), repositoryValidator: repositories, nodes: nodes,
            placementShadow: shadow);
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
        var bytes = Encoding.UTF8.GetBytes(Fill(body ?? fixture.Body));
        ctx.Request.Body = new MemoryStream(bytes);
        ctx.Request.ContentLength = bytes.Length;
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

    /// <summary>
    /// The Repository entity RedLeaf answers for <see cref="RepositoryId"/>: active, branch work allowed. It has
    /// no checkout entities yet, so its <c>local_path</c> is the legacy node's checkout.
    /// </summary>
    private sealed class RepositoryEntity(string localPath) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.PathAndQuery.StartsWith("/api/entities?", StringComparison.Ordinal))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"items":[],"total":0}""", Encoding.UTF8, "application/json"),
                });
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

        /// <summary>This recorder under every session provider id the fixtures name.</summary>
        public Dictionary<string, IBackendProvider> Providers() => new()
        {
            ["claude-code"] = As("claude-code"),
            ["codex"] = As("codex"),
            ["opencode"] = As("opencode"),
        };

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
