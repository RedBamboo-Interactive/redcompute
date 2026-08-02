using System.Net;
using System.Text;
using RedCompute.App.Services;
using RedCompute.Core.Configuration;
using Xunit;

namespace RedCompute.App.Tests;

public sealed class QualityModeServiceTests
{
    [Fact]
    public async Task InitialSync_WritesAndRestoresLastKnownGoodCatalog()
    {
        var cachePath = TempCachePath();
        try
        {
            var online = CreateService(new CatalogHandler(), cachePath);
            Assert.True(await online.InitialSyncAsync());
            Assert.True(online.LoadedFromRedLeaf);
            Assert.True(File.Exists(cachePath));

            var offline = CreateService(new CatalogHandler(failuresRemaining: int.MaxValue), cachePath);
            Assert.True(await offline.InitialSyncAsync());
            Assert.False(offline.LoadedFromRedLeaf);
            Assert.True(offline.LoadedFromCache);
            Assert.Equal("deep", Assert.Single(offline.GetTiers()).Slug);

            Assert.True(offline.TryResolveRequested("deep", null, out var resolved, out var failure));
            Assert.Equal(QualityResolutionFailure.None, failure);
            Assert.Equal("gpt-5.6-sol", resolved!.Model);

            Assert.True(offline.TryResolveRequested("deep", "codex", out var aliased, out _));
            Assert.Equal("codex-default", aliased!.Provider);
        }
        finally
        {
            DeleteCache(cachePath);
        }
    }

    [Fact]
    public async Task EnsureLoaded_RetriesUntilRedLeafRecovers()
    {
        var cachePath = TempCachePath();
        try
        {
            var handler = new CatalogHandler(failuresRemaining: 2);
            var service = CreateService(handler, cachePath);

            Assert.False(await service.InitialSyncAsync());
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await service.EnsureLoadedAsync(cts.Token);

            Assert.True(service.LoadedFromRedLeaf);
            Assert.True(service.HasSnapshot);
            Assert.True(handler.RequestCount >= 6); // two failed attempts, then four catalog requests
        }
        finally
        {
            DeleteCache(cachePath);
        }
    }

    [Fact]
    public async Task RequestedTier_FailsClosedForUnavailableUnknownAndUnmappedModel()
    {
        var cachePath = TempCachePath();
        try
        {
            var unavailable = CreateService(new CatalogHandler(failuresRemaining: int.MaxValue), cachePath);
            Assert.False(unavailable.TryResolveRequested("deep", null, out _, out var unavailableFailure));
            Assert.Equal(QualityResolutionFailure.CatalogUnavailable, unavailableFailure);

            var loaded = CreateService(new CatalogHandler(), cachePath);
            Assert.True(await loaded.InitialSyncAsync());

            Assert.False(loaded.TryResolveRequested("missing", null, out _, out var unknownFailure));
            Assert.Equal(QualityResolutionFailure.UnknownTier, unknownFailure);

            Assert.False(loaded.TryResolveRequested("deep", "provider-without-a-model", out _, out var modelFailure));
            Assert.Equal(QualityResolutionFailure.ModelUnavailable, modelFailure);
        }
        finally
        {
            DeleteCache(cachePath);
        }
    }

    private static QualityModeService CreateService(HttpMessageHandler handler, string cachePath)
    {
        var config = new RedComputeConfig { RedLeafUrl = "http://127.0.0.1:18804" };
        var log = new Action<string, Guid?>((_, _) => { });
        var providerConfig = new ProviderConfigService(config, log);
        return new QualityModeService(
            config,
            log,
            providerConfig,
            new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(1) },
            cachePath,
            TimeSpan.FromMilliseconds(1),
            TimeSpan.FromMilliseconds(2));
    }

    private static string TempCachePath() => Path.Combine(
        Path.GetTempPath(), $"redcompute-quality-modes-{Guid.NewGuid():N}.json");

    private static void DeleteCache(string cachePath)
    {
        if (File.Exists(cachePath)) File.Delete(cachePath);
        if (File.Exists(cachePath + ".tmp")) File.Delete(cachePath + ".tmp");
    }

    private sealed class CatalogHandler(int failuresRemaining = 0) : HttpMessageHandler
    {
        private int _failuresRemaining = failuresRemaining;
        private int _requestCount;

        public int RequestCount => _requestCount;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requestCount);
            if (Interlocked.Decrement(ref _failuresRemaining) >= 0)
                throw new HttpRequestException("RedLeaf unavailable");

            var query = request.RequestUri?.Query ?? "";
            var json = query switch
            {
                var q when q.Contains("type=quality-tier", StringComparison.Ordinal) => TiersJson,
                var q when q.Contains("type=quality-mode", StringComparison.Ordinal) => ModesJson,
                var q when q.Contains("type=inference-model", StringComparison.Ordinal) => "{\"items\":[]}",
                var q when q.Contains("type=suite-config", StringComparison.Ordinal) => SuiteConfigJson,
                _ => "{\"items\":[]}",
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            });
        }
    }

    private const string TiersJson = """
        {"items":[{"id":"tier-deep","slug":"deep","name":"Deep","data":{"label":"Deep","sort_order":1}}]}
        """;

    private const string ModesJson = """
        {"items":[{"id":"mode-deep-codex","slug":"deep-codex","data":{"quality_tier":"tier-deep","provider":"codex-default","model":"gpt-5.6-sol","effort":"high","is_default":true}}]}
        """;

    private const string SuiteConfigJson = """
        {"items":[{"id":"suite","slug":"suite-config","data":{"default_quality_tier":"tier-deep"}}]}
        """;
}
