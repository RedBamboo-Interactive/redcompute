using System.Collections.Concurrent;
using RedCompute.PluginSdk;

namespace RedCompute.App.Services;

/// <summary>
/// Serializes incompatible accelerator work and performs just-in-time release of
/// idle, explicitly registered residents when the requested residency class changes.
/// A completed lease leaves its residency warm for the next same-class job.
/// </summary>
public sealed class AcceleratorAdmissionCoordinator : IAcceleratorAdmissionCoordinator
{
    private sealed class AcceleratorState
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public object Sync { get; } = new();
        public Dictionary<Guid, AcceleratorResidentRegistration> Registrations { get; } = [];
        public string? ResidencyClass { get; set; }
    }

    private readonly ConcurrentDictionary<string, AcceleratorState> _accelerators =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Action<string> _log;

    public AcceleratorAdmissionCoordinator(Action<string>? log = null)
    {
        _log = log ?? (_ => { });
    }

    public IDisposable RegisterPreemptibleResident(AcceleratorResidentRegistration registration)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(registration.AcceleratorId);
        ArgumentException.ThrowIfNullOrWhiteSpace(registration.ResidencyClass);
        ArgumentException.ThrowIfNullOrWhiteSpace(registration.ParticipantId);
        ArgumentNullException.ThrowIfNull(registration.ReleaseAsync);

        var state = StateFor(registration.AcceleratorId);
        var token = Guid.NewGuid();
        lock (state.Sync)
            state.Registrations[token] = registration;
        return new Registration(() =>
        {
            lock (state.Sync)
                state.Registrations.Remove(token);
        });
    }

    public async ValueTask<IAsyncDisposable> AcquireAsync(
        AcceleratorWorkload workload,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workload.AcceleratorId);
        ArgumentException.ThrowIfNullOrWhiteSpace(workload.ResidencyClass);
        ArgumentException.ThrowIfNullOrWhiteSpace(workload.WorkloadName);

        var state = StateFor(workload.AcceleratorId);
        await state.Gate.WaitAsync(cancellationToken);
        try
        {
            if (!string.Equals(state.ResidencyClass, workload.ResidencyClass, StringComparison.OrdinalIgnoreCase))
            {
                AcceleratorResidentRegistration[] releases;
                lock (state.Sync)
                {
                    // Several capabilities can share one backend (for example image and
                    // music through one ComfyUI process). Release each participant once.
                    releases = state.Registrations.Values
                        .Where(r => !string.Equals(r.ResidencyClass, workload.ResidencyClass,
                            StringComparison.OrdinalIgnoreCase))
                        .GroupBy(r => r.ParticipantId, StringComparer.OrdinalIgnoreCase)
                        .Select(group => group.Last())
                        .ToArray();
                }

                foreach (var resident in releases)
                {
                    _log($"[Accelerator] Releasing {resident.ParticipantId} on {workload.AcceleratorId} " +
                         $"before {workload.WorkloadName}");
                    var result = await resident.ReleaseAsync(cancellationToken);
                    if (!result.Success)
                    {
                        throw new AcceleratorAdmissionException(
                            workload.AcceleratorId,
                            resident.ParticipantId,
                            result.Detail ?? $"{resident.ParticipantId} refused accelerator release");
                    }
                }

                state.ResidencyClass = workload.ResidencyClass;
            }

            return new Lease(state.Gate);
        }
        catch
        {
            state.Gate.Release();
            throw;
        }
    }

    private AcceleratorState StateFor(string acceleratorId)
        => _accelerators.GetOrAdd(acceleratorId, _ => new AcceleratorState());

    private sealed class Lease(SemaphoreSlim gate) : IAsyncDisposable
    {
        private SemaphoreSlim? _gate = gate;

        public ValueTask DisposeAsync()
        {
            Interlocked.Exchange(ref _gate, null)?.Release();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class Registration(Action dispose) : IDisposable
    {
        private Action? _dispose = dispose;
        public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
    }
}
