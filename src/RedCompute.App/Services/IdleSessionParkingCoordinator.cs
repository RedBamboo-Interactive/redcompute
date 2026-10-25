using System.Collections.Concurrent;
using RedCompute.Core.Sessions;
using RedCompute.PluginSdk;

namespace RedCompute.App.Services;

/// <summary>
/// Owns the suite-wide resident-process idle policy. Providers keep responsibility for the
/// atomic process teardown, while the coordinator serializes against durable input admission.
/// </summary>
/// <remarks>
/// Its eligibility clock (Idle, every background task terminal, nothing queued) also answers a
/// second question: has a session settled? A background task can end without the provider
/// waking a new turn (the Claude CLI wakes on some endings and not others, and does not say
/// which), so <see cref="Settled"/> fires once per eligible period after a short settle window.
/// A woken turn leaves eligibility first, so the two never both apply. With parking disabled
/// (an infinite idle timeout) the clock still runs and only parking is skipped.
/// <para>
/// A supervisor that delegated work is waiting, not idle: while any session bound to it as its
/// supervisor is still working (a turn running or background work alive), it stays resident.
/// Waiting is not idle time, so the hold restarts the supervisor's idle clock: once the delegate
/// stops working, its completion callback has a full timeout to arrive before parking applies.
/// The hold is derived from the delegate's live state on every check, so a delegate that ends,
/// parks or is stopped releases it without any bookkeeping.
/// </para>
/// </remarks>
public sealed class IdleSessionParkingCoordinator
{
    private static readonly TimeSpan DefaultSweepInterval = TimeSpan.FromMinutes(1);
    /// <summary>Well above the provider's wake latency (Claude CLI 2.1.309: 0.5-1.9 s observed).</summary>
    internal static readonly TimeSpan DefaultSettleAfter = TimeSpan.FromSeconds(10);
    private readonly CapabilityRegistry _registry;
    private readonly SessionInputQueueService _inputQueue;
    private readonly TimeSpan _idleTimeout;
    private readonly TimeSpan _sweepInterval;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly Action<string, Guid?> _log;
    private readonly Func<UnifiedSessionInfo, bool> _keepResident;
    private readonly Func<string, IReadOnlyCollection<Guid>> _supervisedJobs;
    private readonly ConcurrentDictionary<string, byte> _heldLogged = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _eligibleSince = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _settled = new(StringComparer.Ordinal);
    private readonly TimeSpan _settleAfter;

    /// <summary>
    /// A session has been continuously Idle with nothing running and nothing queued for the settle
    /// window: (providerId, sessionId). Raised once per eligible period.
    /// </summary>
    public event Action<string, string>? Settled;

    public IdleSessionParkingCoordinator(
        CapabilityRegistry registry,
        SessionInputQueueService inputQueue,
        TimeSpan idleTimeout,
        Action<string, Guid?> log,
        TimeSpan? sweepInterval = null,
        Func<DateTimeOffset>? utcNow = null,
        Func<UnifiedSessionInfo, bool>? keepResident = null,
        TimeSpan? settleAfter = null,
        Func<string, IReadOnlyCollection<Guid>>? supervisedJobs = null)
    {
        if (idleTimeout <= TimeSpan.Zero && idleTimeout != Timeout.InfiniteTimeSpan)
            throw new ArgumentOutOfRangeException(nameof(idleTimeout));
        _settleAfter = settleAfter ?? DefaultSettleAfter;
        _registry = registry;
        _inputQueue = inputQueue;
        _idleTimeout = idleTimeout;
        _sweepInterval = sweepInterval ?? DefaultSweepInterval;
        if (_sweepInterval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(sweepInterval));
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _log = log;
        _keepResident = keepResident ?? (_ => false);
        _supervisedJobs = supervisedJobs ?? (_ => []);

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
            _settled.Clear();
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
                    Forget(key);
                    continue;
                }

                var since = _eligibleSince.GetOrAdd(key, now);
                if (now - since >= _settleAfter && _settled.TryAdd(key, 0))
                {
                    try { Settled?.Invoke(provider.ProviderId, candidate.Id); }
                    catch (Exception ex) { _log($"[IdleParking] Settled handler failed for {candidate.Id}: {ex.Message}", candidate.JobId); }
                }
                if (_idleTimeout == Timeout.InfiniteTimeSpan || now - since < _idleTimeout) continue;
                // Checked only once the clock has expired: the resident mark and the delegate
                // lookup are job reads, and a session held mid-idle must still never be parked.
                if (_keepResident(candidate)) continue;
                if (SupervisesWorkingDelegate(candidate.Id))
                {
                    _eligibleSince[key] = now;
                    if (_heldLogged.TryAdd(key, 0))
                        _log($"[IdleParking] Holding {provider.ProviderId} session {candidate.Id}: a delegated session is still working", candidate.JobId);
                    continue;
                }

                var parked = false;
                await _inputQueue.WithSessionLockAsync(candidate.Id, async () =>
                {
                    if (await _inputQueue.HasQueuedInputAsync(candidate.Id, ct)) return;
                    var current = provider.GetResidentIdleSessions()
                        .FirstOrDefault(session => string.Equals(session.Id, candidate.Id, StringComparison.Ordinal));
                    if (current is null || !IsProviderEligible(current) || _keepResident(current)
                        || SupervisesWorkingDelegate(current.Id)) return;
                    parked = await provider.TryParkIdleSessionAsync(
                        candidate.Id, SessionStopReasons.IdleTimeout, ct);
                }, ct);

                if (!parked) continue;
                Forget(key);
                _log($"[IdleParking] Parked {provider.ProviderId} session {candidate.Id} after {_idleTimeout.TotalMinutes:0.#} idle minute(s)", candidate.JobId);
            }
        }

        foreach (var key in _eligibleSince.Keys)
            if (!seen.Contains(key)) Forget(key);
    }

    private void Forget(string key)
    {
        _eligibleSince.TryRemove(key, out _);
        _settled.TryRemove(key, out _);
        _heldLogged.TryRemove(key, out _);
    }

    /// <summary>
    /// True while a session bound to <paramref name="supervisorSessionId"/> is running a turn or
    /// still has background work alive. Statuses come from the providers' per-job status read,
    /// which never loads a transcript.
    /// </summary>
    private bool SupervisesWorkingDelegate(string supervisorSessionId)
    {
        var jobs = _supervisedJobs(supervisorSessionId);
        if (jobs.Count == 0) return false;
        foreach (var provider in _registry.FindProviders<ISessionProvider>()
                     .Distinct<ISessionProvider>(ReferenceEqualityComparer.Instance))
        {
            if (provider.GetSessionStatusesByJobIds(jobs).Values
                .Any(status => status is SessionStatus.Active or SessionStatus.Starting))
                return true;
        }
        foreach (var provider in _registry.FindProviders<IIdleSessionParkingProvider>()
                     .Distinct<IIdleSessionParkingProvider>(ReferenceEqualityComparer.Instance))
        {
            if (provider.GetResidentIdleSessions().Any(session => session.JobId is { } jobId
                    && jobs.Contains(jobId) && !IsProviderEligible(session)))
                return true;
        }
        return false;
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
            Forget(key);
    }

    private static string Key(string providerId, string sessionId) => $"{providerId}\n{sessionId}";
}
