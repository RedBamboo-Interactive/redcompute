using System.Text.Json;
using System.Text.Json.Nodes;
using RedCompute.NodeLink;

namespace RedCompute.NodeLink.Tests;

/// <summary>A clock the pure tests move by hand (no timers: only timestamps and wall time).</summary>
internal sealed class ManualTime : TimeProvider
{
    private long _ticks = TimeSpan.FromDays(1).Ticks;
    private DateTimeOffset _utc = new(2026, 11, 16, 12, 0, 0, TimeSpan.Zero);

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long GetTimestamp() => _ticks;
    public override DateTimeOffset GetUtcNow() => _utc;

    public void Advance(TimeSpan by)
    {
        _ticks += by.Ticks;
        _utc += by;
    }
}

/// <summary>A handler that records what it receives and commits it in memory.</summary>
internal sealed class RecordingHandler : IChannelHandler
{
    private readonly object _lock = new();
    public List<byte[]> Messages { get; } = [];
    public TaskCompletionSource RemoteClosed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource<string> Reset { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public bool CloseWhenRemoteCloses { get; init; } = true;
    public bool Echo { get; init; }

    public async ValueTask OnDataAsync(LinkChannelHandle channel, ChannelData data, CancellationToken ct)
    {
        lock (_lock) Messages.Add(data.Payload.ToArray());
        if (Echo) await channel.SendAsync(data.Payload, ct);
    }

    public ValueTask OnRemoteCloseAsync(LinkChannelHandle channel, CancellationToken ct)
    {
        if (CloseWhenRemoteCloses) channel.Close();
        RemoteClosed.TrySetResult();
        return ValueTask.CompletedTask;
    }

    public void OnReset(LinkChannelHandle channel, string code, string message) => Reset.TrySetResult(code);

    public int Count { get { lock (_lock) return Messages.Count; } }
}

/// <summary>An application handler for tests: echo channels, a few RPC methods.</summary>
internal sealed class TestLinkHandler : LinkHandler
{
    public Func<ChannelOpenInfo, ChannelAcceptance?>? Accept { get; init; }
    public List<string> Notifications { get; } = [];

    public override ChannelAcceptance? AcceptChannel(LinkSession session, ChannelOpenInfo info)
        => Accept?.Invoke(info) ?? (info.Class == ChannelClasses.DiagEcho.Name ? new ChannelAcceptance(new RecordingHandler { Echo = true }) : null);

    public override async ValueTask<JsonNode?> OnRequestAsync(LinkSession session, string method, JsonElement parameters, CancellationToken ct)
    {
        switch (method)
        {
            case "test.add":
                return parameters.GetProperty("a").GetInt32() + parameters.GetProperty("b").GetInt32();
            case "test.slow":
                await Task.Delay(200, ct);
                return "slow";
            case "test.fail":
                throw new LinkRpcException("test_failed", "as asked");
            case "test.boom":
                throw new InvalidOperationException("boom");
            default:
                return await base.OnRequestAsync(session, method, parameters, ct);
        }
    }

    public override ValueTask OnNotificationAsync(LinkSession session, string method, JsonElement parameters, CancellationToken ct)
    {
        lock (Notifications) Notifications.Add(method);
        return ValueTask.CompletedTask;
    }
}

internal static class Wait
{
    public static async Task UntilAsync(Func<bool> condition, TimeSpan? timeout = null, string? what = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Timed out waiting for " + (what ?? "a condition"));
            await Task.Delay(5);
        }
    }
}

/// <summary>A client and a server joined by in-memory pipes, with short clocks.</summary>
internal sealed class LinkPair : IAsyncDisposable
{
    public const string NodeId = "node_01JCK4Z8X9T5V6W7Y8Z9A0B1C2";

    public static LinkOptions FastOptions { get; } = new()
    {
        HeartbeatInterval = TimeSpan.FromMilliseconds(100),
        MissedHeartbeats = 3,
        AckDelay = TimeSpan.FromMilliseconds(5),
        RtoMin = TimeSpan.FromMilliseconds(30),
        RtoMax = TimeSpan.FromMilliseconds(400),
        HandshakeTimeout = TimeSpan.FromMilliseconds(500),
        Tick = TimeSpan.FromMilliseconds(5),
        SessionRetention = TimeSpan.FromMinutes(1),
    };

    private readonly CancellationTokenSource _cts = new();
    private Task? _clientRun;

    public LinkPair(ILinkHandler? nodeHandler = null, ILinkHandler? serverHandler = null, LinkOptions? options = null,
        Func<CancellationToken, Task<ILinkTransport>>? dial = null, AgentPolicy? policy = null, string agentVersion = "0.1.0",
        string nodeId = NodeId, IReadOnlyList<string>? nodeFeatures = null)
    {
        Options = options ?? FastOptions;
        NodeHandler = nodeHandler ?? new TestLinkHandler();
        ServerHandler = serverHandler ?? new TestLinkHandler();
        _policy = policy;
        Server = NewServer();
        Client = new LinkClient(new LinkClientOptions
        {
            NodeId = nodeId,
            Agent = new AgentInfo(agentVersion, "linux", "x64"),
            Handler = NodeHandler,
            Features = nodeFeatures ?? [LinkFeatures.DescribeV1, LinkFeatures.DiagEcho, LinkFeatures.SpoolDisk],
            Dial = dial ?? DialAsync,
            Link = Options,
            Backoff = new ReconnectBackoff(new Random(7), TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(100)),
        });
    }

    private readonly AgentPolicy? _policy;
    private int _epoch;

    private LinkServer NewServer() => new(new LinkServerOptions
    {
        ServerEpoch = "epoch-" + Interlocked.Increment(ref _epoch),
        HandlerFactory = _ => ServerHandler,
        Features = [LinkFeatures.DescribeV1, LinkFeatures.DiagEcho],
        AgentPolicy = _policy,
        Link = Options,
    });

    /// <summary>A RedCompute restart: every link session is gone; the next hello cannot resume.</summary>
    public async Task RestartServerAsync()
    {
        var old = Server;
        Server = NewServer();
        await old.ShutdownAsync(LinkCloseCodes.ServiceRestart);
    }

    public LinkOptions Options { get; }
    public ILinkHandler NodeHandler { get; }
    public ILinkHandler ServerHandler { get; }
    public LinkServer Server { get; private set; }
    public LinkClient Client { get; }
    public List<Task<LinkDisconnect?>> ServerRuns { get; } = [];
    public string BoundNodeId { get; set; } = NodeId;

    public Task<ILinkTransport> DialAsync(CancellationToken ct)
    {
        var (node, server) = LinkPipe.Create();
        var hub = Server;
        var bound = BoundNodeId;
        lock (ServerRuns) ServerRuns.Add(Task.Run(() => hub.RunAsync(server, bound, _cts.Token)));
        return Task.FromResult(node);
    }

    public LinkPair Start()
    {
        _clientRun = Task.Run(() => Client.RunAsync(_cts.Token));
        return this;
    }

    public Task ClientRun => _clientRun!;

    public async Task<LinkSession> OnlineAsync()
    {
        await Wait.UntilAsync(() => Client.Status == LinkClientStatus.Online && Server.IsOnline(NodeId), what: "the link to come up");
        return Client.Session!;
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        if (_clientRun is not null) await _clientRun.WaitAsync(TimeSpan.FromSeconds(5)).ContinueWith(_ => { });
        await Client.DisposeAsync();
        await Server.DisposeAsync();
    }
}
