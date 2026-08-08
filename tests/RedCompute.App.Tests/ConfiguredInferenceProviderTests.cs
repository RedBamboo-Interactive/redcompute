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
    public void EmptyQualityModeCatalogDoesNotExposeCapabilityOnlyProviders()
    {
        var providers = new[]
        {
            Provider("suno", "Suno"),
            Provider("comfyui", "ComfyUI"),
        };

        var result = UnifiedSessionEndpoints.FilterInferenceProviders(providers, []);

        Assert.Empty(result);
    }

    private static ProviderEntityConfig Provider(string slug, string name) =>
        new(slug, slug, name, "test", null, null, null, null, "active", null);

    private static QualityMode Mode(string slug, string provider) =>
        new(slug, slug, "deep", provider, "test-model", null, null, null, null, false, null);
}
