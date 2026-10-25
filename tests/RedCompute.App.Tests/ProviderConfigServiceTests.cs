using System.Net;
using System.Text;
using RedCompute.App.Services;
using RedCompute.Core.Configuration;
using Xunit;

namespace RedCompute.App.Tests;

public sealed class ProviderConfigServiceTests
{
    [Fact]
    public async Task Fallback_prefers_claude_code_then_codex_then_slug_order()
    {
        var all = await RefreshedAsync(Providers("opencode-default", "codex-default", "anthropic-direct"), suiteConfig: null);
        Assert.Equal("anthropic-direct", all.GetDefault().Slug);
        Assert.Equal("anthropic-direct", all.DefaultProviderSlug);

        var noClaude = await RefreshedAsync(Providers("opencode-default", "codex-default"), suiteConfig: null);
        Assert.Equal("codex-default", noClaude.GetDefault().Slug);
        Assert.Equal("codex-default", noClaude.DefaultProviderSlug);

        var others = await RefreshedAsync(Providers("zeta", "alpha", "mid"), suiteConfig: null);
        Assert.Equal("alpha", others.GetDefault().Slug);
        Assert.Equal("alpha", others.DefaultProviderSlug);
    }

    [Fact]
    public async Task Fallback_ignores_providers_that_are_not_active()
    {
        var service = await RefreshedAsync(
            Providers(("anthropic-direct", "disabled"), ("codex-default", "active")), suiteConfig: null);

        Assert.Equal("codex-default", service.GetDefault().Slug);
        Assert.Equal("codex-default", service.DefaultProviderSlug);
    }

    [Fact]
    public async Task A_resolving_suite_default_wins_over_the_fallback_order()
    {
        var service = await RefreshedAsync(
            Providers("anthropic-direct", "opencode-default"), SuiteConfig("opencode-default"));

        Assert.Equal("opencode-default", service.GetDefault().Slug);
        Assert.Equal("opencode-default", service.DefaultProviderSlug);
    }

    [Fact]
    public async Task A_suite_default_that_stops_resolving_is_cleared_not_kept()
    {
        var handler = new EntityHandler(Providers("anthropic-direct", "opencode-default"), SuiteConfig("opencode-default"));
        var service = Create(handler);
        await service.RefreshAsync();
        Assert.Equal("opencode-default", service.DefaultProviderSlug);

        // The default is cleared in RedLeaf.
        handler.SuiteConfigJson = SuiteConfig(null);
        await service.RefreshAsync();
        Assert.Equal("anthropic-direct", service.DefaultProviderSlug);
        Assert.Equal("anthropic-direct", service.GetDefault().Slug);

        // The default points at a provider that was then removed.
        handler.SuiteConfigJson = SuiteConfig("opencode-default");
        await service.RefreshAsync();
        Assert.Equal("opencode-default", service.DefaultProviderSlug);
        handler.ProvidersJson = Providers("anthropic-direct", "codex-default");
        await service.RefreshAsync();
        Assert.Equal("anthropic-direct", service.DefaultProviderSlug);
    }

    [Fact]
    public async Task An_unreachable_suite_config_keeps_the_last_known_default()
    {
        var handler = new EntityHandler(Providers("anthropic-direct", "opencode-default"), SuiteConfig("opencode-default"));
        var service = Create(handler);
        await service.RefreshAsync();

        handler.FailSuiteConfig = true;
        await service.RefreshAsync();

        Assert.Equal("opencode-default", service.DefaultProviderSlug);
    }

    private static async Task<ProviderConfigService> RefreshedAsync(string providersJson, string? suiteConfig)
    {
        var service = Create(new EntityHandler(providersJson, suiteConfig ?? SuiteConfig(null)));
        await service.RefreshAsync();
        return service;
    }

    private static ProviderConfigService Create(EntityHandler handler) => new(
        new RedComputeConfig { RedLeafUrl = "http://127.0.0.1:18804" },
        (_, _) => { },
        new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(1) });

    private static string Providers(params string[] slugs) => Providers(slugs.Select(slug => (slug, "active")).ToArray());

    private static string Providers(params (string Slug, string Status)[] providers)
        => "{\"items\":[" + string.Join(",", providers.Select(p =>
            $"{{\"id\":\"id-{p.Slug}\",\"slug\":\"{p.Slug}\",\"name\":\"{p.Slug}\",\"data\":{{\"backend\":\"{p.Slug}\",\"status\":\"{p.Status}\"}}}}")) + "]}";

    private static string SuiteConfig(string? defaultProvider)
        => "{\"items\":[{\"id\":\"suite\",\"slug\":\"default\",\"data\":{"
           + (defaultProvider is null ? "" : $"\"default_provider\":\"{defaultProvider}\"")
           + "}}]}";

    private sealed class EntityHandler(string providersJson, string suiteConfigJson) : HttpMessageHandler
    {
        public string ProvidersJson { get; set; } = providersJson;
        public string SuiteConfigJson { get; set; } = suiteConfigJson;
        public bool FailSuiteConfig { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var query = request.RequestUri?.Query ?? "";
            if (query.Contains("type=suite-config", StringComparison.Ordinal) && FailSuiteConfig)
                throw new HttpRequestException("RedLeaf unavailable");

            var json = query switch
            {
                var q when q.Contains("type=suite-config", StringComparison.Ordinal) => SuiteConfigJson,
                var q when q.Contains("type=provider", StringComparison.Ordinal) => ProvidersJson,
                _ => "{\"items\":[]}",
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            });
        }
    }
}
