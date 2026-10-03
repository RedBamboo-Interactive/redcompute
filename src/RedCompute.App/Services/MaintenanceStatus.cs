namespace RedCompute.App.Services;

/// <summary>Aggregate operational progress, without session identities, titles or paths.</summary>
public sealed record MaintenanceStatusSnapshot(
    string State, bool Paused, string? RunId, DateTimeOffset? StartedAt,
    DateTimeOffset ChangedAt, int? ActiveTurnCount);

internal sealed class MaintenanceStatus(Action<Exception>? observerError = null)
{
    private readonly object _gate = new();
    private MaintenanceStatusSnapshot _current = new("idle", false, null, null, DateTimeOffset.UtcNow, null);
    public event Action<MaintenanceStatusSnapshot>? Changed;
    public MaintenanceStatusSnapshot Current { get { lock (_gate) return _current; } }

    public void Set(string state, string? runId, int? activeTurnCount = null)
    {
        MaintenanceStatusSnapshot snapshot;
        lock (_gate)
        {
            if (_current.State == state && _current.RunId == runId && _current.ActiveTurnCount == activeTurnCount) return;
            var now = DateTimeOffset.UtcNow;
            snapshot = new(state, state is "draining" or "launching", runId,
                runId is not null && runId == _current.RunId ? _current.StartedAt : runId is null ? null : now,
                now, activeTurnCount);
            _current = snapshot;
        }
        // Visibility must never prevent the deployment from starting or releasing its pause.
        foreach (var handler in Changed?.GetInvocationList() ?? [])
        {
            try { ((Action<MaintenanceStatusSnapshot>)handler)(snapshot); }
            catch (Exception ex) { observerError?.Invoke(ex); }
        }
    }
}
