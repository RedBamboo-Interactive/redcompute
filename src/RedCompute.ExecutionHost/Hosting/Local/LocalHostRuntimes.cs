using RedCompute.Core.Providers;

namespace RedCompute.PluginSdk.Hosting.Local;

/// <summary>
/// One backend a node-zero provider launches, as it registers it with
/// <see cref="LocalHostRuntimes.Register"/>. The delegates are the provider's own: node zero's
/// runtimes start and stop exactly as the provider always did.
/// </summary>
public sealed record LocalRuntimeRegistration(
    string Id,
    string Kind,
    string Label,
    string Capability,
    int? Port,
    string? Accelerator,
    Func<BackendStatus> Status,
    Func<string?> StatusDetail,
    Func<CancellationToken, Task<bool>> Start,
    Func<CancellationToken, Task> Stop);

/// <summary>
/// Node zero's <see cref="IHostRuntimes"/> (Phase 4, design 2.4): a registry the providers fill. It
/// wraps today's provider-driven launch and adds nothing to it; the provider still decides how its
/// backend starts (its <c>RuntimeRecipes</c> spec). Several providers can share a runtime (ComfyUI
/// serves image-gen and music-gen on one port): one entry, every capability, the first
/// registration's start and stop, and Running when any of them is.
/// </summary>
public sealed class LocalHostRuntimes : IHostRuntimes
{
    private readonly object _gate = new();
    private readonly List<LocalRuntimeRegistration> _registrations = [];

    public event Action<HostRuntime>? Changed;

    /// <summary>Adds a provider's backend; disposing the result removes it.</summary>
    public IDisposable Register(LocalRuntimeRegistration registration)
    {
        lock (_gate) _registrations.Add(registration);
        Raise(registration.Id);
        return new Registration(this, registration);
    }

    public IReadOnlyList<HostRuntime> List()
    {
        List<string> ids;
        lock (_gate) ids = _registrations.Select(registration => registration.Id).Distinct(StringComparer.Ordinal).ToList();
        return ids.Select(Status).OfType<HostRuntime>().ToList();
    }

    public HostRuntime? Status(string id)
    {
        var group = Group(id);
        if (group.Count == 0) return null;
        var first = group[0];
        var statuses = group.Select(registration => (registration, status: registration.Status())).ToList();
        var running = statuses.FirstOrDefault(item => item.status == BackendStatus.Running);
        var (shown, status) = running.registration is not null ? running : statuses[0];
        return new HostRuntime(
            first.Id, first.Kind, first.Label, HostRuntimeManagement.Spawn, Enabled: true, status, shown.StatusDetail(),
            first.Accelerator,
            group.Select(registration => registration.Capability).Distinct(StringComparer.Ordinal).ToList(),
            group.Select(registration => registration.Port).OfType<int>().Distinct().ToList(),
            StartedAt: null);
    }

    public async Task<HostRuntime> StartAsync(string id, CancellationToken ct = default)
    {
        var first = First(id);
        await first.Start(ct);
        return Raise(id)!;
    }

    public async Task<HostRuntime> StopAsync(string id, TimeSpan? grace = null, CancellationToken ct = default)
    {
        var first = First(id);
        await first.Stop(ct);
        return Raise(id)!;
    }

    private LocalRuntimeRegistration First(string id)
        => Group(id) is { Count: > 0 } group
            ? group[0]
            : throw new HostRuntimeException(HostRuntimeException.Unknown, $"No runtime has the id '{id}'");

    private List<LocalRuntimeRegistration> Group(string id)
    {
        lock (_gate) return _registrations.Where(registration => registration.Id == id).ToList();
    }

    private HostRuntime? Raise(string id)
    {
        var entry = Status(id);
        if (entry is not null) Changed?.Invoke(entry);
        return entry;
    }

    private void Remove(LocalRuntimeRegistration registration)
    {
        lock (_gate) _registrations.Remove(registration);
        Raise(registration.Id);
    }

    private sealed class Registration(LocalHostRuntimes owner, LocalRuntimeRegistration registration) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) owner.Remove(registration);
        }
    }
}
