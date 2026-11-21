using System.Buffers.Binary;
using System.Collections.Concurrent;
using RedCompute.NodeLink;
using RedCompute.NodeLink.Spool;
using Xunit;
using Xunit.Abstractions;

namespace RedCompute.NodeLink.Tests;

/// <summary>
/// The acceptance proof of exactly-once, in-order delivery (design 8.2). Per seed, a node and a server
/// joined through <see cref="LinkFaultProxy"/> each push numbered records of random size (1 B to
/// 200 KiB, across the 64 KiB split) on a durable stream and on an ephemeral channel. After
/// quiescence every durable stream holds exactly the sender's records, once each, in order; every
/// ephemeral channel holds a gap-free prefix, and the whole of it unless the scenario legitimately
/// lost the link session. Seeds: 200 per schedule, or NODELINK_FAULT_SEEDS. A failure names its seed.
/// </summary>
public sealed class FaultInjectionTests(ITestOutputHelper output)
{
    private static int Seeds => int.TryParse(Environment.GetEnvironmentVariable("NODELINK_FAULT_SEEDS"), out var n) && n > 0 ? n : 200;
    private static int Records => int.TryParse(Environment.GetEnvironmentVariable("NODELINK_FAULT_RECORDS"), out var n) && n > 0 ? n : 16;

    public static TheoryData<string> Schedules => new(FaultSchedule.All.Select(s => s.Name));
    public static TheoryData<string> EncryptedSchedules => new(FaultSchedule.Encrypted.Select(s => s.Name));

    [Theory]
    [MemberData(nameof(Schedules))]
    public Task Every_record_arrives_exactly_once_and_in_order(string schedule)
        => RunSeedsAsync(Seeds, seed => new Harness(FaultSchedule.Named(schedule), seed, Records).RunAsync(Scenario.FaultsOnly));

    /// <summary>
    /// The same proof over an end-to-end encrypted link (Phase 4 design, 1b.8). A dropped, duplicated,
    /// reordered, corrupted or tampered record never opens: the connection closes with
    /// <c>e2e_frame_rejected</c> and the link resumes on a new one, so every invariant above still holds,
    /// and the relay never carries a plaintext frame.
    /// </summary>
    [Theory]
    [MemberData(nameof(EncryptedSchedules))]
    public async Task Every_record_arrives_exactly_once_and_in_order_over_an_encrypted_link(string schedule)
    {
        var rejected = 0;
        var injected = 0;
        await RunSeedsAsync(Seeds, async seed =>
        {
            var harness = new Harness(FaultSchedule.Named(schedule), seed, Records, encrypted: true);
            var failure = await harness.RunAsync(Scenario.FaultsOnly);
            Interlocked.Add(ref rejected, harness.FramesRejected);
            Interlocked.Add(ref injected, harness.Proxy.Tampered + harness.Proxy.Corrupted);
            return failure ?? (harness.Proxy.PlaintextMessages > 0 ? $"{harness.Proxy.PlaintextMessages} plaintext messages relayed" : null);
        });
        output.WriteLine($"{schedule}: {rejected} connections closed e2e_frame_rejected, {injected} messages tampered or corrupted");
        if (schedule is "tamper" or "corrupt" or "drop" or "duplicate" or "reorder")
            Assert.True(rejected > 0, $"{schedule}: no record was ever rejected, so the schedule did not reach the AEAD");
    }

    [Fact]
    public Task Durable_streams_survive_server_restarts_and_ephemeral_channels_reset_cleanly()
        => RunSeedsAsync(Math.Max(10, Seeds / 4), seed => new Harness(FaultSchedule.Named("mixed"), seed, Records).RunAsync(Scenario.ServerRestart));

    [Fact]
    public Task Durable_streams_survive_node_restarts_from_the_disk_spool()
        => RunSeedsAsync(Math.Max(10, Seeds / 4), seed => new Harness(FaultSchedule.Named("mixed"), seed, Records).RunAsync(Scenario.NodeRestart));

    [Fact]
    public Task A_revoke_mid_stream_stops_delivery_at_once()
        => RunSeedsAsync(Math.Max(10, Seeds / 10), seed => new Harness(FaultSchedule.Named("clean"), seed, Records * 4).RunAsync(Scenario.Revoke));

    private async Task RunSeedsAsync(int seeds, Func<int, Task<string?>> run)
    {
        var failures = new ConcurrentBag<string>();
        var started = DateTime.UtcNow;
        await Parallel.ForEachAsync(Enumerable.Range(0, seeds), new ParallelOptions { MaxDegreeOfParallelism = Math.Max(4, Environment.ProcessorCount) },
            async (seed, _) =>
            {
                string? failure;
                try
                {
                    failure = await run(seed);
                }
                catch (Exception error)
                {
                    failure = error.ToString();
                }
                if (failure is not null) failures.Add($"seed {seed}: {failure}");
            });
        output.WriteLine($"{seeds} seeds in {(DateTime.UtcNow - started).TotalSeconds:F1} s");
        Assert.True(failures.IsEmpty, string.Join("\n", failures.OrderBy(f => f, StringComparer.Ordinal).Take(10)));
    }

    internal enum Scenario { FaultsOnly, ServerRestart, NodeRestart, Revoke }

    /// <summary>One seeded run: the two ends, the proxy, four producers and four sinks.</summary>
    private sealed class Harness
    {
        private const string NodeId = "node_01JCK4Z8X9T5V6W7Y8Z9A0B1C2";
        private const string UpKey = "test/node-to-server";
        private const string DownKey = "test/server-to-node";

        private static readonly LinkOptions Options = new()
        {
            HeartbeatInterval = TimeSpan.FromMilliseconds(100),
            MissedHeartbeats = 3,
            AckDelay = TimeSpan.FromMilliseconds(5),
            RtoMin = TimeSpan.FromMilliseconds(20),
            RtoMax = TimeSpan.FromMilliseconds(300),
            HandshakeTimeout = TimeSpan.FromMilliseconds(300),
            Tick = TimeSpan.FromMilliseconds(5),
            SessionRetention = TimeSpan.FromMinutes(1),
        };

        private readonly FaultSchedule _schedule;
        private readonly int _seed;
        private readonly int _records;
        private readonly CancellationTokenSource _cts = new(TimeSpan.FromSeconds(45));
        private readonly DurableSink _upSink = new();
        private readonly DurableSink _downSink = new();
        private readonly EphemeralSink _upEphemeral = new();
        private readonly EphemeralSink _downEphemeral = new();
        private readonly DurableStreamWriter _downWriter;
        private readonly string _spoolDirectory = Path.Combine(Path.GetTempPath(), "nodelink-harness-" + Guid.NewGuid().ToString("N"));
        private readonly LinkFaultProxy _proxy;
        private readonly E2eKeys? _keys;
        private int _framesRejected;

        public LinkFaultProxy Proxy => _proxy;

        /// <summary>Connections either end closed because a record did not open.</summary>
        public int FramesRejected => Volatile.Read(ref _framesRejected);
        private readonly TaskCompletionSource _upEphemeralDone = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _downEphemeralDone = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private LinkServer _server;
        private LinkClient _client = null!;
        private DurableStreamWriter _upWriter = null!;
        private Task _clientRun = Task.CompletedTask;
        private int _nodeSessions;
        private int _serverSessions;
        private int _upEphemeralStarted;
        private int _downEphemeralStarted;

        public Harness(FaultSchedule schedule, int seed, int records, bool encrypted = false)
        {
            _keys = encrypted ? new E2eKeys() : null;
            _schedule = schedule;
            _seed = seed;
            _records = records;
            _upSink.Seed = _downSink.Seed = _upEphemeral.Seed = _downEphemeral.Seed = seed;
            _downWriter = new DurableStreamWriter(DownKey, new MemoryStreamSpool(), priority: 2, options: Options);
            _server = NewServer(1);
            _proxy = new LinkFaultProxy(schedule, seed, transport => _server.RunAsync(transport, NodeId, _cts.Token));
        }

        private LinkServer NewServer(int epoch)
        {
            var server = new LinkServer(new LinkServerOptions
            {
                ServerEpoch = "epoch-" + epoch,
                HandlerFactory = _ => new SinkHandler(_upSink, _upEphemeral),
                Link = Options,
                Secure = _keys?.ServerSecure,
            });
            server.Disconnected += (_, _, ended) =>
            {
                if (ended.Reason?.Contains(Noise.NoiseLink.FrameRejected, StringComparison.Ordinal) == true) Interlocked.Increment(ref _framesRejected);
            };
            server.Connected += (nodeId, session, resumed) =>
            {
                if (resumed) return;
                Interlocked.Increment(ref _serverSessions);
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await session.AttachDurableAsync(_downWriter, ct: _cts.Token);
                    }
                    catch
                    {
                        // the session ended before the stream attached: the next one attaches it
                    }
                });
                if (Interlocked.Exchange(ref _downEphemeralStarted, 1) == 0)
                    _ = Task.Run(() => ProduceEphemeralAsync(session, 2, _downEphemeralDone));
            };
            return server;
        }

        private LinkClient NewClient() => new(new LinkClientOptions
        {
            NodeId = NodeId,
            Agent = new AgentInfo("0.1.0", "linux", "x64"),
            Handler = new SinkHandler(_downSink, _downEphemeral),
            Dial = _proxy.DialAsync,
            Link = Options,
            Backoff = new ReconnectBackoff(new Random(_seed), TimeSpan.FromMilliseconds(5), TimeSpan.FromMilliseconds(50)),
            Secure = _keys?.ClientSecure(NodeId),
            // A fault on a handshake message can read as "old server" or "key mismatch": retry soon in the harness.
            NeedsUpdateRetry = TimeSpan.FromMilliseconds(20),
        });

        private void StartClient()
        {
            _client = NewClient();
            _client.SessionStarted += async session =>
            {
                Interlocked.Increment(ref _nodeSessions);
                if (Interlocked.Exchange(ref _upEphemeralStarted, 1) == 0)
                    _ = Task.Run(() => ProduceEphemeralAsync(session, 1, _upEphemeralDone));
                await session.AttachDurableAsync(_upWriter, ct: _cts.Token);
            };
            _clientRun = Task.Run(() => _client.RunAsync(_cts.Token));
        }

        private DurableStreamWriter OpenUpWriter()
            => new(UpKey, new SegmentFileSpool(_spoolDirectory, new SegmentFileSpoolOptions { SegmentBytes = 256 * 1024 }), priority: 1, options: Options);

        public async Task<string?> RunAsync(Scenario scenario)
        {
            try
            {
                _upWriter = OpenUpWriter();
                StartClient();
                var upProducer = _upProducer = Task.Run(() => ProduceDurableAsync(() => _upWriter, 1));
                var downProducer = _downProducer = Task.Run(() => ProduceDurableAsync(() => _downWriter, 2));

                var revokedAt = -1;
                switch (scenario)
                {
                    case Scenario.ServerRestart:
                        for (var i = 1; i <= 2; i++)
                        {
                            await Wait.UntilAsync(() => _upSink.Count >= _records * i / 3 || _upSink.Closed, TimeSpan.FromSeconds(30));
                            var old = _server;
                            _server = NewServer(i + 1);
                            await old.ShutdownAsync(LinkCloseCodes.ServiceRestart);
                        }
                        break;
                    case Scenario.NodeRestart:
                        await Wait.UntilAsync(() => _upSink.Count >= _records / 2 || _upSink.Closed, TimeSpan.FromSeconds(30));
                        _restarting = true;
                        await _client.DisposeAsync();
                        await _clientRun;
                        lock (_writerLock)
                        {
                            _upWriter.Dispose();
                            _upWriter = OpenUpWriter(); // the node restarts from its disk spool
                            _restarting = false;
                        }
                        StartClient();
                        break;
                    case Scenario.Revoke:
                        await Wait.UntilAsync(() => _upSink.Count >= _records / 3, TimeSpan.FromSeconds(30));
                        _proxy.Refuse = true;
                        var before = DateTime.UtcNow;
                        await _server.RevokeAsync(NodeId);
                        await _clientRun.WaitAsync(TimeSpan.FromSeconds(5));
                        if (DateTime.UtcNow - before > TimeSpan.FromSeconds(1)) return "the revoked link took over a second to stop";
                        if (_client.Status != LinkClientStatus.Refused) return $"client status {_client.Status} after revoke";
                        revokedAt = _upSink.Count;
                        await Task.Delay(300);
                        if (_upSink.Count != revokedAt) return $"records arrived after the revoke: {revokedAt} then {_upSink.Count}";
                        return _upSink.Check(_records, 1, complete: false) ?? _upEphemeral.Check(_records, 1, complete: false);
                }

                await Task.WhenAll(upProducer, downProducer).WaitAsync(_cts.Token);
                await Task.WhenAll(_upWriter.Finished, _downWriter.Finished, _upEphemeralDone.Task, _downEphemeralDone.Task).WaitAsync(_cts.Token);

                // Transport faults never cost the node its session. The server may lose one the node never
                // saw (its welcome was lost, so the node's next hello could not name it): legitimate.
                if (scenario == Scenario.FaultsOnly && _nodeSessions != 1) return $"{_nodeSessions} node link sessions under transport faults only";
                var losesSession = scenario != Scenario.FaultsOnly || _serverSessions > 1;
                return _upSink.Check(_records, 1, complete: true)
                    ?? _downSink.Check(_records, 2, complete: true)
                    ?? _upEphemeral.Check(_records, 1, complete: !losesSession)
                    ?? _downEphemeral.Check(_records, 2, complete: !losesSession);
            }
            catch (OperationCanceledException) when (_cts.IsCancellationRequested)
            {
                return $"timed out ({_schedule}): up {_upSink.Count}/{_records} closed={_upSink.Closed}, down {_downSink.Count}/{_records} closed={_downSink.Closed}, "
                    + $"ephemeral up {_upEphemeral.Count} down {_downEphemeral.Count}, sessions {_nodeSessions}, connections {_proxy.Connections}, "
                    + $"client {_client.Status} {_client.StatusReason}, spooled up {_upWriter.SpooledBytes} down {_downWriter.SpooledBytes}, "
                    + $"finished up {_upWriter.Finished.IsCompleted} down {_downWriter.Finished.IsCompleted}, "
                    + $"ephemeral done up {_upEphemeralDone.Task.IsCompleted} down {_downEphemeralDone.Task.IsCompleted}, "
                    + $"producers up {_upProducer?.Status} down {_downProducer?.Status}";
            }
            finally
            {
                _cts.Cancel();
                try
                {
                    await _client.DisposeAsync();
                    await _server.DisposeAsync();
                }
                catch
                {
                }
                _upWriter.Dispose();
                _downWriter.Dispose();
                _keys?.Dispose();
                try
                {
                    SegmentFileSpool.Delete(_spoolDirectory);
                }
                catch (IOException)
                {
                }
            }
        }

        private readonly object _writerLock = new();
        private Task? _upProducer;
        private Task? _downProducer;
        private volatile bool _restarting;

        /// <summary>Writes the stream's records, staying about a window ahead of the link (a producer never waits on it otherwise).</summary>
        private async Task ProduceDurableAsync(Func<DurableStreamWriter> writer, int stream)
        {
            for (var i = 0; i < _records; i++)
            {
                while (_restarting || writer().SpooledBytes > 512 * 1024) await Task.Delay(2, _cts.Token);
                var record = Record(_seed, stream, i);
                lock (_writerLock) writer().Write(record);
            }
            lock (_writerLock) writer().Complete();
            // A node restart may swap the writer after Complete: the reopened one knows its CLOSE.
            while (!writer().Finished.IsCompleted) await Task.Delay(5, _cts.Token);
        }

        private async Task ProduceEphemeralAsync(LinkSession session, int stream, TaskCompletionSource done)
        {
            try
            {
                var channel = await session.OpenChannelAsync(new ChannelOpenRequest(ChannelClasses.TestStream.Name, Priority: 3),
                    new EphemeralSink(), _cts.Token);
                for (var i = 0; i < _records; i++) await channel.SendAsync(Record(_seed, stream, i), _cts.Token);
                channel.Close();
                await channel.Completion.WaitAsync(_cts.Token);
            }
            catch (LinkChannelResetException)
            {
                // the link session was lost: the receiver checks it saw a clean prefix
            }
            finally
            {
                done.TrySetResult();
            }
        }
    }

    /// <summary>Record i of a stream: [u32 index][u32 stream][seeded bytes]; 1 B .. 200 KiB, 20% above 16 KiB.</summary>
    internal static byte[] Record(int seed, int stream, int index)
    {
        var random = new Random(HashCode.Combine(seed, stream, index));
        var size = random.NextDouble() < 0.2 ? random.Next(16 * 1024, 200 * 1024) : random.Next(8, 2048);
        var bytes = new byte[size];
        random.NextBytes(bytes);
        BinaryPrimitives.WriteInt32LittleEndian(bytes, index);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), stream);
        return bytes;
    }

    private static string? CheckRecords(IReadOnlyList<byte[]> records, int seed, int stream, int expected, bool complete, string what)
    {
        if (records.Count > expected) return $"{what}: {records.Count} records, more than the {expected} sent";
        for (var i = 0; i < records.Count; i++)
            if (!records[i].AsSpan().SequenceEqual(Record(seed, stream, i)))
                return $"{what}: record {i} is not the sender's record {i} (got index {BinaryPrimitives.ReadInt32LittleEndian(records[i])})";
        if (complete && records.Count != expected) return $"{what}: {records.Count} of {expected} records";
        return null;
    }

    /// <summary>The application side: accepts the peer's durable stream and its ephemeral channel.</summary>
    private sealed class SinkHandler(DurableSink durable, EphemeralSink ephemeral) : LinkHandler
    {
        public override ChannelAcceptance? AcceptChannel(LinkSession session, ChannelOpenInfo info)
            => info.Class != ChannelClasses.TestStream.Name ? null
                : info.StreamKey is not null ? new ChannelAcceptance(durable, durable.Committed)
                : new ChannelAcceptance(ephemeral);
    }

    /// <summary>The "commit store" for a durable stream: frames appended with their seq, atomically.</summary>
    private sealed class DurableSink : IChannelHandler
    {
        private readonly object _lock = new();
        private readonly List<byte[]> _records = [];
        private readonly MemoryStream _partial = new();
        private readonly List<string> _violations = [];
        private ulong _committed;

        public ulong Committed { get { lock (_lock) return _committed; } }
        public int Count { get { lock (_lock) return _records.Count; } }
        public bool Closed { get; private set; }

        public ValueTask OnDataAsync(LinkChannelHandle channel, ChannelData data, CancellationToken ct)
        {
            lock (_lock)
            {
                if (data.Seq != _committed + 1) _violations.Add($"seq {data.Seq} after committed {_committed}");
                _partial.Write(data.Payload.Span);
                if (data.Fin)
                {
                    _records.Add(_partial.ToArray());
                    _partial.SetLength(0);
                }
                _committed = data.Seq;
            }
            return ValueTask.CompletedTask;
        }

        public ValueTask OnRemoteCloseAsync(LinkChannelHandle channel, CancellationToken ct)
        {
            lock (_lock)
            {
                _committed++;
                Closed = true;
            }
            channel.Close();
            return ValueTask.CompletedTask;
        }

        public void OnReset(LinkChannelHandle channel, string code, string message)
        {
        }

        public string? Check(int expected, int stream, bool complete)
        {
            lock (_lock)
            {
                if (_violations.Count > 0) return $"durable stream {stream}: {_violations[0]}";
                return CheckRecords(_records, Seed, stream, expected, complete, $"durable stream {stream}");
            }
        }

        public int Seed { get; set; }
    }

    /// <summary>An ephemeral channel's receiver: messages in arrival order, across every channel it serves.</summary>
    private sealed class EphemeralSink : IChannelHandler
    {
        private readonly object _lock = new();
        private readonly List<byte[]> _records = [];

        public int Count { get { lock (_lock) return _records.Count; } }

        public ValueTask OnDataAsync(LinkChannelHandle channel, ChannelData data, CancellationToken ct)
        {
            lock (_lock) _records.Add(data.Payload.ToArray());
            return ValueTask.CompletedTask;
        }

        public ValueTask OnRemoteCloseAsync(LinkChannelHandle channel, CancellationToken ct)
        {
            channel.Close();
            return ValueTask.CompletedTask;
        }

        public void OnReset(LinkChannelHandle channel, string code, string message)
        {
        }

        public string? Check(int expected, int stream, bool complete)
        {
            lock (_lock) return CheckRecords(_records, Seed, stream, expected, complete, $"ephemeral stream {stream}");
        }

        public int Seed { get; set; }
    }
}
