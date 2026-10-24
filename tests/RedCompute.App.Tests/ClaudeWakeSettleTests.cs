using System.Diagnostics;
using System.Net;
using System.Text.Json;
using System.Threading.Channels;
using RedBamboo.AppHost.WebSockets;
using RedCompute.App.Services;
using RedCompute.Core.Capabilities;
using RedCompute.Core.Configuration;
using RedCompute.Core.Jobs;
using RedCompute.Core.Providers;
using RedCompute.Core.Sessions;
using RedCompute.Plugin.ClaudeCode;
using Xunit;

namespace RedCompute.App.Tests;

// A completion registration re-armed for the turn background work would wake must still end when
// that work ends without waking one. Whether the Claude CLI wakes is decided inside the CLI and is
// not on the stream: 2.1.309 wakes on completed and failed tasks, does not wake after stop_task on
// a shell, and wakes through a resumed subagent when an orphaned child shell completes
// (Fixtures/claude-background/run5-*, recorded 2026-10-24). These replays drive the real
// ClaudeSessionService, ClaudeCodeProvider, callback registry and parking coordinator with the
// recorded timing; the coordinator's settled signal (Idle, nothing running, nothing queued for the
// settle window) closes the never-woken case without racing a woken turn.
public sealed class ClaudeWakeSettleTests : IDisposable
{
    private const string CompleteUrl = "http://localhost/complete";
    private readonly string _root = Path.Combine(
        Environment.GetEnvironmentVariable("REDLEAF_SCRATCH_DIR") ?? Path.GetTempPath(),
        "redcompute-wake-settle", Guid.NewGuid().ToString("N"));
    private readonly List<Process> _processes = [];
    private static readonly DateTimeOffset Epoch = DateTimeOffset.Parse("2026-10-24T13:44:00Z");

    public ClaudeWakeSettleTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task Stopped_shell_that_never_wakes_reports_once_after_the_session_settles()
    {
        using var h = await Harness.StartAsync(this);
        await h.ReplayAsync("run5-stop-task");

        // The launching turn ended with the shell running, then stop_task ended the shell.
        var first = await h.NextAsync();
        Assert.Equal("background", first.GetProperty("workState").GetString());
        Assert.Equal(1, first.GetProperty("deliverySequence").GetInt32());
        await h.AssertNoMoreAsync();

        // Not yet settled: no report inside the window a woken turn would use.
        await h.SweepAsync(h.LastT + 5);
        await h.AssertNoMoreAsync();

        await h.SweepAsync(h.LastT + 11);
        var final = await h.NextAsync();
        Assert.Equal("Idle", final.GetProperty("status").GetString());
        Assert.Equal("op", final.GetProperty("callbackId").GetString());
        Assert.Equal(2, final.GetProperty("deliverySequence").GetInt32());
        Assert.Equal(0, final.GetProperty("backgroundTasksRunning").GetInt32());
        Assert.Equal("idle", final.GetProperty("workState").GetString());

        await h.SweepAsync(h.LastT + 60);
        await h.AssertNoMoreAsync();
    }

    [Fact]
    public async Task Orphaned_child_chain_reports_each_woken_turn_once_and_settling_adds_nothing()
    {
        using var h = await Harness.StartAsync(this);
        await h.ReplayAsync("run5-orphan-child", sweepEachLine: true);

        var posts = new List<JsonElement>();
        for (var i = 0; i < 3; i++) posts.Add(await h.NextAsync());
        await h.AssertNoMoreAsync();
        // Launching turn: subagent running. First woken turn: the orphaned shell still running.
        // Second woken turn (the resumed subagent finished): nothing running, final.
        Assert.Equal(["background", "background", "idle"], posts.Select(p => p.GetProperty("workState").GetString()));
        Assert.Equal([1, 2, 3], posts.Select(p => p.GetProperty("deliverySequence").GetInt32()));

        await h.SweepAsync(h.LastT + 60);
        await h.AssertNoMoreAsync();
    }

    [Fact]
    public async Task Resumed_subagent_keeps_the_session_unsettled_however_long_it_runs()
    {
        using var h = await Harness.StartAsync(this);
        // Stretch the resumed subagent's run (started by its orphaned shell's notification) to a
        // minute, sweeping as it runs: it must count as running, or the session would look
        // settled and report before the subagent wakes the main loop.
        await h.ReplayAsync("run5-orphan-child", sweepEachLine: true, stretchAfterLine: IsResumedAgentStart, stretchBy: 60,
            whileStretched: async t =>
            {
                Assert.Single(h.Provider.GetBackgroundTasks(h.SessionId));
                for (var s = 5; s <= 60; s += 5) await h.SweepAsync(t + s);
            });

        var posts = new List<JsonElement>();
        for (var i = 0; i < 3; i++) posts.Add(await h.NextAsync());
        await h.AssertNoMoreAsync();
        Assert.Equal(["background", "background", "idle"], posts.Select(p => p.GetProperty("workState").GetString()));
    }

    [Fact]
    public async Task Background_task_ending_while_idle_publishes_the_change_to_idle_once()
    {
        using var h = await Harness.StartAsync(this);
        var updates = new List<(string Status, string WorkState)>();
        h.Provider.PluginEvent += (type, data) =>
        {
            if (type != "session.updated" || data is not UnifiedSessionInfo session) return;
            SessionEventProjection.ApplyWorkState(session, h.Provider, (_, _) => { });
            lock (updates) updates.Add((session.Status.ToString(), session.WorkState!));
        };

        await h.ReplayAsync("run5-stop-task");

        // The launching turn ends Idle with the shell running; the stop ends it while Idle.
        var idle = updates.FindIndex(u => u is { Status: "Idle", WorkState: "background" });
        Assert.True(idle >= 0, string.Join(", ", updates));
        Assert.Equal([("Idle", "idle")], updates.Skip(idle + 1));
    }

    private static bool IsResumedAgentStart(JsonElement line)
        => line.GetProperty("type").GetString() == "system"
           && line.TryGetProperty("subtype", out var subtype) && subtype.GetString() == "task_started"
           && line.TryGetProperty("prompt", out var prompt) && prompt.GetString()!.StartsWith("<task-notification>", StringComparison.Ordinal);

    public void Dispose()
    {
        foreach (var process in _processes)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            process.Dispose();
        }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { }
    }

    private sealed class Harness : IDisposable
    {
        private readonly Channel<JsonElement> _posts = Channel.CreateUnbounded<JsonElement>();
        private ClaudeSessionService _service = null!;
        private IdleSessionParkingCoordinator _coordinator = null!;
        private double _offset;
        public ClaudeCodeProvider Provider { get; private set; } = null!;
        public string SessionId { get; private set; } = null!;
        public double LastT { get; private set; }

        public static async Task<Harness> StartAsync(ClaudeWakeSettleTests owner)
        {
            var h = new Harness();
            var claudePath = Path.Combine(owner._root, "claude.exe");
            if (!File.Exists(claudePath)) File.WriteAllText(claudePath, "");
            var jobs = new ClaudeBackgroundWorkTests.MemoryJobTracker();
            var store = new ClaudeBackgroundWorkTests.RecordingStore();
            h._service = new ClaudeSessionService(new ClaudeConfig { ClaudePath = claudePath }, jobs, store,
                new ClaudeBackgroundWorkTests.NullQualityModes(), () => null, (_, _) => { }, action => action());
            h._service.LaunchSessionProcess = _ =>
            {
                var process = Process.Start(new ProcessStartInfo
                {
                    FileName = Path.Combine(Environment.SystemDirectory, "more.com"),
                    CreateNoWindow = true, UseShellExecute = false,
                    RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                })!;
                lock (owner._processes) owner._processes.Add(process);
                return process;
            };
            h.Provider = new ClaudeCodeProvider("ai-session", h._service, store, jobs, (_, _) => { });

            var config = new RedComputeConfig();
            var attachments = new InputAttachmentStore(config, Path.Combine(owner._root, Guid.NewGuid().ToString("N")), Path.Combine(owner._root, $"{Guid.NewGuid():N}.db"));
            var capabilities = new CapabilityRegistry();
            capabilities.Register("ai-session", new CapabilityDefinition { Slug = "ai-session", DisplayName = "AI Session" },
                new CapabilityConfig(), new Dictionary<string, IBackendProvider> { ["claude-code"] = h.Provider }, "claude-code");
            var queue = new SessionInputQueueService(new SessionInputQueueStore(config, attachments), attachments, capabilities,
                jobs, new WebSocketBroadcaster(), (_, _) => { });
            // Parking disabled: only the eligibility clock and its settled signal run.
            h._coordinator = new IdleSessionParkingCoordinator(capabilities, queue, Timeout.InfiniteTimeSpan, (_, _) => { },
                utcNow: () => Epoch.AddSeconds(h.LastT));

            var http = new HttpClient(new Handler(async r =>
            {
                await h._posts.Writer.WriteAsync(JsonSerializer.Deserialize<JsonElement>(await r.Content!.ReadAsStringAsync()));
                return new HttpResponseMessage(HttpStatusCode.OK);
            }));
            var registry = new SessionCallbackRegistry(http, (_, _) => { });
            registry.AttachQuestionSource(h.Provider.GetPendingQuestions, id => h.Provider.GetSession(id).Info);
            // As RelayServer wires them.
            h.Provider.PluginEvent += registry.OnSessionEvent;
            h._coordinator.Settled += (_, id) => registry.OnSessionSettled(id);

            var info = h._service.StartSession(owner._root, "haiku", "owner", null, null, "high", null, null, null,
                JobProvenance.DirectRedCompute("POST", "/ai-session/sessions", new JobBeneficiary("user", "owner"), "request-1", null));
            h.SessionId = info!.Id;
            registry.Register(h.SessionId, CompleteUrl, "owner", "op");
            Assert.True(await h._service.SendInput(h.SessionId, [SessionInputPart.TextPart("go")], messageUid: "user-1"));
            return h;
        }

        /// <summary>Feeds a recorded CLI stream line by line on its recorded clock.</summary>
        public async Task ReplayAsync(string name, bool sweepEachLine = false,
            Func<JsonElement, bool>? stretchAfterLine = null, double stretchBy = 0, Func<double, Task>? whileStretched = null)
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "claude-background", name + ".jsonl");
            foreach (var raw in File.ReadAllLines(path).Where(l => !string.IsNullOrWhiteSpace(l)))
            {
                var entry = JsonDocument.Parse(raw).RootElement;
                if (!entry.TryGetProperty("line", out var line)) continue;
                LastT = entry.GetProperty("t").GetDouble() + _offset;
                Assert.True(_service.ProcessOutputLine(SessionId, line.GetRawText()));
                if (sweepEachLine) await _coordinator.SweepAsync(Epoch.AddSeconds(LastT));
                if (stretchAfterLine?.Invoke(line) == true)
                {
                    if (whileStretched is not null) await whileStretched(LastT);
                    _offset += stretchBy;
                }
            }
        }

        public Task SweepAsync(double t) => _coordinator.SweepAsync(Epoch.AddSeconds(t));

        public async Task<JsonElement> NextAsync() => await _posts.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));

        public async Task AssertNoMoreAsync()
        {
            await Task.Delay(300);
            Assert.False(_posts.Reader.TryRead(out var extra), $"Unexpected callback: {extra}");
        }

        public void Dispose() { }
    }

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request);
    }
}
