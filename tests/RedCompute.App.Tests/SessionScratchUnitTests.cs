using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using RedCompute.App.Services.Nodes;
using RedCompute.App.Services.Placement;
using RedCompute.Core.Jobs;
using RedCompute.Core.Sessions;
using RedCompute.Plugin.ClaudeCode;
using RedCompute.Plugin.Codex;
using RedCompute.Plugin.OpenCode;
using RedCompute.PluginSdk;
using RedCompute.PluginSdk.Hosting.Local;
using RedCompute.Tests;
using Xunit;

namespace RedCompute.App.Tests;

/// <summary>
/// A session started without a scratch folder gets its own unit, <c>Scratch\RedCompute\&lt;session id&gt;</c>, from the
/// kernel's <c>PrepareExecution</c>, instead of inheriting RedCompute's TEMP (the shared <c>Scratch\RedCompute</c>).
/// The production shape: the real provider service and kernel placement client, a real child process, and the TEMP
/// that child actually received.
/// </summary>
[Collection(LaunchGoldenCollection.Name)]
public sealed class SessionScratchUnitTests : IDisposable
{
    private static readonly string[] Variables = ["TEMP", "TMP", "TMPDIR", "REDLEAF_SCRATCH_DIR", "REDLEAF_SCRATCH_ROOT"];

    private readonly ProbeHarness _probe = new("scratch-unit");
    private readonly string _project;
    private readonly Kernel _kernel;
    private readonly KernelPlacementClient _client;
    private string? _claudePath;
    private string? _openCodePath;
    private readonly List<string> _openCodeSessions = [];

    public SessionScratchUnitTests()
    {
        _project = Path.Combine(_probe.Root, "project");
        Directory.CreateDirectory(_project);
        _kernel = new Kernel(Path.Combine(_probe.Root, "Scratch"));
        _client = new KernelPlacementClient(new HttpClient(_kernel) { BaseAddress = new Uri("http://kernel.test/") });
    }

    // ---- Claude Code -------------------------------------------------------------------------------

    [WindowsFact("the probe replaces claude.exe")]
    public async Task Claude_session_started_without_scratch_gets_its_own_unit_and_the_child_TEMP_points_there()
    {
        var store = new ClaudeDeveloperInstructionsTests.MemoryStore();

        var (info, environment) = await StartClaude(Claude(store), scratch: null);

        var unit = _kernel.Unit(info.Id);
        AssertPointsAt(unit, environment);
        Assert.True(Directory.Exists(unit));
        Assert.Equal(unit, store.FindSession(info.Id)!.ScratchDirectory);
        Assert.Equal([("RedCompute", info.Id)], _kernel.Requests);
    }

    [WindowsFact("the probe replaces claude.exe")]
    public async Task Claude_resume_reuses_the_unit_and_prepares_it_again_after_a_sweep()
    {
        var store = new ClaudeDeveloperInstructionsTests.MemoryStore();
        var (info, _) = await StartClaude(Claude(store), scratch: null);
        var unit = _kernel.Unit(info.Id);
        MakeResumable(store, info.Id);
        if (Directory.Exists(unit)) Directory.Delete(unit, recursive: true);

        // A new service over the same store: what a RedCompute restart leaves behind.
        var environment = await ResumeClaude(Claude(store), info.Id);

        AssertPointsAt(unit, environment);
        Assert.True(Directory.Exists(unit));
        Assert.Equal(unit, store.FindSession(info.Id)!.ScratchDirectory);
        Assert.Equal([("RedCompute", info.Id), ("RedCompute", info.Id)], _kernel.Requests);
    }

    [WindowsFact("the probe replaces claude.exe")]
    public async Task Claude_session_from_before_without_a_scratch_folder_gets_its_unit_on_resume()
    {
        var store = new ClaudeDeveloperInstructionsTests.MemoryStore();
        store.SaveSession(ClaudeRecord("before", scratch: null));

        var environment = await ResumeClaude(Claude(store), "before");

        AssertPointsAt(_kernel.Unit("before"), environment);
        Assert.Equal(_kernel.Unit("before"), store.FindSession("before")!.ScratchDirectory);
    }

    [WindowsFact("the probe replaces claude.exe")]
    public async Task Claude_session_with_a_scratch_folder_is_unchanged_on_start_and_resume()
    {
        var own = Directory.CreateDirectory(Path.Combine(_probe.Root, "Scratch", "Nova", "discussion")).FullName;
        var store = new ClaudeDeveloperInstructionsTests.MemoryStore();

        var (info, started) = await StartClaude(Claude(store), own);
        MakeResumable(store, info.Id);
        var resumed = await ResumeClaude(Claude(store), info.Id);

        AssertPointsAt(own, started);
        AssertPointsAt(own, resumed);
        Assert.Equal(own, store.FindSession(info.Id)!.ScratchDirectory);
        Assert.Empty(_kernel.Requests);
    }

    [WindowsFact("the probe replaces claude.exe")]
    public async Task A_kernel_that_cannot_prepare_a_unit_leaves_the_session_the_inherited_TEMP()
    {
        _kernel.Down = true;
        var store = new ClaudeDeveloperInstructionsTests.MemoryStore();

        var (info, environment) = await StartClaude(Claude(store), scratch: null);

        Assert.Equal(Environment.GetEnvironmentVariable("TEMP"), environment["TEMP"]);
        Assert.Null(store.FindSession(info.Id)!.ScratchDirectory);
    }

    [WindowsFact("the probe replaces claude.exe")]
    public async Task A_swept_unit_that_cannot_be_prepared_again_is_not_used_as_TEMP_and_stays_on_record()
    {
        var unit = _kernel.Unit("swept");
        var store = new ClaudeDeveloperInstructionsTests.MemoryStore();
        store.SaveSession(ClaudeRecord("swept", unit));
        _kernel.Down = true;

        var environment = await ResumeClaude(Claude(store), "swept");

        Assert.Equal(Environment.GetEnvironmentVariable("TEMP"), environment["TEMP"]);
        Assert.Equal(unit, store.FindSession("swept")!.ScratchDirectory);
    }

    // ---- OpenCode ----------------------------------------------------------------------------------

    [WindowsFact("the probe replaces opencode.exe")]
    public async Task OpenCode_session_gets_its_own_unit_and_reuses_it_on_resume()
    {
        var store = new LaunchGoldenProcessTests.GoldenOpenCodeStore();
        var service = OpenCode(store);
        OpenCodeSessionInfo? info = null;
        var started = await Launch(async () => info = await service.StartSession(_project, null, "owner", null, null,
            null, null, null, null, null, null, null, Provenance()));
        Assert.NotNull(info);
        _openCodeSessions.Add(info!.Id);
        await service.StopSession(info.Id);
        var unit = _kernel.Unit(info.Id);

        var resumedService = OpenCode(store);
        OpenCodeSessionInfo? resumed = null;
        var again = await Launch(async () => resumed = await resumedService.ResumeSession(info.Id, Provenance()));
        Assert.True(resumed is not null, resumedService.LastStartError);
        await resumedService.StopSession(info.Id);

        AssertPointsAt(unit, started);
        AssertPointsAt(unit, again);
        Assert.Equal(unit, store.FindSession(info.Id)!.ScratchDirectory);
        Assert.Equal([("RedCompute", info.Id), ("RedCompute", info.Id)], _kernel.Requests);
    }

    [WindowsFact("the probe replaces opencode.exe")]
    public async Task OpenCode_session_with_a_scratch_folder_is_unchanged()
    {
        var own = Directory.CreateDirectory(Path.Combine(_probe.Root, "own-scratch")).FullName;
        var store = new LaunchGoldenProcessTests.GoldenOpenCodeStore();
        var service = OpenCode(store);
        OpenCodeSessionInfo? info = null;

        var environment = await Launch(async () => info = await service.StartSession(_project, null, "owner", null, null,
            null, null, null, null, null, null, null, Provenance(), own));
        Assert.NotNull(info);
        _openCodeSessions.Add(info!.Id);
        await service.StopSession(info.Id);

        AssertPointsAt(own, environment);
        Assert.Equal(own, store.FindSession(info.Id)!.ScratchDirectory);
        Assert.Empty(_kernel.Requests);
    }

    // ---- the kernel client and the host wiring ------------------------------------------------------

    [Fact]
    public async Task The_unit_is_the_kernels_RedCompute_folder_named_after_the_session()
    {
        var prepared = await _client.PrepareSessionUnitAsync(LocalExecutionHost.Shared, "abc123def456");

        Assert.Equal(_kernel.Unit("abc123def456"), prepared);
        Assert.Equal([("RedCompute", "abc123def456")], _kernel.Requests);
    }

    [Fact]
    public async Task An_answer_naming_another_folder_is_not_taken_as_the_sessions_unit()
    {
        _kernel.Answer = Directory.CreateDirectory(Path.Combine(_probe.Root, "Scratch", "RedCompute")).FullName;

        Assert.Null(await _client.PrepareSessionUnitAsync(LocalExecutionHost.Shared, "abc123def456"));
    }

    [Fact]
    public async Task A_remote_nodes_session_gets_no_unit_on_this_machine()
    {
        var remote = new RemoteExecutionHost("node_remote", () => null,
            new RedCompute.NodeLink.Forward.ForwardClient(() => null), null!);

        Assert.Null(await _client.PrepareSessionUnitAsync(remote, "abc123def456"));
        Assert.Empty(_kernel.Requests);
    }

    /// <summary>The host hands providers its kernel client; each session provider has to ask for it.</summary>
    [Theory]
    [InlineData(typeof(ClaudeCodeProvider))]
    [InlineData(typeof(CodexProvider))]
    [InlineData(typeof(OpenCodeProvider))]
    public void Every_session_provider_is_constructed_with_the_hosts_scratch_allocator(Type provider)
        => Assert.Contains(provider.GetConstructors().SelectMany(c => c.GetParameters()),
            parameter => parameter.ParameterType == typeof(ISessionScratchAllocator)
                && parameter.ParameterType.IsAssignableFrom(typeof(KernelPlacementClient)));

    // ---- helpers -----------------------------------------------------------------------------------

    private static void AssertPointsAt(string directory, IReadOnlyDictionary<string, string?> environment)
    {
        foreach (var variable in Variables)
            Assert.Equal(directory, environment.GetValueOrDefault(variable));
    }

    /// <summary>The environment the one process <paramref name="operation"/> launched actually received.</summary>
    private async Task<IReadOnlyDictionary<string, string?>> Launch(Func<Task> operation)
    {
        var before = _probe.Launches.Count;
        await _probe.RunAsync(operation);
        return Assert.Single(_probe.Launches.Skip(before)).Environment;
    }

    private ClaudeSessionService Claude(IClaudeSessionStore store)
        => new(new ClaudeConfig { ClaudePath = _claudePath ??= _probe.Install("claude") },
            new ClaudeDeveloperInstructionsTests.MemoryJobTracker(), store,
            new ClaudeDeveloperInstructionsTests.NullQualityModes(), () => null, (_, _) => { },
            scratchAllocator: _client);

    private async Task<(ClaudeSessionInfo Info, IReadOnlyDictionary<string, string?> Environment)> StartClaude(
        ClaudeSessionService service, string? scratch)
    {
        ClaudeSessionInfo? info = null;
        var environment = await Launch(async () => info = await service.StartSessionAsync(_project, "claude-model",
            "owner", null, null, "high", null, null, null, Provenance(), scratchDirectory: scratch));
        Assert.True(info is not null, service.LastStartError);
        await service.StopAllAsync();
        return (info!, environment);
    }

    private async Task<IReadOnlyDictionary<string, string?>> ResumeClaude(ClaudeSessionService service, string id)
    {
        ClaudeSessionInfo? info = null;
        var environment = await Launch(async () => info = await service.ResumeSessionAsync(id, Provenance()));
        Assert.True(info is not null, service.LastStartError);
        await service.StopAllAsync();
        return environment;
    }

    private ClaudeSessionRecord ClaudeRecord(string id, string? scratch) => new()
    {
        Id = id, ProjectName = "p", ProjectPath = _project, Status = "Stopped", ClaudeSessionId = "claude-conversation",
        MessageCount = 1, Model = "claude-model", ScratchDirectory = scratch, StartedAt = DateTimeOffset.UtcNow,
        StopReason = SessionStopReasons.IdleTimeout,
    };

    private static void MakeResumable(IClaudeSessionStore store, string id)
    {
        var record = store.FindSession(id)!;
        record.ClaudeSessionId = "claude-conversation";
        record.MessageCount = 1;
        record.Status = "Stopped";
        record.StopReason = SessionStopReasons.IdleTimeout;
        store.SaveSession(record);
    }

    private OpenCodeSessionService OpenCode(IOpenCodeSessionStore store)
    {
        // Installed once: a second service stands for a restart, and the first probe may still hold its files.
        var path = _openCodePath ??= _probe.Install("opencode", new JsonObject
        {
            ["rpc"] = new JsonObject
            {
                ["results"] = new JsonObject
                {
                    ["session/new"] = new JsonObject { ["sessionId"] = "native-unit" },
                    ["session/load"] = new JsonObject(),
                },
            },
        });
        var dataHome = Directory.CreateDirectory(Path.Combine(_probe.Root, "opencode-data")).FullName;
        return new OpenCodeSessionService(new OpenCodeConfig { OpenCodePath = path, OpenCodeDataHome = dataHome },
            new ClaudeDeveloperInstructionsTests.MemoryJobTracker(), store, (_, _) => { }, new EmptyTranscript(),
            scratchAllocator: _client)
        {
            ReadEnabledMcpServers = _ => Task.FromResult(Array.Empty<string>()),
        };
    }

    /// <summary>A resume reads OpenCode's own transcript for its baseline: here an empty one.</summary>
    private sealed class EmptyTranscript : IOpenCodeNativeSessionReader
    {
        public OpenCodeNativeMessageVisibility GetMessageVisibility(string sessionId, string messageId)
            => OpenCodeNativeMessageVisibility.Visible;
        public IReadOnlyList<OpenCodeNativePart> GetCompletedAssistantParts(string sessionId) => [];
        public PromptCacheUsage? ReadPromptCache(string sessionId) => null;
    }

    /// <summary>
    /// The ACP launch writes the session's instruction file into the data root; exactly that file goes again, also
    /// when the test fails.
    /// </summary>
    private static void DeleteInstructions(string sessionId)
    {
        var file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RedCompute", "plugins", "opencode", "instructions", sessionId + ".md");
        if (File.Exists(file)) File.Delete(file);
    }

    private static JobProvenance Provenance() => JobProvenance.DirectRedCompute(
        "POST", "/ai-session/sessions", new JobBeneficiary("user", "owner"), "scratch-unit-request", null);

    public void Dispose()
    {
        foreach (var id in _openCodeSessions) DeleteInstructions(id);
        _probe.Dispose();
    }

    /// <summary>RedLeaf's scratch resolve route over a private folder, laid out as <c>PrepareExecution</c> lays it out.</summary>
    private sealed class Kernel(string root) : HttpMessageHandler
    {
        public List<(string Owner, string Key)> Requests { get; } = [];
        public bool Down { get; set; }
        public string? Answer { get; set; }

        public string Unit(string sessionId) => Path.Combine(root, "RedCompute", sessionId);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Equal("/api/compute/placement/resolve", request.RequestUri!.AbsolutePath);
            var scratch = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!["scratch"]!;
            var owner = scratch["owner"]!.GetValue<string>();
            var key = scratch["executionKey"]!.GetValue<string>();
            Requests.Add((owner, key));
            if (Down) throw new HttpRequestException("connection refused");
            var path = Answer ?? Directory.CreateDirectory(Path.Combine(root, owner, key)).FullName;
            var environment = new JsonObject();
            foreach (var variable in Variables) environment[variable] = path;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(new JsonObject
                {
                    ["scratch"] = new JsonObject { ["path"] = path, ["environment"] = environment },
                }.ToJsonString(), Encoding.UTF8, "application/json"),
            };
        }
    }
}
