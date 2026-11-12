using System.Diagnostics;
using System.Reflection;
using RedBamboo.AppHost.WebSockets;
using RedCompute.App.Services;
using RedCompute.Core.Capabilities;
using RedCompute.Core.Configuration;
using RedCompute.Core.Jobs;
using RedCompute.Core.Providers;
using RedCompute.Core.Sessions;
using RedCompute.Plugin.ClaudeCode;
using RedCompute.PluginSdk;
using Xunit;
using Xunit.Abstractions;
using MemoryJobTracker = RedCompute.App.Tests.ClaudeBackgroundWorkTests.MemoryJobTracker;
using NullQualityModes = RedCompute.App.Tests.ClaudeBackgroundWorkTests.NullQualityModes;

namespace RedCompute.App.Tests;

/// <summary>
/// The status lookups that dominated RedCompute's allocations (journal 2026-11-12, 9711f8ea): one
/// prompt-callback sweep and the per-session queue summary, measured on the same scratch database
/// through the code as it was (Info taken from a full, change-tracked history read) and as it is
/// now (<see cref="ISessionProvider.GetSessionInfo"/>). The suite runs it small; set
/// <c>REDCOMPUTE_BENCH_MESSAGES=5000</c> for the 20 x 5 000 figures.
/// </summary>
[Collection("Plugin database path")]
public sealed class SessionInfoLookupBenchmarkTests(ITestOutputHelper output) : IDisposable
{
    private const int Sessions = 20;
    private readonly string _root = Path.Combine(
        Environment.GetEnvironmentVariable("REDLEAF_SCRATCH_DIR") ?? Path.GetTempPath(),
        "redcompute-info-bench", Guid.NewGuid().ToString("N"));
    private readonly string _claudePath = ClaudeDbContext.DbPath;

    public void Dispose()
    {
        ClaudeDbContext.DbPath = _claudePath;
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public async Task Status_lookups_no_longer_read_transcripts()
    {
        var messages = int.TryParse(Environment.GetEnvironmentVariable("REDCOMPUTE_BENCH_MESSAGES"), out var n) ? n : 500;
        Directory.CreateDirectory(_root);
        ClaudeDbContext.DbPath = Path.Combine(_root, "claude.db");
        using (var db = new ClaudeDbContext()) db.Initialize();
        var store = new ClaudeSessionStore();
        var ids = Enumerable.Range(0, Sessions).Select(i => $"bench-{i:D2}").ToArray();
        foreach (var id in ids) Seed(store, id, messages);

        var before = Build(new LegacyClaudeProvider(Service(TrackedHistory.Over(store)), store));
        var after = Build(new ClaudeCodeProvider("ai-session", Service(store), store, new MemoryJobTracker(), (_, _) => { }));
        foreach (var fixture in new[] { before, after })
            foreach (var id in ids)
                await fixture.Callbacks.RegisterAsync(id, "callback-" + id, "http://127.0.0.1:9/never", "owner", "prompt-" + id);

        var sweepBefore = await Measure(() => before.Callbacks.SweepAsync(CancellationToken.None));
        var sweepAfter = await Measure(() => after.Callbacks.SweepAsync(CancellationToken.None));
        var summaryBefore = await Measure(() => Summaries(before, ids));
        var summaryAfter = await Measure(() => Summaries(after, ids));

        output.WriteLine($"{Sessions} sessions x {messages} messages");
        output.WriteLine($"callback sweep:  before {sweepBefore}  after {sweepAfter}");
        output.WriteLine($"queue summaries: before {summaryBefore}  after {summaryAfter}");

        // The new path's cost does not grow with the transcript: a fraction of the old one already
        // at the suite's small size (about a twentieth alone; the bound leaves room for a busy run).
        Assert.True(sweepAfter.Bytes * 5 < sweepBefore.Bytes, $"sweep {sweepAfter} vs {sweepBefore}");
        Assert.True(summaryAfter.Bytes * 5 < summaryBefore.Bytes, $"summary {summaryAfter} vs {summaryBefore}");
    }

    private sealed record Fixture(SessionPromptCallbacks Callbacks, SessionInputQueueService Queue);

    private Fixture Build(ClaudeCodeProvider provider)
    {
        var config = new RedComputeConfig();
        var dir = Path.Combine(_root, Guid.NewGuid().ToString("N"));
        var attachments = new InputAttachmentStore(config, dir, Path.Combine(_root, $"{Guid.NewGuid():N}.db"));
        var registry = new CapabilityRegistry();
        registry.Register("ai-session", new CapabilityDefinition { Slug = "ai-session", DisplayName = "AI Session" },
            new CapabilityConfig(), new Dictionary<string, IBackendProvider> { ["claude-code"] = provider }, "claude-code");
        var queueStore = new SessionInputQueueStore(config, attachments);
        // The exact lookup RelayServer hands the callback store.
        var callbacks = new SessionPromptCallbacks(attachments.DatabasePath, queueStore,
            sessionId => registry.FindProviders<ISessionProvider>().Select(p => p.GetSessionInfo(sessionId)).FirstOrDefault(i => i is not null),
            new HttpClient());
        var queue = new SessionInputQueueService(queueStore, attachments, registry, new MemoryJobTracker(),
            new WebSocketBroadcaster(), (_, _) => { });
        return new(callbacks, queue);
    }

    private static async Task Summaries(Fixture fixture, string[] ids)
    {
        foreach (var id in ids) await fixture.Queue.GetSummaryAsync(id, "owner");
    }

    private sealed record Cost(long Bytes, double Milliseconds)
    {
        public override string ToString() => $"{Bytes / 1024.0 / 1024.0:F2} MB, {Milliseconds:F1} ms";
    }

    /// <summary>
    /// One warm-up, then the least of five runs. Allocation is process-wide (precise), so tests running
    /// alongside can only add to a sample; the minimum is the closest to this work alone.
    /// </summary>
    private static async Task<Cost> Measure(Func<Task> run)
    {
        await run();
        var samples = new List<Cost>();
        for (var i = 0; i < 5; i++)
        {
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            var bytes = GC.GetTotalAllocatedBytes(precise: true);
            var watch = Stopwatch.StartNew();
            await run();
            watch.Stop();
            samples.Add(new(GC.GetTotalAllocatedBytes(precise: true) - bytes, watch.Elapsed.TotalMilliseconds));
        }
        return new(samples.Min(s => s.Bytes), samples.Min(s => s.Milliseconds));
    }

    private ClaudeSessionService Service(IClaudeSessionStore store)
    {
        var cli = Path.Combine(_root, "claude.exe");
        if (!File.Exists(cli)) File.WriteAllText(cli, "");
        return new ClaudeSessionService(new ClaudeConfig { ClaudePath = cli }, new MemoryJobTracker(), store,
            new NullQualityModes(), () => null, (_, _) => { });
    }

    private void Seed(ClaudeSessionStore store, string id, int messages)
    {
        store.SaveSession(new ClaudeSessionRecord
        {
            Id = id, ProjectName = "Bench", ProjectPath = _root, Status = "Stopped",
            StopReason = SessionStopReasons.MaintenanceRestart, StartedAt = DateTimeOffset.UtcNow, StoppedAt = DateTimeOffset.UtcNow,
            MessageCount = messages, UserId = "owner", Title = "Bench " + id,
        });
        // Roughly the shape of a real transcript: text turns and tool calls with sizeable bodies.
        store.AddMessages(Enumerable.Range(0, messages).Select(i => new ClaudeMessageRecord
        {
            SessionId = id, Role = i % 3 == 0 ? "user" : "assistant", EventType = i % 3 == 2 ? "tool_use" : "text",
            Content = new string('x', 400), ToolName = i % 3 == 2 ? "Bash" : null,
            ToolInput = i % 3 == 2 ? "{\"command\":\"" + new string('y', 200) + "\"}" : null,
            MessageId = $"m-{id}-{i}", MessageUid = $"u-{id}-{i / 3}", Timestamp = DateTimeOffset.UtcNow.AddSeconds(i),
        }).ToList());
    }

    /// <summary>The provider as it was: metadata came from the full read.</summary>
    private sealed class LegacyClaudeProvider(ClaudeSessionService service, IClaudeSessionStore store)
        : ClaudeCodeProvider("ai-session", service, store, new MemoryJobTracker(), (_, _) => { }), ISessionProvider
    {
        UnifiedSessionInfo? ISessionProvider.GetSessionInfo(string sessionId) => GetSession(sessionId).Info;
    }

    /// <summary>The store's history read as it was: change-tracked.</summary>
    public class TrackedHistory : DispatchProxy
    {
        private IClaudeSessionStore _target = null!;

        public static IClaudeSessionStore Over(IClaudeSessionStore target)
        {
            var proxy = Create<IClaudeSessionStore, TrackedHistory>();
            ((TrackedHistory)(object)proxy)._target = target;
            return proxy;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod!.Name == nameof(IClaudeSessionStore.GetMessages))
            {
                var sessionId = (string)args![0]!;
                var limit = (int)args[1]!;
                using var db = new ClaudeDbContext();
                return db.Messages
                    .Where(m => m.SessionId == sessionId && !m.RolledBack)
                    .OrderByDescending(m => m.Id)
                    .Take(limit)
                    .ToList()
                    .OrderBy(m => m.Id)
                    .ToList();
            }
            try { return targetMethod.Invoke(_target, args); }
            catch (TargetInvocationException ex) when (ex.InnerException is not null) { throw ex.InnerException; }
        }
    }
}
