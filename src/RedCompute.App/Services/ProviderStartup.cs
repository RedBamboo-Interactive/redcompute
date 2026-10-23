using System.Runtime.CompilerServices;
using RedCompute.Core.Providers;

namespace RedCompute.App.Services;

internal static class ProviderStartup
{
    private static readonly ConditionalWeakTable<IBackendProvider, SemaphoreSlim> Gates = new();

    public static async Task<BackendStatus> EnsureRunningAsync(
        IBackendProvider provider, CancellationToken ct = default)
    {
        var status = await provider.GetStatusAsync(ct);
        if (status == BackendStatus.Running) return status;

        var gate = Gates.GetValue(provider, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            status = await provider.GetStatusAsync(ct);
            if (status == BackendStatus.Running) return status;
            await provider.StartAsync(ct);
            return await provider.GetStatusAsync(ct);
        }
        finally
        {
            gate.Release();
        }
    }
}
