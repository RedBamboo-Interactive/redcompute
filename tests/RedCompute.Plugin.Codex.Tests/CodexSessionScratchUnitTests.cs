using System.Text.Json.Nodes;
using RedCompute.Core.Jobs;
using RedCompute.Plugin.Codex;
using RedCompute.PluginSdk;
using RedCompute.PluginSdk.Hosting;
using RedCompute.Tests;
using Xunit;

namespace RedCompute.Plugin.Codex.Tests;

/// <summary>
/// A Codex session started without a scratch folder gets its own unit and keeps it across resumes. The real
/// interactive service and a real app-server process; the environment is the one that process received.
/// </summary>
[Collection(CodexLaunchGoldenCollection.Name)]
public sealed class CodexSessionScratchUnitTests : IDisposable
{
    private static readonly string[] Variables = ["TEMP", "TMP", "TMPDIR", "REDLEAF_SCRATCH_DIR", "REDLEAF_SCRATCH_ROOT"];

    private readonly ProbeHarness _probe = new("codex-scratch-unit");
    private readonly string _project;
    private readonly Allocator _allocator;
    private readonly WriteResolver _writes = new();
    private readonly CodexLaunchGoldenProcessTests.GoldenStore _store = new();
    private readonly CodexLaunchGoldenProcessTests.GoldenJobTracker _jobs = new();

    public CodexSessionScratchUnitTests()
    {
        CodexWindowsSandboxConfig.ConfigTextOverride = (_, _) => null;
        _project = Directory.CreateDirectory(Path.Combine(_probe.Root, "project")).FullName;
        _allocator = new Allocator(Path.Combine(_probe.Root, "Scratch", "RedCompute"));
    }

    [WindowsFact("the probe replaces codex.exe")]
    public async Task Session_started_without_scratch_gets_its_own_unit_and_reuses_it_on_resume()
    {
        await using var interactive = Interactive();
        CodexSessionInfo? started = null, resumed = null;

        var first = await Launch(async () => started = await interactive.StartSessionAsync(
            _project, null, "owner", null, null, null, null, null, null, Provenance()));
        Assert.NotNull(started);
        await interactive.StopSessionAsync(started.Id);
        var unit = _allocator.Unit(started.Id);
        Directory.Delete(unit, recursive: true);
        var second = await Launch(async () => resumed = await interactive.ResumeSessionAsync(started.Id, Provenance()));
        Assert.NotNull(resumed);

        AssertPointsAt(unit, first);
        AssertPointsAt(unit, second);
        Assert.True(Directory.Exists(unit));
        Assert.Equal(unit, _store.FindSession(started.Id)!.ScratchDirectory);
        Assert.Equal([started.Id, started.Id], _allocator.Requests);
        // The unit is the session's TEMP, not an extra sandbox write root: that stays the Agent's allocation.
        Assert.Empty(_writes.Asked);
    }

    [WindowsFact("the probe replaces codex.exe")]
    public async Task Session_with_a_scratch_folder_is_unchanged_on_start_and_resume()
    {
        var own = Directory.CreateDirectory(Path.Combine(_probe.Root, "own-scratch")).FullName;
        await using var interactive = Interactive();
        CodexSessionInfo? started = null, resumed = null;

        var first = await Launch(async () => started = await interactive.StartSessionAsync(
            _project, null, "owner", null, null, null, null, null, null, Provenance(), own));
        Assert.NotNull(started);
        await interactive.StopSessionAsync(started.Id);
        var second = await Launch(async () => resumed = await interactive.ResumeSessionAsync(started.Id, Provenance()));
        Assert.NotNull(resumed);

        AssertPointsAt(own, first);
        AssertPointsAt(own, second);
        Assert.Equal(own, _store.FindSession(started.Id)!.ScratchDirectory);
        Assert.Empty(_allocator.Requests);
        Assert.Equal([own, own], _writes.Asked);
    }

    [WindowsFact("the probe replaces codex.exe")]
    public async Task Conversation_only_session_keeps_its_isolated_workspace_as_scratch()
    {
        await using var interactive = Interactive();
        CodexSessionInfo? started = null;

        var environment = await Launch(async () => started = await interactive.StartSessionAsync(
            _project, null, "owner", null, null, null, null, null, null, Provenance(), _project,
            executionProfile: SessionExecutionProfile.ConversationOnly));
        Assert.NotNull(started);

        AssertPointsAt(_project, environment);
        Assert.Empty(_allocator.Requests);
    }

    private static void AssertPointsAt(string directory, IReadOnlyDictionary<string, string?> environment)
    {
        foreach (var variable in Variables)
            Assert.Equal(directory, environment.GetValueOrDefault(variable));
    }

    private async Task<IReadOnlyDictionary<string, string?>> Launch(Func<Task> operation)
    {
        var before = _probe.Launches.Count;
        await _probe.RunAsync(operation);
        return Assert.Single(_probe.Launches.Skip(before)).Environment;
    }

    private CodexInteractiveService Interactive()
    {
        var results = new JsonObject
        {
            ["initialize"] = new JsonObject { ["userAgent"] = "redcompute/0.170.0" },
            ["config/read"] = new JsonObject { ["config"] = new JsonObject() },
            ["mcpServerStatus/list"] = new JsonObject { ["data"] = new JsonArray() },
            ["thread/start"] = new JsonObject { ["thread"] = new JsonObject { ["id"] = "thread-unit" } },
            ["thread/resume"] = new JsonObject { ["thread"] = new JsonObject { ["id"] = "thread-unit" } },
            ["model/list"] = new JsonObject { ["data"] = new JsonArray() },
        };
        var config = new CodexConfig
        {
            CodexPath = _probe.Install("codex", new JsonObject { ["rpc"] = new JsonObject { ["results"] = results } }),
        };
        return new CodexInteractiveService(
            config, _store, new CodexModelCatalog(config, (_, _) => { }),
            new CodexSessionService(config, _jobs, _store, (_, _) => { }), _jobs,
            new CodexAccountUsageService(config, (_, _) => { }), new CodexLaunchGoldenProcessTests.NoQualityModes(),
            () => null, (_, _) => { }, scratchWriteResolver: _writes, scratchAllocator: _allocator);
    }

    private static JobProvenance Provenance() => JobProvenance.DirectRedCompute(
        "POST", "/ai-session/sessions", new JobBeneficiary("user", "owner"), "scratch-unit-request", null);

    public void Dispose()
    {
        CodexWindowsSandboxConfig.ConfigTextOverride = null;
        _probe.Dispose();
    }

    private sealed class Allocator(string root) : ISessionScratchAllocator
    {
        public List<string> Requests { get; } = [];
        public string Unit(string sessionId) => Path.Combine(root, sessionId);

        public Task<string?> PrepareSessionUnitAsync(IExecutionHost host, string sessionId, CancellationToken ct = default)
        {
            Requests.Add(sessionId);
            return Task.FromResult<string?>(Directory.CreateDirectory(Unit(sessionId)).FullName);
        }
    }

    private sealed class WriteResolver : ISessionScratchWriteResolver
    {
        public List<string?> Asked { get; } = [];

        public Task<string?> ResolveWritableAsync(IExecutionHost host, JobProvenance? provenance, string? scratchDirectory)
        {
            Asked.Add(scratchDirectory);
            return Task.FromResult<string?>(null);
        }
    }
}
