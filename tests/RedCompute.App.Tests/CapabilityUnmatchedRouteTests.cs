using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using RedBamboo.AppHost.Discovery;
using RedCompute.App.Api.Endpoints;
using RedCompute.App.Data;
using RedCompute.App.Services;
using RedCompute.App.Services.Jobs;
using RedCompute.Core.Capabilities;
using RedCompute.Core.Configuration;
using RedCompute.Core.Discovery;
using RedCompute.Core.Jobs;
using RedCompute.Core.Providers;
using Xunit;

namespace RedCompute.App.Tests;

/// <summary>
/// The /{slug}/{**path} catch-all must make a wrong route look like a wrong route (404),
/// and reserve 503 for a proxy backend that owns the path but is unavailable.
/// </summary>
public sealed class CapabilityUnmatchedRouteTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "redcompute-unmatched-route-tests", Guid.NewGuid().ToString("N"));

    private WebApplication? _upstream;
    private WebApplication? _app;
    private HttpClient? _client;

    private readonly FakeProvider _imageGen = new("comfy-like", "image-gen")
    {
        IsProxy = false,
        Status = BackendStatus.Running,
        ContractVersion = "test-1",
    };
    private readonly FakeProvider _ttsStopped = new("tts-local-like", "tts")
    {
        IsProxy = true,
        Status = BackendStatus.Stopped,
    };
    private readonly FakeProvider _sttRunning = new("stt-local-like", "stt")
    {
        IsProxy = true,
        Status = BackendStatus.Running,
    };

    public async Task InitializeAsync()
    {
        var upstreamBuilder = WebApplication.CreateSlimBuilder();
        upstreamBuilder.WebHost.UseUrls("http://127.0.0.1:0");
        _upstream = upstreamBuilder.Build();
        _upstream.MapGet("/health", () => Results.Text("upstream-ok"));
        await _upstream.StartAsync();
        _sttRunning.ProxyUrl = _upstream.Urls.Single();

        Directory.CreateDirectory(_directory);
        var database = Path.Combine(_directory, "jobs.db");
        using (var db = new RedComputeDbContext(database))
        {
            db.Database.EnsureCreated();
            db.MigrateSchema();
        }
        var jobs = new JobTrackingService(() => new RedComputeDbContext(database));

        var registry = new CapabilityRegistry();
        Register(registry, _imageGen);
        Register(registry, _ttsStopped);
        Register(registry, _sttRunning);

        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        _app = builder.Build();
        GenericCapabilityEndpoints.Map(_app, new EndpointRegistry(_app), registry, jobs, (_, _) => { });
        await _app.StartAsync();
        _client = new HttpClient { BaseAddress = new Uri(_app.Urls.Single()) };
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        if (_app is not null) await _app.DisposeAsync();
        if (_upstream is not null) await _upstream.DisposeAsync();
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    [Theory]
    [InlineData("GET", "/image-gen/workflows")]
    [InlineData("GET", "/image-gen/doesnotexist")]
    [InlineData("POST", "/image-gen/nested/doesnotexist")]
    [InlineData("GET", "/image-gen/generate")]
    public async Task Unknown_subpath_of_non_proxy_capability_is_404_json(string method, string path)
    {
        using var response = await _client!.SendAsync(new HttpRequestMessage(new HttpMethod(method), path));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var error = await ReadErrorAsync(response);
        Assert.Equal("not_found", error.Error);
        Assert.Equal($"No route {method} {path}. See GET /discover.", error.Message);
    }

    [Fact]
    public async Task Unknown_subpath_with_stopped_proxy_provider_stays_503_with_named_backend()
    {
        using var response = await _client!.GetAsync("/tts/voices");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var error = await ReadErrorAsync(response);
        Assert.Equal("provider_not_running", error.Error);
        Assert.Contains("'tts-local-like'", error.Message);
        Assert.Contains("'tts'", error.Message);
        Assert.Contains("Stopped", error.Message);
    }

    [Fact]
    public async Task Sleeping_proxy_capability_reports_sleeping()
    {
        var entry = Entry("tts", _ttsStopped, active: true);
        entry.IsSleeping = true;

        var (status, error) = await InvokeAsync(entry, "tts", "voices");

        Assert.Equal(503, status);
        Assert.Equal("capability_sleeping", error.Error);
    }

    [Fact]
    public async Task No_active_provider_is_404_unless_a_proxy_provider_is_configured()
    {
        var withoutProxy = Entry("image-gen", _imageGen, active: false);
        var withProxy = Entry("tts", _ttsStopped, active: false);

        var (notFoundStatus, notFound) = await InvokeAsync(withoutProxy, "image-gen", "workflows");
        var (unavailableStatus, unavailable) = await InvokeAsync(withProxy, "tts", "voices");

        Assert.Equal(404, notFoundStatus);
        Assert.Equal("not_found", notFound.Error);
        Assert.Equal(503, unavailableStatus);
        Assert.Equal("provider_not_configured", unavailable.Error);
    }

    [Fact]
    public async Task Known_capability_route_is_unaffected()
    {
        using var response = await _client!.GetAsync("/image-gen/status");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("comfy-like", json.RootElement.GetProperty("provider").GetString());
        Assert.Equal("Running", json.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Running_proxy_provider_still_forwards_unmatched_paths()
    {
        using var ok = await _client!.GetAsync("/stt/health");
        using var upstreamMissing = await _client.GetAsync("/stt/doesnotexist");

        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal("upstream-ok", await ok.Content.ReadAsStringAsync());
        // The backend's own answer is relayed verbatim.
        Assert.Equal(HttpStatusCode.NotFound, upstreamMissing.StatusCode);
    }

    private static void Register(CapabilityRegistry registry, FakeProvider provider) =>
        registry.Register(provider.CapabilitySlug, new CapabilityDefinition
        {
            Slug = provider.CapabilitySlug,
            DisplayName = provider.CapabilitySlug,
            Description = provider.CapabilitySlug,
        }, new CapabilityConfig(), new Dictionary<string, IBackendProvider>
        {
            [provider.Name] = provider,
        }, provider.Name);

    private static CapabilityEntry Entry(string slug, FakeProvider provider, bool active) => new()
    {
        Definition = new CapabilityDefinition { Slug = slug, DisplayName = slug, Description = slug },
        Config = new CapabilityConfig(),
        Providers = new Dictionary<string, IBackendProvider> { [provider.Name] = provider },
        DefaultProviderName = active ? provider.Name : null,
    };

    private async Task<(int Status, ErrorResponse Error)> InvokeAsync(
        CapabilityEntry entry, string slug, string path)
    {
        var context = new DefaultHttpContext { RequestServices = _app!.Services };
        context.Request.Method = "GET";
        context.Request.Path = $"/{slug}/{path}";
        context.Response.Body = new MemoryStream();
        var result = await GenericCapabilityEndpoints.HandleUnmatchedCapabilityPathAsync(
            context, slug, path, entry, (_, _) => { });
        await result.ExecuteAsync(context);
        context.Response.Body.Position = 0;
        return (context.Response.StatusCode,
            JsonSerializer.Deserialize<ErrorResponse>(context.Response.Body)!);
    }

    private static async Task<ErrorResponse> ReadErrorAsync(HttpResponseMessage response)
    {
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        return JsonSerializer.Deserialize<ErrorResponse>(await response.Content.ReadAsStringAsync())!;
    }

    private sealed class FakeProvider(string name, string slug) : IPluginProvider
    {
        public string Name => name;
        public string CapabilitySlug => slug;
        public string DisplayName => name;
        public string ProviderType => "Fake";
        public Dictionary<string, ParameterSchema> InputParameters => new();
        public ReturnSchema OutputSchema => new() { ContentType = "application/json" };
        public bool IsProxy { get; init; }
        public bool SupportsProgress => false;
        public bool SupportsRerun => false;
        public string? ContractVersion { get; init; }
        public object? RequestSchema => ContractVersion is null ? null : new { type = "object" };
        public object? ResponseSchema => ContractVersion is null ? null : new { type = "object" };
        public BackendStatus Status { get; init; }
        public string? ProxyUrl { get; set; }
        public TimeSpan HealthCheckInterval => TimeSpan.FromSeconds(30);

        public Task<bool> StartAsync(CancellationToken ct = default) => Task.FromResult(true);
        public Task StopAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<BackendStatus> GetStatusAsync(CancellationToken ct = default) => Task.FromResult(Status);
        public string? GetProxyTargetUrl() => Status == BackendStatus.Running ? ProxyUrl : null;
        public Task<JobResult?> ExecuteAsync(JobRequest request, CancellationToken ct = default)
            => Task.FromResult<JobResult?>(null);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
