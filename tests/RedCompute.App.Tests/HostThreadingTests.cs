using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using RedBamboo.AppHost.Auth;
using RedBamboo.AppHost.Streams;
using RedBamboo.AppHost.WebSockets;
using RedCompute.App.Data;
using RedCompute.App.Services;
using RedCompute.App.Services.Jobs;
using RedCompute.Core.Configuration;
using RedCompute.Core.Jobs;
using RedCompute.Core.Sessions;
using RedCompute.Plugin.ClaudeCode;
using RedCompute.PluginSdk;
using Xunit;
using static RedCompute.App.Tests.ClaudeBackgroundWorkTests;

namespace RedCompute.App.Tests;

// The Windows shell's dispatcher used to be the SynchronizationContext every service loop and
// provider reader captured: App.OnStartup awaited the host start on it, so each fire-and-forget
// loop and each stdout reader of a session they launched resumed on that one thread (journal
// 2026-11-12, 9711f8ea). SingleThreadContext stands in for DispatcherSynchronizationContext: one
// thread, and every continuation that captures it is posted to that thread and counted.
public sealed class HostThreadingTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Environment.GetEnvironmentVariable("REDLEAF_SCRATCH_DIR") ?? Path.GetTempPath(),
        "redcompute-host-threading", Guid.NewGuid().ToString("N"));
    private readonly List<Process> _processes = [];

    public HostThreadingTests() => Directory.CreateDirectory(_root);

    // ===== DetachedWork: the contract every loop entry point relies on =====

    [Fact]
    public async Task Detached_work_runs_its_prefix_in_order_and_never_resumes_on_the_caller()
    {
        using var pump = new SingleThreadContext();
        var probe = new ThreadProbe(pump);
        var order = new List<string>();
        SynchronizationContext? prefixContext = pump;
        var prefixThread = 0;
        SynchronizationContext? restored = null;

        var detached = await pump.Run(() =>
        {
            var task = DetachedWork.Start(async () =>
            {
                order.Add("prefix");
                prefixThread = Environment.CurrentManagedThreadId;
                prefixContext = SynchronizationContext.Current;
                await Task.Delay(20);
                probe.Record();
                await Task.Yield();
                probe.Record();
            });
            order.Add("returned");
            restored = SynchronizationContext.Current;
            return task;
        });
        await detached.WaitAsync(TimeSpan.FromSeconds(10));

        // Boot ordering is kept: the synchronous prefix finished before Start returned, on the caller.
        Assert.Equal(["prefix", "returned"], order);
        Assert.Equal(pump.ThreadId, prefixThread);
        Assert.Null(prefixContext);
        Assert.Same(pump, restored);
        probe.AssertNeverOnPumpThread(expectedAtLeast: 2);
        Assert.Equal(0, pump.Posts);
    }

    [Fact]
    public async Task Control_a_loop_called_directly_resumes_on_the_single_threaded_context()
    {
        using var pump = new SingleThreadContext();
        var resumedOn = 0;
        var direct = await pump.Run(async () =>
        {
            await Task.Delay(20);
            resumedOn = Environment.CurrentManagedThreadId;
        });
        await direct.WaitAsync(TimeSpan.FromSeconds(10));

        // What every loop did before: the continuation came back to the one thread.
        Assert.Equal(pump.ThreadId, resumedOn);
        Assert.True(pump.Posts > 0);
    }

    [Fact]
    public async Task Detached_work_returns_a_synchronous_failure_and_restores_the_caller_context()
    {
        using var pump = new SingleThreadContext();
        var failure = new InvalidOperationException("synthetic prefix failure");
        SynchronizationContext? restored = null;

        var detached = await pump.Run(() =>
        {
            var task = DetachedWork.Start(() => throw failure);
            restored = SynchronizationContext.Current;
            return task;
        });

        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => detached));
        Assert.Same(pump, restored);
    }

    [Fact]
    public async Task Detached_work_leaves_a_non_default_scheduler_for_the_pool()
    {
        var exclusive = new ConcurrentExclusiveSchedulerPair().ExclusiveScheduler;
        TaskScheduler? resumedOn = null;
        var detached = await Task.Factory.StartNew(() => DetachedWork.Start(async () =>
        {
            await Task.Delay(10);
            resumedOn = TaskScheduler.Current;
        }), CancellationToken.None, TaskCreationOptions.None, exclusive);
        await detached.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Same(TaskScheduler.Default, resumedOn);
    }

    // ===== The loops themselves, started from the single-threaded context =====

    [Fact]
    public async Task Input_queue_loop_started_from_a_single_threaded_context_never_runs_on_it()
    {
        using var pump = new SingleThreadContext();
        var probe = new ThreadProbe(pump);
        var sweeps = Channel();
        var (service, _) = Queue(ct =>
        {
            probe.Record();
            sweeps.Writer.TryWrite(true);
            return Task.CompletedTask;
        });

        using var cts = new CancellationTokenSource();
        var running = await pump.Run(() => service.RunAsync(cts.Token));
        for (var i = 0; i < 3; i++)
        {
            service.Signal($"nobody-{i}");
            await sweeps.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(15));
        }
        await StopAsync(cts, running);

        probe.AssertNeverOnPumpThread(expectedAtLeast: 3);
        Assert.Equal(0, pump.Posts);
    }

    [Fact]
    public async Task Idle_parking_loop_started_from_a_single_threaded_context_never_resumes_on_it()
    {
        using var pump = new SingleThreadContext();
        var probe = new ThreadProbe(pump);
        var ticks = Channel();
        var (queue, registry) = Queue(null);
        var parking = new IdleSessionParkingCoordinator(registry, queue, TimeSpan.FromMinutes(5), (_, _) => { },
            sweepInterval: TimeSpan.FromMilliseconds(20), utcNow: () =>
            {
                // The first sweep is the synchronous prefix: on the caller's thread, its context cleared.
                probe.Record();
                ticks.Writer.TryWrite(true);
                return DateTimeOffset.UtcNow;
            });

        using var cts = new CancellationTokenSource();
        var running = await pump.Run(() => parking.RunAsync(cts.Token));
        for (var i = 0; i < 4; i++)
            await ticks.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(15));
        await StopAsync(cts, running);

        probe.AssertNeverOnPump(expectedAtLeast: 4);
        Assert.Equal(0, pump.Posts);
    }

    [Fact]
    public async Task Job_audit_outbox_loop_started_from_a_single_threaded_context_never_resumes_on_it()
    {
        using var pump = new SingleThreadContext();
        var probe = new ThreadProbe(pump);
        var batches = Channel();
        var database = Path.Combine(_root, "jobs.db");
        using (var db = new RedComputeDbContext(database))
        {
            db.Database.EnsureCreated();
            db.MigrateSchema();
        }
        await using var client = new RedLeafStreamClient("http://127.0.0.1:9", "RedComputeTests",
            new JwtService(new JwtOptions { SigningKey = new string('a', 64) }));
        var outbox = new JobAuditOutboxService(client, (_, _) => { }, () =>
        {
            probe.Record();
            batches.Writer.TryWrite(true);
            return new RedComputeDbContext(database);
        });

        using var cts = new CancellationTokenSource();
        var running = await pump.Run(() => outbox.RunAsync(cts.Token));
        // An empty outbox waits one second between batches: three reads cross at least one wait.
        for (var i = 0; i < 3; i++)
            await batches.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(15));
        await StopAsync(cts, running);
        SqliteConnection.ClearAllPools();

        probe.AssertNeverOnPump(expectedAtLeast: 3);
        Assert.Equal(0, pump.Posts);
    }

    [WindowsFact("the fake Claude CLI is a PowerShell script")]
    public async Task Claude_readers_started_from_a_single_threaded_context_stream_off_it_in_order()
    {
        const int sessions = 3;
        const int lines = 40;
        using var pump = new SingleThreadContext();
        var claudePath = Path.Combine(_root, "claude.exe");
        File.WriteAllText(claudePath, "");
        var service = new ClaudeSessionService(new ClaudeConfig { ClaudePath = claudePath }, new MemoryJobTracker(),
            new RecordingStore(), new NullQualityModes(), () => null, (_, _) => { }, action => action());
        var launched = 0;
        service.LaunchSessionProcess = _ =>
        {
            var start = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            start.Environment["RC_FAKE_SESSION"] = Interlocked.Increment(ref launched).ToString();
            start.ArgumentList.Add("-NoProfile");
            start.ArgumentList.Add("-NonInteractive");
            start.ArgumentList.Add("-Command");
            // Non-JSON lines reach the reader's raw-text path, one stream event each, in order.
            start.ArgumentList.Add($"for ($i = 1; $i -le {lines}; $i++) {{ [Console]::Out.WriteLine(\"fake-$($env:RC_FAKE_SESSION)-$i\"); [Console]::Out.Flush(); if ($i % 5 -eq 0) {{ Start-Sleep -Milliseconds 10 }} }}; while ($null -ne [Console]::In.ReadLine()) {{ }}");
            var process = Process.Start(start)!;
            lock (_processes) _processes.Add(process);
            return process;
        };

        var probe = new ThreadProbe(pump);
        var seen = new ConcurrentDictionary<string, List<string>>(StringComparer.Ordinal);
        var all = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var total = 0;
        service.StreamEvent += (sessionId, evt) =>
        {
            if (evt.Content is not { } content || !content.StartsWith("fake-", StringComparison.Ordinal)) return;
            probe.Record();
            var list = seen.GetOrAdd(sessionId, _ => []);
            lock (list) list.Add(content);
            if (Interlocked.Increment(ref total) == sessions * lines) all.TrySetResult();
        };

        // Launched the way the input queue or boot recovery launched them: from the one thread.
        var starts = await pump.Run(() => Enumerable.Range(0, sessions)
            .Select(_ => service.StartSessionAsync(_root, "haiku", "owner", null, null, "high", null, null, null,
                JobProvenance.DirectRedCompute("POST", "/ai-session/sessions", new JobBeneficiary("user", "owner"), "req", "corr")))
            .ToArray());
        var infos = await Task.WhenAll(starts).WaitAsync(TimeSpan.FromSeconds(60));
        await all.Task.WaitAsync(TimeSpan.FromSeconds(90));

        probe.AssertNeverOnPump(expectedAtLeast: sessions * lines);
        Assert.Equal(sessions, seen.Count);
        var fakes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var info in infos)
        {
            var list = seen[info!.Id];
            var fake = list[0].Split('-')[1];
            Assert.True(fakes.Add(fake));
            Assert.Equal(Enumerable.Range(1, lines).Select(i => $"fake-{fake}-{i}"), list);
        }
    }

    // ===== The desktop shell's start and stop =====

    [Fact]
    public async Task Host_start_runs_off_the_caller_and_resumes_the_caller_for_failures()
    {
        using var pump = new SingleThreadContext();
        var probe = new ThreadProbe(pump);
        var resumedOnPump = false;
        var failure = new InvalidOperationException("synthetic start failure");

        var start = await pump.Run(async () =>
        {
            await HostThreads.StartOffCaller(async () =>
            {
                probe.Record();
                await Task.Delay(20);
                probe.Record();
            });
            resumedOnPump = Environment.CurrentManagedThreadId == pump.ThreadId;
            await HostThreads.StartOffCaller(() => throw failure);
        });

        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => start.WaitAsync(TimeSpan.FromSeconds(10))));
        probe.AssertNeverOnPumpThread(expectedAtLeast: 2);
        // OnStartup's own await comes back to the dispatcher, so a failed start still surfaces there.
        Assert.True(resumedOnPump);
    }

    [Fact]
    public async Task Host_stop_from_a_blocked_caller_finishes_and_a_hung_one_is_bounded()
    {
        using var pump = new SingleThreadContext();
        var probe = new ThreadProbe(pump);
        var reports = new ConcurrentQueue<string>();

        // The caller blocks, as OnExit does; a stop that needed the caller's thread would deadlock.
        var finished = await pump.Run(() => HostThreads.WaitOffCaller(async () =>
        {
            await Task.Delay(20);
            probe.Record();
            await Task.Yield();
            probe.Record();
        }, TimeSpan.FromSeconds(10), reports.Enqueue));
        Assert.True(finished);
        Assert.Empty(reports);
        probe.AssertNeverOnPumpThread(expectedAtLeast: 2);

        var clock = Stopwatch.StartNew();
        var hung = await pump.Run(() => HostThreads.WaitOffCaller(() => new TaskCompletionSource().Task,
            TimeSpan.FromMilliseconds(200), reports.Enqueue));
        Assert.False(hung);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"bounded stop took {clock.Elapsed}");
        Assert.Contains("did not finish", Assert.Single(reports));

        var failed = await pump.Run(() => HostThreads.WaitOffCaller(async () =>
        {
            await Task.Delay(5);
            throw new InvalidOperationException("synthetic stop failure");
        }, TimeSpan.FromSeconds(10), reports.Enqueue));
        Assert.True(failed);
        Assert.Contains("synthetic stop failure", reports.Last());
        Assert.Equal(0, pump.Posts);
    }

    // ===== Twenty sessions streaming at once =====

    // Each provider reads one session's output on one sequential loop; the loops of different
    // sessions now run concurrently on the pool instead of taking turns on the dispatcher. Per
    // session, events must still be stamped, persisted and published in the order the provider
    // emitted them, a payload must exist in RedLeaf before its reference is published, and no
    // loop may starve the pool.
    [Fact]
    public async Task Twenty_sessions_streaming_at_once_keep_order_stamps_and_persist_before_publish()
    {
        const int sessionCount = 20;
        const int eventsPerSession = 40;
        // What the host raises at boot (b8baaa7); the pipeline's synchronous payload write blocks one
        // pool worker per streaming session while it waits for RedLeaf.
        ThreadPoolFloor.Ensure();

        await using var redLeaf = await PayloadRedLeaf.StartAsync();
        var socket = new StreamSocket();
        var broadcaster = new WebSocketBroadcaster();
        typeof(WebSocketBroadcaster).GetMethod("AddClient", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(broadcaster, ["test", socket]);
        var pipeline = new SessionTranscriptPipeline(redLeaf.Client, broadcaster, (_, _) => { }, (_, _) => false,
            new TranscriptSequenceStore(Path.Combine(_root, "cursor.db")));

        using var pump = new SingleThreadContext();
        var probe = new ThreadProbe(pump);
        var active = 0;
        var maxActive = 0;
        var maxLatency = TimeSpan.Zero;
        using var stopProbe = new CancellationTokenSource();
        var latency = Task.Run(async () =>
        {
            while (!stopProbe.IsCancellationRequested)
            {
                var clock = Stopwatch.StartNew();
                await Task.Run(() => { });
                if (clock.Elapsed > maxLatency) maxLatency = clock.Elapsed;
                try { await Task.Delay(25, stopProbe.Token); } catch (OperationCanceledException) { }
            }
        });

        var total = Stopwatch.StartNew();
        var readers = await pump.Run(() => Enumerable.Range(1, sessionCount).Select(n => DetachedWork.Start(async () =>
        {
            for (var i = 1; i <= eventsPerSession; i++)
            {
                // A reader awaits its next line, then handles it synchronously.
                if (i % 5 == 0) await Task.Delay(1);
                else await Task.Yield();
                probe.Record();
                var now = Interlocked.Increment(ref active);
                InterlockedMax(ref maxActive, now);
                try { pipeline.HandleLiveEvent("test", $"load-{n}", Event(n, i)); }
                finally { Interlocked.Decrement(ref active); }
            }
        })).ToArray());
        await Task.WhenAll(readers).WaitAsync(TimeSpan.FromSeconds(120));
        total.Stop();
        await socket.WaitForAsync(sessionCount * eventsPerSession, TimeSpan.FromSeconds(30));
        await stopProbe.CancelAsync();
        await latency;

        probe.AssertNeverOnPumpThread(expectedAtLeast: sessionCount * eventsPerSession);
        Assert.Equal(0, pump.Posts);
        Assert.Equal(0, socket.Unreadable);
        Assert.True(maxActive > 1, "the session loops never overlapped: they are still serialised");
        Assert.True(maxLatency < TimeSpan.FromSeconds(2), $"the pool stalled for {maxLatency}");
        Assert.True(total.Elapsed < TimeSpan.FromSeconds(60), $"twenty sessions took {total.Elapsed}");

        for (var n = 1; n <= sessionCount; n++)
        {
            var session = $"load-{n}";
            var frames = socket.Frames(session);
            Assert.Equal(eventsPerSession, frames.Count);
            // Emission order, one stamp each: contiguous, no duplicate, no gap, one epoch.
            Assert.Equal(Enumerable.Range(1, eventsPerSession).Select(i => (long)i), frames.Select(f => f.Sequence));
            Assert.Equal(Enumerable.Range(1, eventsPerSession), frames.Select(f => f.Index));
            Assert.Single(frames.Select(f => f.Epoch).Distinct());
            foreach (var frame in frames.Where(f => f.Type == "tool_result"))
            {
                // The reference is the id RedLeaf returned once it held the bytes for this stamp.
                Assert.NotNull(frame.RecordId);
                Assert.Equal((session, frame.Sequence), redLeaf.Issued[frame.RecordId!.Value]);
            }
            // RedLeaf received this session's payloads in stamp order.
            var persisted = redLeaf.Persisted(session);
            Assert.Equal(eventsPerSession / 2, persisted.Count);
            Assert.Equal(persisted.OrderBy(s => s), persisted);
        }
    }

    private static UnifiedStreamEvent Event(int session, int index) => index % 2 == 0
        ? new UnifiedStreamEvent
        {
            Type = "tool_result", MessageId = $"toolu_{session}_{index}", MessageUid = $"turn-{session}",
            ToolName = "Bash", ToolResult = $"out {session}:{index}",
        }
        : new UnifiedStreamEvent { Type = "text", Content = $"{session}:{index}", MessageUid = $"turn-{session}" };

    private static void InterlockedMax(ref int target, int value)
    {
        var current = Volatile.Read(ref target);
        while (value > current)
        {
            var seen = Interlocked.CompareExchange(ref target, value, current);
            if (seen == current) return;
            current = seen;
        }
    }

    private (SessionInputQueueService Service, CapabilityRegistry Registry) Queue(Func<CancellationToken, Task>? reconcile)
    {
        var config = new RedComputeConfig();
        var attachments = new InputAttachmentStore(config, Path.Combine(_root, "bytes"), Path.Combine(_root, "input.db"));
        var store = new SessionInputQueueStore(config, attachments);
        var registry = new CapabilityRegistry();
        return (new SessionInputQueueService(store, attachments, registry, null!, new WebSocketBroadcaster(),
            (_, _) => { }, reconcileCallbacks: reconcile), registry);
    }

    private static System.Threading.Channels.Channel<bool> Channel() =>
        System.Threading.Channels.Channel.CreateUnbounded<bool>();

    private static async Task StopAsync(CancellationTokenSource cts, Task running)
    {
        await cts.CancelAsync();
        try { await running.WaitAsync(TimeSpan.FromSeconds(15)); }
        catch (OperationCanceledException) { }
    }

    public void Dispose()
    {
        lock (_processes)
        {
            // Killed but not disposed: the provider's Exited handler still reads the exit code.
            foreach (var process in _processes)
            {
                try { if (!process.HasExited) process.Kill(true); } catch { }
                try { process.WaitForExit(5000); } catch { }
            }
        }
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, true); } catch { }
    }

    /// <summary>
    /// One thread and a queue, like a dispatcher: every continuation that captures this context is
    /// posted to the thread and counted. <see cref="Run{T}"/> starts work on the thread the way the
    /// dispatcher runs a handler, without counting as a post.
    /// </summary>
    internal sealed class SingleThreadContext : SynchronizationContext, IDisposable
    {
        private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = new();
        private readonly Thread _thread;
        private int _posts;

        public SingleThreadContext()
        {
            _thread = new Thread(() =>
            {
                SetSynchronizationContext(this);
                foreach (var (callback, state) in _queue.GetConsumingEnumerable()) callback(state);
            }) { IsBackground = true, Name = "single-threaded context" };
            _thread.Start();
        }

        public int ThreadId => _thread.ManagedThreadId;
        public int Posts => Volatile.Read(ref _posts);

        public override void Post(SendOrPostCallback d, object? state)
        {
            Interlocked.Increment(ref _posts);
            try { _queue.Add((d, state)); }
            catch (InvalidOperationException) { /* disposed: drop, as a closed dispatcher does */ }
        }

        public override void Send(SendOrPostCallback d, object? state) => throw new NotSupportedException();
        public override SynchronizationContext CreateCopy() => this;

        public Task<T> Run<T>(Func<T> work)
        {
            var result = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            _queue.Add((_ =>
            {
                try { result.SetResult(work()); }
                catch (Exception ex) { result.SetException(ex); }
            }, null));
            return result.Task;
        }

        public void Dispose()
        {
            _queue.CompleteAdding();
            _thread.Join(TimeSpan.FromSeconds(5));
        }
    }

    /// <summary>
    /// Where code ran. Anything the pump resumes runs under its context, so that is the failure. A
    /// loop's synchronous prefix may run on the pump's thread, but only with the context cleared.
    /// </summary>
    private sealed class ThreadProbe(SingleThreadContext pump)
    {
        private int _records;
        private int _underContext;
        private int _prefixOnThread;
        private int _offThread;

        public void Record()
        {
            Interlocked.Increment(ref _records);
            if (SynchronizationContext.Current == pump) Interlocked.Increment(ref _underContext);
            else if (Environment.CurrentManagedThreadId == pump.ThreadId) Interlocked.Increment(ref _prefixOnThread);
            else Interlocked.Increment(ref _offThread);
        }

        /// <summary>Never under the pump's context, and the work after the first wait ran elsewhere.</summary>
        public void AssertNeverOnPump(int expectedAtLeast)
        {
            Assert.True(Volatile.Read(ref _records) >= expectedAtLeast,
                $"only {_records} observations, expected at least {expectedAtLeast}");
            Assert.Equal(0, Volatile.Read(ref _underContext));
            Assert.True(Volatile.Read(ref _offThread) > 0, "nothing ran off the pump's thread");
        }

        /// <summary>Every observation followed a real wait: none may be on the pump's thread at all.</summary>
        public void AssertNeverOnPumpThread(int expectedAtLeast)
        {
            AssertNeverOnPump(expectedAtLeast);
            Assert.Equal(0, Volatile.Read(ref _prefixOnThread));
        }
    }

    private sealed record StreamFrame(string Type, long Sequence, string Epoch, int Index, long? RecordId);

    /// <summary>Records each session.stream frame as it is sent, per session, in send order.</summary>
    private sealed class StreamSocket : WebSocket
    {
        private readonly ConcurrentDictionary<string, List<StreamFrame>> _frames = new(StringComparer.Ordinal);
        private int _count;

        public List<StreamFrame> Frames(string sessionId)
        {
            var list = _frames.GetValueOrDefault(sessionId) ?? [];
            lock (list) return list.ToList();
        }

        public async Task WaitForAsync(int count, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (Volatile.Read(ref _count) < count && DateTime.UtcNow < deadline) await Task.Delay(20);
        }

        public int Unreadable => Volatile.Read(ref _unreadable);
        private int _unreadable;

        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage,
            CancellationToken cancellationToken)
        {
            try { Read(buffer); }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or FormatException or InvalidOperationException)
            {
                Interlocked.Increment(ref _unreadable);
            }
            return Task.CompletedTask;
        }

        private void Read(ArraySegment<byte> buffer)
        {
            using var json = JsonDocument.Parse(Encoding.UTF8.GetString(buffer));
            if (json.RootElement.GetProperty("type").GetString() != SessionTranscriptPipeline.StreamEventType)
                return;
            var data = json.RootElement.GetProperty("data");
            var evt = data.GetProperty("event");
            var type = evt.GetProperty("type").GetString()!;
            // A published payload carries its reference, not the output: index tool results by id.
            var index = type == "tool_result"
                ? int.Parse(evt.GetProperty("messageId").GetString()!.Split('_')[^1])
                : int.Parse(evt.GetProperty("content").GetString()!.Split(':')[^1]);
            var frame = new StreamFrame(type, evt.GetProperty("sequence").GetInt64(), evt.GetProperty("epoch").GetString()!,
                index,
                evt.TryGetProperty("payloadRef", out var payload) ? payload.GetProperty("recordId").GetInt64() : null);
            var list = _frames.GetOrAdd(data.GetProperty("sessionId").GetString()!, _ => []);
            lock (list) list.Add(frame);
            Interlocked.Increment(ref _count);
        }

        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => WebSocketState.Open;
        public override string? SubProtocol => null;
        public override void Abort() { }
        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Task.CompletedTask;
        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Task.CompletedTask;
        public override void Dispose() { }
        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    /// <summary>A RedLeaf that stores payload records with a little latency and issues their ids.</summary>
    private sealed class PayloadRedLeaf : IAsyncDisposable
    {
        private readonly WebApplication _server;
        private readonly ConcurrentDictionary<string, ConcurrentQueue<long>> _persisted = new(StringComparer.Ordinal);
        private long _nextId;

        private PayloadRedLeaf(WebApplication server) => _server = server;

        public RedLeafStreamClient Client { get; private set; } = null!;
        public ConcurrentDictionary<long, (string Session, long Sequence)> Issued { get; } = new();
        public List<long> Persisted(string sessionId) => _persisted.GetValueOrDefault(sessionId)?.ToList() ?? [];

        public static async Task<PayloadRedLeaf> StartAsync()
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            var server = builder.Build();
            var f = new PayloadRedLeaf(server);
            server.MapGet("/api/entities/{slug}", () => Results.Ok(new { id = Guid.Parse("11111111-1111-1111-1111-111111111111") }));
            server.MapPost("/api/streams/{stream}/records", () => Results.Ok(new { created = 1 }));
            server.MapPost("/api/streams/{stream}/records/payload", async (HttpContext ctx) =>
            {
                var form = await ctx.Request.ReadFormAsync();
                using var data = JsonDocument.Parse(form["data"].ToString());
                var session = data.RootElement.GetProperty("session_id").GetString()!;
                var sequence = data.RootElement.GetProperty("sequence").GetInt64();
                await Task.Delay(Random.Shared.Next(0, 4));
                f._persisted.GetOrAdd(session, _ => new ConcurrentQueue<long>()).Enqueue(sequence);
                var id = Interlocked.Increment(ref f._nextId);
                f.Issued[id] = (session, sequence);
                return Results.Ok(new
                {
                    id,
                    payload = new { available = true, length = 1L, contentType = "text/plain; charset=utf-8", encoding = "utf-8", sha256 = "" },
                });
            });
            server.Urls.Add("http://127.0.0.1:0");
            await server.StartAsync();
            f.Client = new RedLeafStreamClient(server.Urls.Single(), "RedComputeTests",
                new JwtService(new JwtOptions { SigningKey = new string('a', 64) }));
            return f;
        }

        public async ValueTask DisposeAsync()
        {
            await Client.DisposeAsync();
            await _server.DisposeAsync();
        }
    }
}
