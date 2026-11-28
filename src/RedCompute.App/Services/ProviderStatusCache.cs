using System.Runtime.CompilerServices;
using RedCompute.Core.Providers;

namespace RedCompute.App.Services;

/// <summary>One provider's last polled status and when it was taken.</summary>
public sealed record ProviderStatusReading(BackendStatus Status, DateTimeOffset At);

/// <summary>
/// The last status each of node zero's providers answered to the status poll (<c>RelayServer</c>, every
/// <see cref="PollInterval"/>). <c>/status</c>, <c>/nodes</c> and <c>/health</c> answer from it and never ask a
/// provider themselves, so one provider that is not running cannot make them wait. A provider the poll has not
/// reached yet has no reading. <c>/{slug}/generate</c> admits on a fresh Running reading and asks the provider
/// itself otherwise (<see cref="AdmissionStatusAsync"/>).
/// </summary>
public sealed class ProviderStatusCache
{
    /// <summary>How often RelayServer polls every provider.</summary>
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    /// <summary>How long one poll waits for its slowest provider before it moves on; the probe still records when it ends.</summary>
    public static readonly TimeSpan PollBudget = TimeSpan.FromSeconds(4);

    /// <summary>A reading younger than this is fresh enough to admit a job on: two poll intervals.</summary>
    public static readonly TimeSpan FreshFor = TimeSpan.FromSeconds(10);

    private readonly ConditionalWeakTable<IBackendProvider, Slot> _slots = new();
    private readonly Func<DateTimeOffset> _now;

    public ProviderStatusCache(Func<DateTimeOffset>? now = null) => _now = now ?? (() => DateTimeOffset.UtcNow);

    public DateTimeOffset Now => _now();

    public ProviderStatusReading? Get(IBackendProvider provider)
        => _slots.TryGetValue(provider, out var slot) ? slot.Reading : null;

    /// <summary>The last polled status; Stopped before the first poll reached the provider.</summary>
    public BackendStatus StatusOf(IBackendProvider provider) => Get(provider)?.Status ?? BackendStatus.Stopped;

    public void Record(IBackendProvider provider, BackendStatus status)
        => _slots.GetValue(provider, _ => new Slot()).Reading = new ProviderStatusReading(status, _now());

    /// <summary>Asks the provider and records its answer; a fault is Error.</summary>
    public async Task<BackendStatus> ProbeAsync(IBackendProvider provider, CancellationToken ct = default)
    {
        BackendStatus status;
        try { status = await provider.GetStatusAsync(ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { status = BackendStatus.Error; }
        Record(provider, status);
        return status;
    }

    /// <summary>
    /// One poll: every provider asked at once, at most one probe per provider in flight (one still running
    /// from an earlier poll is not asked again). Returns when all have answered or after <paramref name="budget"/>.
    /// </summary>
    public async Task RefreshAsync(IEnumerable<IBackendProvider> providers, TimeSpan budget)
    {
        var probes = new List<Task>();
        foreach (var provider in providers.Distinct(ReferenceEqualityComparer.Instance).Cast<IBackendProvider>())
        {
            var slot = _slots.GetValue(provider, _ => new Slot());
            lock (slot)
            {
                if (slot.InFlight is { IsCompleted: false } running)
                {
                    probes.Add(running);
                    continue;
                }
                slot.InFlight = Task.Run(() => ProbeAsync(provider));
                probes.Add(slot.InFlight);
            }
        }
        if (probes.Count == 0) return;
        try { await Task.WhenAll(probes).WaitAsync(budget); }
        catch (TimeoutException) { /* the slow ones record when they end */ }
    }

    /// <summary>
    /// The status a job is admitted on: a fresh Running reading admits without asking the provider; anything
    /// else (no reading, a stale one, or one that is not Running) asks the provider, so a provider started since
    /// the last poll is not refused and one known to be down is confirmed down.
    /// </summary>
    public async Task<BackendStatus> AdmissionStatusAsync(IBackendProvider provider, CancellationToken ct = default)
    {
        if (Get(provider) is { Status: BackendStatus.Running } reading && _now() - reading.At < FreshFor)
            return BackendStatus.Running;
        if (!_slots.TryGetValue(provider, out _))
            // A provider the poll never sees (one on a remote node): ask it, record nothing.
            return await provider.GetStatusAsync(ct);
        return await ProbeAsync(provider, ct);
    }

    /// <summary>The oldest reading among <paramref name="providers"/>, null when none has one yet.</summary>
    public DateTimeOffset? OldestReading(IEnumerable<IBackendProvider> providers)
    {
        DateTimeOffset? oldest = null;
        foreach (var provider in providers)
            if (Get(provider) is { } reading && (oldest is null || reading.At < oldest))
                oldest = reading.At;
        return oldest;
    }

    /// <summary>Seconds since <paramref name="at"/>, to the millisecond; null with no reading.</summary>
    public double? AgeSeconds(DateTimeOffset? at)
        => at is { } value ? Math.Round(Math.Max(0, (_now() - value).TotalSeconds), 3) : null;

    private sealed class Slot
    {
        public volatile ProviderStatusReading? Reading;
        public Task? InFlight;
    }
}
