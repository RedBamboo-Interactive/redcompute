using System.Collections.Concurrent;
using RedCompute.PluginSdk;

namespace RedCompute.App.Services;

/// <summary>
/// Serializes incompatible accelerator work and performs just-in-time release of
/// idle, explicitly registered residents when the requested residency class changes.
/// A completed lease leaves its residency warm for the next same-class job.
/// <para>
/// Gates are keyed by node-qualified ids (<c>&lt;node&gt;/cuda:0</c>): an accelerator belongs to one
/// machine. A configured id without a node is this node's, so ComfyUI's and OpenCode's
/// <c>cuda:0</c> still meet on one gate. Logs and admission errors keep the configured id.
/// </para>
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
    private readonly string _nodeId;

    public AcceleratorAdmissionCoordinator(Action<string>? log = null, string? nodeId = null)
    {
        _log = log ?? (_ => { });
        _nodeId = string.IsNullOrWhiteSpace(nodeId) ? INodeRegistry.LocalAlias : nodeId;
    }

    /// <summary>
    /// The gate key of an accelerator id: an id that names its node is kept; otherwise it is on
    /// <paramref name="nodeId"/> (a remote node's, design 3.2) or, with none, on this node.
    /// </summary>
    internal string Qualify(string acceleratorId, string? nodeId = null)
        => acceleratorId.Contains('/') ? acceleratorId
            : $"{(string.IsNullOrWhiteSpace(nodeId) ? _nodeId : nodeId)}/{acceleratorId}";

    public IDisposable RegisterPreemptibleResident(AcceleratorResidentRegistration registration)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(registration.AcceleratorId);
        ArgumentException.ThrowIfNullOrWhiteSpace(registration.ResidencyClass);
        ArgumentException.ThrowIfNullOrWhiteSpace(registration.ParticipantId);
        ArgumentNullException.ThrowIfNull(registration.ReleaseAsync);

        var state = StateFor(registration.AcceleratorId, registration.NodeId);
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

        var state = StateFor(workload.AcceleratorId, workload.NodeId);
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

    private AcceleratorState StateFor(string acceleratorId, string? nodeId)
        => _accelerators.GetOrAdd(Qualify(acceleratorId, nodeId), _ => new AcceleratorState());

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
