using System.Collections.Concurrent;
using RedCompute.App.Services.Nodes;
using RedCompute.Core.Capabilities;
using RedCompute.Core.Configuration;
using RedCompute.Core.Providers;
using RedCompute.Core.Sessions;
using RedCompute.PluginSdk;

namespace RedCompute.App.Services;

public class CapabilityEntry
{
    public required CapabilityDefinition Definition { get; init; }
    public required CapabilityConfig Config { get; init; }
    /// <summary>Node zero's providers, by name. Unchanged by remote nodes.</summary>
    public Dictionary<string, IBackendProvider> Providers { get; init; } = new();

    /// <summary>
    /// The providers on remote nodes (Phase 4 P7, design 3.1), by node zero's provider name and the node: kept by
    /// <see cref="NodeProviderManager"/> from what each node's runtimes report. Empty with node zero only. Nothing
    /// that walks <see cref="Providers"/> (status, start-up, shutdown) sees them.
    /// </summary>
    public ConcurrentDictionary<NodeProviderKey, IBackendProvider> NodeProviders { get; } = new();
    public string? DefaultProviderName { get; set; }
    public bool IsSleeping { get; set; }
    public bool IsManuallyDisabled { get; set; }

    public bool IsExternal => Definition.ExecutionMode == CapabilityExecutionMode.External;

    public IBackendProvider? ActiveProvider =>
        DefaultProviderName != null && Providers.TryGetValue(DefaultProviderName, out var p) ? p : null;

    public IBackendProvider? ResolveProvider(string? requestedProvider)
    {
        if (requestedProvider != null && Providers.TryGetValue(requestedProvider, out var p))
            return p;
        return ActiveProvider;
    }
}

public class CapabilityRegistry
{
    private readonly Dictionary<string, CapabilityEntry> _capabilities = new();

    public IReadOnlyDictionary<string, CapabilityEntry> Capabilities => _capabilities;

    /// <summary>The status poll's last reading of each of node zero's providers; what the status routes answer from.</summary>
    public ProviderStatusCache ProviderStatuses { get; } = new();

    /// <summary>Node zero's providers, each once (one provider can serve several capabilities).</summary>
    public IReadOnlyList<IBackendProvider> NodeZeroProviders()
        => _capabilities.Values.ToArray().SelectMany(entry => entry.Providers.Values)
            .Distinct(ReferenceEqualityComparer.Instance).Cast<IBackendProvider>().ToList();

    /// <summary>One status poll of node zero's providers (RelayServer runs it every <see cref="ProviderStatusCache.PollInterval"/>).</summary>
    public Task PollProviderStatusesAsync()
        => ProviderStatuses.RefreshAsync(NodeZeroProviders(), ProviderStatusCache.PollBudget);

    /// <summary>A capability's default status from the last poll, without asking a provider.</summary>
    public BackendStatus CachedStatus(CapabilityEntry entry)
        => entry.IsExternal ? BackendStatus.Running
            : entry.ActiveProvider is { } active ? ProviderStatuses.StatusOf(active)
            : BackendStatus.Stopped;

    public void Register(string slug, CapabilityDefinition definition, CapabilityConfig config,
        Dictionary<string, IBackendProvider> providers, string? defaultProviderName)
    {
        _capabilities[slug] = new CapabilityEntry
        {
            Definition = definition,
            Config = config,
            Providers = providers,
            DefaultProviderName = defaultProviderName
        };
    }

    public CapabilityEntry? Get(string slug)
    {
        return _capabilities.GetValueOrDefault(slug);
    }

    public async Task<BackendStatus> GetStatus(string slug)
    {
        var entry = Get(slug);
        if (entry?.IsExternal == true) return BackendStatus.Running;
        if (entry?.ActiveProvider == null) return BackendStatus.Stopped;
        return await entry.ActiveProvider.GetStatusAsync();
    }

    public IEnumerable<T> FindProviders<T>() where T : class
    {
        foreach (var entry in _capabilities.Values)
            foreach (var provider in entry.Providers.Values)
                if (provider is T t)
                    yield return t;
    }

    /// <summary>
    /// Warms every registered provider that implements <see cref="IProviderWarmup"/>, the capability's default or not
    /// (start-up starts only the default). One failure is logged and never stops the others.
    /// </summary>
    public Task WarmProvidersAsync(Action<string> log, CancellationToken ct = default)
        => WarmAsync(FindProviders<IProviderWarmup>().Distinct<IProviderWarmup>(ReferenceEqualityComparer.Instance), log, ct);

    public static Task WarmAsync(IEnumerable<IProviderWarmup> providers, Action<string> log, CancellationToken ct = default)
        => Task.WhenAll(providers.Select(async provider =>
        {
            try { await provider.WarmAsync(ct); }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                log($"[App] Warm-up failed for {provider.GetType().Name}: {ex.Message}");
            }
        }));

    public async Task StopAll(string? sessionStopReason = null)
    {
        if (!string.IsNullOrWhiteSpace(sessionStopReason))
        {
            foreach (var provider in FindProviders<ISessionProvider>()
                         .Distinct<ISessionProvider>(ReferenceEqualityComparer.Instance))
            {
                var sessions = provider.GetSessions(100_000, includeDismissed: false)
                    .Where(session => session.Status is SessionStatus.Starting or SessionStatus.Active or SessionStatus.Idle)
                    .ToArray();
                foreach (var session in sessions)
                    await provider.ForceKillAsync(session.Id, sessionStopReason);
            }
        }

        foreach (var entry in _capabilities.Values)
        {
            foreach (var provider in entry.Providers.Values)
            {
                await provider.StopAsync();
                await provider.DisposeAsync();
            }
        }
    }
}
