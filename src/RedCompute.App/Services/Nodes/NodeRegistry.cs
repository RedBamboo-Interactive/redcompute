using System.IO;
using System.Text.Json;
using RedCompute.Core.Hardware;
using RedCompute.Core.Nodes;
using RedCompute.NodeLink;
using RedCompute.PluginSdk;
using RedCompute.PluginSdk.Hosting;
using RedCompute.PluginSdk.Hosting.Local;

namespace RedCompute.App.Services.Nodes;

/// <summary>The machine facts a node reports, read once at boot.</summary>
public sealed record NodePlatform(string Os, string OsDescription, string Architecture, string MachineName,
    string? ProcessorName, string? Chassis = null)
{
    /// <summary>This machine's facts, read through the shared local host.</summary>
    public static NodePlatform Detect() => Detect(LocalExecutionHost.Shared);

    /// <summary>The facts of the machine <paramref name="host"/> executes on.</summary>
    public static NodePlatform Detect(IExecutionHost host)
    {
        var platform = host.Platform.Info;
        return new(platform.Os, platform.OsDescription, platform.Architecture, platform.MachineName,
            host.Hardware.ProcessorName(), platform.Chassis);
    }
}

/// <summary>A node's status changed (<paramref name="Previous"/> is null for a node seen for the first time).</summary>
public sealed record NodeStatusChange(NodeDescriptor Node, NodeStatus? Previous);

/// <summary>Whether placement may choose a node, and why not (design 7.2: Phase 2's Run-on reads only this).</summary>
public sealed record NodePlacement(bool Eligible, string? Reason);

/// <summary>
/// The nodes RedCompute knows (design 7.1), from three sources:
/// <list type="bullet">
/// <item>node zero, from <c>node.json</c>, unchanged since Phase 1;</item>
/// <item>paired remote nodes, from the kernel's <c>node</c> entities (<see cref="ApplyKernelNodes"/>):
/// identity, owner, confirmation, revocation;</item>
/// <item>their live state, from NodeHub (<see cref="LinkUp"/>, <see cref="LinkDown"/>, <see cref="ApplyReport"/>).</item>
/// </list>
/// The last state of each remote node is cached in <c>&lt;data root&gt;/nodes/&lt;id&gt;.json</c> through node
/// zero's host, so an offline node still shows its last report (its runtimes included) after a restart. Since Phase 4
/// P6 <see cref="HostFor"/> gives an eligible remote node's <see cref="RemoteExecutionHost"/> (from NodeHub, which
/// <see cref="AttachRemoteHosts"/> connects) and throws <see cref="NodeUnavailableException"/> for any other.
/// </summary>
public sealed class NodeRegistry : INodeRegistry
{
    /// <summary>The cache keeps the last hardware sample, rewritten at most this often when only usage moved.</summary>
    internal static readonly TimeSpan HardwareCacheInterval = TimeSpan.FromMinutes(15);

    private static readonly JsonSerializerOptions CacheJson = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly NodePlatform _platform;
    private readonly string _version;
    private readonly DateTimeOffset _startedAt;
    private readonly Func<DateTimeOffset> _clock;
    private readonly string? _cacheDirectory;
    private readonly Action<string> _log;
    private readonly object _gate = new();
    private readonly Dictionary<string, RemoteNode> _remote = new(StringComparer.Ordinal);

    // Cache file writes (review P4-P7, item 8). Every write and delete is serialized under _cacheIo, which is taken
    // inside _gate or alone, never the other way round. A runtime change's write is deferred: snapshotted under _gate,
    // written by one drainer outside it, the latest per node only. A write older than one already done (a revoke written
    // at once after a deferred snapshot) or for a forgotten node is skipped.
    private readonly object _cacheIo = new();
    private readonly object _cacheQueue = new();
    private readonly Dictionary<string, CacheSnapshot> _cachePending = new(StringComparer.Ordinal);
    private bool _cacheDraining;
    private int _cacheWrites;

    private sealed record CacheSnapshot(RemoteNode Node, long Version, string Path, CachedRemoteNode Cache);

    /// <summary>Tests: how many cache files were written.</summary>
    internal int CacheWrites => Volatile.Read(ref _cacheWrites);

    /// <summary>Tests: waits until every deferred cache write is done.</summary>
    internal async Task FlushCacheAsync()
    {
        while (true)
        {
            lock (_cacheQueue)
                if (!_cacheDraining && _cachePending.Count == 0) return;
            await Task.Delay(10);
        }
    }
    // Node zero's look, from its kernel entity (node.json has none): null until the kernel lists it.
    private string? _localIcon;
    private string? _localColor;

    public NodeRegistry(NodeIdentityResult identity, NodePlatform platform, string version,
        DateTimeOffset startedAt, Func<DateTimeOffset>? clock = null, IExecutionHost? localHost = null,
        string? remoteCacheDirectory = null, Action<string>? log = null, string? trustDirectory = null)
        : this(identity, platform, version, startedAt, clock, localHost, remoteCacheDirectory, log, ParseCache, trustDirectory)
    {
    }

    /// <summary>Tests: <paramref name="parseCache"/> stands in for the cache file parser.</summary>
    internal NodeRegistry(NodeIdentityResult identity, NodePlatform platform, string version,
        DateTimeOffset startedAt, Func<DateTimeOffset>? clock, IExecutionHost? localHost,
        string? remoteCacheDirectory, Action<string>? log, Func<string, CachedRemoteNode?> parseCache, string? trustDirectory = null)
    {
        Identity = identity;
        _platform = platform;
        _version = version;
        _startedAt = startedAt;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        LocalHost = localHost ?? new LocalExecutionHost(identity.Identity.Id);
        _cacheDirectory = remoteCacheDirectory;
        _log = log ?? (_ => { });
        _trust = new NodeTrustStore(LocalHost, trustDirectory, _log, _clock);
        LoadCache(parseCache);
    }

    // RedCompute's own pins and "encrypted for good" (review item 2): Durable, apart from the rebuildable cache.
    private readonly NodeTrustStore _trust;

    /// <summary>Node zero's execution host: the code that ran every launch before nodes existed.</summary>
    public IExecutionHost LocalHost { get; }

    /// <summary>
    /// Opens node.json in <paramref name="directory"/> (creating it on first boot) and detects the platform.
    /// <paramref name="remoteCacheDirectory"/>, when given, is where remote nodes' last state is kept;
    /// <paramref name="trustDirectory"/> is where RedCompute's own pins of their identity keys are (Durable).
    /// </summary>
    public static NodeRegistry Open(string directory, string version, Action<string> log, string? remoteCacheDirectory = null,
        string? trustDirectory = null)
    {
        var identity = NodeIdentityStore.LoadOrCreate(directory, log);
        var host = new LocalExecutionHost(identity.Identity.Id);
        return new(identity, NodePlatform.Detect(host), version, DateTimeOffset.UtcNow, localHost: host,
            remoteCacheDirectory: remoteCacheDirectory, log: log, trustDirectory: trustDirectory);
    }

    /// <summary>The identity this run uses, and whether it is the persisted one.</summary>
    public NodeIdentityResult Identity { get; }

    public string LocalNodeId => Identity.Identity.Id;
    public string LegacyNodeId => Identity.Identity.LegacyRowsNodeId;
    public string? StampNodeId => Identity.Persisted ? LocalNodeId : null;

    /// <summary>A node's status changed: raised once per transition, never per heartbeat.</summary>
    public event Action<NodeStatusChange>? StatusChanged;

    /// <summary>A node's identity, link or CLI report changed (not raised for hardware-only reports).</summary>
    public event Action<string>? Updated;

    public NodeDescriptor Local => new(
        LocalNodeId,
        _platform.MachineName,
        NodeKind.Local,
        NodeStatus.Online,
        _platform.Os,
        _platform.OsDescription,
        _platform.Architecture,
        _platform.MachineName,
        _platform.ProcessorName,
        _version,
        _startedAt,
        _clock())
    {
        Chassis = _platform.Chassis,
        Icon = _localIcon,
        Color = _localColor,
    };

    /// <summary>Node zero first, then the remote nodes by name.</summary>
    public IReadOnlyList<NodeDescriptor> Nodes
    {
        get
        {
            List<NodeDescriptor> remote;
            lock (_gate) remote = _remote.Values.Select(Describe).ToList();
            return [Local, .. remote.OrderBy(node => node.Name, StringComparer.OrdinalIgnoreCase).ThenBy(node => node.Id, StringComparer.Ordinal)];
        }
    }

    public NodeDescriptor? Find(string nodeId)
    {
        if (string.Equals(nodeId, INodeRegistry.LocalAlias, StringComparison.OrdinalIgnoreCase)
            || string.Equals(nodeId, LocalNodeId, StringComparison.Ordinal))
            return Local;
        lock (_gate) return _remote.TryGetValue(nodeId, out var node) ? Describe(node) : null;
    }

    public IExecutionHost HostFor(string? nodeId)
    {
        if (nodeId is null
            || string.Equals(nodeId, LegacyNodeId, StringComparison.Ordinal)
            || string.Equals(nodeId, INodeRegistry.LocalAlias, StringComparison.OrdinalIgnoreCase)
            || string.Equals(nodeId, LocalNodeId, StringComparison.Ordinal))
            return LocalHost;
        var node = Find(nodeId) ?? throw new KeyNotFoundException($"No node '{nodeId}' is known");
        if (UnavailableReason(node) is { } reason) throw new NodeUnavailableException(node.Id, reason);
        return _remoteHosts?.Invoke(node.Id)
               ?? throw new NodeUnavailableException(node.Id, NodeUnavailableReasons.RemoteExecutionNotAvailable);
    }

    // NodeHub's remote hosts (P6): null while no NodeHub runs, and then no remote node is eligible.
    private Func<string, IExecutionHost?>? _remoteHosts;

    /// <summary>NodeHub connects its remote hosts: from then on <see cref="HostFor"/> gives an eligible node's host.</summary>
    public void AttachRemoteHosts(Func<string, IExecutionHost?> hosts) => _remoteHosts = hosts;

    /// <summary>
    /// Whether placement may choose <paramref name="node"/> on this server: <see cref="PlacementOf"/>, and a remote node
    /// only while NodeHub runs (<c>remote_execution_not_available</c> otherwise).
    /// </summary>
    public NodePlacement Placement(NodeDescriptor node)
    {
        var placement = PlacementOf(node);
        return placement.Eligible && node.Kind == NodeKind.Remote && _remoteHosts is null
            ? new(false, NodeUnavailableReasons.RemoteExecutionNotAvailable)
            : placement;
    }

    /// <summary>Whether placement may choose <paramref name="node"/> by its own state (node zero: always).</summary>
    public static NodePlacement PlacementOf(NodeDescriptor node)
        => UnavailableReason(node) is { } reason ? new(false, reason) : new(true, null);

    /// <summary>
    /// What a remote node's link must speak to run work (design 3.1): the runtime methods and the HTTP forward. An agent
    /// without them is <c>needs_update</c> (design 7, skew rules).
    /// </summary>
    public static readonly IReadOnlyList<string> RequiredFeatures = [LinkFeatures.RuntimeV1, LinkFeatures.ForwardHttpV1];

    /// <summary>
    /// Why a node cannot run work, the first failing check in the order that tells its owner most; null for node zero
    /// and for an eligible remote node: confirmed, not revoked, online, its agent speaking <see cref="RequiredFeatures"/>.
    /// </summary>
    public static string? UnavailableReason(NodeDescriptor node)
    {
        if (node.Kind == NodeKind.Local) return null;
        if (node.Status == NodeStatus.Revoked) return NodeUnavailableReasons.Revoked;
        if (!node.Confirmed) return NodeUnavailableReasons.NotConfirmed;
        if (node.Status == NodeStatus.NeedsUpdate) return NodeUnavailableReasons.NeedsUpdate;
        if (node.Status == NodeStatus.Offline || node.Link is null) return NodeUnavailableReasons.Offline;
        if (!RequiredFeatures.All(node.Link.Features.Contains)) return NodeUnavailableReasons.NeedsUpdate;
        return null;
    }

    // ================================================================= the runtimes remote nodes report (P6)

    /// <summary>The runtimes a remote node last reported, by id; empty for node zero and an unknown id.</summary>
    public IReadOnlyList<HostRuntime> RuntimesOf(string nodeId)
    {
        lock (_gate) return _remote.TryGetValue(nodeId, out var node) ? node.Runtimes : [];
    }

    /// <summary>A node's whole <c>runtime.list</c>: replaces what it reported before. True when anything changed.</summary>
    public bool ApplyRuntimes(string nodeId, IReadOnlyList<HostRuntime> runtimes)
    {
        var changed = false;
        Mutate(nodeId, node =>
        {
            var next = runtimes.OrderBy(runtime => runtime.Id, StringComparer.Ordinal).ToList();
            changed = RuntimesKey(node.Runtimes) != RuntimesKey(next);
            node.Runtimes = next;
            if (changed) node.CacheDue = true;
            return changed;
        });
        return changed;
    }

    /// <summary>One <c>runtime.changed</c>: that runtime's entry, added when it is new. True when anything changed.</summary>
    public bool ApplyRuntime(string nodeId, HostRuntime runtime) => ApplyRuntimeChanges(nodeId, [runtime]);

    /// <summary>
    /// A batch of <c>runtime.changed</c> (NodeHub coalesces them per node, the latest per runtime): each runtime's entry,
    /// added when new. One update and one deferred cache write for the batch. True when anything changed.
    /// </summary>
    public bool ApplyRuntimeChanges(string nodeId, IReadOnlyList<HostRuntime> runtimes)
    {
        var changed = false;
        Mutate(nodeId, node =>
        {
            var next = node.Runtimes.ToList();
            foreach (var runtime in runtimes)
            {
                var at = next.FindIndex(known => known.Id == runtime.Id);
                if (at >= 0) next[at] = runtime;
                else if (next.Count < RemoteRuntimeReport.MaxRuntimes) next.Add(runtime);
            }
            next = next.OrderBy(known => known.Id, StringComparer.Ordinal).ToList();
            changed = RuntimesKey(node.Runtimes) != RuntimesKey(next);
            node.Runtimes = next;
            if (changed) node.CacheDue = true;
            return changed;
        }, deferCache: true);
        return changed;
    }

    /// <summary>What makes a runtime change a <c>node.updated</c>: everything but when it started.</summary>
    internal static string RuntimesKey(IReadOnlyList<HostRuntime> runtimes)
        => string.Join('\u001e', runtimes.Select(runtime => string.Join('\u001f', runtime.Id, runtime.Kind, runtime.Label,
            runtime.Managed, runtime.Enabled, runtime.Status, runtime.StatusDetail, runtime.Accelerator, runtime.Version,
            string.Join(',', runtime.Capabilities), string.Join(',', runtime.Ports))));

    /// <summary>
    /// Whether NodeHub may accept a link for <paramref name="nodeId"/>: null when it may, else
    /// <c>node_unknown</c> (not a paired node, or node zero) or <c>node_revoked</c>.
    /// </summary>
    public string? LinkRefusal(string nodeId)
    {
        lock (_gate)
        {
            if (!_remote.TryGetValue(nodeId, out var node)) return "node_unknown";
            return node.Revoked ? "node_revoked" : null;
        }
    }

    // ================================================================= the kernel's node entities

    /// <summary>
    /// The kernel's answer to <c>GET /api/nodes</c> (every remote node, RedCompute's service identity).
    /// Adds and refreshes nodes and marks the ones the kernel says are revoked. A node missing from the
    /// answer is forgotten locally (design 3.5): its entry and cache go and <see cref="Updated"/> is raised,
    /// but it is not revoked: no <see cref="StatusChanged"/>, so its link stays up. One bad answer (an empty
    /// list, an access-filter change) must not unpair every node; only the kernel's explicit revoked status
    /// or <c>POST /nodes/{id}/revoked</c> revokes. Node zero's entity (<c>kind: local</c>) is ignored:
    /// <c>node.json</c> is its truth, except for its icon and colour, which only the kernel keeps.
    /// </summary>
    public void ApplyKernelNodes(IReadOnlyList<KernelNode> nodes)
    {
        var changes = new List<NodeStatusChange>();
        var updated = new List<string>();
        lock (_gate)
        {
            var listed = new HashSet<string>(StringComparer.Ordinal);
            foreach (var kernel in nodes)
            {
                if (kernel.Id == LocalNodeId)
                {
                    if (kernel.Kind == KernelNode.LocalKind && (kernel.Icon != _localIcon || kernel.Color != _localColor))
                    {
                        _localIcon = kernel.Icon;
                        _localColor = kernel.Color;
                        updated.Add(LocalNodeId);
                    }
                    continue;
                }
                if (kernel.Kind != KernelNode.RemoteKind) continue;
                listed.Add(kernel.Id);
                var isNew = !_remote.TryGetValue(kernel.Id, out var node);
                node ??= _remote[kernel.Id] = new RemoteNode(kernel.Id);
                var before = isNew ? (NodeStatus?)null : Describe(node).Status;
                var identityBefore = Key(node);

                node.Name = kernel.Name;
                node.OwnerUserId = kernel.OwnerId;
                node.Confirmed = kernel.Confirmed;
                node.PairedAt = kernel.PairedAt ?? node.PairedAt;
                node.KernelStatus = kernel.Status;
                node.KernelStatusReason = kernel.StatusReason;
                node.KernelLastSeen = kernel.LastSeen;
                // The kernel owns the look: an older kernel has none, and the dashboard falls back.
                node.Icon = kernel.Icon;
                node.Color = kernel.Color;
                // E2E (P0.5): the identity key is pinned on first use and never replaced by a later list; "required"
                // only ever turns on. Both live in the trust store, which no forget or rebuild resets.
                _trust.ObserveKernel(kernel.Id, kernel.PublicKeySpki, kernel.LinkE2eRequired);
                // The node's own describe is fresher than the kernel's copy while the link is up.
                if (node.Link is null)
                {
                    node.Os = kernel.Os ?? node.Os;
                    node.Arch = kernel.Arch ?? node.Arch;
                    node.OsDescription = kernel.OsDescription ?? node.OsDescription;
                    node.MachineName = kernel.MachineName ?? node.MachineName;
                    node.AgentVersion = kernel.AgentVersion ?? node.AgentVersion;
                    node.Chassis = kernel.Chassis ?? node.Chassis;
                }
                if (kernel.Status == KernelNode.RevokedStatus && !node.Revoked)
                {
                    node.Revoked = true;
                    node.RevokedReason = kernel.StatusReason ?? "revoked";
                    node.Link = null;
                }

                var after = Describe(node);
                if (before != after.Status) changes.Add(new(after, before));
                if (isNew || Key(node) != identityBefore)
                {
                    updated.Add(node.Id);
                    SaveCache(node);
                }
            }

            foreach (var gone in _remote.Keys.Where(id => !listed.Contains(id)).ToList())
            {
                // Forgotten, not revoked: if the kernel lists it again, it comes back (NodeHub re-attaches a live link).
                if (_remote.Remove(gone, out var forgotten)) forgotten.Forgotten = true;
                DeleteCache(gone);
                updated.Add(gone);
            }
        }
        Raise(changes, updated);
    }

    /// <summary>The kernel revoked the node (<c>POST /nodes/{id}/revoked</c>). Terminal; unknown ids are recorded too.</summary>
    public NodeDescriptor MarkRevoked(string nodeId, string reason)
    {
        NodeStatusChange? change = null;
        NodeDescriptor after;
        lock (_gate)
        {
            var isNew = !_remote.TryGetValue(nodeId, out var node);
            node ??= _remote[nodeId] = new RemoteNode(nodeId) { Name = nodeId };
            var before = isNew ? (NodeStatus?)null : Describe(node).Status;
            node.Revoked = true;
            node.RevokedReason = reason;
            node.KernelStatus = KernelNode.RevokedStatus;
            node.Link = null;
            after = Describe(node);
            if (before != after.Status) change = new(after, before);
            SaveCache(node);
        }
        Raise(change is null ? [] : [change], [nodeId]);
        return after;
    }

    // ================================================================= the live link (NodeHub)

    /// <summary>
    /// What NodeHub needs to set up a node's encrypted link (Phase 4 P0.5), from RedCompute's own trust record, never
    /// straight from the kernel: the identity key pinned on first use (null until a kernel list names one), whether
    /// plaintext is refused, and whether the record does not open (fail closed). A node RedCompute holds no record
    /// for has no pin and may link in plaintext, as before.
    /// </summary>
    public NodeLinkSecurity LinkSecurity(string nodeId)
    {
        if (_trust.IsUnreadable(nodeId)) return new NodeLinkSecurity(null, true, TrustUnreadable: true);
        var trust = _trust.Find(nodeId);
        return new NodeLinkSecurity(trust?.IdentitySpkiBytes(), trust?.PlaintextRefused == true);
    }

    /// <summary>The node's first encrypted record opened: from now on plaintext from it is refused (kept in its trust record).</summary>
    public void MarkSeenEncrypted(string nodeId)
    {
        if (!_trust.MarkSeenEncrypted(nodeId)) return;
        bool known;
        lock (_gate) known = _remote.ContainsKey(nodeId);
        if (known) Raise([], [nodeId]);
    }

    /// <summary>
    /// The node's owner or an admin, in person, resets RedCompute's pin of its identity key: the next kernel list pins
    /// anew. "Encrypted for good" stays. False when there was no pin.
    /// </summary>
    public bool ResetIdentityPin(string nodeId, string by)
    {
        if (!_trust.ResetIdentityPin(nodeId, by)) return false;
        bool known;
        lock (_gate) known = _remote.ContainsKey(nodeId);
        if (known) Raise([], [nodeId]);
        return true;
    }

    /// <summary>What makes a <c>node.updated</c>: the node's identity and platform, and its trust state.</summary>
    private string Key(RemoteNode node)
        => node.IdentityKey() + '\u001f' + (_trust.Find(node.Id) is { } trust ? $"{trust.PlaintextRefused}/{trust.PinState}" : "");

    /// <summary>A link for the node completed its handshake.</summary>
    public void LinkUp(string nodeId, NodeLinkState link)
        => Mutate(nodeId, node =>
        {
            node.Link = link;
            node.LinkDownReason = null;
            node.LastLinkSeen = _clock();
            node.AgentVersion = link.AgentVersion ?? node.AgentVersion;
            return true;
        });

    /// <summary>
    /// The node's link closed. Only the link that is still current counts: a superseded connection
    /// that ends after its replacement came up changes nothing.
    /// </summary>
    public void LinkDown(string nodeId, string linkSessionId, string reason)
        => Mutate(nodeId, node =>
        {
            if (node.Link is null || node.Link.LinkSessionId != linkSessionId) return false;
            node.Link = null;
            node.LinkDownReason = reason;
            node.LastLinkSeen = _clock();
            return true;
        });

    /// <summary>What the node's <c>node.describe</c> answered (platform, agent, credential store).</summary>
    public void ApplyDescribe(string nodeId, NodeDescribe describe)
        => Mutate(nodeId, node =>
        {
            node.Os = describe.Os ?? node.Os;
            node.Arch = describe.Arch ?? node.Arch;
            node.OsDescription = describe.OsDescription ?? node.OsDescription;
            node.MachineName = describe.MachineName ?? node.MachineName;
            node.Processor = describe.Processor ?? node.Processor;
            node.AgentVersion = describe.AgentVersion ?? node.AgentVersion;
            node.Chassis = describe.Chassis ?? node.Chassis;
            if (node.Link is { } link)
                node.Link = link with
                {
                    AgentVersion = describe.AgentVersion ?? link.AgentVersion,
                    CredentialStore = describe.CredentialStore ?? link.CredentialStore,
                };
            return true;
        });

    /// <summary>
    /// A report from the node: its CLIs (when they changed), a hardware sample, and the link's latest
    /// round trip. Raises <see cref="Updated"/> only when the CLIs changed; hardware is live data.
    /// </summary>
    public void ApplyReport(string nodeId, IReadOnlyList<NodeCliReport>? clis, HardwareSnapshot? hardware,
        double? rttMs = null, double? clockOffsetMs = null)
    {
        var now = _clock();
        Mutate(nodeId, node =>
        {
            var current = node.Report ?? new NodeReport([], null, null);
            var clisChanged = clis is not null && !clis.SequenceEqual(current.Clis);
            node.Report = new NodeReport(clis ?? current.Clis, hardware ?? current.Hardware, now);
            node.LastLinkSeen = now;
            if (node.Link is { } link)
                node.Link = link with
                {
                    LastHeartbeatAt = now,
                    RttMs = rttMs ?? link.RttMs,
                    ClockOffsetMs = clockOffsetMs ?? link.ClockOffsetMs,
                };
            var stale = node.CachedHardwareAt is not { } at || now - at >= HardwareCacheInterval;
            if (clisChanged || (hardware is not null && stale)) node.CacheDue = true;
            return clisChanged;
        });
    }

    /// <summary>
    /// Applies <paramref name="change"/> to a known, non-revoked remote node; raises the events it caused. With
    /// <paramref name="deferCache"/> its cache write is snapshotted here and written outside the lock (runtime changes).
    /// </summary>
    private void Mutate(string nodeId, Func<RemoteNode, bool> change, bool deferCache = false)
    {
        NodeStatusChange? statusChange = null;
        CacheSnapshot? deferred = null;
        var updated = false;
        lock (_gate)
        {
            if (!_remote.TryGetValue(nodeId, out var node) || node.Revoked) return;
            var before = Describe(node).Status;
            var identityBefore = Key(node);
            updated = change(node);
            var after = Describe(node);
            if (before != after.Status)
            {
                statusChange = new(after, before);
                updated = true;
            }
            if (Key(node) != identityBefore) updated = true;
            if (updated || node.CacheDue)
            {
                if (deferCache) deferred = Snapshot(node);
                else SaveCache(node);
            }
        }
        if (deferred is not null) QueueCacheWrite(deferred);
        Raise(statusChange is null ? [] : [statusChange], updated ? [nodeId] : []);
    }

    private void Raise(IReadOnlyList<NodeStatusChange> changes, IReadOnlyList<string> updated)
    {
        foreach (var change in changes)
            try { StatusChanged?.Invoke(change); }
            catch (Exception error) { _log($"[Nodes] A status observer failed: {error.Message}"); }
        foreach (var id in updated.Distinct(StringComparer.Ordinal))
            try { Updated?.Invoke(id); }
            catch (Exception error) { _log($"[Nodes] An update observer failed: {error.Message}"); }
    }

    private NodeDescriptor Describe(RemoteNode node)
    {
        NodeStatus status;
        string? reason;
        if (node.Revoked)
        {
            status = NodeStatus.Revoked;
            reason = node.RevokedReason;
        }
        else if (node.Link is not null)
        {
            status = node.Confirmed ? NodeStatus.Online : NodeStatus.PendingConfirmation;
            reason = node.Confirmed ? null : "not_confirmed";
        }
        else if (node.KernelStatus == KernelNode.NeedsUpdateStatus && node.LinkDownReason is null)
        {
            status = NodeStatus.NeedsUpdate;
            reason = node.KernelStatusReason;
        }
        else
        {
            status = NodeStatus.Offline;
            reason = node.LinkDownReason ?? node.KernelStatusReason ?? "not_connected";
        }

        var lastSeen = Max(node.LastLinkSeen, node.KernelLastSeen);
        return new NodeDescriptor(
            node.Id,
            node.Name,
            NodeKind.Remote,
            status,
            node.Os ?? "",
            node.OsDescription ?? "",
            node.Arch ?? "",
            node.MachineName ?? "",
            node.Processor,
            node.Link?.AgentVersion ?? node.AgentVersion ?? "",
            node.Link?.ConnectedAt ?? node.PairedAt ?? lastSeen ?? DateTimeOffset.UnixEpoch,
            node.Link is not null ? _clock() : lastSeen ?? node.PairedAt ?? DateTimeOffset.UnixEpoch)
        {
            OwnerUserId = node.OwnerUserId,
            Confirmed = node.Confirmed,
            PairedAt = node.PairedAt,
            StatusReason = reason,
            Link = node.Link,
            Report = node.Report,
            Chassis = node.Chassis,
            Icon = node.Icon,
            Color = node.Color,
            E2eRequired = _trust.IsUnreadable(node.Id) || _trust.Find(node.Id)?.PlaintextRefused == true,
            IdentityPin = _trust.IsUnreadable(node.Id) ? "unreadable" : _trust.Find(node.Id)?.PinState,
        };
    }

    private static DateTimeOffset? Max(DateTimeOffset? a, DateTimeOffset? b)
        => a is null ? b : b is null ? a : a > b ? a : b;

    // ================================================================= the cache

    private string? CachePath(string nodeId)
        => _cacheDirectory is null || !KernelNode.IsNodeId(nodeId) ? null : Path.Combine(_cacheDirectory, nodeId + ".json");

    internal static CachedRemoteNode? ParseCache(string json) => JsonSerializer.Deserialize<CachedRemoteNode>(json, CacheJson);

    /// <summary>
    /// Loads the remote nodes' last state at startup. The cache is rebuildable, so nothing in it may stop
    /// RedCompute: any failure on a file (whatever the exception) sets that file aside as
    /// <c>&lt;name&gt;.&lt;UTC time&gt;.bad</c>, logs it and moves on; any failure on the folder is logged.
    /// </summary>
    private void LoadCache(Func<string, CachedRemoteNode?> parse)
    {
        if (_cacheDirectory is null) return;
        try
        {
            var directory = LocalHost.Path(_cacheDirectory);
            if (!LocalHost.Files.DirectoryExists(directory)) return;
            foreach (var file in LocalHost.Files.EnumerateFiles(directory, "node_*.json", SearchOption.TopDirectoryOnly).ToList())
            {
                try
                {
                    var cached = parse(LocalHost.Files.ReadAllText(file))
                                 ?? throw new InvalidDataException("the file holds no node");
                    if (!KernelNode.IsNodeId(cached.Id)) throw new InvalidDataException($"'{cached.Id}' is not a node id");
                    if (cached.Id == LocalNodeId) continue;
                    _remote[cached.Id] = RemoteNode.FromCache(cached);
                }
                catch (Exception error)
                {
                    SetAside(file, error);
                }
            }
            if (_remote.Count > 0) _log($"[Nodes] {_remote.Count} remote node(s) from the cache, offline until they link");
        }
        catch (Exception error)
        {
            _log($"[Nodes] Could not read the node cache: {error.Message}");
        }
    }

    private void SetAside(HostPath file, Exception error)
    {
        var aside = LocalHost.Path($"{file.Value}.{_clock():yyyyMMdd'T'HHmmss'Z'}.bad");
        try
        {
            LocalHost.Files.WriteAllBytes(aside, LocalHost.Files.ReadAllBytes(file));
            LocalHost.Files.DeleteFile(file);
            _log($"[Nodes] Set aside an unreadable node cache file {file} as {aside}: {error.GetType().Name}: {error.Message}");
        }
        catch (Exception moveError)
        {
            _log($"[Nodes] Ignored an unreadable node cache file {file} ({error.GetType().Name}: {error.Message}); "
                 + $"it could not be set aside: {moveError.Message}");
        }
    }

    /// <summary>Writes the node's cache now (under the gate): revokes, kernel identity, the link and reports.</summary>
    private void SaveCache(RemoteNode node)
    {
        if (Snapshot(node) is { } snapshot) WriteCache(snapshot);
    }

    /// <summary>The node's cache as it is now, with a version later than every earlier snapshot. Under the gate.</summary>
    private CacheSnapshot? Snapshot(RemoteNode node)
    {
        node.CacheDue = false;
        if (CachePath(node.Id) is not { } path) return null;
        if (node.Report?.Hardware is not null) node.CachedHardwareAt = _clock();
        return new CacheSnapshot(node, ++node.CacheVersion, path, node.ToCache(Max(node.LastLinkSeen, node.KernelLastSeen)));
    }

    private void WriteCache(CacheSnapshot snapshot)
    {
        lock (_cacheIo)
        {
            var node = snapshot.Node;
            if (node.Forgotten || snapshot.Version <= node.WrittenVersion) return;
            node.WrittenVersion = snapshot.Version;
            try
            {
                LocalHost.Files.CreateDirectory(LocalHost.Path(_cacheDirectory!));
                LocalHost.Files.ReplaceAtomically(LocalHost.Path(snapshot.Path), JsonSerializer.Serialize(snapshot.Cache, CacheJson));
                Interlocked.Increment(ref _cacheWrites);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                _log($"[Nodes] Could not cache node {node.Id}: {error.Message}");
            }
        }
    }

    /// <summary>A deferred write: the latest snapshot per node waits; one drainer at a time writes them, off the gate.</summary>
    private void QueueCacheWrite(CacheSnapshot snapshot)
    {
        lock (_cacheQueue)
        {
            _cachePending[snapshot.Node.Id] = snapshot;
            if (_cacheDraining) return;
            _cacheDraining = true;
        }
        ThreadPool.QueueUserWorkItem(_ => DrainCacheWrites());
    }

    private void DrainCacheWrites()
    {
        while (true)
        {
            List<CacheSnapshot> batch;
            lock (_cacheQueue)
            {
                if (_cachePending.Count == 0)
                {
                    _cacheDraining = false;
                    return;
                }
                batch = [.. _cachePending.Values];
                _cachePending.Clear();
            }
            foreach (var snapshot in batch)
            {
                try
                {
                    WriteCache(snapshot);
                }
                catch (Exception error)
                {
                    _log($"[Nodes] Could not cache node {snapshot.Node.Id}: {error.Message}");
                }
            }
        }
    }

    private void DeleteCache(string nodeId)
    {
        if (CachePath(nodeId) is not { } path) return;
        lock (_cacheIo)
        {
            try
            {
                var file = LocalHost.Path(path);
                if (LocalHost.Files.FileExists(file)) LocalHost.Files.DeleteFile(file);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                _log($"[Nodes] Could not delete the cache of node {nodeId}: {error.Message}");
            }
        }
    }

    /// <summary>One remote node's state, mutated only under the registry's gate.</summary>
    private sealed class RemoteNode(string id)
    {
        public string Id { get; } = id;
        public string Name { get; set; } = id;
        public string? OwnerUserId { get; set; }
        public bool Confirmed { get; set; }
        public DateTimeOffset? PairedAt { get; set; }
        public string? KernelStatus { get; set; }
        public string? KernelStatusReason { get; set; }
        public DateTimeOffset? KernelLastSeen { get; set; }
        public string? Os { get; set; }
        public string? Arch { get; set; }
        public string? OsDescription { get; set; }
        public string? MachineName { get; set; }
        public string? Processor { get; set; }
        public string? AgentVersion { get; set; }
        public bool Revoked { get; set; }
        public string? RevokedReason { get; set; }
        public NodeLinkState? Link { get; set; }
        public string? LinkDownReason { get; set; }
        public DateTimeOffset? LastLinkSeen { get; set; }
        public NodeReport? Report { get; set; }
        public string? Chassis { get; set; }
        public string? Icon { get; set; }
        public string? Color { get; set; }
        public IReadOnlyList<HostRuntime> Runtimes { get; set; } = [];
        public bool CacheDue { get; set; }
        public DateTimeOffset? CachedHardwareAt { get; set; }

        /// <summary>The last cache snapshot taken (under the gate) and the last written (under the cache lock).</summary>
        public long CacheVersion { get; set; }
        public long WrittenVersion { get; set; }

        /// <summary>Set under the gate when the kernel's list drops the node: no deferred write brings its file back.</summary>
        public volatile bool Forgotten;

        /// <summary>The fields whose change is a <c>node.updated</c> (identity and platform; not liveness).</summary>
        public string IdentityKey()
            => string.Join('\u001f', Name, OwnerUserId, Confirmed, PairedAt, Os, Arch, OsDescription, MachineName, Processor,
                AgentVersion, Link?.CredentialStore, Link?.Protocol, Link?.UpdateAvailable, Link?.Encrypted, Chassis, Icon, Color);

        public CachedRemoteNode ToCache(DateTimeOffset? lastSeen) => new(Id, Name, OwnerUserId, Confirmed, PairedAt, Os, Arch,
            OsDescription, MachineName, Processor, AgentVersion, Revoked, RevokedReason, KernelStatus, lastSeen, Report,
            Chassis, Icon, Color, Runtimes.Count == 0 ? null : Runtimes.Select(CachedRuntime.From).ToList());

        /// <summary>A cached record, with what an older or hand-edited file may lack filled in.</summary>
        public static RemoteNode FromCache(CachedRemoteNode cached) => new(cached.Id)
        {
            Name = string.IsNullOrWhiteSpace(cached.Name) ? cached.Id : cached.Name,
            OwnerUserId = cached.OwnerUserId,
            Confirmed = cached.Confirmed,
            PairedAt = cached.PairedAt,
            Os = cached.Os,
            Arch = cached.Arch,
            OsDescription = cached.OsDescription,
            MachineName = cached.MachineName,
            Processor = cached.Processor,
            AgentVersion = cached.AgentVersion,
            Revoked = cached.Revoked,
            RevokedReason = cached.RevokedReason,
            KernelStatus = cached.KernelStatus,
            KernelLastSeen = cached.LastSeenAt,
            Report = cached.Report is { } report ? report with { Clis = report.Clis?.Where(cli => cli?.Name is not null).ToList() ?? [] } : null,
            CachedHardwareAt = cached.Report?.ReportedAt,
            Chassis = KernelNode.Token(cached.Chassis),
            Icon = KernelNode.Token(cached.Icon),
            Color = KernelNode.Token(cached.Color),
            // Read back through the parser a live report goes through: a hand-edited cache gains nothing.
            Runtimes = cached.Runtimes is { } runtimes
                ? RemoteRuntimeReport.ParseList(JsonSerializer.SerializeToElement(
                    runtimes.Where(runtime => runtime is not null).Select(runtime => runtime.ToJson()).ToList()))
                : [],
        };
    }

    /// <summary>The shape of <c>nodes/&lt;id&gt;.json</c>.</summary>
    internal sealed record CachedRemoteNode(string Id, string Name, string? OwnerUserId, bool Confirmed, DateTimeOffset? PairedAt,
        string? Os, string? Arch, string? OsDescription, string? MachineName, string? Processor, string? AgentVersion,
        bool Revoked, string? RevokedReason, string? KernelStatus, DateTimeOffset? LastSeenAt, NodeReport? Report,
        string? Chassis = null, string? Icon = null, string? Color = null, IReadOnlyList<CachedRuntime>? Runtimes = null);

    /// <summary>A runtime as the cache keeps it: the node's own wire shape, so it is read back by the same parser.</summary>
    internal sealed record CachedRuntime(string Id, string Kind, string Label, string Managed, bool Enabled, string Status,
        string? StatusDetail, string? Version, string? Accelerator, IReadOnlyList<string>? Capabilities, IReadOnlyList<int>? Ports,
        DateTimeOffset? StartedAt)
    {
        public static CachedRuntime From(HostRuntime runtime) => new(runtime.Id, runtime.Kind, runtime.Label,
            runtime.Managed == HostRuntimeManagement.Spawn ? "spawn" : "attach", runtime.Enabled,
            runtime.Status.ToString().ToLowerInvariant(), runtime.StatusDetail, runtime.Version, runtime.Accelerator,
            runtime.Capabilities, runtime.Ports, runtime.StartedAt);

        public Dictionary<string, object?> ToJson() => new()
        {
            ["id"] = Id, ["kind"] = Kind, ["label"] = Label, ["managed"] = Managed, ["enabled"] = Enabled, ["status"] = Status,
            ["statusDetail"] = StatusDetail, ["version"] = Version, ["accelerator"] = Accelerator,
            ["capabilities"] = Capabilities ?? [], ["ports"] = Ports ?? [], ["startedAt"] = StartedAt?.ToString("O"),
        };
    }
}

/// <summary>
/// A remote node's link security, from RedCompute's trust record: its pinned identity key (null until a kernel list names
/// one), whether plaintext is refused, and whether the record does not open (every link is then refused 1011).
/// </summary>
public sealed record NodeLinkSecurity(byte[]? IdentitySpki, bool E2eRequired, bool TrustUnreadable = false);

/// <summary>What a node's <c>node.describe</c> said about itself.</summary>
public sealed record NodeDescribe(string? Os, string? Arch, string? OsDescription, string? MachineName, string? Processor,
    string? AgentVersion, string? CredentialStore, string? Chassis = null);
