using RedCompute.App.Services;
using Xunit;

namespace RedCompute.App.Tests;

public class ProviderSecretSnapshotTests
{
    [Fact]
    public void AppliesSecretsByIdOrSlugWithoutChangingOtherProviderData()
    {
        var providers = new List<ProviderEntityConfig>
        {
            Provider("first-id", "first"),
            Provider("second-id", "second"),
            Provider("third-id", "third"),
        };
        const string snapshot = """
            {
              "items": [
                { "id": "first-id", "slug": "renamed", "apiKey": "first-secret" },
                { "id": "unknown-id", "slug": "second", "apiKey": "second-secret" }
              ]
            }
            """;

        var hydrated = ProviderConfigService.ApplyProviderSecrets(providers, snapshot);

        Assert.Equal("first-secret", hydrated[0].ApiKey);
        Assert.Equal("second-secret", hydrated[1].ApiKey);
        Assert.Null(hydrated[2].ApiKey);
        Assert.Equal("backend", hydrated[0].Backend);
    }

    [Fact]
    public void AppliesAnAuthoritativeClearAndIgnoresUnknownEntries()
    {
        var providers = new List<ProviderEntityConfig> { Provider("provider-id", "provider") };

        var hydrated = ProviderConfigService.ApplyProviderSecrets(providers,
            """{"items":[{"id":"provider-id","apiKey":""},{"slug":"unknown","apiKey":"secret"}]}""");

        Assert.Equal("", hydrated.Single().ApiKey);
        Assert.True(hydrated.Single().ApiKeyAuthoritative);
    }

    private static ProviderEntityConfig Provider(string id, string slug)
        => new(id, slug, slug, "backend", null, null, null, null, "active", null);
}
