using System.Text.Json;
using System.Text.Json.Serialization;
using RedCompute.PluginSdk.Hosting;

namespace RedCompute.Node;

/// <summary>
/// <c>node.json</c> (design 3.2): the pairing (who this node is, which server it pinned, where its key
/// is) and, once the server revoked or forgot it or the node unpaired, how that ended. A node with no
/// <see cref="Pairing"/> does not dial.
/// </summary>
public sealed record NodeState
{
    public NodePairing? Pairing { get; init; }
    public NodeEnding? Ended { get; init; }

    public static NodeState Empty { get; } = new();
}

public sealed record NodePairing
{
    public required string NodeId { get; init; }
    public required string ServerOrigin { get; init; }
    public required string LinkPath { get; init; }
    public string? ServerInstallId { get; init; }
    public required string KeyRef { get; init; }
    public required string CredentialStore { get; init; }
    public required string Fingerprint { get; init; }
    public string? Name { get; init; }
    public required DateTimeOffset PairedAt { get; init; }

    /// <summary>
    /// RedCompute's link key, pinned at pairing or by <c>leaf-node repin</c> (Phase 4 P0.5c). With a pin the node
    /// always encrypts its link to this key and never sends plaintext; without one (paired before E2E, or with a
    /// kernel without K0) it links in plaintext. Only the owner's re-pin code changes it.
    /// </summary>
    public NodeLinkPin? LinkPin { get; init; }
}

/// <summary>A pinned server link key: the SEC1 point (base64url), its fingerprint and key id, when it was pinned.</summary>
/// <param name="Protocol">
/// The handshake the pin is for (review item 10). A node never dials a pinned server with any other, older or plaintext:
/// a pin for a protocol this agent does not speak means "update leaf-node", never a fallback. A node.json from before
/// this field reads as the only protocol there is.
/// </param>
public sealed record NodeLinkPin(string PublicKey, string Fingerprint, string KeyId, DateTimeOffset PinnedAt,
    string Protocol = RedCompute.NodeLink.Noise.NoiseProtocol.Name)
{
    public byte[] PublicKeyBytes()
        => new RedCompute.NodeLink.Noise.NodeLinkKeyInfo(RedCompute.NodeLink.Noise.NoiseProtocol.Name, PublicKey, Fingerprint, KeyId).PublicKeyBytes()
           ?? throw new NodeCommandException("bad_state", "The pinned server link key in node.json is not a P-256 key. Re-pin: leaf-node repin --code <code>.",
               NodeExitCodes.Configuration);
}

/// <summary>Why the node stopped being paired: <c>revoked</c>, <c>unknown</c> (the server forgot it) or <c>unpaired</c>.</summary>
public sealed record NodeEnding(string NodeId, string ServerOrigin, string Reason, DateTimeOffset At);

/// <summary>Reads and writes <c>node.json</c> atomically through the node's execution host.</summary>
public sealed class NodeStateStore(IExecutionHost host, NodePaths paths)
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly object _gate = new();

    public NodeState Load()
    {
        lock (_gate)
        {
            var file = host.Path(paths.StateFile);
            if (!host.Files.FileExists(file)) return NodeState.Empty;
            try
            {
                return JsonSerializer.Deserialize<NodeState>(host.Files.ReadAllText(file), Json) ?? NodeState.Empty;
            }
            catch (JsonException error)
            {
                throw new NodeCommandException("bad_state", $"{paths.StateFile} is not readable ({error.Message}).",
                    NodeExitCodes.Configuration, error);
            }
        }
    }

    public void Save(NodeState state)
    {
        lock (_gate)
        {
            host.Files.CreateDirectory(host.Path(paths.Data));
            host.Files.ReplaceAtomically(host.Path(paths.StateFile), JsonSerializer.Serialize(state, Json));
        }
    }
}
