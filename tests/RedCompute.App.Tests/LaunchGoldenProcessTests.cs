using System.Text.Json;
using System.Text.Json.Nodes;
using RedCompute.Core.Jobs;
using RedCompute.Core.Sessions;
using RedCompute.Plugin.ClaudeCode;
using RedCompute.Plugin.OpenCode;
using RedCompute.PluginSdk;
using Xunit;

namespace RedCompute.App.Tests;

/// <summary>
/// Layer B launch goldens (node-zero design, section 4): the real provider code launches the probe CLI
/// through its production launch path, and the probe records what the OS actually started: command
/// line, argv, cwd, environment changes against this test host, and the containment the process
/// landed in. They never touch a launch seam, so they keep proving the same thing while the seams
/// change type. Real processes, Windows only (containment is a Windows Job Object).
/// </summary>
[Collection(LaunchGoldenCollection.Name)]
public sealed class LaunchGoldenProcessTests : IDisposable
{
    private const string Token = "golden-execution-token";
    private const string ClaudeConversation = "c1a0de00-0000-4000-8000-000000000001";
    private const string ForkPoint = "11111111-1111-4111-8111-11111111aaaa";

    private const string OneshotOutput =
        "{\"type\":\"system\",\"subtype\":\"init\",\"tools\":[]}";
    private const string OneshotResult =
        "{\"type\":\"result\",\"result\":\"ok\",\"usage\":{\"input_tokens\":1,\"output_tokens\":1}}";

    private readonly ProbeHarness _probe = new("app");
    private readonly string _project;
    private readonly string _scratch;

    public LaunchGoldenProcessTests()
    {
        _project = Path.Combine(_probe.Root, "project");
        _scratch = Path.Combine(_probe.Root, "scratch");
        Directory.CreateDirectory(_project);
        Directory.CreateDirectory(_scratch);
    }

    // ---- Claude Code ------------------------------------------------------------------------------

    [WindowsFact("containment is a Windows Job Object and the probe replaces claude.exe")]
    public async Task Claude_session_start()
    {
        var service = Claude(new ClaudeDeveloperInstructionsTests.MemoryStore());
        ClaudeSessionInfo? info = null;
        var launches = await _probe.RunAsync(async () =>
        {
            using var _ = SessionScratch.PushExecutionToken(Token);
            info = await service.StartSessionAsync(_project, "claude-golden-model", "owner", null, null, "high",
                null, null, null, Provenance(), scratchDirectory: _scratch);
        });
        Assert.NotNull(info);
        _probe.Normalizer.Add(info!.Id, "<session-id>");
        await service.StopAllAsync();
        LaunchGoldens.Verify("B-claude-session-start", Single(launches));
    }

    [WindowsFact("containment is a Windows Job Object and the probe replaces claude.exe")]
    public async Task Claude_session_resume()
    {
        var store = new ClaudeDeveloperInstructionsTests.MemoryStore();
        store.SaveSession(new ClaudeSessionRecord
        {
            Id = "golden-resume", ProjectName = "Golden", ProjectPath = _project, Status = "Stopped",
            ClaudeSessionId = ClaudeConversation, MessageCount = 1, Model = "claude-golden-model",
            Effort = "medium", ScratchDirectory = _scratch, StartedAt = DateTimeOffset.UtcNow,
            StopReason = SessionStopReasons.IdleTimeout,
        });
        var service = Claude(store);
        ClaudeSessionInfo? info = null;
        var launches = await _probe.RunAsync(async () =>
        {
            using var _ = SessionScratch.PushExecutionToken(Token);
            info = await service.ResumeSessionAsync("golden-resume", Provenance());
        });
        Assert.NotNull(info);
        await service.StopAllAsync();
        LaunchGoldens.Verify("B-claude-session-resume", Single(launches));
    }

    [WindowsFact("containment is a Windows Job Object and the probe replaces claude.exe")]
    public async Task Claude_oneshot()
    {
        var service = Claude(new ClaudeDeveloperInstructionsTests.MemoryStore(),
            new JsonObject { ["onEof"] = new JsonArray(OneshotOutput, OneshotResult) });
        ClaudeSessionService.OneshotResult? result = null;
        var launches = await _probe.RunAsync(async () =>
        {
            using var messages = JsonDocument.Parse("[{\"role\":\"user\",\"content\":\"hello\"}]");
            result = await service.ExecuteOneshotAsync("claude-golden-model", "be brief", messages.RootElement, 32,
                CancellationToken.None, "low", 60);
        });
        Assert.True(result!.Success, result.Error);
        var (launch, _) = Assert.Single(launches);
        _probe.Normalizer.Add(launch.Cwd, "<isolated-cwd>");
        LaunchGoldens.Verify("B-claude-oneshot", Single(launches));
    }

    [WindowsFact("containment is a Windows Job Object and the probe replaces claude.exe")]
    public async Task Claude_agent()
    {
        var service = Claude(new ClaudeDeveloperInstructionsTests.MemoryStore(),
            new JsonObject { ["onEof"] = new JsonArray(OneshotResult) });
        ClaudeSessionService.ExecuteResult? result = null;
        var launches = await _probe.RunAsync(async () =>
        {
            result = await service.ExecuteAgentAsync("golden prompt", null, _project, "claude-golden-model", "high",
                3, null, null, null, 60, CancellationToken.None);
        });
        Assert.NotNull(result);
        LaunchGoldens.Verify("B-claude-agent", Single(launches));
    }

    /// <summary>
    /// Today's fork probe is a raw Process.Start: uncontained, inherited environment, no scratch or
    /// token (Rollback.cs:196). The golden pins exactly that until a separately approved change.
    /// </summary>
    [WindowsFact("containment is a Windows Job Object and the probe replaces claude.exe")]
    public async Task Claude_fork_probe()
    {
        var store = SeedRollback(_project, _scratch);
        var projects = Path.Combine(_probe.Root, "projects");
        WriteTwoTurnChain(Path.Combine(projects, "golden"));
        var service = Claude(store, new JsonObject
        {
            ["replyOnce"] = new JsonArray(
                "{\"type\":\"control_response\",\"response\":{\"subtype\":\"success\",\"request_id\":\"rollback-probe\"}}"),
        });
        service.ProjectsRootOverride = projects;

        SessionRollbackResult? result = null;
        var launches = await _probe.RunAsync(async () =>
        {
            using var _ = SessionScratch.PushExecutionToken(Token);
            result = await service.RollbackAsync(RollbackRequest());
        });
        Assert.Equal(SessionRollbackStatus.Applied, result!.Status);
        LaunchGoldens.Verify("B-claude-fork-probe", Single(launches));
    }

    // ---- OpenCode ----------------------------------------------------------------------------------

    [WindowsFact("containment is a Windows Job Object and the probe replaces opencode.exe")]
    public async Task OpenCode_acp_session()
    {
        var store = new GoldenOpenCodeStore();
        var service = OpenCode(store, new JsonObject
        {
            ["rpc"] = new JsonObject
            {
                ["results"] = new JsonObject { ["session/new"] = new JsonObject { ["sessionId"] = "native-golden" } },
            },
        });
        OpenCodeSessionInfo? info = null;
        var instructions = InstructionFilesBefore();
        var launches = await _probe.RunAsync(async () =>
        {
            using var _ = SessionScratch.PushExecutionToken(Token);
            info = await service.StartSession(_project, null, "owner", null, null, null, null, null, null, null,
                null, null, Provenance(), _scratch);
        });
        Assert.NotNull(info);
        _probe.Normalizer.Add(info!.Id, "<session-id>");
        await service.StopSession(info.Id);
        var golden = Single(launches);
        golden["instructionFiles"] = TakeNewInstructionFiles(instructions, info.Id);
        LaunchGoldens.Verify("B-opencode-acp", golden);
    }

    [WindowsFact("containment is a Windows Job Object and the probe replaces opencode.exe")]
    public async Task OpenCode_exec()
    {
        var service = OpenCode(new GoldenOpenCodeStore(), new JsonObject
        {
            ["onEof"] = new JsonArray("{\"type\":\"text\",\"part\":{\"type\":\"text\",\"text\":\"ok\"}}"),
        });
        OpenCodeSessionService.ExecuteResult? result = null;
        var launches = await _probe.RunAsync(async () =>
        {
            result = await service.ExecuteAsync("golden prompt", null, _project, "golden/model", 60,
                CancellationToken.None, env: new Dictionary<string, string> { ["GOLDEN_EXEC_ENV"] = "1" });
        });
        Assert.NotNull(result);
        LaunchGoldens.Verify("B-opencode-exec", Single(launches));
    }

    // ---- helpers -------------------------------------------------------------------------------

    private JsonObject Single(IReadOnlyList<(ProbeLaunch Launch, string Containment)> launches)
    {
        var (launch, containment) = Assert.Single(launches);
        return launch.Canonical(_probe.Normalizer, containment);
    }

    private ClaudeSessionService Claude(IClaudeSessionStore store, JsonObject? script = null)
    {
        var claudePath = _probe.Install("claude", script);
        return new ClaudeSessionService(new ClaudeConfig { ClaudePath = claudePath },
            new ClaudeDeveloperInstructionsTests.MemoryJobTracker(), store,
            new ClaudeDeveloperInstructionsTests.NullQualityModes(), () => null, (_, _) => { });
    }

    private OpenCodeSessionService OpenCode(IOpenCodeSessionStore store, JsonObject? script = null)
    {
        var path = _probe.Install("opencode", script);
        var dataHome = Path.Combine(_probe.Root, "opencode-data");
        Directory.CreateDirectory(dataHome);
        return new OpenCodeSessionService(new OpenCodeConfig { OpenCodePath = path, OpenCodeDataHome = dataHome },
            new ClaudeDeveloperInstructionsTests.MemoryJobTracker(), store, (_, _) => { });
    }

    private static string InstructionsDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RedCompute", "plugins", "opencode", "instructions");

    private static HashSet<string> InstructionFilesBefore() => Directory.Exists(InstructionsDirectory)
        ? Directory.EnumerateFiles(InstructionsDirectory).ToHashSet(StringComparer.OrdinalIgnoreCase)
        : [];

    /// <summary>
    /// The ACP launch writes its developer instructions (here the access note of the default level) to
    /// a file under %LOCALAPPDATA%/RedCompute/plugins/opencode/instructions named after the new
    /// session, and points OPENCODE_CONFIG_CONTENT at it. Part of the launch: recorded, then exactly
    /// those files (this test's session ids) are deleted.
    /// </summary>
    private JsonArray TakeNewInstructionFiles(HashSet<string> before, string sessionId)
    {
        var created = InstructionFilesBefore().Except(before)
            .Where(file => string.Equals(Path.GetFileName(file), sessionId + ".md", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.Ordinal).ToList();
        var files = new JsonArray();
        foreach (var file in created)
        {
            files.Add(new JsonObject
            {
                ["path"] = _probe.Normalizer.Apply(file),
                ["content"] = _probe.Normalizer.Apply(File.ReadAllText(file)),
            });
            File.Delete(file);
        }
        return files;
    }

    /// <summary>A stopped two-turn session "s" the rollback to its second turn forks from.</summary>
    internal static ClaudeRollbackTests.ForkStore SeedRollback(string project, string scratch)
    {
        var store = new ClaudeRollbackTests.ForkStore();
        store.SaveSession(new ClaudeSessionRecord
        {
            Id = "s", ClaudeSessionId = ClaudeConversation, ProjectName = "Golden", ProjectPath = project,
            Model = "haiku", Effort = "high", UserId = "owner", Status = "Stopped",
            StopReason = SessionStopReasons.IdleTimeout, StartedAt = DateTimeOffset.UtcNow, MessageCount = 2,
            ScratchDirectory = scratch,
        });
        foreach (var (uid, prompt, text) in new[] { ("u1", "P1", "first"), ("u2", "P2", "second") })
        {
            store.AddMessage(new ClaudeMessageRecord
            {
                SessionId = "s", Role = "user", EventType = "text", Content = text, MessageUid = uid,
                PromptUuid = prompt, Timestamp = DateTimeOffset.UtcNow,
            });
            store.AddMessage(new ClaudeMessageRecord
            {
                SessionId = "s", Role = "assistant", EventType = "text", Content = "reply", MessageUid = "a" + uid,
                Timestamp = DateTimeOffset.UtcNow,
            });
        }
        return store;
    }

    internal static SessionRollbackRequest RollbackRequest() => new("s", "rollback-1", "u2",
        [new SessionRollbackUserTurn("u1", "first")], [new SessionRollbackUserTurn("u2", "second")]);

    internal static void WriteTwoTurnChain(string folder)
    {
        Directory.CreateDirectory(folder);
        string? parent = null;
        var lines = new List<string>();
        void Prompt(string uuid, string text)
        {
            lines.Add(JsonSerializer.Serialize(new { type = "user", uuid, parentUuid = parent, isSidechain = false, message = new { role = "user", content = text } }));
            parent = uuid;
        }
        void Reply(string uuid)
        {
            lines.Add(JsonSerializer.Serialize(new
            {
                type = "assistant", uuid, parentUuid = parent, isSidechain = false,
                message = new { role = "assistant", content = new[] { new { type = "text", text = "reply" } } },
            }));
            parent = uuid;
        }
        Prompt("P1", "first");
        Reply(ForkPoint);
        Prompt("P2", "second");
        Reply("22222222-2222-4222-8222-22222222bbbb");
        File.WriteAllText(Path.Combine(folder, $"{ClaudeConversation}.jsonl"), string.Join("\n", lines) + "\n");
    }

    private static JobProvenance Provenance() => JobProvenance.DirectRedCompute(
        "POST", "/ai-session/sessions", new JobBeneficiary("user", "owner"), "golden-request", null);

    public void Dispose() => _probe.Dispose();

    internal sealed class GoldenOpenCodeStore : IOpenCodeSessionStore
    {
        private readonly Dictionary<string, OpenCodeSessionRecord> _sessions = [];
        public OpenCodeSessionRecord? FindSession(string sessionId) { lock (_sessions) return _sessions.GetValueOrDefault(sessionId); }
        public OpenCodeSessionRecord? FindSessionByJobId(Guid jobId) { lock (_sessions) return _sessions.Values.FirstOrDefault(s => s.JobId == jobId); }
        public List<OpenCodeSessionRecord> GetActiveSessions() => [];
        public List<OpenCodeSessionRecord> GetRecentSessions(HashSet<string> excludeIds, int limit = 20, bool includeDismissed = false) => [];
        public void SaveSession(OpenCodeSessionRecord record) { lock (_sessions) _sessions[record.Id] = record; }
        public void DismissSession(string sessionId) { }
        public void AddMessage(OpenCodeMessageRecord message) { }
        public void AddMessages(List<OpenCodeMessageRecord> messages) { }
        public List<OpenCodeMessageRecord> GetMessages(string sessionId, int limit = 50_000) => [];
        public Dictionary<Guid, string> GetSessionStatusesByJobIds(IEnumerable<Guid> jobIds) => [];
    }
}

/// <summary>Launch goldens read and compare the whole process environment: never in parallel with each other.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class LaunchGoldenCollection
{
    public const string Name = "Launch goldens";
}
