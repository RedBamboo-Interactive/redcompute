using System.Collections.Concurrent;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text.Json;
using RedCompute.NodeLink;
using RedCompute.NodeLink.Forward;
using Xunit;
using Xunit.Abstractions;

namespace RedCompute.NodeLink.Tests;

/// <summary>
/// The <c>Forward</c> scenario family (Phase 4 design 6.2) on the same <see cref="LinkFaultProxy"/> and schedules as
/// <see cref="FaultInjectionTests"/>. Per seed, concurrent GETs, POSTs (fixed length and chunked) and one WebSocket go
/// from the server's <see cref="ForwardClient"/> to the node's <see cref="ForwardHost"/> and a fake runtime.
/// Invariants:
/// <list type="number">
/// <item>an exchange that succeeds has exactly the source's bytes;</item>
/// <item>one that fails fails typed (<c>link_reset</c>, <c>forward_truncated</c>, ...) before its deadline, and every
/// task ends by deadline + 1 s;</item>
/// <item>no partial body is a success;</item>
/// <item>a WS stream is a gap-free in-order prefix, the whole of it unless the link session was lost;</item>
/// <item>both exchange tables are empty afterwards.</item>
/// </list>
/// Transport faults alone never lose the node's session, so then everything succeeds.
/// </summary>
public sealed class ForwardFaultTests(ITestOutputHelper output)
{
    private static int Seeds => int.TryParse(Environment.GetEnvironmentVariable("NODELINK_FAULT_SEEDS"), out var n) && n > 0 ? n : 200;

    public static TheoryData<string> Schedules => new(FaultSchedule.All.Select(s => s.Name));

    private int _typedFailures;
    private int _connections;

    [Theory]
    [MemberData(nameof(Schedules))]
    public async Task Forward_exchanges_are_exact_or_fail_typed(string schedule)
    {
        await RunSeedsAsync(Seeds, seed => Run(new Harness(FaultSchedule.Named(schedule), seed), restartServer: false));
        output.WriteLine($"{schedule}: {_connections} connections, {_typedFailures} typed failures");
        if (schedule is "cut" or "half-open" or "corrupt")
            Assert.True(_connections > Seeds, $"{schedule}: no connection was ever lost, so the schedule did not reach the forwards");
    }

    /// <summary>
    /// Regression for the flake "WS failed though the session was kept: upstream_failed (Only one usage of each socket
    /// address)": the fake runtime's WebSocket pair was a loopback TCP connection per seed, so a long or loaded run (the
    /// Nightly's whole-solution test pass) ran the machine out of ephemeral ports and the node reported the runtime's
    /// failed connect, correctly, as upstream_failed. The pair must not touch the network at all.
    /// </summary>
    [Fact]
    public async Task The_fake_runtimes_websocket_pair_uses_no_tcp_connection()
    {
        const int pairs = 300;
        var before = System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpConnections().Length;
        for (var i = 0; i < pairs; i++)
        {
            var (client, server) = await SocketPair.CreateAsync(CancellationToken.None);
            await client.SendAsync(new byte[] { 1, 2, 3 }, WebSocketMessageType.Binary, true, CancellationToken.None);
            var buffer = new byte[16];
            var result = await server.ReceiveAsync(buffer, CancellationToken.None);
            Assert.Equal(3, result.Count);
            await client.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
            Assert.Equal(WebSocketMessageType.Close, (await server.ReceiveAsync(buffer, CancellationToken.None)).MessageType);
            client.Dispose();
            server.Dispose();
        }
        var after = System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpConnections().Length;
        // A TCP pair leaves one or two entries each (TIME_WAIT): 300 pairs would add at least 300. Other processes add noise, not hundreds.
        Assert.True(after - before < pairs / 3, $"{after - before} new TCP connections for {pairs} WebSocket pairs");
    }

    [Fact]
    public async Task Forward_exchanges_fail_link_reset_promptly_when_the_server_restarts()
    {
        await RunSeedsAsync(Math.Max(10, Seeds / 4), seed => Run(new Harness(FaultSchedule.Named("mixed"), seed), restartServer: true));
        output.WriteLine($"restart: {_typedFailures} typed failures");
        Assert.True(_typedFailures > 0, "no exchange was in flight at a restart");
    }

    private async Task<string?> Run(Harness harness, bool restartServer)
    {
        var failure = await harness.RunAsync(restartServer);
        Interlocked.Add(ref _typedFailures, harness.TypedFailures);
        Interlocked.Add(ref _connections, harness.Connections);
        return failure;
    }

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

    private sealed class Harness
    {
        private const string NodeId = "node_01JCK4Z8X9T5V6W7Y8Z9A0B1C2";
        private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(20);

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
        private readonly CancellationTokenSource _cts = new(TimeSpan.FromSeconds(45));
        private readonly FakeRuntime _runtime = new();
        private readonly ForwardHost _host;
        private readonly LinkFaultProxy _proxy;
        private readonly ForwardClient _forward;
        private LinkServer _server;
        private LinkClient _client = null!;
        private int _serverSessions;
        private int _nodeSessions;

        public int TypedFailures { get; private set; }
        public int Connections => _proxy.Connections;

        public Harness(FaultSchedule schedule, int seed)
        {
            _schedule = schedule;
            _seed = seed;
            _host = new ForwardHost(_runtime);
            _server = NewServer(1);
            _proxy = new LinkFaultProxy(schedule, seed, transport => _server.RunAsync(transport, NodeId, _cts.Token));
            _forward = new ForwardClient(() => _server.FindSession(NodeId));
        }

        private LinkServer NewServer(int epoch)
        {
            var server = new LinkServer(new LinkServerOptions
            {
                ServerEpoch = "epoch-" + epoch,
                HandlerFactory = _ => new LinkHandler(),
                Features = ForwardFeatures.Server,
                Link = Options,
            });
            server.Connected += (_, _, resumed) =>
            {
                if (!resumed) Interlocked.Increment(ref _serverSessions);
            };
            return server;
        }

        public async Task<string?> RunAsync(bool restartServer)
        {
            try
            {
                _client = new LinkClient(new LinkClientOptions
                {
                    NodeId = NodeId,
                    Agent = new AgentInfo("0.1.0", "linux", "x64"),
                    Handler = new ForwardNodeHandler(_host),
                    Features = ForwardFeatures.Node,
                    Dial = _proxy.DialAsync,
                    Link = Options,
                    Backoff = new ReconnectBackoff(new Random(_seed), TimeSpan.FromMilliseconds(5), TimeSpan.FromMilliseconds(50)),
                    NeedsUpdateRetry = TimeSpan.FromMilliseconds(20),
                });
                _client.SessionStarted += _ =>
                {
                    Interlocked.Increment(ref _nodeSessions);
                    return Task.CompletedTask;
                };
                _ = Task.Run(() => _client.RunAsync(_cts.Token));
                await Wait.UntilAsync(() => _server.FindSession(NodeId) is { IsAttached: true } && _client.Status == LinkClientStatus.Online,
                    TimeSpan.FromSeconds(20), "the link to come up");
                var sessionsAtStart = Volatile.Read(ref _serverSessions);

                var random = new Random(_seed);
                var http = new HttpClient(_forward.CreateHandler(FakeRuntime.Runtime)) { BaseAddress = new Uri("http://rt-1.node.leaf-node.invalid/") };
                var exchanges = new List<(string What, Task<string?> Run)>();
                for (var i = 0; i < 4; i++)
                {
                    var size = random.NextDouble() < 0.25 ? random.Next(64 * 1024, 512 * 1024) : random.Next(1, 64 * 1024);
                    var blobSeed = _seed * 100 + i;
                    exchanges.Add(($"GET {size}", Timed(ct => GetAsync(http, blobSeed, size, ct))));
                }
                for (var i = 0; i < 2; i++)
                {
                    var size = random.Next(0, 256 * 1024);
                    var chunked = i == 1;
                    var bodySeed = _seed * 100 + 50 + i;
                    exchanges.Add(($"POST {size}{(chunked ? " chunked" : "")}", Timed(ct => PostAsync(http, bodySeed, size, chunked, ct))));
                }
                exchanges.Add(("WS", Timed(WebSocketAsync)));

                if (restartServer)
                {
                    await Task.Delay(random.Next(0, 30));
                    var old = _server;
                    _server = NewServer(2);
                    await old.ShutdownAsync(LinkCloseCodes.ServiceRestart);
                }

                // A restart fails what is in flight at once (link_reset); otherwise every task ends by its deadline + 1 s.
                var all = Task.WhenAll(exchanges.Select(e => e.Run));
                var bound = restartServer ? TimeSpan.FromSeconds(3) : Deadline + TimeSpan.FromSeconds(1);
                if (await Task.WhenAny(all, Task.Delay(bound)) != all)
                    return $"hung ({_schedule}{(restartServer ? ", server restart" : "")}): "
                        + string.Join(", ", exchanges.Where(e => !e.Run.IsCompleted).Select(e => e.What));
                var lostSession = restartServer || Volatile.Read(ref _serverSessions) != sessionsAtStart;
                foreach (var (what, run) in exchanges)
                {
                    var failure = run.Result;
                    if (failure is null) continue;
                    TypedFailures++;
                    if (failure.StartsWith("WRONG", StringComparison.Ordinal)) return $"{what}: {failure}";
                    if (!lostSession) return $"{what} failed though the session was kept ({_schedule}): {failure}";
                }
                await Wait.UntilAsync(() => _host.OpenExchanges == 0 && _forward.OpenExchanges == 0, TimeSpan.FromSeconds(5),
                    $"empty tables (node {_host.OpenExchanges}, server {_forward.OpenExchanges})");
                if (!restartServer && Volatile.Read(ref _nodeSessions) != 1)
                    return $"{_nodeSessions} node link sessions under transport faults only";
                return null;
            }
            catch (TimeoutException timeout)
            {
                return $"{timeout.Message} ({_schedule}, client {_client?.Status} {_client?.StatusReason}, connections {_proxy.Connections})";
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
            }
        }

        /// <summary>
        /// Runs one exchange under its deadline. Null: it succeeded with the right bytes. "WRONG …": it returned
        /// wrong bytes as a success, or failed untyped (always a failure of the seed). Anything else: a typed failure.
        /// </summary>
        private static async Task<string?> Timed(Func<CancellationToken, Task<string?>> run)
        {
            using var deadline = new CancellationTokenSource(Deadline);
            var started = DateTime.UtcNow;
            try
            {
                return await run(deadline.Token);
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested)
            {
                return "timeout";
            }
            catch (Exception error)
            {
                var code = Code(error);
                if (code is null) return "WRONG untyped failure: " + error;
                if (DateTime.UtcNow - started > Deadline + TimeSpan.FromSeconds(1)) return $"WRONG {code} after the deadline";
                return code + " [" + error.GetType().Name + ": " + error.Message + (error.InnerException is { } inner ? " <- " + inner.Message : "") + "]";
            }
        }

        private static string? Code(Exception error)
        {
            for (var e = error; e is not null; e = e.InnerException)
            {
                switch (e)
                {
                    case ForwardException forward: return forward.Code;
                    case ForwardStreamException stream: return stream.Code;
                    case WebSocketException { WebSocketErrorCode: WebSocketError.ConnectionClosedPrematurely }: return "ws_reset";
                }
            }
            return null;
        }

        private static async Task<string?> GetAsync(HttpClient http, int seed, int size, CancellationToken ct)
        {
            using var response = await http.GetAsync($"/blob/{seed}/{size}", HttpCompletionOption.ResponseHeadersRead, ct);
            var body = await response.Content.ReadAsByteArrayAsync(ct);
            if (body.Length != size || !body.AsSpan().SequenceEqual(SeededStream.Expected(seed, size)))
                return $"WRONG body: {body.Length} of {size} bytes, or other bytes";
            return null;
        }

        private static async Task<string?> PostAsync(HttpClient http, int seed, int size, bool chunked, CancellationToken ct)
        {
            HttpContent content = chunked ? new StreamContent(new SeededStream(seed, size)) : new ByteArrayContent(SeededStream.Expected(seed, size));
            using var response = await http.PostAsync("/echo-hash", content, ct);
            var answer = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct)).RootElement;
            if (answer.GetProperty("bytes").GetInt64() != size || answer.GetProperty("sha256").GetString() != SeededStream.ExpectedSha(seed, size))
                return "WRONG the runtime got other bytes than were posted";
            return null;
        }

        private async Task<string?> WebSocketAsync(CancellationToken ct)
        {
            const int count = 8;
            using var socket = await _forward.ConnectWebSocketAsync(FakeRuntime.Runtime, "/ws", $"seed={_seed}&count={count}", ct);
            for (var i = 0; i < count; i++)
                await socket.SendAsync(FakeRuntime.WsMessage(_seed, 8, i), WebSocketMessageType.Binary, true, ct);
            var buffer = new byte[64 * 1024];
            for (var i = 0; i < count; i++)
            {
                var message = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await socket.ReceiveAsync(buffer, ct);
                    if (result.MessageType == WebSocketMessageType.Close) return $"WRONG closed after {i} of {count} messages";
                    message.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);
                if (!message.ToArray().AsSpan().SequenceEqual(FakeRuntime.WsMessage(_seed, 7, i)))
                    return $"WRONG message {i} is not the runtime's message {i}";
            }
            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", ct);
            var received = _runtime.WsReceived[_seed.ToString()];
            lock (received)
            {
                if (received.Count != count) return $"WRONG the runtime got {received.Count} of {count} messages after a clean close";
                for (var i = 0; i < count; i++)
                    if (!received[i].AsSpan().SequenceEqual(FakeRuntime.WsMessage(_seed, 8, i))) return $"WRONG the runtime's message {i} differs";
            }
            return null;
        }
    }
}
