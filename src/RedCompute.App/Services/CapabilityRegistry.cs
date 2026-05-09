using RedCompute.Core.Capabilities;
using RedCompute.Core.Configuration;
using RedCompute.Core.Providers;

namespace RedCompute.App.Services;

public class CapabilityEntry
{
    public required CapabilityDefinition Definition { get; init; }
    public required CapabilityConfig Config { get; init; }
    public Dictionary<string, IBackendProvider> Providers { get; init; } = new();
    public string? DefaultProviderName { get; set; }
    public bool IsSleeping { get; set; }
    public bool IsManuallyDisabled { get; set; }

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
        if (entry?.ActiveProvider == null) return BackendStatus.Stopped;
        return await entry.ActiveProvider.GetStatusAsync();
    }

    public async Task StopAll()
    {
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
