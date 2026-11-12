using RedCompute.App.Services;
using RedCompute.Core.Configuration;
using System.Net;
using System.Text;
using Xunit;

namespace RedCompute.App.Tests;

/// <summary>
/// Onboarding's Connect AI sets RedLeaf's suite-config default_provider after RedCompute loaded
/// its provider snapshot. The default (and every isDefault Code reads) must follow it without a
/// restart (Linux e2e, 2026-11-09: New Session preselected the unavailable Claude Code).
/// </summary>
public class ProviderDefaultRefreshTests
{
    [Fact]
    public async Task TheDefaultFollowsTheSuiteConfigChosenAfterLoad()
    {
        var redleaf = new FakeRedLeaf();
        var service = Service(redleaf);
        await service.RefreshAsync();
        Assert.Equal("anthropic-direct", service.DefaultProviderSlug); // fallback order, nothing chosen yet

        redleaf.DefaultProvider = "22222222-2222-2222-2222-222222222222"; // Connect AI chose Codex
        var before = redleaf.Requests.Count;
        await service.RefreshDefaultProviderAsync(TimeSpan.FromSeconds(2));

        Assert.Equal("codex-default", service.DefaultProviderSlug);
        Assert.Equal("codex-default", service.GetDefault().Slug);
        // One light read: suite-config only, no provider list or secrets.
        Assert.Equal(["/api/entities?type=suite-config&limit=1"], redleaf.Requests.Skip(before).ToArray());
    }

    [Fact]
    public async Task AnUnreachableOrFailingRedLeaf_KeepsTheCurrentDefault()
    {
        var redleaf = new FakeRedLeaf { DefaultProvider = "codex-default" };
        var service = Service(redleaf);
        await service.RefreshAsync();
        Assert.Equal("codex-default", service.DefaultProviderSlug);

        redleaf.SuiteConfigStatus = HttpStatusCode.InternalServerError;
        await service.RefreshDefaultProviderAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("codex-default", service.DefaultProviderSlug);

        redleaf.SuiteConfigStatus = HttpStatusCode.OK;
        redleaf.SuiteConfigBody = "not json";
        await service.RefreshDefaultProviderAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("codex-default", service.DefaultProviderSlug);
    }

    [Fact]
    public async Task AClearedDefault_FallsBackToThePreferenceOrder()
    {
        var redleaf = new FakeRedLeaf { DefaultProvider = "codex-default" };
        var service = Service(redleaf);
        await service.RefreshAsync();

        redleaf.DefaultProvider = null;
        await service.RefreshDefaultProviderAsync(TimeSpan.FromSeconds(2));

        Assert.Equal("anthropic-direct", service.DefaultProviderSlug);
    }

    private static ProviderConfigService Service(FakeRedLeaf redleaf)
        => new(new RedComputeConfig { RedLeafUrl = "http://redleaf.test" }, (_, _) => { }, new HttpClient(redleaf));

    private sealed class FakeRedLeaf : HttpMessageHandler
    {
        public string? DefaultProvider { get; set; }
        public HttpStatusCode SuiteConfigStatus { get; set; } = HttpStatusCode.OK;
        public string? SuiteConfigBody { get; set; }
        public List<string> Requests { get; } = [];


        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.PathAndQuery;
            Requests.Add(path);
            if (path.StartsWith("/api/entities?type=provider&", StringComparison.Ordinal))
            {

                return Json("""
                    {"items":[
                      {"id":"11111111-1111-1111-1111-111111111111","slug":"anthropic-direct","name":"Claude Code","data":{"backend":"claude-code","status":"active"}},
                      {"id":"22222222-2222-2222-2222-222222222222","slug":"codex-default","name":"Codex","data":{"backend":"codex","status":"active"}}
                    ]}
                    """);
            }
            if (path.StartsWith("/api/entities?type=suite-config", StringComparison.Ordinal))
            {
                if (SuiteConfigStatus != HttpStatusCode.OK)
                    return Task.FromResult(new HttpResponseMessage(SuiteConfigStatus));
                return Json(SuiteConfigBody ?? (DefaultProvider is null
                    ? """{"items":[{"slug":"default","data":{}}]}"""
                    : $$$"""{"items":[{"slug":"default","data":{"default_provider":"{{{DefaultProvider}}}"}}]}"""));
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private static Task<HttpResponseMessage> Json(string body)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
    }
}
