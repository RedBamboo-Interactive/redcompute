using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using RedBamboo.AppHost.Auth;
using RedBamboo.AppHost.Discovery;
using RedCompute.App.Api.Endpoints;
using RedCompute.App.Services;
using RedCompute.Core.Capabilities;
using RedCompute.Core.Configuration;
using RedCompute.Core.Providers;
using RedCompute.Plugin.Kev;
using Xunit;

namespace RedCompute.App.Tests;

public sealed class DecisionDiscoveryAndJobPathTests
{
    [Fact]
    public async Task Discovery_and_openapi_expose_full_discriminated_schemas()
    {
        var config = DecisionConfig();
        await using var provider = new KevLocalQualityProvider(
            config.Providers["kev-local-quality"], "decision", _ => { },
            new HttpClient(new UnavailableHandler()));
        var registry = new CapabilityRegistry();
        registry.Register("decision", new CapabilityDefinition
        {
            Slug = "decision",
            DisplayName = "Decision",
            Description = "Typed local decisions",
        }, config, new Dictionary<string, IBackendProvider>
        {
            ["kev-local-quality"] = provider,
        }, "kev-local-quality");

        var builder = WebApplication.CreateSlimBuilder();
        await using var app = builder.Build();
        var endpoints = new EndpointRegistry(app);
        var descriptor = new RedComputeServiceDescriptor(
            new RedComputeConfig { ApiPort = 18800 },
            registry, null, endpoints);

        var capabilities = await descriptor.GetCapabilitiesAsync();
        var decision = Assert.Single(capabilities, item => item.Slug == "decision");
        var generate = Assert.Single(decision.Endpoints!, endpoint =>
            endpoint.Method == "POST" && endpoint.Path == "/decision/generate");
        Assert.NotNull(generate.RequestBody);
        Assert.NotNull(generate.Response);
        Assert.Contains(decision.Endpoints!, endpoint => endpoint.Path == "/decision/contract");
        Assert.Contains(decision.Endpoints!, endpoint => endpoint.Path == "/decision/status");
        var validate = Assert.Single(decision.Endpoints!, endpoint =>
            endpoint.Method == "POST" && endpoint.Path == "/decision/validate");
        Assert.NotNull(validate.Response);
        Assert.Contains("job-backed", validate.Description);

        var openApi = JsonSerializer.Serialize(
            await OpenApiGenerator.GenerateAsync(descriptor));
        Assert.Contains("/decision/generate", openApi);
        Assert.Contains("discriminator", openApi);
        Assert.Contains("probabilities", openApi);
        Assert.Contains("actionAuthority", openApi);
        Assert.Contains("/decision/validate", openApi);
        Assert.Contains("jobId", openApi);
        Assert.Contains("validation", openApi);
    }

    [Fact]
    public void Default_config_registers_provider_neutral_decision_path()
    {
        var config = ConfigManager.CreateDefault();
        var decision = config.Capabilities["decision"];
        var kev = decision.Providers["kev-local-quality"];

        Assert.Equal("kev-local-quality", decision.ActiveProvider);
        Assert.Equal("KevLocalQuality", kev.Type);
        Assert.Equal("http://127.0.0.1:8008", kev.Endpoint);
        Assert.Equal("kev-latest", kev.Model);
        Assert.Equal("2629c06a5aeb0feb3b9783bafed17ed8f39ecf5c", kev.ModelRevision);
        Assert.Matches("^[a-f0-9]{40}$", kev.ModelRevision);
        var baseRevision = Assert.IsType<string>(kev.Extra!["BaseModelRevision"]);
        Assert.Equal("68c46c4b3498877f3ef123c856ecfde50c39f404", baseRevision);
        Assert.Matches("^[a-f0-9]{40}$", baseRevision);
        Assert.DoesNotContain("@", kev.ModelRevision, StringComparison.Ordinal);
        Assert.Equal(120, kev.TimeoutSeconds);
        Assert.Null(kev.LaunchCommand);
        Assert.False(Assert.IsType<bool>(kev.Extra!["MergeLora"]));
        Assert.Equal("bfloat16", kev.Extra["Dtype"]);
        Assert.Equal("flash-qla-sm120", kev.Extra["KernelBackend"]);
    }

    [Fact]
    public void Provider_discovery_finds_kev_plugin_identity()
    {
        _ = typeof(KevLocalQualityProvider);
        var discovery = new ProviderDiscovery(_ => { });

        discovery.ScanAssemblies();

        Assert.Contains("KevLocalQuality", discovery.AvailableTypes);
        var provider = discovery.Create(
            "KevLocalQuality",
            DecisionConfig().Providers["kev-local-quality"],
            "decision",
            _ => { });
        Assert.NotNull(provider);
        Assert.Equal("kev-local-quality", provider!.Name);
    }

    [Theory]
    [InlineData("?async=false", null, false)]
    [InlineData("?async=true", null, true)]
    [InlineData("?async", null, true)]
    [InlineData("", "false", false)]
    [InlineData("", "true", true)]
    public void Async_false_preserves_the_synchronous_job_path(
        string query,
        string? header,
        bool expected)
    {
        var context = new DefaultHttpContext();
        context.Request.QueryString = new QueryString(query);
        if (header is not null)
            context.Request.Headers["X-Async"] = header;

        Assert.Equal(expected, GenericCapabilityEndpoints.IsAsyncRequested(context));
    }


    [Theory]
    [InlineData("/decision/generate")]
    [InlineData("/decision/validate")]
    public async Task Unauthorized_decision_paths_are_rejected_before_job_admission(string path)
    {
        const string signingKey =
            "decision-auth-negative-test-key-that-is-at-least-thirty-two-bytes";
        var options = new AuthOptions
        {
            Mode = AuthMode.Required,
            Jwt = new JwtOptions { SigningKey = signingKey },
            BypassPaths = ["/ping", "/discover", "/openapi.json"],
        };
        var dispatched = false;
        var middleware = new AuthMiddleware(
            _ =>
            {
                dispatched = true;
                return Task.CompletedTask;
            },
            options,
            new JwtService(options.Jwt));
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.Request.Headers.Authorization = "Bearer not-a-valid-token";
        context.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.9");
        context.Connection.LocalIpAddress = IPAddress.Loopback;
        context.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
        Assert.False(dispatched);
    }
    private static CapabilityConfig DecisionConfig() => new()
    {
        ActiveProvider = "kev-local-quality",
        Providers = new Dictionary<string, ProviderConfig>
        {
            ["kev-local-quality"] = new()
            {
                Type = "KevLocalQuality",
                Endpoint = "http://127.0.0.1:8008",
                Model = "kev-latest",
                ModelRevision = "2629c06a5aeb0feb3b9783bafed17ed8f39ecf5c",
                CalibrationRevision = "test",
                TimeoutSeconds = 30,
                Extra = new Dictionary<string, object?>
                {
                    ["MergeLora"] = false,
                    ["BaseModelRevision"] = "68c46c4b3498877f3ef123c856ecfde50c39f404",
                    ["Dtype"] = "bfloat16",
                },
            },
        },
    };

    private sealed class UnavailableHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(
                System.Net.HttpStatusCode.ServiceUnavailable));
    }
}
