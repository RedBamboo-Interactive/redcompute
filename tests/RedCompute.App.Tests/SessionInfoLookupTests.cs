using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using RedCompute.Core.Jobs;
using RedCompute.Core.Sessions;
using RedCompute.Plugin.ClaudeCode;
using RedCompute.Plugin.Codex;
using RedCompute.Plugin.OpenCode;
using RedCompute.PluginSdk;
using Xunit;
using MemoryJobTracker = RedCompute.App.Tests.ClaudeBackgroundWorkTests.MemoryJobTracker;
using NullQualityModes = RedCompute.App.Tests.ClaudeBackgroundWorkTests.NullQualityModes;

namespace RedCompute.App.Tests;

/// <summary>
/// <see cref="ISessionProvider.GetSessionInfo"/> is the metadata-only twin of
/// <c>GetSession(id).Info</c>: for every provider, every kind of session and every unknown id it
/// answers exactly the same value, and it never reads a transcript message to do so. Real
/// provider stores on scratch databases, never the installed ones.
/// </summary>
[Collection("Plugin database path")]
public sealed class SessionInfoLookupTests : IDisposable
{
    private const int LargeTranscript = 5_000;
    private readonly string _root = Path.Combine(
        Environment.GetEnvironmentVariable("REDLEAF_SCRATCH_DIR") ?? Path.GetTempPath(),
        "redcompute-session-info", Guid.NewGuid().ToString("N"));
    private readonly string _claudePath = ClaudeDbContext.DbPath;
    private readonly string _codexPath = CodexDbContext.DbPath;
    private readonly string _openCodePath = OpenCodeDbContext.DbPath;
    private readonly List<Process> _processes = [];

    public SessionInfoLookupTests()
    {
        Directory.CreateDirectory(_root);
        ClaudeDbContext.DbPath = Path.Combine(_root, "claude.db");
        CodexDbContext.DbPath = Path.Combine(_root, "codex.db");
        OpenCodeDbContext.DbPath = Path.Combine(_root, "opencode.db");
        using (var db = new ClaudeDbContext()) db.Initialize();
        using (var db = new CodexDbContext()) db.Initialize();
        using (var db = new OpenCodeDbContext()) db.Initialize();
    }

    public void Dispose()
    {
        foreach (var process in _processes)
        {
            // Not disposed: the provider's Exited handler reads ExitCode, and may run after this.
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); process.WaitForExit(5_000); } catch { }
        }
        ClaudeDbContext.DbPath = _claudePath;
        CodexDbContext.DbPath = _codexPath;
        OpenCodeDbContext.DbPath = _openCodePath;
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    // --- Claude -------------------------------------------------------------------------------

    [Fact]
    public void Claude_info_lookup_equals_the_full_read_for_every_session_shape()
    {
        var store = new ClaudeSessionStore();
        var (reads, counter) = Counting<IClaudeSessionStore>.Wrap(store);
        var service = new ClaudeSessionService(new ClaudeConfig { ClaudePath = FakeCli() }, new MemoryJobTracker(),
            reads, new NullQualityModes(), () => null, (_, _) => { });
        service.LaunchSessionProcess = _ => Track(Process.Start(Sink())!);
        ISessionProvider provider = new ClaudeCodeProvider("ai-session", service, reads, new MemoryJobTracker(), (_, _) => { });

        SaveClaude(store, "empty", "Stopped", SessionStopReasons.UserStopped, messages: 0);
        SaveClaude(store, "large", "Stopped", SessionStopReasons.MaintenanceRestart, messages: LargeTranscript);
        SaveClaude(store, "rolled", "Stopped", SessionStopReasons.IdleTimeout, messages: 12);
        store.MarkMessagesRolledBack("rolled", "u-rolled-3");
        SaveClaude(store, "errored", "Error", "process_exited", messages: 3);
        // Stored as live but not loaded here: both reads must report the same orphaned stop.
        SaveClaude(store, "stale-active", "Active", null, messages: 4);
        SaveClaude(store, "stale-idle", "Idle", "completed", messages: 4, dismissed: true);
        var live = service.StartSession(_root, "opus", "owner", null, null, "high", null, null, null,
            JobProvenance.DirectRedCompute("POST", "/ai-session/sessions", new JobBeneficiary("user", "owner"), "request-1", null));
        Assert.NotNull(live);

        AssertEquivalent(provider, counter, ["empty", "large", "rolled", "errored", "stale-active", "stale-idle", live!.Id]);
        AssertUnknown(provider, counter, ["missing", "", "codex-only", "opencode-only"]);
        Assert.Equal(LargeTranscript, provider.GetSession("large").History.Count);
    }

    // --- Codex --------------------------------------------------------------------------------

    [Fact]
    public void Codex_info_lookup_equals_the_full_read_for_every_session_shape()
    {
        var store = new CodexSessionStore();
        var (reads, counter) = Counting<ICodexSessionStore>.Wrap(store);
        var service = new CodexInteractiveService(new CodexConfig(), reads, null!, null!, null!, null!, null!, () => null, (_, _) => { });
        ISessionProvider provider = new CodexProvider("ai-session", service, new MemoryJobTracker(), (_, _) => { });

        SaveCodex(store, "empty", "Stopped", SessionStopReasons.UserStopped, messages: 0);
        SaveCodex(store, "large", "Idle", null, messages: LargeTranscript, historyMode: "paginated");
        SaveCodex(store, "rolled", "Stopped", SessionStopReasons.MaintenanceRestart, messages: 12, historyMode: "paginated");
        store.MarkMessagesRolledBack("rolled", "u-rolled-3");
        SaveCodex(store, "errored", "Error", "turn_failed", messages: 3);
        SaveCodex(store, "waiting", "Waiting", null, messages: 2, historyMode: "legacy");
        InsertLive(service, new CodexSessionInfo
        {
            Id = "codex-live", ProjectName = "Live", ProjectPath = _root, Status = "Active", ThreadId = "thread-live",
            Model = "gpt", Title = "Live one", MessageCount = 9, CostUsd = 0.5, UserId = "owner",
            AccessLevel = SessionAccessLevels.Auto, HistoryMode = "paginated", LastTurnOutcome = "completed",
        });

        AssertEquivalent(provider, counter, ["empty", "large", "rolled", "errored", "waiting", "codex-live"]);
        AssertUnknown(provider, counter, ["missing", "", "claude-only", "opencode-only"]);
        Assert.Equal(LargeTranscript, provider.GetSession("large").History.Count);
    }

    // --- OpenCode -----------------------------------------------------------------------------

    [Fact]
    public void OpenCode_info_lookup_equals_the_full_read_for_every_session_shape()
    {
        var store = new OpenCodeSessionStore();
        var (reads, counter) = Counting<IOpenCodeSessionStore>.Wrap(store);
        var service = new OpenCodeSessionService(new OpenCodeConfig(), new MemoryJobTracker(), reads, (_, _) => { },
            new EmptyNativeReader());
        ISessionProvider provider = new OpenCodeProvider("ai-session", service, store, new MemoryJobTracker(), (_, _) => { });

        SaveOpenCode(store, "empty", "Stopped", SessionStopReasons.UserStopped, messages: 0);
        SaveOpenCode(store, "large", "Stopped", SessionStopReasons.MaintenanceRestart, messages: LargeTranscript);
        SaveOpenCode(store, "errored", "Error", "process_exited", messages: 3);
        SaveOpenCode(store, "dismissed", "Stopped", SessionStopReasons.UserStopped, messages: 1, dismissed: true);
        var managed = new OpenCodeSessionService.ManagedSession(new OpenCodeSessionInfo
        {
            Id = "opencode-live", ProjectName = "Live", ProjectPath = _root, Status = "Idle", Title = "Live one",
            MessageCount = 7, AccessLevel = SessionAccessLevels.Ask, OpenCodeSessionId = "native-1",
        }, null!, new CancellationTokenSource());
        var sessions = typeof(OpenCodeSessionService).GetField("_sessions", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(service)!;
        sessions.GetType().GetMethod("TryAdd")!.Invoke(sessions, ["opencode-live", managed]);

        AssertEquivalent(provider, counter, ["empty", "large", "errored", "dismissed", "opencode-live"]);
        AssertUnknown(provider, counter, ["missing", "", "claude-only", "codex-only"]);
        Assert.Equal(LargeTranscript, provider.GetSession("large").History.Count);
    }

    /// <summary>A provider plugin that predates the method still answers through its full read.</summary>
    [Fact]
    public void A_provider_without_its_own_lookup_falls_back_to_the_full_read()
    {
        var legacy = new LegacyProvider();
        ISessionProvider provider = legacy;

        Assert.Same(legacy.Info, provider.GetSessionInfo("legacy"));
        Assert.Null(provider.GetSessionInfo("missing"));
        Assert.Equal(2, legacy.Reads);
    }

    // --- assertions ---------------------------------------------------------------------------

    private static void AssertEquivalent<TStore>(ISessionProvider provider, Counting<TStore> reads, string[] ids)
        where TStore : class
    {
        foreach (var id in ids)
        {
            reads.Reset();
            var lookup = provider.GetSessionInfo(id);
            Assert.True(reads.MessageReads == 0, $"{provider.ProviderId}:{id} read {reads.MessageReads} message batch(es) for metadata");
            Assert.NotNull(lookup);

            var full = provider.GetSession(id).Info;
            Assert.True(reads.MessageReads > 0, $"{provider.ProviderId}:{id}: the full read is expected to read messages");
            Assert.NotNull(full);
            Assert.Equal(Json(full), Json(lookup));
        }
    }

    private static void AssertUnknown<TStore>(ISessionProvider provider, Counting<TStore> reads, string[] ids)
        where TStore : class
    {
        foreach (var id in ids)
        {
            reads.Reset();
            Assert.Null(provider.GetSessionInfo(id));
            Assert.Equal(0, reads.MessageReads);
            Assert.Null(provider.GetSession(id).Info);
        }
    }

    private static string Json(UnifiedSessionInfo? info) => JsonSerializer.Serialize(info);

    // --- fixtures -----------------------------------------------------------------------------

    private void SaveClaude(ClaudeSessionStore store, string id, string status, string? stopReason, int messages,
        bool dismissed = false)
    {
        store.SaveSession(new ClaudeSessionRecord
        {
            Id = id, ProjectName = "P-" + id, ProjectPath = _root, Status = status, StopReason = stopReason,
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-messages), StoppedAt = status is "Stopped" or "Error" ? DateTimeOffset.UtcNow : null,
            Model = "opus", ClaudeSessionId = "claude-" + id, Title = "Title " + id, MessageCount = messages,
            CostUsd = messages * 0.001, InputTokens = messages * 10, OutputTokens = messages * 3,
            CacheReadInputTokens = messages, CacheCreationInputTokens = 2, ContextTokens = 1000, ContextWindow = 200_000,
            Effort = "high", QualityTier = "deep", ProviderEntity = "anthropic", JobId = Guid.NewGuid(), Source = "nova",
            UserId = "owner", AccessLevel = SessionAccessLevels.Auto, MaxAccessLevel = SessionAccessLevels.Full,
            Unattended = true, AccessLevelSource = SessionAccessLevelSources.Explicit, Dismissed = dismissed,
        });
        store.AddMessages(Enumerable.Range(0, messages).Select(i => new ClaudeMessageRecord
        {
            SessionId = id, Role = i % 2 == 0 ? "user" : "assistant", EventType = "text", Content = $"message {i} of {id}",
            MessageUid = $"u-{id}-{i / 2}", Timestamp = DateTimeOffset.UtcNow.AddSeconds(i),
        }).ToList());
    }

    private void SaveCodex(CodexSessionStore store, string id, string status, string? stopReason, int messages,
        string? historyMode = null)
    {
        store.SaveSession(new CodexSessionRecord
        {
            Id = id, ProjectName = "P-" + id, ProjectPath = _root, Status = status, StopReason = stopReason,
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-messages), StoppedAt = status is "Stopped" or "Error" ? DateTimeOffset.UtcNow : null,
            Model = "gpt", Title = "Title " + id, MessageCount = messages, CostUsd = messages * 0.001,
            InputTokens = messages * 10, OutputTokens = messages * 3, CachedInputTokens = messages, ContextTokens = 900,
            ContextWindow = 400_000, JobId = Guid.NewGuid(), ThreadId = "thread-" + id, LastActivity = DateTimeOffset.UtcNow,
            Effort = "medium", QualityTier = "fast", ProviderEntity = "openai", Source = "code", UserId = "owner",
            AccessLevel = SessionAccessLevels.Ask, MaxAccessLevel = SessionAccessLevels.Full, HistoryMode = historyMode,
            ExecutionProfile = SessionExecutionProfile.Default,
        });
        store.AddMessages(Enumerable.Range(0, messages).Select(i => new CodexMessageRecord
        {
            SessionId = id, Role = i % 2 == 0 ? "user" : "assistant", EventType = "text", Content = $"message {i} of {id}",
            MessageUid = $"u-{id}-{i / 2}", Timestamp = DateTimeOffset.UtcNow.AddSeconds(i),
        }).ToList());
    }

    private void SaveOpenCode(OpenCodeSessionStore store, string id, string status, string? stopReason, int messages,
        bool dismissed = false)
    {
        store.SaveSession(new OpenCodeSessionRecord
        {
            Id = id, ProjectName = "P-" + id, ProjectPath = _root, Status = status, StopReason = stopReason,
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-messages), StoppedAt = status is "Stopped" or "Error" ? DateTimeOffset.UtcNow : null,
            Model = "glm", Title = "Title " + id, MessageCount = messages, CostUsd = messages * 0.001,
            InputTokens = messages * 10, OutputTokens = messages * 3, JobId = Guid.NewGuid(), OpenCodeSessionId = "native-" + id,
            Effort = "low", QualityTier = "cheap", ProviderEntity = "zai", Source = "nova", Dismissed = dismissed,
            AccessLevel = SessionAccessLevels.Full, MaxAccessLevel = SessionAccessLevels.Full,
        });
        store.AddMessages(Enumerable.Range(0, messages).Select(i => new OpenCodeMessageRecord
        {
            SessionId = id, Role = i % 2 == 0 ? "user" : "assistant", EventType = "text", Content = $"message {i} of {id}",
            MessageUid = $"u-{id}-{i / 2}", ProviderPartId = $"part-{id}-{i}", Timestamp = DateTimeOffset.UtcNow.AddSeconds(i),
        }).ToList());
    }

    private static void InsertLive(CodexInteractiveService service, CodexSessionInfo info)
    {
        var type = typeof(CodexInteractiveService).GetNestedType("ManagedSession", BindingFlags.NonPublic)!;
        var session = Activator.CreateInstance(type, nonPublic: true)!;
        type.GetProperty("Info")!.SetValue(session, info);
        var sessions = typeof(CodexInteractiveService).GetField("_sessions", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(service)!;
        sessions.GetType().GetMethod("TryAdd")!.Invoke(sessions, [info.Id, session]);
    }

    private string FakeCli()
    {
        var path = Path.Combine(_root, "claude.exe");
        if (!File.Exists(path)) File.WriteAllText(path, "");
        return path;
    }

    private static ProcessStartInfo Sink() => new()
    {
        FileName = TestProcesses.StdinSink, CreateNoWindow = true, UseShellExecute = false,
        RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
    };

    private Process Track(Process process)
    {
        _processes.Add(process);
        return process;
    }

    private sealed class EmptyNativeReader : IOpenCodeNativeSessionReader
    {
        public OpenCodeNativeMessageVisibility GetMessageVisibility(string sessionId, string messageId) => OpenCodeNativeMessageVisibility.Visible;
        public IReadOnlyList<OpenCodeNativePart> GetCompletedAssistantParts(string sessionId) => [];
    }

    /// <summary>Forwards every call to the real store and counts transcript reads.</summary>
    public class Counting<TStore> : DispatchProxy where TStore : class
    {
        private TStore _target = null!;
        public int MessageReads { get; private set; }
        public void Reset() => MessageReads = 0;

        public static (TStore Store, Counting<TStore> Counter) Wrap(TStore target)
        {
            var proxy = Create<TStore, Counting<TStore>>();
            var counting = (Counting<TStore>)(object)proxy;
            counting._target = target;
            return (proxy, counting);
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod!.Name == "GetMessages") MessageReads++;
            try { return targetMethod.Invoke(_target, args); }
            catch (TargetInvocationException ex) when (ex.InnerException is not null) { throw ex.InnerException; }
        }
    }

    private sealed class LegacyProvider : ISessionProvider
    {
        public int Reads;
        public UnifiedSessionInfo Info { get; } = new() { Id = "legacy", Provider = "legacy", ProjectName = "L", ProjectPath = "." };
        public (UnifiedSessionInfo? Info, List<UnifiedMessageRecord> History) GetSession(string sessionId)
        {
            Reads++;
            return sessionId == "legacy" ? (Info, []) : (null, []);
        }

        public string ProviderId => "legacy";
        public string ProviderDisplayName => "Legacy";
        public SessionCapabilities Capabilities => SessionCapabilities.PersistentSessions;
        public string? LastStartError => null;
        public event Action<string, UnifiedStreamEvent>? SessionStreamEvent { add { } remove { } }
        public Task<UnifiedSessionInfo?> StartSessionAsync(string projectPath, string? model, string? userId, string? userName,
            string? userAvatarUrl, string? effort, string? endpointUrl, string? apiKey, int? thinkingBudget, string? qualityTier,
            string? providerEntity, Guid? repositoryId, JobProvenance provenance, string? scratchDirectory = null,
            bool confidential = false) => throw new NotSupportedException();
        public Task<UnifiedSessionInfo?> ResumeSessionAsync(string sessionId) => throw new NotSupportedException();
        public Task StopSessionAsync(string sessionId) => Task.CompletedTask;
        public Task ForceKillAsync(string sessionId) => Task.CompletedTask;
        public void DismissSession(string sessionId) { }
        public Task<bool> SendInputAsync(string sessionId, IReadOnlyList<SessionInputPart> input, string? attachmentsJson = null,
            string? messageUid = null) => Task.FromResult(false);
        public bool SendAnswer(string sessionId, string answer) => false;
        public InterruptResult InterruptSession(string sessionId) => InterruptResult.NotFound;
        public bool SetPermissionMode(string sessionId, string mode) => false;
        public List<UnifiedSessionInfo> GetSessions(int limit = 20, bool includeDismissed = false) => [];
        public (UnifiedSessionInfo? Info, List<UnifiedMessageRecord> History) GetSessionByJobId(Guid jobId) => (null, []);
        public Dictionary<Guid, RedCompute.Core.Sessions.SessionStatus> GetSessionStatusesByJobIds(IEnumerable<Guid> jobIds) => [];
        public Task<SessionExecuteResult> ExecuteAsync(string prompt, string? workingDir, string? model, int timeout,
            CancellationToken ct, string? streamKey = null, Dictionary<string, string>? env = null,
            Dictionary<string, object?>? providerParams = null) => throw new NotSupportedException();
        public Task<SessionGenerateResult> GenerateAsync(string? model, string? system, string messagesJson, int maxTokens,
            CancellationToken ct, string? effort = null, int? timeout = null) => throw new NotSupportedException();
        public List<ModelInfo> GetAvailableModels() => [];
        public void CancelExecution(string key) { }
        public Task StopAllAsync() => Task.CompletedTask;
    }
}
