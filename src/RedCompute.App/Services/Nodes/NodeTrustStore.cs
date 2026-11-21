using System.IO;
using System.Text.Json;
using RedCompute.NodeLink.Noise;
using RedCompute.PluginSdk.Hosting;

namespace RedCompute.App.Services.Nodes;

/// <summary>
/// What RedCompute itself holds about a remote node's link security (Phase 4 design 1b.11, review item 2), apart from
/// the kernel's copy. Two things are kept:
/// <list type="bullet">
/// <item>The node's identity key, pinned on first use (TOFU) from the first kernel list that names it.</item>
/// <item>Whether its link is end-to-end encrypted for good: the kernel said <c>linkE2e: required</c>, or it linked encrypted.</item>
/// </list>
/// <para>
/// One file per node, in <c>&lt;data root&gt;/node-trust</c>, which is Durable. The node cache (<c>nodes/</c>) is rebuilt
/// from the kernel and deleted when one kernel list leaves a node out. This store is not: a forget or a rebuild never
/// resets it. So a kernel that drops a node and lists it again can neither reopen plaintext nor swap its key.
/// </para>
/// <para>
/// A later kernel list with another identity key does not replace the pin. The handshake still verifies against the
/// pinned key, so only the real node can link encrypted. The conflict is logged and shown (<c>identityPin: conflict</c>).
/// Only an explicit reset by the node's owner or an admin, in person, lets the next list pin a key anew.
/// </para>
/// <para>
/// A record that does not open fails closed. It is never overwritten, and NodeHub answers every link from that node
/// 1011 until someone fixes or removes the file.
/// </para>
/// </summary>
public sealed class NodeTrustStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly IExecutionHost _host;
    private readonly string? _directory;
    private readonly Action<string> _log;
    private readonly Func<DateTimeOffset> _clock;
    private readonly object _gate = new();
    private readonly Dictionary<string, NodeTrust> _trust = new(StringComparer.Ordinal);
    private readonly HashSet<string> _unreadable = new(StringComparer.Ordinal);

    /// <param name="directory">Where the records live; null keeps them in memory only (tests, a host without storage).</param>
    public NodeTrustStore(IExecutionHost host, string? directory, Action<string> log, Func<DateTimeOffset> clock)
    {
        _host = host;
        _directory = directory;
        _log = log;
        _clock = clock;
        Load();
    }

    public NodeTrust? Find(string nodeId)
    {
        lock (_gate) return _trust.GetValueOrDefault(nodeId);
    }

    /// <summary>True when the node's record exists but does not open: fail closed.</summary>
    public bool IsUnreadable(string nodeId)
    {
        lock (_gate) return _unreadable.Contains(nodeId);
    }

    /// <summary>
    /// What one kernel list says about the node. The first identity key it names is pinned; a different one later is a
    /// conflict, recorded and logged once per key, and never replaces the pin. <c>required</c> only ever turns on.
    /// Returns true when the record changed.
    /// </summary>
    public bool ObserveKernel(string nodeId, byte[]? identitySpki, bool e2eRequired)
    {
        lock (_gate)
        {
            if (_unreadable.Contains(nodeId)) return false;
            var before = _trust.GetValueOrDefault(nodeId) ?? new NodeTrust(nodeId);
            var after = before with { E2eRequired = before.E2eRequired || e2eRequired };
            if (identitySpki is not null)
            {
                var listed = Convert.ToBase64String(identitySpki);
                if (after.IdentitySpki is null)
                {
                    after = after with { IdentitySpki = listed, IdentityPinnedAt = _clock(), ConflictSpki = null, ConflictSeenAt = null };
                    _log($"[Nodes] Pinned the identity key of {nodeId} ({Fingerprint(identitySpki)}) on first use.");
                }
                else if (after.IdentitySpki != listed && after.ConflictSpki != listed)
                {
                    after = after with { ConflictSpki = listed, ConflictSeenAt = _clock() };
                    _log($"[Nodes] SECURITY: the kernel lists another identity key for {nodeId} ({Fingerprint(identitySpki)}) "
                         + $"than the one RedCompute pinned on {after.IdentityPinnedAt:yyyy-MM-dd} ({Fingerprint(after.IdentitySpkiBytes())}). "
                         + "The pin stays: only the node that holds the pinned key can link encrypted. If the node's key really changed, "
                         + $"its owner or an admin resets the pin in person: POST /nodes/{nodeId}/identity-pin/reset.");
                }
            }
            if (after == before) return false;
            Save(after);
            return true;
        }
    }

    /// <summary>The node linked encrypted (its first record opened): from now on plaintext from it is refused.</summary>
    public bool MarkSeenEncrypted(string nodeId)
    {
        lock (_gate)
        {
            if (_unreadable.Contains(nodeId)) return false;
            var before = _trust.GetValueOrDefault(nodeId) ?? new NodeTrust(nodeId);
            if (before.SeenEncrypted) return false;
            Save(before with { SeenEncrypted = true });
            return true;
        }
    }

    /// <summary>
    /// Clears the identity pin (and any conflict) so that the next kernel list pins a key anew. "Encrypted for good"
    /// stays: a reset never reopens plaintext. Returns false when there was no pin.
    /// </summary>
    public bool ResetIdentityPin(string nodeId, string by)
    {
        lock (_gate)
        {
            if (_unreadable.Contains(nodeId) || _trust.GetValueOrDefault(nodeId) is not { IdentitySpki: not null } before) return false;
            Save(before with { IdentitySpki = null, IdentityPinnedAt = null, ConflictSpki = null, ConflictSeenAt = null });
            _log($"[Nodes] {by} reset the identity pin of {nodeId} (was {Fingerprint(before.IdentitySpkiBytes())}); the next kernel list pins anew.");
            return true;
        }
    }

    private static string Fingerprint(byte[]? spki) => spki is null ? "none" : NoiseKeyPair.Fingerprint(spki);

    private string? PathFor(string nodeId)
        => _directory is null || !KernelNode.IsNodeId(nodeId) ? null : Path.Combine(_directory, nodeId + ".json");

    private void Save(NodeTrust trust)
    {
        _trust[trust.NodeId] = trust;
        if (PathFor(trust.NodeId) is not { } path) return;
        try
        {
            _host.Files.CreateDirectory(_host.Path(_directory!));
            _host.Files.ReplaceAtomically(_host.Path(path), JsonSerializer.Serialize(trust, Json));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Held in memory for this run either way; the next change writes it again.
            _log($"[Nodes] Could not write the trust record of {trust.NodeId}: {error.Message}");
        }
    }

    private void Load()
    {
        if (_directory is null) return;
        try
        {
            var directory = _host.Path(_directory);
            if (!_host.Files.DirectoryExists(directory)) return;
            foreach (var file in _host.Files.EnumerateFiles(directory, "node_*.json", SearchOption.TopDirectoryOnly).ToList())
            {
                var nodeId = Path.GetFileNameWithoutExtension(file.Value);
                try
                {
                    var trust = JsonSerializer.Deserialize<NodeTrust>(_host.Files.ReadAllText(file), Json)
                                ?? throw new InvalidDataException("the file holds no record");
                    if (trust.NodeId != nodeId) throw new InvalidDataException($"the record is for {trust.NodeId}");
                    if (trust.IdentitySpki is not null && trust.IdentitySpkiBytes() is null) throw new InvalidDataException("the identity key does not decode");
                    _trust[nodeId] = trust;
                }
                catch (Exception error)
                {
                    _unreadable.Add(nodeId);
                    _log($"[Nodes] SECURITY: the trust record {file} does not open ({error.GetType().Name}: {error.Message}). "
                         + "It is left as it is, and NodeHub refuses every link from that node until it is fixed or removed.");
                }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            _log($"[Nodes] Could not read the node trust records: {error.Message}");
        }
    }
}

/// <summary>The shape of <c>node-trust/&lt;id&gt;.json</c>. Keys are base64 DER SPKI.</summary>
public sealed record NodeTrust(string NodeId, string? IdentitySpki = null, DateTimeOffset? IdentityPinnedAt = null,
    bool E2eRequired = false, bool SeenEncrypted = false, string? ConflictSpki = null, DateTimeOffset? ConflictSeenAt = null,
    int Version = 1)
{
    public byte[]? IdentitySpkiBytes()
    {
        if (IdentitySpki is null) return null;
        try
        {
            return Convert.FromBase64String(IdentitySpki);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>Plaintext from this node is refused for good.</summary>
    public bool PlaintextRefused => E2eRequired || SeenEncrypted;

    /// <summary><c>pinned</c>, <c>conflict</c> (the kernel lists another key) or null (not pinned yet).</summary>
    public string? PinState => IdentitySpki is null ? null : ConflictSpki is null ? "pinned" : "conflict";
}
