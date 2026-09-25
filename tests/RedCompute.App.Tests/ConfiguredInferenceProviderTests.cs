using RedCompute.App.Api.Endpoints;
using RedCompute.App.Services;
using Xunit;

namespace RedCompute.App.Tests;

public class ConfiguredInferenceProviderTests
{
    [Fact]
    public void FiltersUnifiedProviderCatalogToProvidersUsedByQualityModes()
    {
        var providers = new[]
        {
            Provider("suno", "Suno"),
            Provider("codex-default", "Codex"),
            Provider("OLLAMA-local", "Ollama"),
            Provider("comfyui", "ComfyUI"),
        };
        var modes = new[]
        {
            Mode("deep-codex", "codex-default"),
            Mode("deep-ollama", "ollama-LOCAL"),
        };

        var result = UnifiedSessionEndpoints.FilterInferenceProviders(providers, modes);

        Assert.Collection(result,
            provider => Assert.Equal("codex-default", provider.Slug),
            provider => Assert.Equal("OLLAMA-local", provider.Slug));
    }

    [Fact]
    public void InferenceCapabilityAdmitsProviderWithoutAQualityMode()
    {
        var providers = new[]
        {
            Provider("direct", "Direct", "ai-inference"),
            Provider("suno", "Suno", "music-generation"),
            Provider("comfyui", "ComfyUI", "image-generation"),
        };

        var result = UnifiedSessionEndpoints.FilterInferenceProviders(providers, []);

        Assert.Collection(result, provider => Assert.Equal("direct", provider.Slug));
    }

    private static ProviderEntityConfig Provider(string slug, string name, params string[] capabilities) =>
        new ProviderEntityConfig(slug, slug, name, "test", null, null, null, null, "active", null)
        {
            Capabilities = capabilities,
        };

    private static QualityMode Mode(string slug, string provider) =>
        new(slug, slug, "deep", provider, "test-model", null, null, null, null, false, null);
}
