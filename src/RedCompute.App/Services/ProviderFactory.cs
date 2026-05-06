using RedCompute.Core.Capabilities;
using RedCompute.Core.Configuration;
using RedCompute.Core.Providers;
using RedCompute.Providers.Anthropic;
using RedCompute.Providers.ComfyUI;
using RedCompute.Providers.Local;
using RedCompute.Providers.Suno;

namespace RedCompute.App.Services;

public static class ProviderFactory
{
    public static IBackendProvider? Create(CapabilityType capability, ProviderConfig config, Action<string> log)
    {
        return config.Type switch
        {
            "LocalWsl" => new LocalWslProvider(config, capability, log),
            "LocalNative" => new LocalWslProvider(config, capability, log),
            "ComfyUI" => new ComfyUIProvider(config, capability, log),
            "Suno" => new SunoProvider(config, capability, log),
            "Anthropic" => new AnthropicProvider(config, capability, log),
            _ => null
        };
    }
}
