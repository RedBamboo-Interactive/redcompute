using System.Net.Http;
using System.Net.WebSockets;
using System.Reflection.Metadata;
using Xunit;

namespace RedCompute.App.Tests;

/// <summary>
/// The network seam guard (Phase 4 P2, design 6.1): no code may open its own HTTP connection or
/// WebSocket except through <c>IHostNetwork.CreateServiceHandler</c>/<c>ConnectWebSocketAsync</c>, or
/// with a reason it never reaches a service an execution host serves. A remote host's resolved
/// service address is a <c>.invalid</c> sentinel, so code that builds its own client from it fails
/// closed; this guard makes it fail here first. It reads the IL of every built RedCompute assembly
/// (<see cref="ExecutionHostGuardTests.ScannedAssemblies"/>), so it sees lambdas, async state machines
/// and field initialisers.
/// <para>
/// What counts as opening a connection: <c>new HttpClient()</c> with no handler (it builds its own),
/// <c>new HttpClientHandler()</c>, <c>new SocketsHttpHandler()</c> and <c>new ClientWebSocket()</c>.
/// <c>new HttpClient(handler)</c> is the seam's own shape and is not a hit by itself.
/// </para>
/// </summary>
public sealed class NetworkSeamGuardTests
{
    internal static readonly (string Why, string[] Types)[] Allowlist =
    [
        ("the seam itself: node zero's network (LocalExecutionHost.LocalNetwork) and the refusing handler", [
            "RedCompute.PluginSdk.Hosting.Local.LocalExecutionHost",
        ]),
        ("the seam's probe handler: node zero's status and health probes (LocalNetwork.CreateProbeHandler) with a short loopback connect", [
            "RedCompute.PluginSdk.Hosting.LoopbackProbe",
        ]),
        ("cloud APIs: Suno's, OpenAI's and ElevenLabs' own servers on the internet, never a host's service", [
            "RedCompute.Plugin.OpenAILive.OpenAILiveProvider",
            "RedCompute.Plugin.Suno.SunoProvider",
            "RedCompute.Plugin.TtsElevenLabs.TtsElevenLabsProvider",
        ]),
        ("Kev: its endpoint is configured (a private-local URL), not served by an execution host; node zero only in Phase 4", [
            "RedCompute.Plugin.Kev.KevLocalQualityProvider",
        ]),
        ("the Codex tool-free relay: the upstream it relays to is the model API, not a host's service", [
            "RedCompute.Plugin.Codex.CodexToolFreeRelay",
        ]),
        ("RedCompute itself: the rerun re-posts to RedCompute's own generate route on this server", [
            "RedCompute.App.Api.Endpoints.GlobalEndpoints",
        ]),
        ("the generic proxy's fallback client, for a proxy provider that is not an IServiceProxyClient (each host-served one passes its own)", [
            "RedCompute.App.Api.StreamingProxy",
        ]),
        ("the kernel and the public release feed, never a node", [
            "RedCompute.App.Services.Nodes.NodeAgentRelease",
            "RedCompute.App.Services.ProviderConfigService",
            "RedCompute.App.Services.QualityModeService",
            "RedCompute.App.Services.RedLeafSessionReader",
            "RedCompute.App.Services.SessionCallbackRegistry",
        ]),
        ("the suite's own services on this server: the telemetry probe of each suite port, and CodeRed's navigate", [
            "RedCompute.App.Api.Endpoints.SuiteTelemetryEndpoints",
            "RedCompute.App.Api.Endpoints.UnifiedSessionEndpoints",
        ]),
        ("node-side: leaf-node dials the kernel and calls its pairing API from the node's own machine", [
            "RedCompute.Node.Link.NodeDialer",
            "RedCompute.Node.Pairing.NodeServerClient",
        ]),
        ("node-side: the far end of the forward. leaf-node reaches its owner's runtimes on its own 127.0.0.1 (the forward target and the health probe), with a hardened handler: no proxy, no cookies, no redirects", [
            "RedCompute.Node.Runtimes.RuntimeForwardTarget",
            "RedCompute.Node.Runtimes.RuntimeManager",
        ]),
    ];

    [Fact]
    public void Only_the_seam_and_allowlisted_types_open_their_own_connections()
    {
        var hits = ScanAll();
        var allowed = Allowlist.SelectMany(group => group.Types).ToHashSet(StringComparer.Ordinal);
        var offending = hits.Where(hit => !allowed.Contains(hit.Type)).ToList();
        Assert.True(offending.Count == 0,
            "A connection opened outside IHostNetwork (build the client on host.Network.CreateServiceHandler or "
            + "ConnectWebSocketAsync, or allowlist it with why it never reaches a host's service):\n"
            + string.Join("\n", offending.Select(hit => $"  {hit.Assembly}: {hit.Type}.{hit.Method} -> {hit.Target}").Distinct())
            + "\n\nEvery type with a hit now:\n"
            + string.Join("\n", hits.Select(hit => hit.Type).Distinct().Order(StringComparer.Ordinal)));
    }

    [Fact]
    public void The_allowlist_only_names_types_that_still_need_it()
    {
        var hitTypes = ScanAll().Select(hit => hit.Type).ToHashSet(StringComparer.Ordinal);
        var stale = Allowlist.SelectMany(group => group.Types).Where(type => !hitTypes.Contains(type)).ToList();
        Assert.True(stale.Count == 0, "Allowlist entries no longer needed:\n" + string.Join("\n", stale));
    }

    /// <summary>The guard's own test: each shape is found, and new HttpClient(handler) is not.</summary>
    [Fact]
    public void Each_way_of_opening_a_connection_is_found()
    {
        var hits = ExecutionHostGuardTests.Scan(typeof(NetworkSeamGuardTests).Assembly.Location, IsConnection)
            .Where(hit => hit.Type == typeof(NetworkGuardFixture).FullName).ToList();
        Assert.Contains(hits, hit => hit.Method == nameof(NetworkGuardFixture.OwnClient) && hit.Target == "System.Net.Http.HttpClient::.ctor");
        Assert.Contains(hits, hit => hit.Method == nameof(NetworkGuardFixture.OwnHandler) && hit.Target == "System.Net.Http.HttpClientHandler::.ctor");
        Assert.Contains(hits, hit => hit.Method == nameof(NetworkGuardFixture.OwnSocketsHandler) && hit.Target == "System.Net.Http.SocketsHttpHandler::.ctor");
        Assert.Contains(hits, hit => hit.Target == "System.Net.WebSockets.ClientWebSocket::.ctor"); // in an async state machine
        Assert.DoesNotContain(hits, hit => hit.Method == nameof(NetworkGuardFixture.OnAHandler));
        // The handler OwnHandler builds is one hit; the HttpClient(handler) around it is none.
        Assert.Single(hits, hit => hit.Method == nameof(NetworkGuardFixture.OwnHandler));
    }

    private static List<ExecutionHostGuardTests.Hit> ScanAll()
        => ExecutionHostGuardTests.ScannedAssemblies().SelectMany(path => ExecutionHostGuardTests.Scan(path, IsConnection)).ToList();

    private static bool IsConnection(MetadataReader metadata, EntityHandle handle, string type, string member)
        => member == ".ctor" && type switch
        {
            "System.Net.Http.HttpClientHandler" or "System.Net.Http.SocketsHttpHandler" or "System.Net.WebSockets.ClientWebSocket" => true,
            "System.Net.Http.HttpClient" => ParameterCount(metadata, handle) == 0,
            _ => false,
        };

    private static int ParameterCount(MetadataReader metadata, EntityHandle handle)
    {
        if (handle.Kind != HandleKind.MemberReference) return -1;
        var reader = metadata.GetBlobReader(metadata.GetMemberReference((MemberReferenceHandle)handle).Signature);
        reader.ReadSignatureHeader();
        return reader.ReadCompressedInteger();
    }
}

/// <summary>What the network guard must catch: never called, only scanned.</summary>
internal sealed class NetworkGuardFixture
{
    public static HttpClient OwnClient() => new() { Timeout = TimeSpan.FromSeconds(1) };

    public static HttpClient OwnHandler() => new(new HttpClientHandler());

    public static HttpMessageHandler OwnSocketsHandler() => new SocketsHttpHandler();

    public static HttpClient OnAHandler(HttpMessageHandler handler) => new(handler);

    public static async Task<WebSocket> OwnSocket(Uri uri)
    {
        var socket = new ClientWebSocket();
        await socket.ConnectAsync(uri, CancellationToken.None);
        return socket;
    }
}
