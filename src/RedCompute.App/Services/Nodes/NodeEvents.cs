using System.Collections.Concurrent;
using RedCompute.App.Api.Endpoints;

namespace RedCompute.App.Services.Nodes;

/// <summary>
/// The node events on RedCompute's <c>/ws</c>, which the kernel relays verbatim (design 7.2). No event per
/// heartbeat or per hardware sample (the footprint audit's lesson):
/// <list type="bullet">
/// <item><c>node.status {nodeId, status, reason, lastSeenAt}</c> on a status transition only;</item>
/// <item><c>node.updated {nodeId}</c> when a node's identity, link or CLIs change, debounced per node
/// over <see cref="Debounce"/>: a burst (link up, describe, first report) is one event.</item>
/// </list>
/// </summary>
public sealed class NodeEvents : IDisposable
{
    public const string StatusEvent = "node.status";
    public const string UpdatedEvent = "node.updated";

    public static readonly TimeSpan DefaultDebounce = TimeSpan.FromSeconds(2);

    private readonly NodeRegistry _registry;
    private readonly Action<string, object> _broadcast;
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<string, ITimer> _pending = new(StringComparer.Ordinal);

    public NodeEvents(NodeRegistry registry, Action<string, object> broadcast, TimeSpan? debounce = null, TimeProvider? time = null)
    {
        _registry = registry;
        _broadcast = broadcast;
        _time = time ?? TimeProvider.System;
        Debounce = debounce ?? DefaultDebounce;
        _registry.StatusChanged += OnStatusChanged;
        _registry.Updated += OnUpdated;
    }

    public TimeSpan Debounce { get; }

    private void OnStatusChanged(NodeStatusChange change)
    {
        // A node seen for the first time (the boot sync) is not a transition; node.updated announces it.
        if (change.Previous is null) return;
        _broadcast(StatusEvent, new
        {
            nodeId = change.Node.Id,
            status = NodeEndpoints.StatusText(change.Node.Status),
            reason = change.Node.StatusReason,
            lastSeenAt = change.Node.LastSeenAt,
        });
    }

    private void OnUpdated(string nodeId)
    {
        // The first change in a quiet period arms the timer; later ones ride on it.
        var timer = _time.CreateTimer(_ => Fire(nodeId), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        if (_pending.TryAdd(nodeId, timer)) timer.Change(Debounce, Timeout.InfiniteTimeSpan);
        else timer.Dispose();
    }

    private void Fire(string nodeId)
    {
        if (_pending.TryRemove(nodeId, out var timer)) timer.Dispose();
        _broadcast(UpdatedEvent, new { nodeId });
    }

    public void Dispose()
    {
        _registry.StatusChanged -= OnStatusChanged;
        _registry.Updated -= OnUpdated;
        foreach (var timer in _pending.Values) timer.Dispose();
        _pending.Clear();
    }
}
