namespace RedCompute.PluginSdk;

/// <summary>
/// Describes one bounded use of an accelerator. ResidencyClass identifies weights or
/// caches that can remain warm after the lease ends; it is deliberately provider-neutral.
/// </summary>
public sealed record AcceleratorWorkload(
    string AcceleratorId,
    string ResidencyClass,
    string WorkloadName);

public sealed record AcceleratorResidentRegistration(
    string AcceleratorId,
    string ResidencyClass,
    string ParticipantId,
    Func<CancellationToken, Task<AcceleratorReleaseResult>> ReleaseAsync);

public sealed record AcceleratorReleaseResult(bool Success, string? Detail = null)
{
    public static AcceleratorReleaseResult Released(string? detail = null) => new(true, detail);
    public static AcceleratorReleaseResult Refused(string detail) => new(false, detail);
}

public interface IAcceleratorAdmissionCoordinator
{
    IDisposable RegisterPreemptibleResident(AcceleratorResidentRegistration registration);

    ValueTask<IAsyncDisposable> AcquireAsync(
        AcceleratorWorkload workload,
        CancellationToken cancellationToken = default);
}

public sealed class AcceleratorAdmissionException : InvalidOperationException
{
    public string AcceleratorId { get; }
    public string ParticipantId { get; }

    public AcceleratorAdmissionException(string acceleratorId, string participantId, string message)
        : base(message)
    {
        AcceleratorId = acceleratorId;
        ParticipantId = participantId;
    }
}
