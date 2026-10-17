using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using RedBamboo.AppHost.Discovery;
using RedCompute.App.Api.Endpoints;
using RedCompute.App.Services;
using RedCompute.Core.Capabilities;
using RedCompute.Core.Configuration;
using RedCompute.Core.Jobs;
using RedCompute.Core.Providers;
using RedCompute.Core.Sessions;
using RedCompute.PluginSdk;
using Xunit;

namespace RedCompute.App.Tests;

[Collection("Unified session endpoint mapping")]
public sealed class GenerateModeTests
{
    [Theory]
    [InlineData("{}", true)]
    [InlineData("{\"mode\":\"oneshot\"}", true)]
    [InlineData("{\"mode\":\"ONESHOT\"}", true)]
    [InlineData("{\"mode\":\"session\"}", false)]
    [InlineData("{\"mode\":null}", false)]
    [InlineData("{\"mode\":42}", false)]
    public void GenerateEndpoint_AcceptsOnlyStatelessMode(string json, bool expected)
    {
        using var document = JsonDocument.Parse(json);

        Assert.Equal(expected, UnifiedSessionEndpoints.IsStatelessGenerateMode(document.RootElement));
    }

    [Theory]
    [InlineData("{}", false)]
    [InlineData("{\"confidential\":false}", false)]
    [InlineData("{\"confidential\":true}", true)]
    [InlineData("{\"confidential\":\"true\"}", false)]
    [InlineData("{\"confidential\":null}", false)]
    public void GenerateEndpoint_ConfidentialityIsExplicitAndTighteningOnly(
        string json, bool expected)
    {
        using var document = JsonDocument.Parse(json);

        Assert.Equal(expected,
            UnifiedSessionEndpoints.IsConfidentialGenerate(document.RootElement));
    }

    [Fact]
    public async Task GenerateEndpoint_AcceptsRequireToolFreeOnCapableRuntime()
    {
        var result = await InvokeGenerateAsync(
            SessionCapabilities.Generate | SessionCapabilities.ToolFreeGenerate);

        Assert.True(result.StatusCode == 200, $"{result.StatusCode}: {result.Body}");
        Assert.Equal(1, result.GenerateCalls);
        using var response = JsonDocument.Parse(result.Body);
        Assert.Equal("generated", response.RootElement.GetProperty("text").GetString());
    }

    [Fact]
    public async Task GenerateEndpoint_RefusesRequireToolFreeOnIncapableRuntime()
    {
        var result = await InvokeGenerateAsync(SessionCapabilities.Generate);

        Assert.Equal(422, result.StatusCode);
        Assert.Equal(0, result.GenerateCalls);
        using var response = JsonDocument.Parse(result.Body);
        Assert.Equal("tool_free_unavailable", response.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task GenerateEndpoint_KeepsExistingBehaviorWhenRequireToolFreeIsAbsent()
    {
        var result = await InvokeGenerateAsync(SessionCapabilities.Generate, requireToolFree: null);

        Assert.True(result.StatusCode == 200, $"{result.StatusCode}: {result.Body}");
        Assert.Equal(1, result.GenerateCalls);
    }

    [Fact]
    public async Task GenerateDiscovery_DescribesRequireToolFree()
    {
        using var f = new SessionRecoveryTests.Fixture("maintenance_restart");
        var provider = CreateProvider(SessionCapabilities.Generate | SessionCapabilities.ToolFreeGenerate);
        var registry = Register(provider);
        var builder = WebApplication.CreateSlimBuilder();
        await using var app = builder.Build();
        var endpoints = new EndpointRegistry(app);
        UnifiedSessionEndpoints.Map(endpoints, registry, new MemoryJobTracker(), (_, _) => { }, f.Config,
            attachmentStore: f.Attachments, inputQueue: f.Service());

        var generate = endpoints.GetEndpoints().Single(endpoint =>
            endpoint.Method == "POST" && endpoint.Path == "/ai-session/generate");
        var parameter = Assert.Single(generate.Parameters!, item => item.Name == "requireToolFree");
        Assert.Equal("boolean", parameter.Type);
        Assert.False(parameter.Required);
    }

    private static async Task<(int StatusCode, string Body, int GenerateCalls)> InvokeGenerateAsync(
        SessionCapabilities capabilities, bool? requireToolFree = true)
    {
        using var f = new SessionRecoveryTests.Fixture("maintenance_restart");
        var provider = CreateProvider(capabilities);
        var proxy = (GenerateProvider)(object)provider;
        var registry = Register(provider);
        var builder = WebApplication.CreateSlimBuilder();
        await using var app = builder.Build();
        UnifiedSessionEndpoints.Map(new EndpointRegistry(app), registry, new MemoryJobTracker(),
            (_, _) => { }, f.Config, attachmentStore: f.Attachments, inputQueue: f.Service());

        var endpoint = ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Single(item => item.RoutePattern.RawText == "/ai-session/generate"
                && item.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods.Contains("POST"));
        var context = new DefaultHttpContext { RequestServices = app.Services };
        context.SetEndpoint(endpoint);
        context.Request.Method = "POST";
        context.Request.ContentType = "application/json";
        context.Request.Headers["X-System-Reason"] = "tool-free endpoint contract test";
        var request = new Dictionary<string, object?>
        {
            ["mode"] = "oneshot",
            ["provider"] = "test",
            ["messages"] = new[] { new { role = "user", content = "hello" } },
        };
        if (requireToolFree.HasValue) request["requireToolFree"] = requireToolFree.Value;
        var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(request));
        context.Request.Body = new MemoryStream(body);
        context.Request.ContentLength = body.Length;
        context.Features.Set<Microsoft.AspNetCore.Http.Features.IHttpRequestBodyDetectionFeature>(new BodyFeature());
        context.Response.Body = new MemoryStream();

        await endpoint.RequestDelegate!(context);
        context.Response.Body.Position = 0;
        var response = await new StreamReader(context.Response.Body).ReadToEndAsync();
        return (context.Response.StatusCode, response, proxy.GenerateCalls);
    }

    private static ITestProvider CreateProvider(SessionCapabilities capabilities)
    {
        var provider = DispatchProxy.Create<ITestProvider, GenerateProvider>();
        ((GenerateProvider)(object)provider).Capabilities = capabilities;
        return provider;
    }

    private static CapabilityRegistry Register(ITestProvider provider)
    {
        var registry = new CapabilityRegistry();
        registry.Register("ai-session", new CapabilityDefinition
        {
            Slug = "ai-session",
            DisplayName = "AI Session",
        }, new CapabilityConfig(), new Dictionary<string, IBackendProvider> { ["test"] = provider }, "test");
        return registry;
    }

    public interface ITestProvider : ISessionProvider, IBackendProvider { }

    public class GenerateProvider : DispatchProxy
    {
        public SessionCapabilities Capabilities { get; set; }
        public int GenerateCalls { get; private set; }

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            return method!.Name switch
            {
                "get_ProviderId" => "test",
                "get_ProviderDisplayName" => "Test",
                "get_Capabilities" => Capabilities,
                "get_Name" => "Test",
                "get_CapabilitySlug" => "ai-session",
                "get_HealthCheckInterval" => TimeSpan.FromSeconds(10),
                nameof(ISessionProvider.GetAvailableModels) => new List<ModelInfo>(),
                nameof(ISessionProvider.GenerateAsync) => Generate(),
                _ => throw new NotSupportedException(method.Name),
            };
        }

        private Task<SessionGenerateResult> Generate()
        {
            GenerateCalls++;
            return Task.FromResult(new SessionGenerateResult(
                true, "generated", "stream", "test-model", 1, 1, null, null));
        }
    }

    private sealed class MemoryJobTracker : IJobTracker
    {
        public JobRecord CreateJob(JobSubmission submission) => new()
            { CapabilitySlug = submission.CapabilitySlug, ProviderName = submission.ProviderName };
        public void MarkRunning(Guid jobId) { }
        public void MarkCompleted(Guid jobId, string? outputLocation = null, long? outputSizeBytes = null,
            string? contentType = null, string? resultJson = null, double? costUsd = null) { }
        public void MarkFailed(Guid jobId, string errorMessage, string? errorDetails = null, string? resultJson = null) { }
        public void MarkCancelled(Guid jobId) { }
        public void UpdateName(Guid jobId, string name) { }
    }

    private sealed class BodyFeature : Microsoft.AspNetCore.Http.Features.IHttpRequestBodyDetectionFeature
    {
        public bool CanHaveBody => true;
    }
}
