using RedCompute.App.Services;
using RedCompute.Core.Configuration;
using System.Net;
using System.Text;
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

    [Fact]
    public async Task CompletedLegacyImportIsNeverOfferedAgainFromHydratedRuntimeConfig()
    {
        var handler = new ImportHandler();
        var config = new RedComputeConfig
        {
            RedLeafUrl = "http://redleaf.test",
            Capabilities = new Dictionary<string, CapabilityConfig>
            {
                ["tts"] = new()
                {
                    Providers = new Dictionary<string, ProviderConfig>
                    {
                        ["elevenlabs"] = new() { Type = "ElevenLabs", ApiKey = "legacy-secret" },
                    },
                },
            },
        };
        var service = new ProviderConfigService(config, (_, _) => { }, new HttpClient(handler));

        Assert.True(await service.ImportLegacyApiKeysAsync());

        // The property intentionally remains populated for runtime use. A later vault
        // hydration has the same shape, so the completed migration flag is the guard.
        Assert.Equal("legacy-secret", config.Capabilities["tts"].Providers["elevenlabs"].ApiKey);
        Assert.False(await service.ImportLegacyApiKeysAsync());
        Assert.Equal(1, handler.Calls);
    }

    private sealed class ImportHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/api/internal/compute/provider-secrets/import", request.RequestUri?.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"accepted":[{"providerName":"elevenlabs","capabilitySlug":"tts"}],"missing":[]}""",
                    Encoding.UTF8,
                    "application/json"),
            });
        }
    }

    private static ProviderEntityConfig Provider(string id, string slug)
        => new(id, slug, slug, "backend", null, null, null, null, "active", null);
}
