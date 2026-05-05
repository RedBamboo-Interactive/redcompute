using RedCompute.Core.Capabilities;
using RedCompute.Core.Jobs;
using RedCompute.Core.Providers;

namespace RedCompute.App.Services.Claude;

public class ClaudeCodeProvider : IBackendProvider
{
    private readonly ClaudeSessionService _sessionService;

    public string Name => "Claude Code";
    public CapabilityType Capability => CapabilityType.AiSession;
    public TimeSpan HealthCheckInterval => TimeSpan.FromSeconds(10);

    public ClaudeCodeProvider(ClaudeSessionService sessionService)
    {
        _sessionService = sessionService;
    }

    public Task<bool> StartAsync(CancellationToken ct = default) => Task.FromResult(true);

    public Task StopAsync(CancellationToken ct = default) => _sessionService.StopAllAsync();

    public Task<BackendStatus> GetStatusAsync(CancellationToken ct = default) => Task.FromResult(BackendStatus.Running);

    public string? GetProxyTargetUrl() => null;

    public Task<JobResult?> ExecuteAsync(JobRequest request, CancellationToken ct = default)
    {
        // Sessions are started via ClaudeSessionEndpoints, not through ExecuteAsync
        return Task.FromResult<JobResult?>(null);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
