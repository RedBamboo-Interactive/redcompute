using RedCompute.Core.Capabilities;
using RedCompute.Core.Configuration;
using RedCompute.Core.Providers;

namespace RedCompute.App.Services;

public class CapabilityEntry
{
    public required CapabilityDefinition Definition { get; init; }
    public required CapabilityConfig Config { get; init; }
    public IBackendProvider? ActiveProvider { get; set; }
}

public class CapabilityRegistry
{
    private readonly Dictionary<string, CapabilityEntry> _capabilities = new();

    public IReadOnlyDictionary<string, CapabilityEntry> Capabilities => _capabilities;

    public void Register(string slug, CapabilityDefinition definition, CapabilityConfig config, IBackendProvider? provider)
    {
        _capabilities[slug] = new CapabilityEntry
        {
            Definition = definition,
            Config = config,
            ActiveProvider = provider
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
            if (entry.ActiveProvider != null)
            {
                await entry.ActiveProvider.StopAsync();
                await entry.ActiveProvider.DisposeAsync();
            }
        }
    }
}
