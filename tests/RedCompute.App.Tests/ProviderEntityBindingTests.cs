using System.Net;
using System.Text;
using RedCompute.App.Api.Endpoints;
using RedCompute.App.Services;
using RedCompute.Core.Capabilities;
using RedCompute.Core.Jobs;
using RedCompute.Core.Providers;
using RedCompute.Core.Sessions;
using RedCompute.PluginSdk;
using RedCompute.Core.Configuration;
using Xunit;

namespace RedCompute.App.Tests;

public class ProviderEntityBindingTests
{
    [Fact]
    public async Task DedicatedProviderEntityCreatesSlugKeyedRuntimeWithoutSettings()
    {
        var config = new RedComputeConfig
        {
            RedLeafUrl = "http://redleaf.test",
            Capabilities = new Dictionary<string, CapabilityConfig>
            {
                ["ai-session"] = new()
                {
                    ActiveProvider = "opencode",
                    Providers = new Dictionary<string, ProviderConfig>
                    {
                        ["opencode"] = new() { Type = "OpenCode" },
                    },
                },
            },
        };
        var service = new ProviderConfigService(
            config, (_, _) => { }, new HttpClient(new EntityCatalogHandler()));

        await service.RefreshAsync();
        service.ApplyToConfig(config);

        var providers = config.Capabilities["ai-session"].Providers;
        var dedicated = Assert.Contains("acme-cloud", providers);
        Assert.Equal("AcmeHarness", dedicated.Type);
        Assert.Equal("custom-id", dedicated.EntityId);
        Assert.Equal("acme-cloud", dedicated.EntitySlug);
        Assert.Equal("acme", dedicated.Backend);
        Assert.Equal("dedicated", dedicated.RuntimeBinding);
        Assert.Equal("https://api.acme.test/v1", dedicated.Endpoint);
        Assert.Equal("acme-reasoner", dedicated.Model);
        Assert.Equal("vault-secret", dedicated.ApiKey);
        Assert.Equal("acme-cloud", service.ResolveProviderName(
            config.Capabilities["ai-session"], "acme-cloud"));

        // Existing harness-backed profiles keep sharing the configured backend runtime.
        Assert.DoesNotContain("muse-profile", providers);
        Assert.Equal("muse/model", providers["opencode"].Model);
    }

    [Fact]
    public void EntityDefaultsApplyWithoutPluginSettingsAndSettingsCanOverrideThem()
    {
        var entity = new ProviderEntityConfig(
            "id", "slug", "Display", "backend", null,
            "https://entity.example/v1", "secret", "entity-model", "active", null)
        {
            ProviderType = "Harness",
            RuntimeBinding = "dedicated",
            ApiKeyAuthoritative = true,
        };
        var config = new ProviderConfig { Type = "placeholder" };

        ProviderConfigService.ApplyEntityToProvider(entity, config);

        Assert.Equal("Harness", config.Type);
        Assert.Equal("https://entity.example/v1", config.Endpoint);
        Assert.Equal("entity-model", config.Model);
        Assert.Equal("secret", config.ApiKey);
        Assert.Equal("dedicated", config.RuntimeBinding);
    }

    [Fact]
    public void DedicatedRuntimeWinsBeforeSharedBackendFallback()
    {
        var dedicated = new StubSessionProvider("acme-dedicated");
        var shared = new StubSessionProvider("opencode");
        var entry = new CapabilityEntry
        {
            Definition = new CapabilityDefinition { Slug = "ai-session", DisplayName = "AI Session" },
            Config = new CapabilityConfig(),
            Providers = new Dictionary<string, IBackendProvider>
            {
                ["acme-cloud"] = dedicated,
                ["opencode"] = shared,
            },
        };

        Assert.Same(dedicated, UnifiedSessionEndpoints.FindRegisteredSessionProvider(
            entry, "acme-cloud", "opencode"));
        Assert.Same(shared, UnifiedSessionEndpoints.FindRegisteredSessionProvider(
            entry, "muse-profile", "opencode"));
    }

    private sealed class StubSessionProvider(string providerId) : IBackendProvider, ISessionProvider
    {
        public string Name => providerId;
        public string CapabilitySlug => "ai-session";
        public string ProviderId => providerId;
        public string ProviderDisplayName => providerId;
        public SessionCapabilities Capabilities => SessionCapabilities.None;
        public TimeSpan HealthCheckInterval => TimeSpan.FromMinutes(1);
        public string? LastStartError => null;
        public event Action<string, UnifiedStreamEvent>? SessionStreamEvent { add { } remove { } }

        public Task<bool> StartAsync(CancellationToken ct = default) => Task.FromResult(true);
        public Task StopAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<BackendStatus> GetStatusAsync(CancellationToken ct = default) => Task.FromResult(BackendStatus.Running);
        public string? GetProxyTargetUrl() => null;
        public Task<JobResult?> ExecuteAsync(JobRequest request, CancellationToken ct = default) => Task.FromResult<JobResult?>(null);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public Task<UnifiedSessionInfo?> StartSessionAsync(string projectPath, string? model, string? userId,
            string? userName, string? userAvatarUrl, string? effort, string? endpointUrl, string? apiKey,
            int? thinkingBudget, string? qualityTier, string? providerEntity, Guid? repositoryId,
            JobProvenance provenance, string? scratchDirectory = null, bool confidential = false)
            => Task.FromResult<UnifiedSessionInfo?>(null);
        public Task<UnifiedSessionInfo?> ResumeSessionAsync(string sessionId) => Task.FromResult<UnifiedSessionInfo?>(null);
        public Task StopSessionAsync(string sessionId) => Task.CompletedTask;
        public Task ForceKillAsync(string sessionId) => Task.CompletedTask;
        public void DismissSession(string sessionId) { }
        public Task<bool> SendInputAsync(string sessionId, IReadOnlyList<SessionInputPart> input,
            string? attachmentsJson = null, string? messageUid = null) => Task.FromResult(false);
        public bool SendAnswer(string sessionId, string answer) => false;
        public InterruptResult InterruptSession(string sessionId) => InterruptResult.NotFound;
        public bool SetPermissionMode(string sessionId, string mode) => false;
        public List<UnifiedSessionInfo> GetSessions(int limit = 20, bool includeDismissed = false) => [];
        public (UnifiedSessionInfo? Info, List<UnifiedMessageRecord> History) GetSession(string sessionId) => (null, []);
        public (UnifiedSessionInfo? Info, List<UnifiedMessageRecord> History) GetSessionByJobId(Guid jobId) => (null, []);
        public Dictionary<Guid, SessionStatus> GetSessionStatusesByJobIds(IEnumerable<Guid> jobIds) => [];
        public Task<SessionExecuteResult> ExecuteAsync(string prompt, string? workingDir, string? model,
            int timeout, CancellationToken ct, string? streamKey = null,
            Dictionary<string, string>? env = null, Dictionary<string, object?>? providerParams = null)
            => Task.FromResult(new SessionExecuteResult(false, null, null, model, 0, 0, null, "unsupported"));
        public Task<SessionGenerateResult> GenerateAsync(string? model, string? system, string messagesJson,
            int maxTokens, CancellationToken ct, string? effort = null, int? timeout = null)
            => Task.FromResult(new SessionGenerateResult(false, null, null, model, 0, 0, null, "unsupported"));
        public List<ModelInfo> GetAvailableModels() => [];
        public void CancelExecution(string key) { }
        public Task StopAllAsync() => Task.CompletedTask;
    }
    private sealed class EntityCatalogHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? "";
            var json = path switch
            {
                "/api/entities" when request.RequestUri?.Query.Contains("type=provider") == true => Providers,
                "/api/entities" when request.RequestUri?.Query.Contains("type=capability") == true => Capabilities,
                "/api/entities" when request.RequestUri?.Query.Contains("type=suite-config") == true => "{\"items\":[]}",
                "/api/internal/compute/provider-secrets" => Secrets,
                _ => throw new InvalidOperationException($"Unexpected request: {request.RequestUri}"),
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            });
        }

        private const string Providers = """
            {
              "items": [
                {
                  "id": "custom-id",
                  "slug": "acme-cloud",
                  "name": "Acme Cloud",
                  "data": {
                    "backend": "acme",
                    "provider_type": "AcmeHarness",
                    "runtime_binding": "dedicated",
                    "endpoint_url": "https://api.acme.test/v1",
                    "default_model": "acme-reasoner",
                    "status": "active",
                    "capabilities": ["ai-inference"]
                  }
                },
                {
                  "id": "muse-id",
                  "slug": "muse-profile",
                  "name": "Muse Profile",
                  "data": {
                    "backend": "opencode",
                    "provider_type": "OpenCode",
                    "runtime_binding": "shared",
                    "status": "active",
                    "capabilities": ["ai-inference"],
                    "settings": { "model": "muse/model" }
                  }
                }
              ]
            }
            """;

        private const string Capabilities = """
            {"items":[{"slug":"ai-inference","data":{"compute_slug":"ai-session"}}]}
            """;

        private const string Secrets = """
            {"items":[{"id":"custom-id","slug":"acme-cloud","apiKey":"vault-secret"}]}
            """;
    }
}