using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using RedCompute.Node.Credentials;
using RedCompute.Node.Reports;
using RedCompute.NodeLink;
using Xunit;

namespace RedCompute.Node.Tests;

/// <summary>
/// The agent against an in-process server (NodeLink's LinkServer over an in-memory pipe): the Phase 3
/// methods and nothing else, the audit log, echo and kill-all, reports, and the revocation cleanup.
/// </summary>
public sealed class NodeAgentLinkTests : IAsyncLifetime
{
    internal const string NodeId = "node_01JTESTTESTTESTTESTTESTTES";
    private readonly NodeHome _home = new("link");
    private readonly ScriptedProbes _probes = new();
    private readonly CancellationTokenSource _stop = new();
    private LinkedAgent _linked = null!;

    public async Task InitializeAsync()
    {
        _probes.Answers["claude --version"] = new ProbeOutput(true, 0, "2.1.3 (Claude Code)\n", "");
        _probes.Answers["claude auth status --json"] = new ProbeOutput(true, 0, """{"loggedIn":true,"authMethod":"claude.ai"}""", "");
        _linked = await LinkedAgent.StartAsync(_home, _probes, _stop.Token, hardwareReport: TimeSpan.FromMilliseconds(200));
    }

    public async Task DisposeAsync()
    {
        _stop.Cancel();
        await _linked.DisposeAsync();
        _home.Dispose();
    }

    [Fact]
    public async Task The_node_describes_itself_and_its_hardware_and_clis()
    {
        var session = _linked.ServerSession;
        var describe = await session.CallAsync("node.describe");
        Assert.Equal(NodeId, describe.GetProperty("nodeId").GetString());
        Assert.Equal(AgentIdentity.Version, describe.GetProperty("agent").GetProperty("version").GetString());
        Assert.Equal("file", describe.GetProperty("credentialStore").GetString());
        Assert.Equal(_home.Host.Platform.Info.Os, describe.GetProperty("platform").GetProperty("os").GetString());
        Assert.True(describe.GetProperty("hardware").GetProperty("cpuCores").GetInt32() > 0);
        Assert.Contains("diag.echo", describe.GetProperty("features").EnumerateArray().Select(f => f.GetString()));

        var hardware = await session.CallAsync("node.hardware");
        Assert.Equal(NodeId, hardware.GetProperty("nodeId").GetString());
        Assert.True(hardware.GetProperty("ram").GetProperty("totalBytes").GetUInt64() > 0);

        var clis = (await session.CallAsync("node.cli")).GetProperty("clis").EnumerateArray().ToList();
        Assert.Equal(["claude", "codex", "opencode"], clis.Select(c => c.GetProperty("name").GetString()));
        // What is reported, and nothing of the CLI's output.
        foreach (var cli in clis)
            Assert.Equal(["name", "installed", "version", "signedIn", "authMethod", "path"], cli.EnumerateObject().Select(p => p.Name));

        var audit = _home.Environment().Audit.Read();
        foreach (var method in new[] { "node.describe", "node.hardware", "node.cli" })
            Assert.Contains(audit, line => line["name"]!.GetValue<string>() == method && line["outcome"]!.GetValue<string>() == "answered");
    }

    [Theory]
    [InlineData("fs.read")]
    [InlineData("process.spawn")]
    [InlineData("shell.exec")]
    [InlineData("port.forward")]
    [InlineData("node.update")]
    [InlineData("link.hello")]
    public async Task Every_other_method_is_refused_and_audited(string method)
    {
        var error = await Assert.ThrowsAsync<LinkRpcException>(() =>
            _linked.ServerSession.CallAsync(method, new JsonObject { ["path"] = "/etc/passwd", ["argv"] = new JsonArray("sh") }));
        Assert.Equal(LinkErrorCodes.MethodNotAllowed, error.Code);
        var line = Assert.Single(_home.Environment().Audit.Read(), l => l["name"]!.GetValue<string>() == method);
        Assert.Equal("refused", line["outcome"]!.GetValue<string>());
        Assert.Equal("request", line["kind"]!.GetValue<string>());
        Assert.DoesNotContain("passwd", line.ToJsonString()); // the audit never holds payloads
    }

    [Fact]
    public async Task Channels_other_than_echo_are_refused_and_audited()
    {
        foreach (var @class in new[] { "stdio", "port", "file", "blob" })
        {
            var error = await Assert.ThrowsAsync<LinkChannelResetException>(() =>
                _linked.ServerSession.OpenChannelAsync(new ChannelOpenRequest(@class), new Collector()));
            Assert.Equal(LinkErrorCodes.ClassNotSupported, error.Code);
        }
        var refused = _home.Environment().Audit.Read().Where(l => l["kind"]!.GetValue<string>() == "channel").ToList();
        Assert.Equal(4, refused.Count(l => l["outcome"]!.GetValue<string>() == "refused"));
    }

    [Fact]
    public async Task The_echo_channel_returns_every_byte_in_order_and_kill_all_ends_it()
    {
        var answer = await _linked.ServerSession.CallAsync("diag.echo.open", new JsonObject { ["bytes"] = 1 << 20 });
        Assert.True(answer.GetProperty("accepted").GetBoolean());

        var collector = new Collector();
        var channel = await _linked.ServerSession.OpenChannelAsync(new ChannelOpenRequest(ChannelClasses.DiagEcho.Name), collector);
        var sent = new byte[1 << 20];
        new Random(3).NextBytes(sent);
        for (var offset = 0; offset < sent.Length; offset += 50_000)
            await channel.SendAsync(sent.AsMemory(offset, Math.Min(50_000, sent.Length - offset)));
        await Wait.UntilAsync(() => collector.Length == sent.Length, "the echo");
        Assert.Equal(sent, collector.Bytes());

        // kill-all (the control verb) resets the open echo channel on the node.
        Assert.Equal(1, _linked.Agent.KillAll());
        await Wait.UntilAsync(() => collector.ResetCode is not null, "the reset");
        Assert.Equal("killed_by_owner", collector.ResetCode);
        Assert.Contains(_home.Environment().Audit.Read(), l => l["name"]!.GetValue<string>() == "kill-all");
    }

    [Fact]
    public async Task The_node_pushes_hardware_reports()
    {
        await Wait.UntilAsync(() => _linked.Server.Reports.Any(r => r["hardware"] is not null), "a hardware report");
        var report = _linked.Server.Reports.First(r => r["hardware"] is not null);
        Assert.Equal(NodeId, report["hardware"]!["nodeId"]!.GetValue<string>());
        // The first report also carries the CLIs (the probe at start).
        Assert.Contains(_linked.Server.Reports, r => r["clis"] is JsonArray);
    }

    [Fact]
    public async Task A_revoked_node_wipes_its_key_and_spool_and_stops_dialling()
    {
        Directory.CreateDirectory(_home.Paths.Spool);
        File.WriteAllText(Path.Combine(_home.Paths.Spool, "segment-0001"), "spooled");
        var dialsBefore = _linked.Dials;

        await _linked.LinkServer.RevokeAsync(NodeId);

        await Wait.UntilAsync(() => _home.Environment().StateStore.Load().Ended is not null, "the local cleanup");
        var state = _home.Environment().StateStore.Load();
        Assert.Null(state.Pairing);
        Assert.Equal("revoked", state.Ended!.Reason);
        Assert.Equal(NodeId, state.Ended.NodeId);
        Assert.False(File.Exists(_home.Paths.KeyFile));
        Assert.False(Directory.Exists(_home.Paths.Spool));
        await Task.Delay(300);
        Assert.Equal(dialsBefore, _linked.Dials); // no redial after a revoke
        Assert.Equal("revoked", _linked.Agent.Status()["link"]!["status"]!.GetValue<string>());
        Assert.Contains("Revoked by the server", NodeCommands.DescribeStatus(_linked.Agent.Status()));
    }

    [Fact]
    public async Task Unpair_says_bye_tells_the_server_and_forgets()
    {
        var answer = await _linked.Agent.UnpairAsync(CancellationToken.None);
        Assert.Equal(NodeId, answer["nodeId"]!.GetValue<string>());
        await Wait.UntilAsync(() => _linked.Server.Notifications.Contains("link.bye"), "link.bye");
        Assert.Null(_home.Environment().StateStore.Load().Pairing);
        Assert.Equal("unpaired", _home.Environment().StateStore.Load().Ended!.Reason);
        Assert.False(File.Exists(_home.Paths.KeyFile));
    }
}

/// <summary>A paired agent home linked to an in-process LinkServer.</summary>
internal sealed class LinkedAgent : IAsyncDisposable
{
    private readonly Task _run;

    private LinkedAgent(NodeAgent agent, LinkServer server, ServerSide side, Task run, Func<int> dials)
    {
        Agent = agent;
        LinkServer = server;
        Server = side;
        _run = run;
        _dials = dials;
    }

    private readonly Func<int> _dials;
    public NodeAgent Agent { get; }
    public LinkServer LinkServer { get; }
    public ServerSide Server { get; }
    public int Dials => _dials();
    public LinkSession ServerSession => LinkServer.FindSession(NodeAgentLinkTests.NodeId)!;

    public static async Task<LinkedAgent> StartAsync(NodeHome home, IProbeRunner probes, CancellationToken ct, TimeSpan? hardwareReport = null)
    {
        await PairLocallyAsync(home);
        var side = new ServerSide();
        var server = new LinkServer(new LinkServerOptions
        {
            ServerEpoch = "epoch-1",
            HandlerFactory = _ => side,
            Features = [LinkFeatures.DescribeV1, LinkFeatures.HardwareV1, LinkFeatures.CliV1, LinkFeatures.DiagEcho],
        });
        var dials = 0;
        var env = home.Environment(probes: probes, hardwareReport: hardwareReport, dial: (pairing, key) => token =>
        {
            Interlocked.Increment(ref dials);
            using (key()) { } // the agent can open its key
            var (node, hub) = LinkPipe.Create();
            _ = server.RunAsync(hub, pairing.NodeId, CancellationToken.None);
            return Task.FromResult(node);
        });
        var agent = new NodeAgent(env);
        var run = agent.RunAsync(ct);
        await Wait.UntilAsync(() => server.IsOnline(NodeAgentLinkTests.NodeId), "the link");
        return new LinkedAgent(agent, server, side, run, () => Volatile.Read(ref dials));
    }

    /// <summary>A pairing as <c>leaf-node pair</c> leaves it: a committed file key and node.json.</summary>
    public static Task PairLocallyAsync(NodeHome home, string nodeId = NodeAgentLinkTests.NodeId, string origin = "https://leaf.example.test")
    {
        var store = new FileKeyStore(home.Paths);
        using var key = store.Create(origin);
        store.Commit(key, nodeId);
        home.Environment().StateStore.Save(new NodeState
        {
            Pairing = new NodePairing
            {
                NodeId = nodeId,
                ServerOrigin = origin,
                LinkPath = "/api/nodes/link",
                KeyRef = key.KeyRef,
                CredentialStore = store.Kind,
                Fingerprint = Pairing.NodeCrypto.Fingerprint(key.SubjectPublicKeyInfo),
                PairedAt = DateTimeOffset.UtcNow,
            },
        });
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await Agent.DisposeAsync();
        try
        {
            await _run.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (Exception error) when (error is OperationCanceledException or TimeoutException)
        {
        }
        await LinkServer.DisposeAsync();
    }
}

/// <summary>The server's application in these tests: records notifications.</summary>
internal sealed class ServerSide : LinkHandler
{
    public ConcurrentQueue<string> Notifications { get; } = new();
    public ConcurrentQueue<JsonObject> Reports { get; } = new();

    public override ValueTask<JsonNode?> OnRequestAsync(LinkSession session, string method, JsonElement parameters, CancellationToken ct)
    {
        Notifications.Enqueue(method);
        return method == "link.bye"
            ? ValueTask.FromResult<JsonNode?>(new JsonObject { ["ok"] = true })
            : base.OnRequestAsync(session, method, parameters, ct);
    }

    public override ValueTask OnNotificationAsync(LinkSession session, string method, JsonElement parameters, CancellationToken ct)
    {
        Notifications.Enqueue(method);
        if (method == "node.report") Reports.Enqueue(JsonNode.Parse(parameters.GetRawText())!.AsObject());
        return ValueTask.CompletedTask;
    }
}

/// <summary>Collects what a channel delivers.</summary>
internal sealed class Collector : IChannelHandler
{
    private readonly MemoryStream _bytes = new();

    public long Length { get { lock (_bytes) return _bytes.Length; } }
    public byte[] Bytes() { lock (_bytes) return _bytes.ToArray(); }
    public string? ResetCode { get; private set; }

    public ValueTask OnDataAsync(LinkChannelHandle channel, ChannelData data, CancellationToken ct)
    {
        lock (_bytes) _bytes.Write(data.Payload.Span);
        return ValueTask.CompletedTask;
    }

    public ValueTask OnRemoteCloseAsync(LinkChannelHandle channel, CancellationToken ct) => ValueTask.CompletedTask;

    public void OnReset(LinkChannelHandle channel, string code, string message) => ResetCode = code;
}
