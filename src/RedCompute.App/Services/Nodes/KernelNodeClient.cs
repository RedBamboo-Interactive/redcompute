using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using RedBamboo.AppHost.Auth;
using RedCompute.Core.Nodes;

namespace RedCompute.App.Services.Nodes;

/// <summary>
/// One <c>node</c> entity as the kernel's <c>GET /api/nodes</c> lists it (RedLeaf L1:
/// <c>{id, name, kind, status, statusReason, ownerId, confirmed, platform{os, arch, osDescription, machineName},
/// agentVersion, protocolVersion, lastSeen, pairedAt, …}</c>). Unknown members are ignored.
/// </summary>
public sealed partial record KernelNode(
    string Id,
    string Name,
    string Kind,
    string Status,
    string? StatusReason,
    string? OwnerId,
    bool Confirmed,
    string? Os,
    string? Arch,
    string? OsDescription,
    string? MachineName,
    string? AgentVersion,
    int? ProtocolVersion,
    DateTimeOffset? LastSeen,
    DateTimeOffset? PairedAt)
{
    public const string LocalKind = "local";
    public const string RemoteKind = "remote";
    public const string RevokedStatus = "revoked";
    public const string NeedsUpdateStatus = "needs-update";

    [GeneratedRegex("^node_[0-9A-HJKMNP-TV-Z]{26}$")]
    private static partial Regex NodeIdPattern();

    /// <summary><c>node_</c> + a 26-character ULID: the only ids a remote node can have (and safe as a file name).</summary>
    public static bool IsNodeId(string? text) => text is not null && NodeIdPattern().IsMatch(text);

    /// <summary>One item of the list, or null when it is not a node this registry can hold.</summary>
    public static KernelNode? Parse(JsonNode? item)
    {
        if (item is not JsonObject node) return null;
        var id = Text(node, "id");
        var kind = Text(node, "kind");
        if (!IsNodeId(id) || kind is not (LocalKind or RemoteKind)) return null;
        var platform = node["platform"] as JsonObject;
        return new KernelNode(
            id!,
            Clip(Text(node, "name"), 80) ?? id!,
            kind,
            Text(node, "status") ?? "offline",
            Clip(Text(node, "statusReason"), 120),
            Text(node, "ownerId"),
            node["confirmed"] is JsonValue confirmed && confirmed.TryGetValue<bool>(out var yes) && yes,
            Clip(Text(platform, "os"), 200),
            Clip(Text(platform, "arch"), 200),
            Clip(Text(platform, "osDescription"), 200),
            Clip(Text(platform, "machineName"), 200),
            Clip(Text(node, "agentVersion"), 64),
            node["protocolVersion"] is JsonValue protocol && protocol.TryGetValue<int>(out var version) ? version : null,
            Date(node, "lastSeen"),
            Date(node, "pairedAt"));
    }

    private static string? Text(JsonObject? node, string key)
        => node?[key] is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text) ? text.Trim() : null;

    private static string? Clip(string? text, int max) => text is null ? null : text.Length <= max ? text : text[..max];

    private static DateTimeOffset? Date(JsonObject node, string key)
        => node[key] is JsonValue value && value.TryGetValue<DateTimeOffset>(out var date) ? date
            : Text(node, key) is { } text && DateTimeOffset.TryParse(text, out var parsed) ? parsed : null;
}

/// <summary>The kernel's node routes RedCompute calls with its service identity (<c>service:redcompute</c>).</summary>
public interface IKernelNodes
{
    /// <summary>Every node entity, or null when the kernel cannot say (unreachable, no node support, refused).</summary>
    Task<IReadOnlyList<KernelNode>?> ListAsync(CancellationToken ct);

    /// <summary><c>PUT /api/nodes/local</c>: node zero's entity. False when the kernel did not take it.</summary>
    Task<bool> PutLocalAsync(JsonObject body, CancellationToken ct);

    /// <summary><c>PUT /api/nodes/{id}/presence</c>: what RedCompute knows live about a remote node.</summary>
    Task<bool> PutPresenceAsync(string nodeId, JsonObject body, CancellationToken ct);
}

/// <summary>
/// <see cref="IKernelNodes"/> over HTTP, as the other kernel clients (<see cref="Placement.KernelPlacementClient"/>):
/// a short-lived service badge on every request, 15 s timeout, never an exception for an unavailable kernel.
/// </summary>
public sealed class KernelNodeClient : IKernelNodes
{
    private readonly HttpClient _http;

    public KernelNodeClient(string redLeafBaseUrl, JwtService jwtService)
        : this(new HttpClient(new BadgeBearerHandler(() => jwtService.GenerateServiceAccessToken("redcompute", roles: ["service"])))
        {
            BaseAddress = new Uri(redLeafBaseUrl.TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromSeconds(15),
        })
    {
    }

    internal KernelNodeClient(HttpClient http) => _http = http;

    /// <summary>Why the last call failed, for the log (null after a success).</summary>
    public string? LastProblem { get; private set; }

    public async Task<IReadOnlyList<KernelNode>?> ListAsync(CancellationToken ct)
    {
        try
        {
            using var response = await _http.GetAsync("api/nodes", ct);
            if (!response.IsSuccessStatusCode)
            {
                LastProblem = $"GET /api/nodes answered {(int)response.StatusCode}";
                return null;
            }
            var body = await response.Content.ReadFromJsonAsync<JsonObject>(ct);
            if (body?["nodes"] is not JsonArray items)
            {
                LastProblem = "GET /api/nodes answered without a nodes list";
                return null;
            }
            LastProblem = null;
            return items.Select(KernelNode.Parse).OfType<KernelNode>().ToList();
        }
        catch (Exception error) when (error is HttpRequestException or JsonException or NotSupportedException
                                          or TaskCanceledException && !ct.IsCancellationRequested)
        {
            LastProblem = $"GET /api/nodes failed: {error.Message}";
            return null;
        }
    }

    public Task<bool> PutLocalAsync(JsonObject body, CancellationToken ct) => PutAsync("api/nodes/local", body, ct);

    public Task<bool> PutPresenceAsync(string nodeId, JsonObject body, CancellationToken ct)
        => KernelNode.IsNodeId(nodeId) ? PutAsync($"api/nodes/{nodeId}/presence", body, ct) : Task.FromResult(false);

    private async Task<bool> PutAsync(string path, JsonObject body, CancellationToken ct)
    {
        try
        {
            using var response = await _http.PutAsJsonAsync(path, body, ct);
            LastProblem = response.IsSuccessStatusCode ? null : $"PUT /{path} answered {(int)response.StatusCode}";
            return response.IsSuccessStatusCode;
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            LastProblem = $"PUT /{path} failed: {error.Message}";
            return false;
        }
    }
}

/// <summary>
/// Keeps the registry in step with the kernel (design 7.1, source b) and the kernel in step with the
/// links (its presence fields):
/// <list type="bullet">
/// <item>at start, then every <see cref="Interval"/>, and whenever <see cref="RequestSync"/> is called (the
/// kernel's <c>POST /nodes/changed</c>, or NodeHub meeting a node it does not know): node zero's entity, then
/// the list of nodes;</item>
/// <item>a remote node's status transition or report change is pushed with <c>PUT /api/nodes/{id}/presence</c>,
/// coalesced per node over <see cref="PresenceDelay"/>; the kernel throttles its own writes. With no remote
/// node nothing is queued and the presence loop sleeps.</item>
/// </list>
/// With no kernel (or one without nodes) nothing changes: the registry keeps node zero and its cache.
/// </summary>
public sealed class KernelNodeSync : IAsyncDisposable
{
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan DefaultPresenceDelay = TimeSpan.FromSeconds(2);

    /// <summary>Two reads of the kernel's list start at least this far apart (an unknown node cannot make it spin).</summary>
    public static readonly TimeSpan DefaultMinimumResyncInterval = TimeSpan.FromSeconds(2);

    private readonly NodeRegistry _registry;
    private readonly IKernelNodes _kernel;
    private readonly Action<string> _log;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _wake = new(0, int.MaxValue);
    private readonly SemaphoreSlim _presenceWake = new(0, int.MaxValue);
    private readonly object _flightGate = new();
    private readonly object _pendingGate = new();
    private readonly HashSet<string> _pendingPresence = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _life = new();
    private Task? _loop;
    private Task? _presenceLoop;
    private SyncFlight? _flight;
    private long _lastListAt;
    private int _lists;
    private int _presenceWakeups;
    private readonly HashSet<string> _loggedProblems = new(StringComparer.Ordinal);

    public KernelNodeSync(NodeRegistry registry, IKernelNodes kernel, Action<string> log, TimeSpan? interval = null,
        TimeProvider? time = null, TimeSpan? minimumResyncInterval = null, TimeSpan? presenceDelay = null)
    {
        _registry = registry;
        _kernel = kernel;
        _log = log;
        _time = time ?? TimeProvider.System;
        Interval = interval ?? DefaultInterval;
        MinimumResyncInterval = minimumResyncInterval ?? DefaultMinimumResyncInterval;
        PresenceDelay = presenceDelay ?? DefaultPresenceDelay;
        _registry.StatusChanged += OnStatusChanged;
        _registry.Updated += QueuePresence;
    }

    public TimeSpan Interval { get; }
    public TimeSpan MinimumResyncInterval { get; }
    public TimeSpan PresenceDelay { get; }

    /// <summary>When the last successful list was applied (null before one).</summary>
    public DateTimeOffset? LastSyncedAt { get; private set; }

    /// <summary>How many times the kernel's list was read (tests).</summary>
    internal int Lists => Volatile.Read(ref _lists);

    /// <summary>How many times the presence loop woke up (tests: it sleeps while nothing is queued).</summary>
    internal int PresenceWakeups => Volatile.Read(ref _presenceWakeups);

    public void Start()
    {
        _loop ??= Task.Run(() => LoopAsync(_life.Token));
        _presenceLoop ??= Task.Run(() => PresenceLoopAsync(_life.Token));
    }

    /// <summary>Asks for a sync now (the kernel said a node changed).</summary>
    public void RequestSync() => _wake.Release();

    /// <summary>
    /// Reads the kernel's node list and applies it, from a read that starts after this call: a node paired
    /// just before it is in the answer. Concurrent callers share one read; a read already under way when
    /// this is called (it may predate the pairing) is awaited and followed by a new one. Reads start at
    /// least <see cref="MinimumResyncInterval"/> apart: a caller waits for the next one, it is never refused.
    /// True when the kernel answered and the registry holds its answer; false when it could not say.
    /// </summary>
    public async Task<bool> RefreshAsync(CancellationToken ct)
    {
        while (true)
        {
            SyncFlight flight;
            bool fresh;
            lock (_flightGate)
            {
                if (_life.IsCancellationRequested) return false;
                if (_flight is { Started: false } pending) (flight, fresh) = (pending, true);
                else if (_flight is { } running && !running.Task.IsCompleted) (flight, fresh) = (running, false);
                else (flight, fresh) = (_flight = Launch(), true);
            }
            if (fresh) return await flight.Task.WaitAsync(ct);
            // A read already under way may have started before what the caller must see: wait it out, go again.
            await flight.Task.WaitAsync(ct);
        }
    }

    /// <summary>One read of the kernel's list; <see cref="Started"/> flips (under the gate) as the read begins.</summary>
    private sealed class SyncFlight
    {
        public bool Started;
        public Task<bool> Task = null!;
    }

    private SyncFlight Launch()
    {
        var delay = _lastListAt == 0 ? TimeSpan.Zero : MinimumResyncInterval - _time.GetElapsedTime(_lastListAt);
        var flight = new SyncFlight();
        var ct = _life.Token;
        flight.Task = Task.Run(() => FlyAsync(flight, delay, ct));
        return flight;
    }

    private async Task<bool> FlyAsync(SyncFlight flight, TimeSpan delay, CancellationToken ct)
    {
        try
        {
            if (delay > TimeSpan.Zero) await Task.Delay(delay, _time, ct);
            lock (_flightGate)
            {
                flight.Started = true;
                _lastListAt = _time.GetTimestamp();
            }
            Interlocked.Increment(ref _lists);
            var nodes = await _kernel.ListAsync(ct);
            if (nodes is null)
            {
                Problem("the kernel's node list is unavailable");
                return false;
            }
            _registry.ApplyKernelNodes(nodes);
            LastSyncedAt = _time.GetUtcNow();
            lock (_loggedProblems)
                if (_loggedProblems.Count > 0)
                {
                    _loggedProblems.Clear();
                    _log("[Nodes] Kernel sync: the kernel's node list is available again");
                }
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception error)
        {
            Problem(error.Message);
            return false;
        }
    }

    /// <summary>Node zero's entity (<c>PUT /api/nodes/local</c>), from <c>node.json</c> and the detected platform.</summary>
    public Task<bool> PutLocalAsync(CancellationToken ct)
    {
        var local = _registry.Local;
        return _kernel.PutLocalAsync(new JsonObject
        {
            ["id"] = local.Id,
            ["name"] = local.Name,
            ["platform"] = Platform(local),
            ["agentVersion"] = local.AgentVersion,
        }, ct);
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                // Only a persisted identity is node zero's entity: an unpersisted id is new every run.
                if (_registry.StampNodeId is not null && !await PutLocalAsync(ct)) Problem("node zero could not be reported");
                await RefreshAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception error)
            {
                Problem(error.Message);
            }

            try
            {
                using var timer = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timer.CancelAfter(Interval);
                await _wake.WaitAsync(timer.Token);
                // Several requests that piled up are one sync.
                while (_wake.CurrentCount > 0) await _wake.WaitAsync(ct);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // the interval elapsed
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private void Problem(string problem)
    {
        // Each distinct problem is logged once until a sync succeeds, not every five minutes.
        lock (_loggedProblems)
            if (!_loggedProblems.Add(problem)) return;
        var detail = (_kernel as KernelNodeClient)?.LastProblem;
        _log($"[Nodes] Kernel sync: {problem}{(detail is null ? "" : $" ({detail})")}");
    }

    private void OnStatusChanged(NodeStatusChange change) => QueuePresence(change.Node.Id);

    private void QueuePresence(string nodeId)
    {
        if (nodeId == _registry.LocalNodeId) return;
        lock (_pendingGate) _pendingPresence.Add(nodeId);
        _presenceWake.Release();
    }

    /// <summary>Sleeps until a remote node's presence is queued (with no remote node it never wakes), then coalesces.</summary>
    private async Task PresenceLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await _presenceWake.WaitAsync(ct);
                Interlocked.Increment(ref _presenceWakeups);
                await Task.Delay(PresenceDelay, _time, ct);
                // Everything queued during the delay goes in this flush.
                while (_presenceWake.Wait(0)) { }
                await FlushPresenceAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception error)
            {
                Problem($"presence: {error.Message}");
            }
        }
    }

    /// <summary>Pushes every queued node's presence once (tests call it directly).</summary>
    public async Task FlushPresenceAsync(CancellationToken ct)
    {
        string[] due;
        lock (_pendingGate)
        {
            due = [.. _pendingPresence];
            _pendingPresence.Clear();
        }
        foreach (var nodeId in due)
        {
            if (_registry.Find(nodeId) is not { Kind: NodeKind.Remote } node || PresenceBody(node) is not { } body) continue;
            if (!await _kernel.PutPresenceAsync(nodeId, body, ct)) Problem($"presence of {nodeId} was not taken");
        }
    }

    /// <summary>The presence body for a remote node; null for a revoked one (the kernel owns that status).</summary>
    internal static JsonObject? PresenceBody(NodeDescriptor node)
    {
        var status = node.Status switch
        {
            NodeStatus.Online or NodeStatus.PendingConfirmation => "online",
            NodeStatus.Offline => "offline",
            NodeStatus.NeedsUpdate => "needs-update",
            _ => null,
        };
        if (status is null) return null;
        var body = new JsonObject
        {
            ["status"] = status,
            ["statusReason"] = node.Status == NodeStatus.Offline ? node.StatusReason : null,
        };
        if (node.Os.Length > 0) body["platform"] = Platform(node);
        if (node.AgentVersion.Length > 0) body["agentVersion"] = node.AgentVersion;
        if (node.Link is { } link) body["protocolVersion"] = link.Protocol;
        if (node.Report is { } report)
            body["report"] = new JsonObject
            {
                ["processor"] = node.ProcessorName,
                ["clis"] = new JsonArray(report.Clis.Select(cli => (JsonNode)new JsonObject
                {
                    ["name"] = cli.Name,
                    ["installed"] = cli.Installed,
                    ["version"] = cli.Version,
                    ["signedIn"] = cli.SignedIn,
                    ["authMethod"] = cli.AuthMethod,
                }).ToArray()),
                // Names and totals only: the kernel entity is not a live usage feed.
                ["hardware"] = report.Hardware is not { } hardware ? null : new JsonObject
                {
                    ["cpuCores"] = hardware.Cpu.CoreCount,
                    ["ramTotalBytes"] = hardware.Ram.TotalBytes,
                    ["gpus"] = new JsonArray(hardware.Gpus.Select(gpu => (JsonNode)new JsonObject
                    {
                        ["name"] = gpu.Name,
                        ["vramTotalBytes"] = gpu.Memory.TotalBytes,
                    }).ToArray()),
                },
            };
        return body;
    }

    private static JsonObject Platform(NodeDescriptor node) => new()
    {
        ["os"] = node.Os,
        ["arch"] = node.Architecture,
        ["osDescription"] = node.OsDescription,
        ["machineName"] = node.MachineName,
    };

    public async ValueTask DisposeAsync()
    {
        _registry.StatusChanged -= OnStatusChanged;
        _registry.Updated -= QueuePresence;
        lock (_flightGate) _life.Cancel();
        foreach (var task in new[] { _loop, _presenceLoop, _flight?.Task })
            if (task is not null)
                try { await task; }
                catch (OperationCanceledException) { }
        _life.Dispose();
    }
}
