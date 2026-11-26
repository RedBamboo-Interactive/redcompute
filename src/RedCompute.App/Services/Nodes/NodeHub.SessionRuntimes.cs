using System.Net;
using System.Net.Http;
using System.Security.Claims;
using System.Text.Json;
using RedCompute.Core.Nodes;
using RedCompute.Core.Providers;
using RedCompute.NodeLink;
using RedCompute.NodeLink.Noise;
using RedCompute.PluginSdk.Hosting;

namespace RedCompute.App.Services.Nodes;

public sealed partial class NodeHub
{
    internal async Task<(HttpStatusCode Status, byte[] Body)> SendSessionRuntimeCommandAsync(ClaimsPrincipal caller,
        SessionRuntimeScope scope, SessionRuntimeOperation operation, string requestId, byte[] payload, CancellationToken ct)
    {
        if (_registry.Find(scope.NodeId) is not { Kind: NodeKind.Remote } node
            || !NodeAccess.Allows(caller, node, NodeUse.Place)
            || !string.Equals(NodeAccess.Subject(caller), scope.OwnerId, StringComparison.OrdinalIgnoreCase))
            throw new NodeHubException("node_not_found", "No accessible node matches the session placement.");

        if (Server.IsRevoked(node.Id) || _registry.Placement(node) is { Eligible: false })
            throw new NodeHubException("node_unavailable", "The session node is unavailable.");

        var runtime = _registry.RuntimesOf(node.Id).FirstOrDefault(runtime => runtime.Id == scope.RuntimeId);
        if (runtime is not { Kind: SessionRuntimeProtocol.Kind, Managed: HostRuntimeManagement.Attach,
                Enabled: true, Status: BackendStatus.Running, Ports.Count: 1 }
            || !runtime.Capabilities.Contains(SessionRuntimeProtocol.Kind, StringComparer.Ordinal))
            throw new NodeHubException("session_runtime_unavailable", "The registered session runtime is unavailable.");

        if (SessionRuntimeProtocol.IsMoveOperation(operation)
            && !runtime.Capabilities.Contains(SessionRuntimeProtocol.BindingMoveCapability, StringComparer.Ordinal))
            throw new NodeHubException("session_runtime_move_unsupported", "The node agent cannot forward session move commands.");

        var keyId = Server.FindSession(node.Id)?.ControllerKeyId;
        var key = keyId is null ? null : _options.LinkKeys?.Keys()
            .FirstOrDefault(key => NodeLinkKeyInfo.For(key.PublicKey).KeyId == keyId);
        if (key is null)
            throw new NodeHubException("secure_node_link_required", "The session runtime requires an active pinned node link.");

        var expires = _options.Time.GetUtcNow().AddSeconds(SessionRuntimeGrant.MaxLifetimeSeconds);
        var grant = SessionRuntimeGrant.Issue(key, scope, operation, requestId, expires, payload);
        var network = RemoteHost(node.Id).ForRuntime(runtime.Id).Network;
        var baseUrl = $"http://127.0.0.1:{runtime.Ports[0]}";
        using var client = network.CreateServiceClient(baseUrl, TimeSpan.FromSeconds(20),
            () => SessionRuntimeProtocol.MaxExchangeBodyBytes);
        using var content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(grant));
        content.Headers.ContentType = new("application/json");
        using var response = await client.PostAsync(baseUrl + SessionRuntimeProtocol.PathFor(operation), content, ct);
        return (response.StatusCode, await response.Content.ReadAsByteArrayAsync(ct));
    }
}
