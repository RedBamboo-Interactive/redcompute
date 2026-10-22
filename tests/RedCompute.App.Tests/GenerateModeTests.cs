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
            SessionCapabilities.Generate | SessionCapabilities.ToolFreeGenerate,
            configure: provider => provider.Isolation = SessionIsolation.VerifiedToolFree);

        Assert.True(result.StatusCode == 200, $"{result.StatusCode}: {result.Body}");
        Assert.Equal(1, result.GenerateCalls);
        Assert.True(result.Provider.LastRequest!.RequireToolFree);
        using var response = JsonDocument.Parse(result.Body);
        Assert.Equal("generated", response.RootElement.GetProperty("text").GetString());
        Assert.Equal(SessionIsolation.VerifiedToolFree, response.RootElement.GetProperty("isolation").GetString());
        Assert.Equal("test-mechanism", response.RootElement.GetProperty("isolationMechanism").GetString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData(SessionIsolation.SandboxedBestEffort)]
    [InlineData(SessionIsolation.Unverified)]
    public async Task GenerateEndpoint_WithholdsTextWhenACapableRuntimeDoesNotVerifyTheCall(string? reported)
    {
        var result = await InvokeGenerateAsync(
            SessionCapabilities.Generate | SessionCapabilities.ToolFreeGenerate,
            configure: provider => provider.Isolation = reported);

        Assert.Equal(502, result.StatusCode);
        Assert.Equal(1, result.GenerateCalls);
        using var response = JsonDocument.Parse(result.Body);
        Assert.Equal("tool_free_unverified", response.RootElement.GetProperty("error").GetString());
        Assert.DoesNotContain("generated", result.Body);
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
        Assert.False(result.Provider.LastRequest!.RequireToolFree);
        Assert.Equal("system-test", result.Provider.LastRequest.System);
        using var response = JsonDocument.Parse(result.Body);
        Assert.Equal("generated", response.RootElement.GetProperty("text").GetString());
        // A provider that reports nothing is labelled as such, never upgraded.
        Assert.Equal(SessionIsolation.Unverified, response.RootElement.GetProperty("isolation").GetString());
        Assert.False(response.RootElement.TryGetProperty("schemaValid", out _));
    }

    [Fact]
    public async Task GenerateEndpoint_RejectsAnUnsupportedResponseSchemaBeforeRunning()
    {
        var result = await InvokeGenerateAsync(SessionCapabilities.Generate, requireToolFree: null,
            extra: new() { ["responseSchema"] = new Dictionary<string, object> { ["$ref"] = "#/x" } });

        Assert.Equal(422, result.StatusCode);
        Assert.Equal(0, result.GenerateCalls);
        using var response = JsonDocument.Parse(result.Body);
        Assert.Equal("invalid_response_schema", response.RootElement.GetProperty("error").GetString());
        Assert.Contains("unsupported keyword '$ref'", response.RootElement.GetProperty("errors")[0].GetString());
    }

    [Fact]
    public async Task GenerateEndpoint_PromptsForTheSchemaAndValidatesTheModelOutput()
    {
        var result = await InvokeGenerateAsync(SessionCapabilities.Generate, requireToolFree: null,
            extra: new() { ["responseSchema"] = VerdictSchema },
            configure: provider => provider.Text = "Sure:\n```json\n{\"verdict\":\"block\"}\n```");

        Assert.True(result.StatusCode == 200, $"{result.StatusCode}: {result.Body}");
        var request = result.Provider.LastRequest!;
        Assert.NotNull(request.ResponseSchema);
        Assert.StartsWith("system-test\n\nRespond with exactly one JSON value", request.System);
        Assert.Contains(request.ResponseSchema!.Json, request.System);
        using var response = JsonDocument.Parse(result.Body);
        var root = response.RootElement;
        Assert.True(root.GetProperty("schemaValid").GetBoolean());
        Assert.Equal(0, root.GetProperty("errors").GetArrayLength());
        Assert.Equal("block", root.GetProperty("json").GetProperty("verdict").GetString());
        Assert.Equal("prompt", root.GetProperty("schemaMode").GetString());
    }

    [Fact]
    public async Task GenerateEndpoint_ReportsSchemaViolationsWithoutTrustingANativeProvider()
    {
        var result = await InvokeGenerateAsync(SessionCapabilities.Generate, requireToolFree: null,
            extra: new() { ["responseSchema"] = VerdictSchema },
            configure: provider =>
            {
                provider.Text = "{\"verdict\":\"maybe\",\"extra\":1}";
                provider.NativeSchema = true;
            });

        Assert.True(result.StatusCode == 200, $"{result.StatusCode}: {result.Body}");
        using var response = JsonDocument.Parse(result.Body);
        var root = response.RootElement;
        Assert.False(root.GetProperty("schemaValid").GetBoolean());
        Assert.Equal("native", root.GetProperty("schemaMode").GetString());
        Assert.Equal("maybe", root.GetProperty("json").GetProperty("verdict").GetString());
        var errors = root.GetProperty("errors").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Contains(errors, e => e!.StartsWith("$.verdict: must be one of"));
        Assert.Contains("$.extra: property is not allowed", errors);
    }

    [Fact]
    public async Task GenerateEndpoint_ReportsMissingJsonAsASchemaFailure()
    {
        var result = await InvokeGenerateAsync(SessionCapabilities.Generate, requireToolFree: null,
            extra: new() { ["responseSchema"] = VerdictSchema },
            configure: provider => provider.Text = "I would rather not.");

        using var response = JsonDocument.Parse(result.Body);
        var root = response.RootElement;
        Assert.False(root.GetProperty("schemaValid").GetBoolean());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("json").ValueKind);
        Assert.Equal("$: response did not contain a JSON value", root.GetProperty("errors")[0].GetString());
    }

    [Fact]
    public void ExecuteResult_reports_isolation_and_defaults_to_unverified()
    {
        using var reported = JsonDocument.Parse(UnifiedSessionEndpoints.ExecuteResultJson(new SessionExecuteResult(
            true, "t", null, "m", 1, 1, null, null, SessionIsolation.SandboxedBestEffort, "codex-sandbox:read-only")));
        Assert.Equal(SessionIsolation.SandboxedBestEffort, reported.RootElement.GetProperty("isolation").GetString());
        Assert.Equal("codex-sandbox:read-only", reported.RootElement.GetProperty("isolationMechanism").GetString());
        Assert.Equal("t", reported.RootElement.GetProperty("text").GetString());

        using var silent = JsonDocument.Parse(UnifiedSessionEndpoints.ExecuteResultJson(new SessionExecuteResult(
            true, "t", null, "m", 1, 1, null, null)));
        Assert.Equal(SessionIsolation.Unverified, silent.RootElement.GetProperty("isolation").GetString());
    }

    private static readonly Dictionary<string, object> VerdictSchema = new()
    {
        ["type"] = "object",
        ["properties"] = new Dictionary<string, object>
        {
            ["verdict"] = new Dictionary<string, object> { ["type"] = "string", ["enum"] = new[] { "allow", "block" } },
        },
        ["required"] = new[] { "verdict" },
        ["additionalProperties"] = false,
    };

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
        var schema = Assert.Single(generate.Parameters!, item => item.Name == "responseSchema");
        Assert.Equal("object", schema.Type);
        Assert.False(schema.Required);
    }

    private static async Task<(int StatusCode, string Body, int GenerateCalls, GenerateProvider Provider)> InvokeGenerateAsync(
        SessionCapabilities capabilities, bool? requireToolFree = true,
        Dictionary<string, object?>? extra = null, Action<GenerateProvider>? configure = null)
    {
        using var f = new SessionRecoveryTests.Fixture("maintenance_restart");
        var provider = CreateProvider(capabilities);
        var proxy = (GenerateProvider)(object)provider;
        configure?.Invoke(proxy);
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
            ["system"] = "system-test",
        };
        if (requireToolFree.HasValue) request["requireToolFree"] = requireToolFree.Value;
        foreach (var (key, value) in extra ?? []) request[key] = value;
        var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(request));
        context.Request.Body = new MemoryStream(body);
        context.Request.ContentLength = body.Length;
        context.Features.Set<Microsoft.AspNetCore.Http.Features.IHttpRequestBodyDetectionFeature>(new BodyFeature());
        context.Response.Body = new MemoryStream();

        await endpoint.RequestDelegate!(context);
        context.Response.Body.Position = 0;
        var response = await new StreamReader(context.Response.Body).ReadToEndAsync();
        return (context.Response.StatusCode, response, proxy.GenerateCalls, proxy);
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
        public string? Isolation { get; set; }
        public string Text { get; set; } = "generated";
        public bool NativeSchema { get; set; }
        public SessionGenerateRequest? LastRequest { get; private set; }

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
                nameof(ISessionProvider.GenerateAsync) => Generate(args),
                _ => throw new NotSupportedException(method.Name),
            };
        }

        private Task<SessionGenerateResult> Generate(object?[]? args)
        {
            GenerateCalls++;
            LastRequest = args?[0] as SessionGenerateRequest;
            return Task.FromResult(new SessionGenerateResult(
                true, Text, "stream", "test-model", 1, 1, null, null,
                Isolation, Isolation == null ? null : "test-mechanism", NativeSchema));
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
