using System.IO;
using System.Text.Json;
using RedCompute.Core.Hardware;
using RedCompute.Core.Nodes;
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
/// zero's host, so an offline node still shows its last report after a restart. No remote node runs work in
/// Phase 3: <see cref="HostFor"/> throws <see cref="NodeUnavailableException"/> for every one.
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
    // Node zero's look, from its kernel entity (node.json has none): null until the kernel lists it.
    private string? _localIcon;
    private string? _localColor;

    public NodeRegistry(NodeIdentityResult identity, NodePlatform platform, string version,
        DateTimeOffset startedAt, Func<DateTimeOffset>? clock = null, IExecutionHost? localHost = null,
        string? remoteCacheDirectory = null, Action<string>? log = null)
        : this(identity, platform, version, startedAt, clock, localHost, remoteCacheDirectory, log, ParseCache)
    {
    }

    /// <summary>Tests: <paramref name="parseCache"/> stands in for the cache file parser.</summary>
    internal NodeRegistry(NodeIdentityResult identity, NodePlatform platform, string version,
        DateTimeOffset startedAt, Func<DateTimeOffset>? clock, IExecutionHost? localHost,
        string? remoteCacheDirectory, Action<string>? log, Func<string, CachedRemoteNode?> parseCache)
    {
        Identity = identity;
        _platform = platform;
        _version = version;
        _startedAt = startedAt;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        LocalHost = localHost ?? new LocalExecutionHost(identity.Identity.Id);
        _cacheDirectory = remoteCacheDirectory;
        _log = log ?? (_ => { });
        LoadCache(parseCache);
    }

    /// <summary>Node zero's execution host: the code that ran every launch before nodes existed.</summary>
    public IExecutionHost LocalHost { get; }

    /// <summary>
    /// Opens node.json in <paramref name="directory"/> (creating it on first boot) and detects the platform.
    /// <paramref name="remoteCacheDirectory"/>, when given, is where remote nodes' last state is kept.
    /// </summary>
    public static NodeRegistry Open(string directory, string version, Action<string> log, string? remoteCacheDirectory = null)
    {
        var identity = NodeIdentityStore.LoadOrCreate(directory, log);
        var host = new LocalExecutionHost(identity.Identity.Id);
        return new(identity, NodePlatform.Detect(host), version, DateTimeOffset.UtcNow, localHost: host,
            remoteCacheDirectory: remoteCacheDirectory, log: log);
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
        // Phase 3: a remote node never runs work; RemoteExecutionHost arrives behind this same call in Phase 4.
        throw new NodeUnavailableException(node.Id, UnavailableReason(node)!);
    }

    /// <summary>Whether placement may choose <paramref name="node"/> (node zero: always).</summary>
    public static NodePlacement PlacementOf(NodeDescriptor node)
        => UnavailableReason(node) is { } reason ? new(false, reason) : new(true, null);

    /// <summary>Why a node cannot run work, in the order that tells its owner most; null for node zero.</summary>
    public static string? UnavailableReason(NodeDescriptor node)
    {
        if (node.Kind == NodeKind.Local) return null;
        if (node.Status == NodeStatus.Revoked) return NodeUnavailableReasons.Revoked;
        if (!node.Confirmed) return NodeUnavailableReasons.NotConfirmed;
        if (node.Status == NodeStatus.NeedsUpdate) return NodeUnavailableReasons.NeedsUpdate;
        if (node.Status == NodeStatus.Offline) return NodeUnavailableReasons.Offline;
        return NodeUnavailableReasons.RemoteExecutionNotAvailable;
    }

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
                var identityBefore = node.IdentityKey();

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
                // E2E (P0.5): the identity key the link binding is checked against, and "required", which only
                // ever turns on here (a kernel that stops saying it, or an older one, never reopens plaintext).
                var spkiBefore = node.IdentitySpki;
                var requiredBefore = node.E2eRequired;
                node.IdentitySpki = kernel.PublicKeySpki ?? node.IdentitySpki;
                node.E2eRequired |= kernel.LinkE2eRequired;
                var securityChanged = !SameBytes(spkiBefore, node.IdentitySpki) || requiredBefore != node.E2eRequired;
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
                if (isNew || node.IdentityKey() != identityBefore)
                {
                    updated.Add(node.Id);
                    SaveCache(node);
                }
                else if (securityChanged)
                {
                    SaveCache(node);
                }
            }

            foreach (var gone in _remote.Keys.Where(id => !listed.Contains(id)).ToList())
            {
                // Forgotten, not revoked: if the kernel lists it again, it comes back (NodeHub re-attaches a live link).
                _remote.Remove(gone);
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
    /// What NodeHub needs to set up a node's encrypted link (Phase 4 P0.5): the identity key to verify the
    /// binding against (null until the kernel lists it, K0), and whether plaintext is refused. Null for an
    /// unknown node.
    /// </summary>
    public NodeLinkSecurity? LinkSecurity(string nodeId)
    {
        lock (_gate)
            return _remote.TryGetValue(nodeId, out var node)
                ? new NodeLinkSecurity(node.IdentitySpki, node.E2eRequired || node.SeenEncrypted)
                : null;
    }

    /// <summary>The node linked encrypted: from now on plaintext from it is refused (kept in its cache file).</summary>
    public void MarkSeenEncrypted(string nodeId)
    {
        lock (_gate)
        {
            if (!_remote.TryGetValue(nodeId, out var node) || node.SeenEncrypted) return;
            node.SeenEncrypted = true;
            SaveCache(node);
        }
    }

    private static bool SameBytes(byte[]? a, byte[]? b)
        => a is null ? b is null : b is not null && a.AsSpan().SequenceEqual(b);

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

    /// <summary>Applies <paramref name="change"/> to a known, non-revoked remote node; raises the events it caused.</summary>
    private void Mutate(string nodeId, Func<RemoteNode, bool> change)
    {
        NodeStatusChange? statusChange = null;
        var updated = false;
        lock (_gate)
        {
            if (!_remote.TryGetValue(nodeId, out var node) || node.Revoked) return;
            var before = Describe(node).Status;
            var identityBefore = node.IdentityKey();
            updated = change(node);
            var after = Describe(node);
            if (before != after.Status)
            {
                statusChange = new(after, before);
                updated = true;
            }
            if (node.IdentityKey() != identityBefore) updated = true;
            if (updated || node.CacheDue) SaveCache(node);
        }
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
            E2eRequired = node.E2eRequired || node.SeenEncrypted,
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

    private void SaveCache(RemoteNode node)
    {
        node.CacheDue = false;
        if (CachePath(node.Id) is not { } path) return;
        try
        {
            LocalHost.Files.CreateDirectory(LocalHost.Path(_cacheDirectory!));
            LocalHost.Files.ReplaceAtomically(LocalHost.Path(path), JsonSerializer.Serialize(node.ToCache(Max(node.LastLinkSeen, node.KernelLastSeen)), CacheJson));
            if (node.Report?.Hardware is not null) node.CachedHardwareAt = _clock();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            _log($"[Nodes] Could not cache node {node.Id}: {error.Message}");
        }
    }

    private void DeleteCache(string nodeId)
    {
        if (CachePath(nodeId) is not { } path) return;
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
        public bool CacheDue { get; set; }
        public DateTimeOffset? CachedHardwareAt { get; set; }
        public byte[]? IdentitySpki { get; set; }
        public bool E2eRequired { get; set; }
        public bool SeenEncrypted { get; set; }

        /// <summary>The fields whose change is a <c>node.updated</c> (identity and platform; not liveness).</summary>
        public string IdentityKey()
            => string.Join('\u001f', Name, OwnerUserId, Confirmed, PairedAt, Os, Arch, OsDescription, MachineName, Processor,
                AgentVersion, Link?.CredentialStore, Link?.Protocol, Link?.UpdateAvailable, Link?.Encrypted, Chassis, Icon, Color,
                E2eRequired || SeenEncrypted);

        public CachedRemoteNode ToCache(DateTimeOffset? lastSeen) => new(Id, Name, OwnerUserId, Confirmed, PairedAt, Os, Arch,
            OsDescription, MachineName, Processor, AgentVersion, Revoked, RevokedReason, KernelStatus, lastSeen, Report,
            Chassis, Icon, Color, IdentitySpki is null ? null : Convert.ToBase64String(IdentitySpki), E2eRequired, SeenEncrypted);

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
            IdentitySpki = SpkiOrNull(cached.IdentitySpki),
            E2eRequired = cached.E2eRequired,
            SeenEncrypted = cached.SeenEncrypted,
        };

        private static byte[]? SpkiOrNull(string? base64)
        {
            if (base64 is null) return null;
            try { return Convert.FromBase64String(base64); }
            catch (FormatException) { return null; }
        }
    }

    /// <summary>The shape of <c>nodes/&lt;id&gt;.json</c>.</summary>
    internal sealed record CachedRemoteNode(string Id, string Name, string? OwnerUserId, bool Confirmed, DateTimeOffset? PairedAt,
        string? Os, string? Arch, string? OsDescription, string? MachineName, string? Processor, string? AgentVersion,
        bool Revoked, string? RevokedReason, string? KernelStatus, DateTimeOffset? LastSeenAt, NodeReport? Report,
        string? Chassis = null, string? Icon = null, string? Color = null,
        string? IdentitySpki = null, bool E2eRequired = false, bool SeenEncrypted = false);
}

/// <summary>A remote node's link security: its identity key (null until the kernel lists it) and whether plaintext is refused.</summary>
public sealed record NodeLinkSecurity(byte[]? IdentitySpki, bool E2eRequired);

/// <summary>What a node's <c>node.describe</c> said about itself.</summary>
public sealed record NodeDescribe(string? Os, string? Arch, string? OsDescription, string? MachineName, string? Processor,
    string? AgentVersion, string? CredentialStore, string? Chassis = null);
