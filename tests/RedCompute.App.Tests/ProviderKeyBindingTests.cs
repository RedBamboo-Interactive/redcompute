using System.Net;
using System.Net.WebSockets;
using System.Text;
using RedCompute.App.Api.Endpoints;
using RedCompute.App.Services;
using RedCompute.Core.Configuration;
using RedCompute.Core.Jobs;
using RedCompute.Plugin.OpenAILive;
using RedCompute.Plugin.Suno;
using RedCompute.PluginSdk;
using Xunit;

namespace RedCompute.App.Tests;

/// <summary>
/// A vaulted provider key leaves RedCompute only for the origin RedLeaf bound it to,
/// even when the cached provider's endpoint changes afterwards. Fake keys only.
/// </summary>
public sealed class ProviderKeyBindingTests
{
    private const string FakeKey = "sk-test-fake-key";

    [Theory]
    [InlineData("https://API.OpenAI.com/v1", "https://api.openai.com:443")]
    [InlineData("wss://api.openai.com/v1/live", "https://api.openai.com:443")]
    [InlineData("http://localhost:11434/v1", "http://localhost:11434")]
    [InlineData("https://user:pw@api.sunoapi.org:8443/", "https://api.sunoapi.org:8443")]
    [InlineData("api.openai.com", null)]
    [InlineData("ftp://api.openai.com", null)]
    public void Normalize_MatchesRedLeafOriginRule(string url, string? expected)
        => Assert.Equal(expected, ProviderKeyBindings.Normalize(url));

    [Fact]
    public void Snapshot_ReleasesOnlyKeysThatCarryABinding()
    {
        var providers = new List<ProviderEntityConfig> { Provider("a"), Provider("b"), Provider("c") };

        var hydrated = ProviderConfigService.ApplyProviderSecrets(providers, $$"""
            {"items":[
              {"id":"a","apiKey":"{{FakeKey}}","boundDestination":"https://api.sunoapi.org:443"},
              {"id":"b","apiKey":"{{FakeKey}}"},
              {"id":"c","apiKey":null,"keyError":"endpoint_changed",
               "keyErrorMessage":"endpoint changed since the key was stored; re-enter the API key"}
            ]}
            """);

        Assert.Equal(FakeKey, hydrated[0].ApiKey);
        Assert.Equal("https://api.sunoapi.org:443", hydrated[0].ApiKeyDestination);
        Assert.Null(hydrated[1].ApiKey);
        Assert.Null(hydrated[2].ApiKey);
        Assert.Equal(ProviderKeyBindings.MismatchMessage, hydrated[2].ApiKeyError);

        var config = new ProviderConfig { Type = "Suno" };
        ProviderConfigService.ApplyEntityToProvider(hydrated[0], config);
        Assert.Equal(new ProviderKeyBinding(FakeKey, "https://api.sunoapi.org:443"), config.KeyBinding);
        ProviderConfigService.ApplyEntityToProvider(hydrated[2], config);
        Assert.Null(config.ApiKey);
        Assert.Null(config.KeyBinding);
    }

    [Fact]
    public async Task Suno_CachedKey_SendsToItsBoundHost()
    {
        var requests = new List<HttpRequestMessage>();
        var config = SunoConfig("https://api.sunoapi.org");
        await using var provider = new SunoProvider(config, "music-gen", _ => { },
            new HttpClient(new Recorder(requests, HttpStatusCode.OK, "{\"code\":200,\"data\":{\"taskId\":\"t\"}}")),
            new HttpClient(new Recorder([], HttpStatusCode.OK, "")),
            new SunoProviderTiming(TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(50)));

        Assert.True(await provider.StartAsync());
        await provider.ExecuteAsync(GenerateRequest());

        Assert.NotEmpty(requests);
        Assert.All(requests, request =>
        {
            Assert.Equal("api.sunoapi.org", request.RequestUri!.Host);
            Assert.Equal(FakeKey, request.Headers.Authorization!.Parameter);
        });
    }

    [Fact]
    public async Task Suno_BaseUrlChangedAfterCaching_NothingIsSentToTheNewHost()
    {
        var requests = new List<HttpRequestMessage>();
        var config = SunoConfig("https://api.sunoapi.org");
        // The key was cached while bound to the official host; the non-secret base URL changes later.
        config.Extra!["BaseUrl"] = "https://attacker.example.test";
        await using var provider = new SunoProvider(config, "music-gen", _ => { },
            new HttpClient(new Recorder(requests, HttpStatusCode.OK, "{\"code\":200,\"data\":{\"taskId\":\"t\"}}")),
            new HttpClient(new Recorder([], HttpStatusCode.OK, "")),
            new SunoProviderTiming(TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(50)));

        Assert.False(await provider.StartAsync());
        var result = await provider.ExecuteAsync(GenerateRequest());

        Assert.False(result!.Success);
        Assert.Contains(ProviderKeyBindings.MismatchMessage, result.ErrorMessage);
        Assert.Empty(requests);
    }

    [Fact]
    public async Task Suno_RebindingAfterConstruction_StopsTheRunningInstance()
    {
        var requests = new List<HttpRequestMessage>();
        var config = SunoConfig("https://api.sunoapi.org");
        await using var provider = new SunoProvider(config, "music-gen", _ => { },
            new HttpClient(new Recorder(requests, HttpStatusCode.OK, "{\"code\":200,\"data\":{\"taskId\":\"t\"}}")),
            new HttpClient(new Recorder([], HttpStatusCode.OK, "")),
            new SunoProviderTiming(TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(50)));
        Assert.True(await provider.StartAsync());

        // RedLeaf rebinds the key elsewhere; the instance still holds the old base URL.
        config.KeyBinding = new ProviderKeyBinding(FakeKey, "https://proxy.example.test:443");
        var result = await provider.ExecuteAsync(GenerateRequest());

        Assert.False(result!.Success);
        Assert.Empty(requests);
    }

    [Fact]
    public async Task OpenAILive_KeyIsSentOnlyToItsBoundHost()
    {
        var bound = new List<HttpRequestMessage>();
        var provider = new OpenAILiveProvider(LiveConfig(), _ => { },
            new HttpClient(new Recorder(bound, HttpStatusCode.InternalServerError, "{}"))
            {
                BaseAddress = new Uri("https://api.openai.com"),
            },
            () => throw new InvalidOperationException("no socket in this test"));
        await Assert.ThrowsAsync<OpenAILiveException>(() => provider.CreateVoiceSessionAsync(
            LiveRequest(), _ => Task.CompletedTask, CancellationToken.None));
        Assert.Equal($"Bearer {FakeKey}", Assert.Single(bound).Headers.Authorization!.ToString());

        var redirected = new List<HttpRequestMessage>();
        var moved = new OpenAILiveProvider(LiveConfig(), _ => { },
            new HttpClient(new Recorder(redirected, HttpStatusCode.Created, "{}"))
            {
                BaseAddress = new Uri("https://attacker.example.test"),
            },
            () => new ClientWebSocket());
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => moved.CreateVoiceSessionAsync(
            LiveRequest(), _ => Task.CompletedTask, CancellationToken.None));
        Assert.Equal(ProviderKeyBindings.MismatchMessage, error.Message);
        Assert.Empty(redirected);
    }

    [Fact]
    public void ExecuteEnv_InjectsKeyOnlyForItsBoundBaseUrl()
    {
        const string openAi = "https://api.openai.com:443";

        var official = UnifiedSessionEndpoints.WithProviderCredentialEnv(null, null, FakeKey, openAi);
        Assert.Equal(FakeKey, official!["OPENAI_API_KEY"]);

        var moved = UnifiedSessionEndpoints.WithProviderCredentialEnv(
            null, "https://attacker.example.test/v1", FakeKey, openAi);
        Assert.Equal("https://attacker.example.test/v1", moved!["OPENAI_BASE_URL"]);
        Assert.False(moved.ContainsKey("OPENAI_API_KEY"));

        var callerBase = UnifiedSessionEndpoints.WithProviderCredentialEnv(
            new Dictionary<string, string> { ["openai_base_url"] = "https://attacker.example.test" },
            null, FakeKey, openAi);
        Assert.False(callerBase!.ContainsKey("OPENAI_API_KEY"));

        var proxied = UnifiedSessionEndpoints.WithProviderCredentialEnv(
            new Dictionary<string, string> { ["HTTPS_PROXY"] = "http://attacker.example.test:8080" },
            null, FakeKey, openAi);
        Assert.False(proxied!.ContainsKey("OPENAI_API_KEY"));

        var unbound = UnifiedSessionEndpoints.WithProviderCredentialEnv(null, null, FakeKey, null);
        Assert.Null(unbound);

        var custom = UnifiedSessionEndpoints.WithProviderCredentialEnv(
            null, "http://localhost:11434/v1", FakeKey, "http://localhost:11434");
        Assert.Equal(FakeKey, custom!["OPENAI_API_KEY"]);
    }

    private static ProviderEntityConfig Provider(string id)
        => new(id, id, id, "backend", null, null, null, null, "active", null);

    private static ProviderConfig SunoConfig(string baseUrl) => new()
    {
        Type = "Suno",
        ApiKey = FakeKey,
        KeyBinding = new ProviderKeyBinding(FakeKey, "https://api.sunoapi.org:443"),
        Extra = new Dictionary<string, object?> { ["BaseUrl"] = baseUrl, ["Model"] = "V5" },
    };

    private static ProviderConfig LiveConfig() => new()
    {
        Type = "OpenAILive",
        ApiKey = FakeKey,
        KeyBinding = new ProviderKeyBinding(FakeKey, "https://api.openai.com:443"),
    };

    private static JobRequest GenerateRequest() => new()
    {
        CapabilitySlug = "music-gen",
        Parameters = new Dictionary<string, object?>
        {
            ["operation"] = "generate",
            ["prompt"] = "prompt",
            ["style"] = "style",
            ["title"] = "title",
            ["instrumental"] = true,
        },
    };

    private static VoiceSessionCreateRequest LiveRequest()
        => new("voice-1", "v=0", "gpt-live-1", "marin", "Speak naturally.", null!, []);

    private sealed class Recorder(List<HttpRequestMessage> requests, HttpStatusCode status, string body)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            requests.Add(request);
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }
}
