using RedCompute.Core.Capabilities;
using RedCompute.Core.Jobs;

namespace RedCompute.Core.Providers;

public interface IBackendProvider : IAsyncDisposable
{
    string Name { get; }
    CapabilityType Capability { get; }
    Task<bool> StartAsync(CancellationToken ct = default);
    Task StopAsync(CancellationToken ct = default);
    Task<BackendStatus> GetStatusAsync(CancellationToken ct = default);
    string? GetProxyTargetUrl();
    Task<JobResult?> ExecuteAsync(JobRequest request, CancellationToken ct = default);
    TimeSpan HealthCheckInterval { get; }
}
