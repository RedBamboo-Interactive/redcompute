using RedCompute.Core.Capabilities;
using RedCompute.Core.Configuration;
using RedCompute.Core.Providers;
using RedCompute.Providers.Local;

namespace RedCompute.App.Services;

public static class ProviderFactory
{
    public static IBackendProvider? Create(CapabilityType capability, ProviderConfig config, Action<string> log)
    {
        return config.Type switch
        {
            "LocalWsl" => new LocalWslProvider(config, capability, log),
            "LocalNative" => new LocalWslProvider(config, capability, log),
            _ => null
        };
    }
}
