using System.Text.Json;
using RedCompute.App.Services;
using RedCompute.Core.Configuration;
using Xunit;

namespace RedCompute.App.Tests;

public class ConfigSecretPersistenceTests
{
    [Fact]
    public void VaultedApiKeyIsAbsentFromSerializedConfigButRemainsAvailableAtRuntime()
    {
        var config = new RedComputeConfig
        {
            Capabilities = new Dictionary<string, CapabilityConfig>
            {
                ["tts"] = new()
                {
                    Providers = new Dictionary<string, ProviderConfig>
                    {
                        ["vaulted"] = new() { Type = "Cloud", ApiKey = "vault-secret" },
                        ["legacy"] = new() { Type = "Legacy", ApiKey = "legacy-secret" },
                    },
                },
            },
        };

        var json = ConfigManager.SerializeForPersistence(config, new[]
        {
            ConfigManager.ApiKeyCoordinate("tts", "vaulted"),
        });

        using var doc = JsonDocument.Parse(json);
        var providers = doc.RootElement.GetProperty("Capabilities").GetProperty("tts").GetProperty("Providers");
        Assert.False(providers.GetProperty("vaulted").TryGetProperty("ApiKey", out _));
        Assert.Equal("legacy-secret", providers.GetProperty("legacy").GetProperty("ApiKey").GetString());
        Assert.Equal("vault-secret", config.Capabilities["tts"].Providers["vaulted"].ApiKey);
    }

    [Fact]
    public void ShadowedExtensionPropertyDoesNotCrashSerializationOrMutateRuntimeConfig()
    {
        var provider = new ProviderConfig
        {
            Type = "Suno",
            Model = "V4_5",
            Extra = new Dictionary<string, object?>
            {
                ["Model"] = "legacy-model",
                ["BaseUrl"] = "https://example.test",
            },
        };
        var config = new RedComputeConfig
        {
            Capabilities = new Dictionary<string, CapabilityConfig>
            {
                ["music-gen"] = new()
                {
                    Providers = new Dictionary<string, ProviderConfig> { ["suno"] = provider },
                },
            },
        };

        var json = ConfigManager.SerializeForPersistence(config, []);

        using var doc = JsonDocument.Parse(json);
        var persisted = doc.RootElement.GetProperty("Capabilities").GetProperty("music-gen")
            .GetProperty("Providers").GetProperty("suno");
        Assert.Equal("V4_5", persisted.GetProperty("Model").GetString());
        Assert.Equal("https://example.test", persisted.GetProperty("BaseUrl").GetString());
        Assert.Equal("legacy-model", provider.Extra!["Model"]);
    }
}
