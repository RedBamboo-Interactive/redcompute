using System.Net;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using RedBamboo.AppHost.Auth;
using RedCompute.App.Services.Nodes;
using RedCompute.Core.Hardware;
using RedCompute.NodeLink;

namespace RedCompute.App.Tests;

/// <summary>Remote nodes, kernel identities and a scripted node agent for the Phase 3 server tests.</summary>
internal static class NodeTestKit
{
    public static readonly DateTimeOffset Now = new(2026, 11, 16, 12, 0, 0, TimeSpan.Zero);
    public const string LocalId = "node_01JCZ0000000000000000000AB";
    public const string Owner = "user-owner";

    public static readonly NodeIdentityResult Identity = new(new NodeIdentity(LocalId, Now.AddDays(-30), LocalId), true, false, null);

    /// <summary>A valid remote node id: <c>node_01JD</c> + 22 digits.</summary>
    public static string RemoteId(int n) => "node_01JD" + n.ToString("D22");

    public static NodeRegistry Registry(string? cacheDirectory = null, Func<DateTimeOffset>? clock = null)
        => new(Identity, NodeRegistryTests.Platform(), "0.2.0", Now.AddHours(-1), clock ?? (() => Now),
            remoteCacheDirectory: cacheDirectory);

    public static KernelNode Kernel(string id, string name = "Lab VM", bool confirmed = true, string status = "offline",
        string owner = Owner, string? reason = null)
        => new(id, name, KernelNode.RemoteKind, status, reason, owner, confirmed, "linux", "x64", "Fedora Linux 41", "leaflinux",
            "0.1.0", 1, Now.AddMinutes(-5), Now.AddDays(-1));

    public static JwtOptions JwtOptions() => new()
    {
        SigningKey = "node-hub-tests-require-a-long-signing-key-0123456789abcdef",
        ExecutionTokenLifetime = TimeSpan.FromMinutes(1),
        ClockSkew = TimeSpan.Zero,
    };

    /// <summary>The identity the kernel mints for one node-link hop (design 5.2).</summary>
    public static string IngressToken(JwtService jwt, string nodeId, string owner = Owner, string app = NodeHub.IngressAppId,
        string actorKind = NodeHub.NodeActorKind, TimeSpan? lifetime = null, string? executionId = null,
        IReadOnlyList<string>? roles = null)
    {
        var identity = new ExecutionIdentity(
            ExecutionIdentity.CurrentSchemaVersion,
            executionId ?? Guid.NewGuid().ToString(),
            new ExecutionAppIdentity(app, app == NodeHub.IngressAppId ? "Leaf node ingress" : app),
            new ExecutionActorIdentity(actorKind, nodeId, "Lab VM", nodeId),
            new ExecutionBeneficiaryIdentity("user", owner, "Owner"),
            [new ExecutionContextReference("node-link", Guid.NewGuid().ToString("N"))]);
        return jwt.GenerateExecutionToken(identity, new ExecutionPrincipal(owner, "owner@example.test", "Owner", roles ?? ["admin"]),
            DateTimeOffset.UtcNow + (lifetime ?? TimeSpan.FromSeconds(60)));
    }

    /// <summary>
    /// A request as AppHost's auth middleware leaves it: the verified principal, the execution identity in
    /// the items, and a peer. No token is the anonymous loopback identity (<c>LocalDefault</c>).
    /// </summary>
    public static DefaultHttpContext Context(JwtService jwt, string? token, IPAddress? peer = null)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = peer ?? IPAddress.Loopback;
        if (token is null)
        {
            context.User = LocalDefault();
            return context;
        }
        context.User = jwt.ValidateToken(token) ?? throw new InvalidOperationException("the test token did not validate");
        if (ExecutionIdentityClaims.TryRead(context.User, out var identity, out _))
            context.Items[ExecutionIdentityClaims.HttpContextItemKey] = identity;
        return context;
    }

    public static ClaimsPrincipal LocalDefault() => new(new ClaimsIdentity(
    [
        new Claim("sub", "local-user"),
        new Claim("roles", "[\"admin\"]"),
    ], ConfidentialResourcePolicy.LocalDefaultAuthenticationType));

    public static ClaimsPrincipal RedLeafService(JwtService jwt)
        => jwt.ValidateToken(jwt.GenerateServiceAccessToken("redleaf", computeProvenance: true))!;

    public static HardwareSnapshot Hardware(double usage = 12.5) => new()
    {
        Timestamp = Now,
        Cpu = new CpuInfo { CoreCount = 8, UsagePercent = usage },
        Ram = new RamInfo { TotalBytes = 16UL << 30, UsedBytes = 4UL << 30 },
        Gpus = [new GpuInfo { Index = 0, Name = "Virtual GPU", Memory = new GpuMemoryInfo { TotalBytes = 2UL << 30, UsedBytes = 0 } }],
    };

    public static async Task WaitAsync(Func<bool> condition, string what, int timeoutMs = 10_000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!condition())
        {
            if (Environment.TickCount64 > deadline) throw new TimeoutException($"Timed out waiting for {what}");
            await Task.Delay(10);
        }
    }
}

/// <summary>
/// The node's side of the link, scripted: answers describe, cli, hardware and diag.echo.open, echoes
/// diag.echo channels and refuses everything else, like the real agent's handler.
/// </summary>
internal sealed class FakeNodeHandler : LinkHandler
{
    public string AgentVersion { get; set; } = "0.1.0";
    public JsonArray Clis { get; set; } = new()
    {
        new JsonObject { ["name"] = "claude", ["installed"] = true, ["version"] = "2.1.0", ["signedIn"] = true, ["authMethod"] = "subscription", ["path"] = "/home/lab/.local/bin/claude" },
        new JsonObject { ["name"] = "codex", ["installed"] = false, ["signedIn"] = false },
    };
    public int DescribeCalls;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public override ChannelAcceptance? AcceptChannel(LinkSession session, ChannelOpenInfo info)
        => info.Class == ChannelClasses.DiagEcho.Name ? new ChannelAcceptance(new Echo()) : null;

    public override ValueTask<JsonNode?> OnRequestAsync(LinkSession session, string method, JsonElement parameters, CancellationToken ct)
    {
        JsonNode answer = method switch
        {
            NodeHub.DescribeMethod => Describe(),
            NodeHub.CliMethod => new JsonObject { ["clis"] = Clis.DeepClone() },
            NodeHub.HardwareMethod => JsonSerializer.SerializeToNode(NodeTestKit.Hardware(), Json)!,
            NodeHub.EchoOpenMethod => new JsonObject { ["accepted"] = true },
            _ => throw new LinkRpcException(LinkErrorCodes.MethodNotAllowed, $"This node does not do '{method}'."),
        };
        return ValueTask.FromResult<JsonNode?>(answer);
    }

    private JsonObject Describe()
    {
        Interlocked.Increment(ref DescribeCalls);
        return new JsonObject
        {
            ["nodeId"] = "ignored",
            ["platform"] = new JsonObject { ["os"] = "linux", ["osDescription"] = "Fedora Linux 41 (Workstation)", ["arch"] = "x64" },
            ["machineName"] = "leaflinux",
            ["processor"] = "Virtual CPU",
            ["agent"] = new JsonObject { ["version"] = AgentVersion, ["os"] = "linux", ["arch"] = "x64" },
            ["protocol"] = 1,
            ["credentialStore"] = "secret-service",
        };
    }

    private sealed class Echo : IChannelHandler
    {
        public async ValueTask OnDataAsync(LinkChannelHandle channel, ChannelData data, CancellationToken ct)
            => await channel.SendAsync(data.Payload.ToArray(), ct);

        public ValueTask OnRemoteCloseAsync(LinkChannelHandle channel, CancellationToken ct)
        {
            channel.Close();
            return ValueTask.CompletedTask;
        }

        public void OnReset(LinkChannelHandle channel, string code, string message)
        {
        }
    }
}

/// <summary>A node agent's link client wired straight to a hub over in-memory pipes.</summary>
internal sealed class PipedNode : IAsyncDisposable
{
    private readonly CancellationTokenSource _run = new();
    private readonly Task _loop;

    public PipedNode(NodeHub hub, string nodeId, FakeNodeHandler? handler = null, string? boundNodeId = null, string agentVersion = "0.1.0",
        Func<ILinkTransport, CancellationToken, Task<ILinkTransport>>? secure = null)
    {
        Handler = handler ?? new FakeNodeHandler { AgentVersion = agentVersion };
        Client = new LinkClient(new LinkClientOptions
        {
            NodeId = nodeId,
            Agent = new AgentInfo(agentVersion, "linux", "x64"),
            Handler = Handler,
            Features = [LinkFeatures.DescribeV1, LinkFeatures.HardwareV1, LinkFeatures.CliV1, LinkFeatures.DiagEcho],
            Dial = ct =>
            {
                var (node, server) = LinkPipe.Create();
                _ = Task.Run(() => hub.Server.RunAsync(server, boundNodeId ?? nodeId, CancellationToken.None));
                return Task.FromResult(node);
            },
            Backoff = new ReconnectBackoff(new Random(7), TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(200)),
            Secure = secure,
        });
        _loop = Task.Run(() => Client.RunAsync(_run.Token));
    }

    public FakeNodeHandler Handler { get; }
    public LinkClient Client { get; }

    public async ValueTask DisposeAsync()
    {
        _run.Cancel();
        try { await _loop.WaitAsync(TimeSpan.FromSeconds(10)); }
        catch (Exception error) when (error is OperationCanceledException or TimeoutException) { }
        await Client.DisposeAsync();
    }
}
