using System.Collections.Concurrent;
using RedCompute.Core.Sessions;
using RedCompute.PluginSdk;

namespace RedCompute.App.Services;

/// <summary>
/// Owns the suite-wide resident-process idle policy. Providers keep responsibility for the
/// atomic process teardown, while the coordinator serializes against durable input admission.
/// </summary>
public sealed class IdleSessionParkingCoordinator
{
    private static readonly TimeSpan DefaultSweepInterval = TimeSpan.FromMinutes(1);
    private readonly CapabilityRegistry _registry;
    private readonly SessionInputQueueService _inputQueue;
    private readonly TimeSpan _idleTimeout;
    private readonly TimeSpan _sweepInterval;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly Action<string, Guid?> _log;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _eligibleSince = new(StringComparer.Ordinal);

    public IdleSessionParkingCoordinator(
        CapabilityRegistry registry,
        SessionInputQueueService inputQueue,
        TimeSpan idleTimeout,
        Action<string, Guid?> log,
        TimeSpan? sweepInterval = null,
        Func<DateTimeOffset>? utcNow = null)
    {
        if (idleTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(idleTimeout));
        _registry = registry;
        _inputQueue = inputQueue;
        _idleTimeout = idleTimeout;
        _sweepInterval = sweepInterval ?? DefaultSweepInterval;
        if (_sweepInterval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(sweepInterval));
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _log = log;

        foreach (var provider in _registry.FindProviders<IIdleSessionParkingProvider>()
                     .Distinct<IIdleSessionParkingProvider>(ReferenceEqualityComparer.Instance))
        {
            if (provider is not IPluginEventSource events) continue;
            events.PluginEvent += (type, data) => ObserveProviderEvent(provider.ProviderId, type, data);
        }
    }

    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await SweepAsync(_utcNow(), ct);
                await Task.Delay(_sweepInterval, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _log($"[IdleParking] Sweep failed: {ex.Message}", null);
                try { await Task.Delay(_sweepInterval, ct); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            }
        }
    }

    internal async Task SweepAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        if (_inputQueue.IsDeliveryPaused)
        {
            _eligibleSince.Clear();
            return;
        }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var provider in _registry.FindProviders<IIdleSessionParkingProvider>()
                     .Distinct<IIdleSessionParkingProvider>(ReferenceEqualityComparer.Instance))
        {
            foreach (var candidate in provider.GetResidentIdleSessions())
            {
                ct.ThrowIfCancellationRequested();
                var key = Key(provider.ProviderId, candidate.Id);
                seen.Add(key);

                if (!IsProviderEligible(candidate) || await _inputQueue.HasQueuedInputAsync(candidate.Id, ct))
                {
                    _eligibleSince.TryRemove(key, out _);
                    continue;
                }

                var since = _eligibleSince.GetOrAdd(key, now);
                if (now - since < _idleTimeout) continue;

                var parked = false;
                await _inputQueue.WithSessionLockAsync(candidate.Id, async () =>
                {
                    if (await _inputQueue.HasQueuedInputAsync(candidate.Id, ct)) return;
                    var current = provider.GetResidentIdleSessions()
                        .FirstOrDefault(session => string.Equals(session.Id, candidate.Id, StringComparison.Ordinal));
                    if (current is null || !IsProviderEligible(current)) return;
                    parked = await provider.TryParkIdleSessionAsync(
                        candidate.Id, SessionStopReasons.IdleTimeout, ct);
                }, ct);

                if (!parked) continue;
                _eligibleSince.TryRemove(key, out _);
                _log($"[IdleParking] Parked {provider.ProviderId} session {candidate.Id} after {_idleTimeout.TotalMinutes:0.#} idle minute(s)", candidate.JobId);
            }
        }

        foreach (var key in _eligibleSince.Keys)
            if (!seen.Contains(key)) _eligibleSince.TryRemove(key, out _);
    }

    private static bool IsProviderEligible(UnifiedSessionInfo session)
        => session.Status == SessionStatus.Idle
           && session.BackgroundTasks.All(task => BackgroundTaskStatuses.IsTerminal(task.Status));

    private void ObserveProviderEvent(string providerId, string type, object data)
    {
        if (type is not ("session.created" or "session.updated") || data is not UnifiedSessionInfo session)
            return;
        var key = Key(providerId, session.Id);
        if (IsProviderEligible(session))
            _eligibleSince.TryAdd(key, _utcNow());
        else
            _eligibleSince.TryRemove(key, out _);
    }

    private static string Key(string providerId, string sessionId) => $"{providerId}\n{sessionId}";
}
