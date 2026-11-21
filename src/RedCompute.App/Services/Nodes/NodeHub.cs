using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.WebSockets;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using IEndpointRouteBuilder = Microsoft.AspNetCore.Routing.IEndpointRouteBuilder;
using RedBamboo.AppHost.Auth;
using RedCompute.Core.Hardware;
using RedCompute.Core.Nodes;
using RedCompute.NodeLink;
using RedCompute.NodeLink.Noise;

namespace RedCompute.App.Services.Nodes;

/// <summary>Why NodeHub refused an upgrade: an HTTP status and a stable code, before any WebSocket is accepted.</summary>
public sealed record NodeHubRefusal(int Status, string Code, string Message)
{
    /// <summary>Sent as <c>Retry-After</c>: the refusal is not a verdict, the node should dial again.</summary>
    public TimeSpan? RetryAfter { get; init; }
}

/// <summary>The upgrade NodeHub accepted: the node the kernel bound to it, and the token that did.</summary>
public sealed record NodeHubAdmission(string NodeId, string OwnerId, string ExecutionId, DateTimeOffset ExpiresAt);

/// <summary>One <c>POST /nodes/{id}/diag/echo</c> run (design 7.2).</summary>
public sealed record NodeEchoResult(long Bytes, long FramesSent, long FramesReceived, long Duplicates, long OutOfOrder,
    double? RttMs, double DurationMs, double MebibytesPerSecond);

/// <summary>A diagnostics request NodeHub cannot run: <c>node_offline</c>, <c>feature_not_supported</c>, <c>echo_timeout</c>…</summary>
public sealed class NodeHubException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public sealed class NodeHubOptions
{
    public LinkOptions Link { get; init; } = LinkOptions.Default;

    /// <summary>The oldest agent allowed to link, and the one the Nodes tab recommends.</summary>
    public AgentPolicy AgentPolicy { get; init; } = new(Minimum: "0.1.0", Recommended: "0.1.0");

    public TimeProvider Time { get; init; } = TimeProvider.System;

    /// <summary>
    /// Asked when a node the registry does not know links (it may have paired since the last sync): reads
    /// the kernel's list from a read that starts after the call; true when the registry holds the kernel's
    /// answer. Only that answer can make a node <c>node_unknown</c> (a verdict: the agent forgets its
    /// pairing); false, a failure or no resync at all is a 503 the node retries.
    /// </summary>
    public Func<CancellationToken, Task<bool>>? Resync { get; init; }

    /// <summary>
    /// RedCompute's static link key (Phase 4 P0.5). With it, NodeHub answers a node's Noise handshake and
    /// refuses plaintext from a node whose link must be encrypted; a node that has never pinned the key
    /// still links in plaintext, as before. Null: every link is plaintext (no handshake is answered).
    /// </summary>
    public NodeLinkKeyStore? LinkKeys { get; init; }
}

/// <summary>
/// <c>GET /nodes/hub</c> (design 5.2): the one door through which a node's link reaches RedCompute.
/// The kernel authenticates the node (its signed assertion), mints a 60-second, single-use node-link
/// execution identity and opens this WebSocket on loopback. NodeHub accepts only that identity:
/// <list type="bullet">
/// <item>a verified execution identity (never <c>LocalDefault</c>, a user or browser badge, or a service token);</item>
/// <item>app <c>leaf-node-ingress</c> (never another app's execution token, nor RedCompute's long-lived
/// session tokens), actor kind <c>node</c> with a node id, beneficiary the node's owner;</item>
/// <item>unexpired, with a lifetime of at most <see cref="MaxTokenLifetime"/>, and an execution id not seen before;</item>
/// <item>from a loopback peer with no forwarding or tunnel header;</item>
/// <item>for a node the registry knows and has not revoked.</item>
/// </list>
/// The actor id is the link's node identity, the only way a link gets one; the node's hello must name
/// the same node (else 4400). Everything after the upgrade is <see cref="LinkServer"/>.
/// </summary>
public sealed partial class NodeHub : IAsyncDisposable
{
    public const string Route = "/nodes/hub";
    public const string IngressAppId = "leaf-node-ingress";
    public const string NodeActorKind = "node";

    /// <summary>The kernel mints 60 s tokens (design 5.2); anything living longer than this is not one of them.</summary>
    public static readonly TimeSpan MaxTokenLifetime = TimeSpan.FromSeconds(60);

    /// <summary>What a 503 <c>node_registry_unavailable</c> asks the node to wait before dialling again.</summary>
    public static readonly TimeSpan RegistryRetryAfter = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The features this server speaks: Phase 3's (design 4.9), and Phase 4's runtime methods and forwards (design 2.1).
    /// A link speaks what both sides list, so an older agent simply lacks the Phase 4 ones (and is not eligible).
    /// </summary>
    public static readonly IReadOnlyList<string> Features =
        [LinkFeatures.DescribeV1, LinkFeatures.HardwareV1, LinkFeatures.CliV1, LinkFeatures.DiagEcho,
         LinkFeatures.RuntimeV1, LinkFeatures.ForwardHttpV1, LinkFeatures.ForwardWsV1];

    public const string DescribeMethod = "node.describe";
    public const string HardwareMethod = "node.hardware";
    public const string CliMethod = "node.cli";
    public const string ReportMethod = "node.report";
    public const string ByeMethod = "link.bye";
    public const string EchoOpenMethod = "diag.echo.open";

    /// <summary>The largest echo run (the acceptance sends 256 MiB).</summary>
    public const long MaxEchoBytes = 1L << 30;

    /// <summary>The largest report a node may push; a bigger one is dropped (design 6.1: report 64 KiB).</summary>
    public const int MaxReportBytes = 64 * 1024;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static readonly string[] ForwardingHeaderPrefixes = ["X-Forwarded-", "X-Original-", "Cf-"];
    private static readonly string[] ForwardingHeaders = ["Forwarded", "X-Real-IP", "Via", "True-Client-IP"];

    private readonly NodeRegistry _registry;
    private readonly NodeHubOptions _options;
    private readonly Action<string> _log;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _seenExecutions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _byeReasons = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, bool> _lastConnectionEncrypted = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, RemoteExecutionHost> _hosts = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _life = new();

    public NodeHub(NodeRegistry registry, Action<string> log, NodeHubOptions? options = null)
    {
        _registry = registry;
        _log = log;
        _options = options ?? new NodeHubOptions();
        ServerEpoch = Guid.NewGuid().ToString("N");
        Server = new LinkServer(new LinkServerOptions
        {
            ServerEpoch = ServerEpoch,
            HandlerFactory = nodeId => new HubLinkHandler(this, nodeId),
            Features = Features,
            AgentPolicy = _options.AgentPolicy,
            Link = _options.Link,
            Secure = _options.LinkKeys is null ? null : SecureAsync,
        });
        Server.Connected += OnConnected;
        Server.Disconnected += OnDisconnected;
        _registry.StatusChanged += OnStatusChanged;
        _registry.AttachRemoteHosts(RemoteHost);
    }

    // ================================================================= remote execution hosts (P6)

    /// <summary>
    /// A remote node's execution host (design 2.4): one per node for the life of this run, its forward and runtime calls
    /// always on the node's current link session (none while it is offline). The registry gives it out only for an
    /// eligible node (<see cref="NodeRegistry.HostFor"/>).
    /// </summary>
    public RemoteExecutionHost RemoteHost(string nodeId) => _hosts.GetOrAdd(nodeId, id =>
    {
        LinkSession? Current() => Server.FindSession(id) is { IsAttached: true } session ? session : null;
        return new RemoteExecutionHost(id, () => _registry.Find(id), new NodeLink.Forward.ForwardClient(Current),
            new RemoteHostRuntimes(id, _registry, Current));
    });

    /// <summary>
    /// How long a node's <c>runtime.changed</c> pushes are gathered before they are applied (review P4-P7, item 8): a node
    /// that floods them costs one registry update, one <c>node.updated</c> and one deferred cache write per node per
    /// window, the latest state of each runtime winning.
    /// </summary>
    internal TimeSpan RuntimeChangeWindow { get; init; } = TimeSpan.FromMilliseconds(100);

    private readonly ConcurrentDictionary<string, RuntimeChanges> _runtimeChanges = new(StringComparer.Ordinal);

    /// <summary>One node's pending <c>runtime.changed</c>: the latest per runtime id, applied once per window.</summary>
    private sealed class RuntimeChanges(NodeHub hub, string nodeId)
    {
        private readonly object _gate = new();
        private Dictionary<string, PluginSdk.Hosting.HostRuntime> _pending = new(StringComparer.Ordinal);
        private bool _scheduled;

        public void Add(PluginSdk.Hosting.HostRuntime runtime)
        {
            lock (_gate)
            {
                // Bounded as the registry is: a node reports at most MaxRuntimes runtimes.
                if (_pending.Count >= RemoteRuntimeReport.MaxRuntimes && !_pending.ContainsKey(runtime.Id)) return;
                _pending[runtime.Id] = runtime;
                if (_scheduled) return;
                _scheduled = true;
            }
            _ = ApplyLaterAsync();
        }

        /// <summary>What is pending, now (a whole list is about to replace it, and must come after it).</summary>
        public IReadOnlyList<PluginSdk.Hosting.HostRuntime> Take()
        {
            lock (_gate)
            {
                var taken = _pending.Values.ToList();
                _pending = new(StringComparer.Ordinal);
                return taken;
            }
        }

        private async Task ApplyLaterAsync()
        {
            try
            {
                await Task.Delay(hub.RuntimeChangeWindow, hub._life.Token);
            }
            catch (OperationCanceledException)
            {
                return; // RedCompute is stopping: the node reports its runtimes again at the next link-up
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            IReadOnlyList<PluginSdk.Hosting.HostRuntime> batch;
            lock (_gate)
            {
                batch = _pending.Values.ToList();
                _pending = new(StringComparer.Ordinal);
                _scheduled = false;
            }
            if (batch.Count == 0) return;
            try
            {
                hub.ApplyRuntimesNow(nodeId, batch, whole: false);
            }
            catch (Exception error)
            {
                hub._log($"[NodeHub] Applying {nodeId}'s runtime changes failed: {error.Message}");
            }
        }
    }

    /// <summary>A node reported runtimes: a whole list at once, single changes gathered per node (see <see cref="RuntimeChangeWindow"/>).</summary>
    private void ApplyRuntimes(string nodeId, IReadOnlyList<PluginSdk.Hosting.HostRuntime> runtimes, bool whole)
    {
        var changes = _runtimeChanges.GetOrAdd(nodeId, id => new RuntimeChanges(this, id));
        if (!whole)
        {
            foreach (var runtime in runtimes) changes.Add(runtime);
            return;
        }
        // What was pushed before the list answer is older than it: applied first, then replaced by the list.
        if (changes.Take() is { Count: > 0 } earlier) ApplyRuntimesNow(nodeId, earlier, whole: false);
        ApplyRuntimesNow(nodeId, runtimes, whole: true);
    }

    /// <summary>The registry keeps them, and the node's host's observers hear each one.</summary>
    private void ApplyRuntimesNow(string nodeId, IReadOnlyList<PluginSdk.Hosting.HostRuntime> runtimes, bool whole)
    {
        if (whole) _registry.ApplyRuntimes(nodeId, runtimes);
        else _registry.ApplyRuntimeChanges(nodeId, runtimes);
        if (!_hosts.TryGetValue(nodeId, out var host)) return;
        foreach (var runtime in runtimes)
            if (_registry.RuntimesOf(nodeId).FirstOrDefault(known => known.Id == runtime.Id) is { } kept)
                ((RemoteHostRuntimes)host.Runtimes).Raise(kept);
    }

    /// <summary>This run's epoch: a node tells a RedCompute restart (its session is gone) from a network drop by it.</summary>
    public string ServerEpoch { get; }

    public LinkServer Server { get; }

    // ================================================================= admission

    /// <summary>
    /// Decides an upgrade from the verified principal and the connection alone (no body, no query).
    /// Returns the admission or the refusal; a refused request never reaches the WebSocket accept.
    /// The execution id is consumed only when every identity check passed.
    /// </summary>
    public async Task<(NodeHubAdmission? Admission, NodeHubRefusal? Refusal)> AdmitAsync(HttpContext context, CancellationToken ct)
    {
        if (RefusePeer(context) is { } peer) return (null, peer);
        var (identity, refusal) = ReadIdentity(context, _options.Time.GetUtcNow());
        if (refusal is not null) return (null, refusal);
        var (nodeId, ownerId, executionId, expiresAt) = identity!.Value;

        if (!ConsumeExecution(executionId, expiresAt))
            return (null, new(StatusCodes.Status401Unauthorized, "token_replayed",
                "This node-link identity was already used; the kernel mints one per connection."));

        if (string.Equals(nodeId, _registry.LocalNodeId, StringComparison.Ordinal))
            return (null, Unknown());

        var registryRefusal = Server.IsRevoked(nodeId) ? "node_revoked" : _registry.LinkRefusal(nodeId);
        if (registryRefusal == "node_unknown")
        {
            // Paired since the last sync, or the registry has not caught up: only the kernel's own answer,
            // read after this request arrived, may say the node is unknown (the agent then forgets its key).
            var answered = false;
            if (_options.Resync is { } resync)
                try
                {
                    answered = await resync(ct);
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    _log($"[NodeHub] Resync for {nodeId} failed: {error.Message}");
                }
            if (!answered)
                return (null, new(StatusCodes.Status503ServiceUnavailable, "node_registry_unavailable",
                    "RedCompute cannot check this node with the kernel right now; dial again.") { RetryAfter = RegistryRetryAfter });
            registryRefusal = Server.IsRevoked(nodeId) ? "node_revoked" : _registry.LinkRefusal(nodeId);
        }
        if (registryRefusal == "node_revoked")
            return (null, new(StatusCodes.Status403Forbidden, "node_revoked", "This node was revoked."));
        if (registryRefusal is not null)
            return (null, Unknown());
        // Fail closed: a node whose owner the kernel did not say cannot be matched to the identity's beneficiary.
        if (_registry.Find(nodeId) is not { OwnerUserId: { } owner } || string.IsNullOrWhiteSpace(owner))
            return (null, new(StatusCodes.Status403Forbidden, "node_owner_unknown", "The kernel did not say who owns this node."));
        if (!string.Equals(owner, ownerId, StringComparison.OrdinalIgnoreCase))
            return (null, new(StatusCodes.Status403Forbidden, "node_owner_mismatch", "The identity's beneficiary is not the node's owner."));

        return (new NodeHubAdmission(nodeId, ownerId, executionId, expiresAt), null);
    }

    private static NodeHubRefusal Unknown() => new(StatusCodes.Status404NotFound, "node_unknown", "No paired node has this id.");

    // ================================================================= end-to-end encryption (P0.5)

    /// <summary>
    /// The link's first message, before the hello (design 1b.7, 1b.11). A Noise handshake is answered with RedCompute's
    /// link key once the node's identity binding verifies against RedCompute's own pin of its identity key, taken on
    /// first use from the kernel's list and never replaced by a later one (4497 when it does not verify; 1012 while no
    /// list has named a key yet, so the node retries). Plaintext is accepted only from a node that is not encrypted for
    /// good (else 4496). A handshake for a key RedCompute does not hold is 4495. A trust record that does not open, or
    /// any fault here, is 1011: the server's problem, never a verdict on the node.
    /// <para>
    /// Message 1 can be replayed, so nothing is recorded when it is answered: the node is marked encrypted, and the use
    /// of a previous key logged, only when its first record opens (<see cref="NoiseLinkTransport.FirstRecordOpened"/>).
    /// </para>
    /// </summary>
    private async Task<ILinkTransport> SecureAsync(ILinkTransport transport, string nodeId, CancellationToken ct)
    {
        var security = _registry.LinkSecurity(nodeId);
        if (security.TrustUnreadable)
            throw new LinkProtocolException(LinkCloseCodes.InternalError,
                $"{NoiseLink.IdentityUnverifiable}: RedCompute's trust record for this node does not open.");
        IReadOnlyList<NoiseKeyPair> keys;
        try
        {
            keys = _options.LinkKeys!.Keys();
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            // Never a new key in its place (that would orphan every pinned node): handshakes get 4495 until it opens.
            _log($"[NodeHub] The node link key in {_options.LinkKeys!.Directory} does not open: {error.Message}");
            keys = [];
        }
        var result = await NoiseLink.AcceptAsync(transport, new NoiseResponderOptions
        {
            NodeId = nodeId,
            LocalStatics = keys,
            AllowPlaintext = _ => ValueTask.FromResult(!security.E2eRequired),
            Verify = (binding, payload, _) => security.IdentitySpki is { } spki
                ? ValueTask.FromResult(NoiseBinding.Verify(spki, binding, payload))
                : throw new LinkProtocolException(LinkCloseCodes.ServiceRestart,
                    "link_identity_unavailable: the kernel has not listed this node's identity key yet."),
        }, ct);
        if (result.Transport is NoiseLinkTransport sealedLink)
        {
            var keyIndex = result.Accepted!.ServerKeyIndex;
            sealedLink.FirstRecordOpened = () =>
            {
                _lastConnectionEncrypted[nodeId] = true;
                _registry.MarkSeenEncrypted(nodeId);
                if (keyIndex > 0)
                    _log($"[NodeHub] {nodeId} linked with the previous link key; it needs the current one before the grace ends.");
            };
        }
        else
        {
            _lastConnectionEncrypted[nodeId] = false;
        }
        return result.Transport;
    }

    /// <summary>
    /// <c>GET /nodes/link-key</c>: the current link key for the kernel to hand a pairing node (K0). Kernel only
    /// (<c>service:redleaf</c>), hidden from <c>/discover</c>. 503 <c>link_key_unavailable</c> when the key file does not open.
    /// </summary>
    public const string LinkKeyRoute = "/nodes/link-key";

    /// <summary>
    /// RedCompute's current link key as <c>GET /nodes</c> shows it to the person adding a node (review item 2): read
    /// from RedCompute's own key file, so the fingerprint to compare with <c>leaf-node status</c> does not come only from
    /// the kernel's pair response. Null before the key exists (it is never created for this) or when it does not open.
    /// </summary>
    public NodeLinkKeyView? LinkKeyIfCreated() => _options.LinkKeys?.DescribeIfCreated();

    public static IEndpointConventionBuilder MapLinkKeyRoute(IEndpointRouteBuilder app, NodeLinkKeyStore keys)
        => app.MapGet(LinkKeyRoute, (HttpContext context) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (!Api.Endpoints.SettingsEndpoints.IsRedLeafService(context.User))
                return Results.Json(new { error = "service_only", message = "Only the kernel reads the node link key." },
                    statusCode: StatusCodes.Status403Forbidden);
            try
            {
                return Results.Json(keys.Describe(), Json);
            }
            catch (Exception error) when (error is System.Security.Cryptography.CryptographicException or IOException
                                              or JsonException or FormatException or UnauthorizedAccessException)
            {
                return Results.Json(new { error = "link_key_unavailable", message = "RedCompute's node link key does not open." },
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        }).ExcludeFromDescription();

    /// <summary>Only a direct loopback peer, with nothing describing another client in front of it.</summary>
    internal static NodeHubRefusal? RefusePeer(HttpContext context)
    {
        var remote = context.Connection.RemoteIpAddress;
        if (remote is null || !IPAddress.IsLoopback(remote))
            return new(StatusCodes.Status403Forbidden, "loopback_required", "NodeHub accepts only the kernel's loopback hop.");
        foreach (var header in context.Request.Headers.Keys)
            if (ForwardingHeaders.Contains(header, StringComparer.OrdinalIgnoreCase)
                || ForwardingHeaderPrefixes.Any(prefix => header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
                return new(StatusCodes.Status403Forbidden, "loopback_required",
                    $"NodeHub refuses a forwarded request ({header}); the kernel strips every forwarding header.");
        return null;
    }

    /// <summary>The node-link identity the kernel minted, or why the principal is not one.</summary>
    internal static ((string NodeId, string OwnerId, string ExecutionId, DateTimeOffset ExpiresAt)? Identity, NodeHubRefusal? Refusal)
        ReadIdentity(HttpContext context, DateTimeOffset now)
    {
        static NodeHubRefusal Required(string message)
            => new(StatusCodes.Status403Forbidden, "node_identity_required", message);

        var user = context.User;
        if (user.Identity?.IsAuthenticated != true
            || string.Equals(user.Identity.AuthenticationType, ConfidentialResourcePolicy.LocalDefaultAuthenticationType, StringComparison.Ordinal))
            return (null, new(StatusCodes.Status401Unauthorized, "node_identity_required",
                "NodeHub needs the kernel's node-link identity; the anonymous local identity is refused."));

        var execution = ConfidentialResourcePolicy.ExecutionIdentity(context);
        if (execution is null && ExecutionIdentityClaims.TryRead(user, out var read, out _)) execution = read;
        if (execution is null)
            return (null, Required("NodeHub accepts only an execution identity; user, browser and service badges are refused."));

        if (!string.Equals(execution.App.Id, IngressAppId, StringComparison.Ordinal)
            || !string.Equals(user.FindFirst("client_id")?.Value, IngressAppId, StringComparison.Ordinal))
            return (null, Required($"Only the kernel's {IngressAppId} identity opens a node link."));
        if (!string.Equals(execution.Actor.Kind, NodeActorKind, StringComparison.Ordinal) || !KernelNode.IsNodeId(execution.Actor.Id))
            return (null, Required("The identity's actor must be a node."));
        var subject = user.FindFirst("sub")?.Value;
        if (string.IsNullOrWhiteSpace(subject) || subject.StartsWith("service:", StringComparison.OrdinalIgnoreCase)
            || subject is "system" or "local-user"
            || !execution.Beneficiary.Kind.Equals("user", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(execution.Beneficiary.Id, subject, StringComparison.OrdinalIgnoreCase))
            return (null, Required("A node link acts for the node's owner; this identity names no owner."));

        if (ExpiresAt(user) is not { } expiresAt)
            return (null, Required("The identity has no expiry."));
        if (expiresAt <= now)
            return (null, new(StatusCodes.Status401Unauthorized, "token_expired", "The node-link identity expired."));
        if (expiresAt - now > MaxTokenLifetime)
            return (null, Required("The identity lives too long to be the kernel's single-use node-link identity."));

        var jti = user.FindFirst("jti")?.Value;
        if (jti is not null && !string.Equals(jti, execution.ExecutionId, StringComparison.OrdinalIgnoreCase))
            return (null, Required("The token id does not match its execution id."));

        return ((execution.Actor.Id, subject, execution.ExecutionId, expiresAt), null);
    }

    private static DateTimeOffset? ExpiresAt(ClaimsPrincipal user)
        => long.TryParse(user.FindFirst("exp")?.Value, out var seconds) ? DateTimeOffset.FromUnixTimeSeconds(seconds) : null;

    /// <summary>True the first time an execution id is seen; remembered until its token expires (plus skew).</summary>
    private bool ConsumeExecution(string executionId, DateTimeOffset expiresAt)
    {
        var now = _options.Time.GetUtcNow();
        if (_seenExecutions.Count > 256)
            foreach (var (id, until) in _seenExecutions)
                if (until < now) _seenExecutions.TryRemove(id, out _);
        return _seenExecutions.TryAdd(executionId, expiresAt + TimeSpan.FromMinutes(2));
    }

    /// <summary>
    /// Maps <see cref="Route"/> straight on the app, outside the endpoint registry and excluded from
    /// description: the kernel's internal hop never appears in <c>/discover</c> or OpenAPI.
    /// </summary>
    public static IEndpointConventionBuilder MapRoute(IEndpointRouteBuilder app, NodeHub hub)
        => app.Map(Route, hub.HandleAsync).ExcludeFromDescription();

    /// <summary>The route: admits, accepts the WebSocket (sub-protocol <c>leaf-node.v1</c>, no compression), runs the link.</summary>
    public async Task HandleAsync(HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        var (admission, refusal) = await AdmitAsync(context, context.RequestAborted);
        if (refusal is not null)
        {
            if (refusal.Status != StatusCodes.Status404NotFound)
                _log($"[NodeHub] Refused an upgrade: {refusal.Code}");
            await WriteRefusalAsync(context, refusal);
            return;
        }
        if (!context.WebSockets.IsWebSocketRequest)
        {
            await WriteRefusalAsync(context, new(StatusCodes.Status400BadRequest, "websocket_required", "NodeHub speaks only WebSocket."));
            return;
        }

        var subProtocol = context.WebSockets.WebSocketRequestedProtocols.Contains(LinkProtocol.SubProtocol)
            ? LinkProtocol.SubProtocol
            : null;
        using var socket = await context.WebSockets.AcceptWebSocketAsync(new WebSocketAcceptContext
        {
            SubProtocol = subProtocol,
            DangerousEnableCompression = false,
            KeepAliveInterval = TimeSpan.Zero,
        });
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, _life.Token);
        await using var transport = new WebSocketLinkTransport(socket);
        try
        {
            await Server.RunAsync(transport, admission!.NodeId, linked.Token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception error) when (error is WebSocketException or LinkClosedException)
        {
        }
    }

    /// <summary>
    /// The refusal's code also goes in this header: the kernel's ingress relays NodeHub's refusal to the node
    /// unchanged (status, this header, <c>Date</c>, <c>Retry-After</c>), and the node reads its verdict from it.
    /// </summary>
    public const string ErrorHeader = "X-Leaf-Node-Error";

    private static Task WriteRefusalAsync(HttpContext context, NodeHubRefusal refusal)
    {
        context.Response.StatusCode = refusal.Status;
        context.Response.Headers[ErrorHeader] = refusal.Code;
        if (refusal.RetryAfter is { } retryAfter)
            context.Response.Headers.RetryAfter = ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture);
        return context.Response.WriteAsJsonAsync(new { error = refusal.Code, message = refusal.Message });
    }

    // ================================================================= link events

    private void OnConnected(string nodeId, LinkSession session, bool resumed)
    {
        // Revoked in the registry (the cache, a sync) since admission: the link must not stand.
        if (Server.IsRevoked(nodeId) || _registry.LinkRefusal(nodeId) == "node_revoked")
        {
            _ = ObserveAsync(Server.RevokeAsync(nodeId), nodeId);
            return;
        }
        AttachToRegistry(nodeId, session);
        _log($"[NodeHub] {nodeId} linked ({(resumed ? "resumed" : "new session")} {session.LinkSessionId})");
    }

    private void AttachToRegistry(string nodeId, LinkSession session)
    {
        var now = _options.Time.GetUtcNow();
        _registry.LinkUp(nodeId, new NodeLinkState(session.LinkSessionId, now, now, session.Rtt?.TotalMilliseconds,
            session.ClockOffset?.TotalMilliseconds, LinkProtocol.Current, session.Features, null, false, null,
            _lastConnectionEncrypted.TryGetValue(nodeId, out var encrypted) && encrypted));
        _ = Task.Run(() => LearnAsync(nodeId, session));
    }

    private async Task ObserveAsync(Task revoke, string nodeId)
    {
        try { await revoke; }
        catch (Exception error) { _log($"[NodeHub] Closing {nodeId}'s link failed: {error.Message}"); }
    }

    private void OnDisconnected(string nodeId, LinkSession session, LinkDisconnect ended)
    {
        // A newer connection already took over this node (superseded, or resumed elsewhere): nothing went down.
        if (Server.FindSession(nodeId) is { IsAttached: true }) return;
        var reason = _byeReasons.TryRemove(session.LinkSessionId, out var bye) ? bye : ended.Code switch
        {
            LinkCloseCodes.Timeout => "heartbeat_timeout",
            LinkCloseCodes.Superseded => "superseded",
            LinkCloseCodes.Revoked => "revoked",
            LinkCloseCodes.ServiceRestart => "server_restart",
            LinkCloseCodes.RateLimited => "rate_limited",
            LinkCloseCodes.ProtocolError => "protocol_error",
            _ => "link_closed",
        };
        _registry.LinkDown(nodeId, session.LinkSessionId, reason);
        _log($"[NodeHub] {nodeId} link closed ({ended.Code} {reason}) after {ended.UpFor.TotalSeconds:0}s");
    }

    private void OnStatusChanged(NodeStatusChange change)
    {
        if (change.Node.Kind != NodeKind.Remote) return;
        var nodeId = change.Node.Id;
        // Only the kernel's explicit revocation ends the link (design 3.5). LinkServer remembers the node as
        // revoked before RevokeAsync first yields, so no connection mid-handshake slips past it.
        if (change.Node.Status == NodeStatus.Revoked)
        {
            _ = ObserveAsync(Server.RevokeAsync(nodeId), nodeId);
            return;
        }
        // A node the registry forgot (absent from one sync) and learns again: its link never closed.
        if (change.Previous is null && Server.FindSession(nodeId) is { IsAttached: true } session)
            AttachToRegistry(nodeId, session);
    }

    /// <summary>What a freshly linked node is: its describe, then its CLIs and one hardware sample.</summary>
    private async Task LearnAsync(string nodeId, LinkSession session)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_life.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        var ct = timeout.Token;
        try
        {
            if (session.HasFeature(LinkFeatures.DescribeV1))
            {
                var describe = await session.CallAsync(DescribeMethod, null, ct);
                var parsed = ParseDescribe(describe);
                _registry.ApplyDescribe(nodeId, parsed);
                if (parsed.AgentVersion is { } version && AgentVersions.Compare(version, _options.AgentPolicy.Recommended) < 0
                    && _registry.Find(nodeId)?.Link is { } link && link.LinkSessionId == session.LinkSessionId)
                    _registry.LinkUp(nodeId, link with { UpdateAvailable = true });
            }
            IReadOnlyList<NodeCliReport>? clis = null;
            HardwareSnapshot? hardware = null;
            if (session.HasFeature(LinkFeatures.CliV1))
                clis = ParseClis((await session.CallAsync(CliMethod, null, ct)).TryGetProperty("clis", out var list) ? list : default);
            if (session.HasFeature(LinkFeatures.HardwareV1))
                hardware = ParseHardware(nodeId, await session.CallAsync(HardwareMethod, null, ct));
            _registry.ApplyReport(nodeId, clis, hardware, session.Rtt?.TotalMilliseconds, session.ClockOffset?.TotalMilliseconds);
            // The node pushes every change after this (runtime.changed): the server never polls.
            if (session.HasFeature(LinkFeatures.RuntimeV1))
                ApplyRuntimes(nodeId, RemoteRuntimeReport.ParseList(await session.CallAsync(RemoteHostRuntimes.ListMethod, null, ct)), whole: true);
        }
        catch (Exception error) when (error is LinkRpcException or LinkClosedException or OperationCanceledException
                                          or InvalidOperationException or ObjectDisposedException or JsonException)
        {
            if (!_life.IsCancellationRequested) _log($"[NodeHub] Could not read {nodeId}'s description: {error.Message}");
        }
    }

    internal static NodeDescribe ParseDescribe(JsonElement describe)
    {
        string? Text(JsonElement parent, string name, int max = 200)
            => parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? Clip(value.GetString(), max)
                : null;
        var platform = describe.ValueKind == JsonValueKind.Object && describe.TryGetProperty("platform", out var p) ? p : default;
        var agent = describe.ValueKind == JsonValueKind.Object && describe.TryGetProperty("agent", out var a) ? a : default;
        var version = Text(agent, "version", 64);
        return new NodeDescribe(Text(platform, "os"), Text(platform, "arch"), Text(platform, "osDescription"),
            Text(describe, "machineName"), Text(describe, "processor"),
            AgentVersions.IsValid(version) ? version : null, Text(describe, "credentialStore", 32),
            KernelNode.Token(Text(platform, "chassis", 40)));
    }

    internal static IReadOnlyList<NodeCliReport>? ParseClis(JsonElement clis)
    {
        if (clis.ValueKind != JsonValueKind.Array) return null;
        var result = new List<NodeCliReport>();
        foreach (var cli in clis.EnumerateArray().Take(32))
        {
            if (cli.ValueKind != JsonValueKind.Object) continue;
            string? Text(string name, int max) => cli.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? Clip(value.GetString(), max)
                : null;
            bool Flag(string name) => cli.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
            if (Text("name", 32) is not { } name) continue;
            result.Add(new NodeCliReport(name, Flag("installed"), Text("version", 64), Flag("signedIn"), Text("authMethod", 32), Text("path", 400)));
        }
        return result;
    }

    internal static HardwareSnapshot? ParseHardware(string nodeId, JsonElement hardware)
    {
        if (hardware.ValueKind != JsonValueKind.Object) return null;
        try
        {
            var snapshot = hardware.Deserialize<HardwareSnapshot>(Json);
            if (snapshot is null) return null;
            snapshot.NodeId = nodeId;
            if (snapshot.Gpus.Count > 16) snapshot.Gpus = snapshot.Gpus.Take(16).ToList();
            return snapshot;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Clip(string? text, int max)
        => string.IsNullOrWhiteSpace(text) ? null : text.Length <= max ? text.Trim() : text[..max].Trim();

    [GeneratedRegex("^[a-z][a-z0-9_-]{0,31}$")]
    private static partial Regex ByeReasonPattern();

    /// <summary>The status reason a node's <c>link.bye {reason}</c> leaves.</summary>
    internal static string ByeReason(string? reason) => reason switch
    {
        "unpair" or "unpaired" => "unpaired_by_node",
        { } text when ByeReasonPattern().IsMatch(text) => "agent_" + text.Replace('-', '_'),
        _ => "agent_closed",
    };

    /// <summary>
    /// The server's half of a node's link: reports and runtime changes in, a goodbye, nothing else. The node opens no
    /// channel to the server (the base refuses every one): forwards run server to node only.
    /// </summary>
    private sealed class HubLinkHandler(NodeHub hub, string nodeId) : LinkHandler
    {
        public override ValueTask<JsonNode?> OnRequestAsync(LinkSession session, string method, JsonElement parameters, CancellationToken ct)
        {
            if (method != ByeMethod) return base.OnRequestAsync(session, method, parameters, ct);
            var reason = parameters.ValueKind == JsonValueKind.Object && parameters.TryGetProperty("reason", out var value)
                         && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
            hub._byeReasons[session.LinkSessionId] = ByeReason(reason);
            return ValueTask.FromResult<JsonNode?>(new JsonObject { ["ok"] = true });
        }

        public override ValueTask OnNotificationAsync(LinkSession session, string method, JsonElement parameters, CancellationToken ct)
        {
            if (method == RemoteHostRuntimes.ChangedMethod && session.HasFeature(LinkFeatures.RuntimeV1))
            {
                if (parameters.ValueKind == JsonValueKind.Object && parameters.GetRawText().Length <= RemoteRuntimeReport.MaxEntryBytes
                    && RemoteRuntimeReport.Parse(parameters) is { } runtime)
                    hub.ApplyRuntimes(nodeId, [runtime], whole: false);
                else
                    hub._log($"[NodeHub] Dropped a malformed or oversized runtime.changed from {nodeId}");
                return ValueTask.CompletedTask;
            }
            if (method != ReportMethod || parameters.ValueKind != JsonValueKind.Object) return ValueTask.CompletedTask;
            if (parameters.GetRawText().Length > MaxReportBytes)
            {
                hub._log($"[NodeHub] Dropped an oversized report from {nodeId}");
                return ValueTask.CompletedTask;
            }
            var clis = parameters.TryGetProperty("clis", out var list) ? ParseClis(list) : null;
            var hardware = parameters.TryGetProperty("hardware", out var sample) ? ParseHardware(nodeId, sample) : null;
            hub._registry.ApplyReport(nodeId, clis, hardware, session.Rtt?.TotalMilliseconds, session.ClockOffset?.TotalMilliseconds);
            return ValueTask.CompletedTask;
        }
    }

    // ================================================================= revocation and diagnostics

    /// <summary>The kernel revoked the node: the registry marks it (terminal) and its link closes 4403 at once.</summary>
    public async Task<NodeDescriptor> RevokeAsync(string nodeId, string reason)
    {
        // The link server first: from here no connection of this node can complete, then the registry.
        var closing = Server.RevokeAsync(nodeId);
        var node = _registry.MarkRevoked(nodeId, reason);
        await closing;
        return node;
    }

    /// <summary>
    /// Streams <paramref name="bytes"/> through the node's <c>diag.echo</c> channel and checks what comes
    /// back: every 8-byte word carries its index, so a word seen twice is a duplicate and a skipped one is
    /// out of order. Throws <see cref="NodeHubException"/> when the node is offline or lacks the feature.
    /// </summary>
    public async Task<NodeEchoResult> EchoAsync(string nodeId, long bytes, TimeSpan timeout, CancellationToken ct)
    {
        if (bytes is <= 0 or > MaxEchoBytes)
            throw new NodeHubException("invalid_bytes", $"bytes must be between 1 and {MaxEchoBytes}.");
        bytes = (bytes + 7) / 8 * 8;
        if (Server.FindSession(nodeId) is not { IsAttached: true } session)
            throw new NodeHubException("node_offline", "The node's link is down.");
        if (!session.HasFeature(LinkFeatures.DiagEcho))
            throw new NodeHubException("feature_not_supported", "The node does not speak diag.echo.");

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct, _life.Token);
        deadline.CancelAfter(timeout);
        var token = deadline.Token;
        var watch = Stopwatch.StartNew();
        try
        {
            await session.CallAsync(EchoOpenMethod, new JsonObject { ["bytes"] = bytes }, token);
            var receiver = new EchoReceiver(bytes);
            var channel = await session.OpenChannelAsync(new ChannelOpenRequest(ChannelClasses.DiagEcho.Name), receiver, token);
            const int chunk = 8 * 1024;
            var buffer = new byte[chunk];
            long sent = 0, frames = 0, word = 0;
            while (sent < bytes)
            {
                var size = (int)Math.Min(chunk, bytes - sent);
                for (var offset = 0; offset < size; offset += 8) BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(offset, 8), word++);
                await channel.SendAsync(buffer.AsMemory(0, size), token);
                sent += size;
                frames++;
            }
            await receiver.Done.WaitAsync(token);
            channel.Close();
            watch.Stop();
            var seconds = Math.Max(watch.Elapsed.TotalSeconds, 1e-6);
            return new NodeEchoResult(bytes, frames, receiver.Frames, receiver.Duplicates, receiver.OutOfOrder,
                session.Rtt?.TotalMilliseconds, watch.Elapsed.TotalMilliseconds, Math.Round(bytes / 1048576.0 / seconds, 2));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && !_life.IsCancellationRequested)
        {
            throw new NodeHubException("echo_timeout", $"The echo did not complete within {timeout.TotalSeconds:0} s.");
        }
        catch (Exception error) when (error is LinkRpcException or LinkChannelResetException or LinkClosedException)
        {
            throw new NodeHubException("echo_failed", error.Message);
        }
    }

    /// <summary>Counts what the node echoes back, word by word, across any frame boundaries.</summary>
    private sealed class EchoReceiver(long expected) : IChannelHandler
    {
        private readonly TaskCompletionSource _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly byte[] _partial = new byte[8];
        private int _partialLength;
        private long _received;
        private long _nextWord;

        public Task Done => _done.Task;
        public long Frames { get; private set; }
        public long Duplicates { get; private set; }
        public long OutOfOrder { get; private set; }

        public ValueTask OnDataAsync(LinkChannelHandle channel, ChannelData data, CancellationToken ct)
        {
            Frames++;
            var span = data.Payload.Span;
            _received += span.Length;
            while (span.Length > 0)
            {
                var take = Math.Min(8 - _partialLength, span.Length);
                span[..take].CopyTo(_partial.AsSpan(_partialLength));
                _partialLength += take;
                span = span[take..];
                if (_partialLength < 8) break;
                _partialLength = 0;
                var word = BinaryPrimitives.ReadInt64LittleEndian(_partial);
                if (word < _nextWord) Duplicates++;
                else
                {
                    if (word > _nextWord) OutOfOrder++;
                    _nextWord = word + 1;
                }
            }
            if (_received >= expected) _done.TrySetResult();
            return ValueTask.CompletedTask;
        }

        public ValueTask OnRemoteCloseAsync(LinkChannelHandle channel, CancellationToken ct)
        {
            _done.TrySetResult();
            return ValueTask.CompletedTask;
        }

        public void OnReset(LinkChannelHandle channel, string code, string message)
            => _done.TrySetException(new LinkChannelResetException(code, message));
    }

    /// <summary>A restart closes every link with 1012; nodes redial and get <c>resumed: false</c>.</summary>
    public async Task ShutdownAsync()
    {
        if (_life.IsCancellationRequested) return;
        _life.Cancel();
        await Server.ShutdownAsync(LinkCloseCodes.ServiceRestart);
    }

    public async ValueTask DisposeAsync()
    {
        _registry.StatusChanged -= OnStatusChanged;
        await ShutdownAsync();
        await Server.DisposeAsync();
        _life.Dispose();
    }
}
