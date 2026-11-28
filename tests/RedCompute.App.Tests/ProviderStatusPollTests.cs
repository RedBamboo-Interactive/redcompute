using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using RedBamboo.AppHost.Discovery;
using RedBamboo.AppHost.Tunnel;
using RedCompute.App.Api.Endpoints;
using RedCompute.App.Data;
using RedCompute.App.Services;
using RedCompute.App.Services.Jobs;
using RedCompute.App.Services.Nodes;
using RedCompute.Core.Capabilities;
using RedCompute.Core.Configuration;
using RedCompute.Core.Jobs;
using RedCompute.Core.Providers;
using RedCompute.PluginSdk.Hosting;
using RedCompute.PluginSdk.Hosting.Local;
using Xunit;

namespace RedCompute.App.Tests;

/// <summary>
/// /status, /nodes and /health answer from the status poll's last readings (<see cref="ProviderStatusCache"/>):
/// no provider is asked inside the request, so a stopped provider whose probe takes two seconds cannot slow them.
/// </summary>
public sealed class ProviderStatusPollTests : IAsyncLifetime
{
    private const string NodeZero = "node_01JCZ0000000000000000POLL";

    private readonly string _directory = Path.Combine(
        Environment.GetEnvironmentVariable("REDLEAF_SCRATCH_DIR") ?? Path.GetTempPath(), "redcompute-status-poll-tests", Guid.NewGuid().ToString("N"));
    private readonly CountingProvider _up = new("up", BackendStatus.Running);
    private readonly CountingProvider _down = new("down", BackendStatus.Stopped);
    private readonly CapabilityRegistry _registry = new();
    private WebApplication _app = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_directory);
        var database = Path.Combine(_directory, "jobs.db");
        using (var db = new RedComputeDbContext(database))
        {
            db.Database.EnsureCreated();
            db.MigrateSchema();
        }
        var jobs = new JobTrackingService(() => new RedComputeDbContext(database));
        _registry.Register("image-gen", new CapabilityDefinition { Slug = "image-gen", DisplayName = "Image" },
            new CapabilityConfig(), new Dictionary<string, IBackendProvider> { [_up.Name] = _up }, _up.Name);
        _registry.Register("tts", new CapabilityDefinition { Slug = "tts", DisplayName = "Speech" },
            new CapabilityConfig(), new Dictionary<string, IBackendProvider> { [_down.Name] = _down }, _down.Name);
        var nodes = new NodeRegistry(
            new NodeIdentityResult(new NodeIdentity(NodeZero, DateTimeOffset.UtcNow.AddDays(-1), NodeZero), true, false, null),
            NodeRegistryTests.Platform(), "0.2.0", DateTimeOffset.UtcNow, () => DateTimeOffset.UtcNow);

        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        _app = builder.Build();
        var endpoints = new EndpointRegistry(_app);
        GlobalEndpoints.Map(endpoints, _registry, jobs, null!, nodes);
        NodeEndpoints.Map(endpoints, nodes, _registry, () => null);
        var descriptor = new RedComputeServiceDescriptor(new RedComputeConfig { ApiPort = 18800 }, _registry, null, endpoints);
        _app.MapDiscoveryEndpoints(descriptor, CloudflareTunnelService.WithStateFile(Path.Combine(_directory, "tunnel.json")));
        await _app.StartAsync();
        _client = new HttpClient { BaseAddress = new Uri(_app.Urls.Single()) };
    }

    public async Task DisposeAsync()
    {
        _down.Release();
        _client.Dispose();
        await _app.DisposeAsync();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }

    [Theory]
    [InlineData("/status")]
    [InlineData("/nodes")]
    [InlineData("/health")]
    public async Task A_status_route_asks_no_provider_and_answers_fast_while_one_provider_hangs(string route)
    {
        await _registry.PollProviderStatusesAsync();
        // From here the stopped provider takes as long to answer as a refused loopback connect, or longer.
        _down.Hang();
        var before = (_up.Calls, _down.Calls);

        using (var warmUp = await _client.GetAsync(route)) Assert.Equal(HttpStatusCode.OK, warmUp.StatusCode);
        for (var i = 0; i < 3; i++)
        {
            var clock = Stopwatch.StartNew();
            using var response = await _client.GetAsync(route);
            clock.Stop();
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.True(clock.ElapsedMilliseconds < 50, $"{route} took {clock.ElapsedMilliseconds} ms");
        }

        Assert.Equal(before, (_up.Calls, _down.Calls));
    }

    [Fact]
    public async Task Each_route_reports_the_polled_statuses_and_their_age()
    {
        await _registry.PollProviderStatusesAsync();

        using var status = JsonDocument.Parse(await _client.GetStringAsync("/status"));
        var capabilities = status.RootElement.GetProperty("capabilities").EnumerateArray().ToDictionary(c => c.GetProperty("slug").GetString()!);
        Assert.Equal("Running", capabilities["image-gen"].GetProperty("status").GetString());
        Assert.Equal("Stopped", capabilities["tts"].GetProperty("status").GetString());
        Assert.True(Single(capabilities["tts"]).TryGetProperty("statusAsOf", out _));
        Assert.True(status.RootElement.GetProperty("statusAgeSeconds").GetDouble() >= 0);
        Assert.Equal(5, status.RootElement.GetProperty("statusPollSeconds").GetDouble());
        Assert.True(status.RootElement.GetProperty("statusAsOf").GetDateTimeOffset() <= DateTimeOffset.UtcNow);

        using var nodes = JsonDocument.Parse(await _client.GetStringAsync("/nodes"));
        var zero = Assert.Single(nodes.RootElement.GetProperty("nodes").EnumerateArray());
        var tts = zero.GetProperty("capabilities").EnumerateArray().Single(c => c.GetProperty("slug").GetString() == "tts");
        Assert.Equal("Stopped", tts.GetProperty("status").GetString());
        Assert.True(tts.TryGetProperty("statusAsOf", out _));
        Assert.True(tts.GetProperty("statusAgeSeconds").GetDouble() >= 0);
        Assert.True(Single(tts).TryGetProperty("statusAsOf", out _));

        using var health = JsonDocument.Parse(await _client.GetStringAsync("/health"));
        Assert.Equal("ok", health.RootElement.GetProperty("status").GetString());
        var extras = health.RootElement.GetProperty("extras");
        Assert.True(extras.TryGetProperty("statusAsOf", out _));
        Assert.True(extras.GetProperty("statusAgeSeconds").GetDouble() >= 0);
    }

    [Fact]
    public async Task A_provider_that_changes_state_shows_after_the_next_poll()
    {
        await _registry.PollProviderStatusesAsync();
        Assert.Equal("Running", await CapabilityStatusAsync("image-gen"));

        _up.Status = BackendStatus.Error;
        Assert.Equal("Running", await CapabilityStatusAsync("image-gen")); // the request asks nobody
        await _registry.PollProviderStatusesAsync();
        Assert.Equal("Error", await CapabilityStatusAsync("image-gen"));
        using var health = JsonDocument.Parse(await _client.GetStringAsync("/health"));
        Assert.Equal("degraded", health.RootElement.GetProperty("status").GetString());

        _up.Status = BackendStatus.Running;
        await _registry.PollProviderStatusesAsync();
        Assert.Equal("Running", await CapabilityStatusAsync("image-gen"));
    }

    [Fact]
    public async Task Before_the_first_poll_a_provider_reads_stopped_without_an_age()
    {
        using var status = JsonDocument.Parse(await _client.GetStringAsync("/status"));
        var image = status.RootElement.GetProperty("capabilities").EnumerateArray().Single(c => c.GetProperty("slug").GetString() == "image-gen");
        Assert.Equal("Stopped", image.GetProperty("status").GetString());
        Assert.False(Single(image).TryGetProperty("statusAsOf", out var asOf) && asOf.ValueKind != JsonValueKind.Null);
        Assert.Equal(0, _up.Calls);
    }

    private async Task<string?> CapabilityStatusAsync(string slug)
    {
        using var status = JsonDocument.Parse(await _client.GetStringAsync("/status"));
        return status.RootElement.GetProperty("capabilities").EnumerateArray()
            .Single(c => c.GetProperty("slug").GetString() == slug).GetProperty("status").GetString();
    }

    private static JsonElement Single(JsonElement capability) => Assert.Single(capability.GetProperty("providers").EnumerateArray());

    // ---- the cache itself, on a clock the test moves ----------------------------------------------

    [Fact]
    public async Task Admission_trusts_only_a_fresh_running_reading()
    {
        var now = DateTimeOffset.Parse("2026-11-28T10:00:00Z");
        var cache = new ProviderStatusCache(() => now);
        var provider = new CountingProvider("p", BackendStatus.Running);

        // Never polled: the provider is asked, nothing is recorded.
        Assert.Equal(BackendStatus.Running, await cache.AdmissionStatusAsync(provider));
        Assert.Equal(1, provider.Calls);
        Assert.Null(cache.Get(provider));

        await cache.RefreshAsync([provider], TimeSpan.FromSeconds(1));
        Assert.Equal(2, provider.Calls);
        now += ProviderStatusCache.FreshFor - TimeSpan.FromSeconds(1);
        Assert.Equal(BackendStatus.Running, await cache.AdmissionStatusAsync(provider));
        Assert.Equal(2, provider.Calls); // fresh Running: admitted without asking

        now += TimeSpan.FromSeconds(2);
        provider.Status = BackendStatus.Stopped;
        Assert.Equal(BackendStatus.Stopped, await cache.AdmissionStatusAsync(provider));
        Assert.Equal(3, provider.Calls); // stale: asked, and the answer recorded
        Assert.Equal(new ProviderStatusReading(BackendStatus.Stopped, now), cache.Get(provider));

        provider.Status = BackendStatus.Running;
        Assert.Equal(BackendStatus.Running, await cache.AdmissionStatusAsync(provider));
        Assert.Equal(4, provider.Calls); // a fresh reading that is not Running is confirmed, so a provider started since is admitted
    }

    [Fact]
    public async Task A_poll_moves_on_after_its_budget_and_never_stacks_probes_on_a_hung_provider()
    {
        var cache = new ProviderStatusCache();
        var fast = new CountingProvider("fast", BackendStatus.Running);
        var hung = new CountingProvider("hung", BackendStatus.Running);
        hung.Hang();
        try
        {
            var clock = Stopwatch.StartNew();
            await cache.RefreshAsync([fast, hung, fast], TimeSpan.FromMilliseconds(200));
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(2), $"the poll took {clock.ElapsedMilliseconds} ms");
            Assert.Equal(BackendStatus.Running, cache.Get(fast)?.Status);
            Assert.Null(cache.Get(hung));

            await cache.RefreshAsync([fast, hung], TimeSpan.FromMilliseconds(50));
            Assert.Equal(2, fast.Calls);
            Assert.Equal(1, hung.Calls); // still in flight: not asked again
        }
        finally { hung.Release(); }

        await WaitUntilAsync(() => cache.Get(hung) is not null);
        Assert.Equal(BackendStatus.Running, cache.Get(hung)?.Status);
    }

    [Fact]
    public async Task A_provider_that_throws_reads_error()
    {
        var cache = new ProviderStatusCache();
        var broken = new CountingProvider("broken", BackendStatus.Running) { Throws = true };
        await cache.RefreshAsync([broken], TimeSpan.FromSeconds(1));
        Assert.Equal(BackendStatus.Error, cache.StatusOf(broken));
    }

    // ---- the loopback probe handler --------------------------------------------------------------

    [Fact]
    public async Task A_probe_of_a_closed_loopback_port_is_refused_within_its_connect_budget()
    {
        var port = FreePort();
        using var client = new HttpClient(LoopbackProbe.CreateHandler()) { Timeout = TimeSpan.FromSeconds(10) };
        var clock = Stopwatch.StartNew();
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync($"http://127.0.0.1:{port}/"));
        // Windows refuses a closed loopback port only after about 2 s; the probe gives up after 250 ms.
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(1), $"took {clock.ElapsedMilliseconds} ms");
    }

    [Fact]
    public async Task A_probe_of_localhost_reaches_a_service_that_listens_on_ipv4_only()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        app.MapGet("/health", () => "ok");
        await app.StartAsync();
        var port = new Uri(app.Urls.Single()).Port;

        using var client = LocalExecutionHost.Shared.Network.CreateProbeClient($"http://localhost:{port}", TimeSpan.FromSeconds(10));
        var clock = Stopwatch.StartNew();
        Assert.Equal("ok", await client.GetStringAsync($"http://localhost:{port}/health"));
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(1), $"took {clock.ElapsedMilliseconds} ms");
    }

    [Theory]
    [InlineData("localhost", "127.0.0.1,::1")]
    [InlineData("LOCALHOST", "127.0.0.1,::1")]
    [InlineData("127.0.0.1", "127.0.0.1")]
    [InlineData("127.0.0.2", "127.0.0.2")]
    [InlineData("[::1]", "::1")]
    [InlineData("172.20.1.5", null)]
    [InlineData("192.168.1.10", null)]
    [InlineData("comfyui", null)]
    public void Only_loopback_hosts_get_the_short_connect(string host, string? expected)
        => Assert.Equal(expected, LoopbackProbe.LoopbackAddresses(host) is { } addresses ? string.Join(",", addresses.Select(a => a.ToString())) : null);

    [Fact]
    public void Node_zeros_probe_handler_is_the_loopback_probe_and_its_service_handler_is_unchanged()
    {
        using var probe = LocalExecutionHost.Shared.Network.CreateProbeHandler(new Uri("http://127.0.0.1:8188"));
        Assert.IsType<SocketsHttpHandler>(probe);
        Assert.NotNull(((SocketsHttpHandler)probe).ConnectCallback);
        using var service = LocalExecutionHost.Shared.Network.CreateServiceHandler(new Uri("http://127.0.0.1:8188"));
        Assert.IsType<HttpClientHandler>(service);
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++) await Task.Delay(25);
        Assert.True(condition());
    }

    private sealed class CountingProvider(string name, BackendStatus status) : IBackendProvider
    {
        private int _calls;
        private TaskCompletionSource? _hang;

        public int Calls => Volatile.Read(ref _calls);
        public BackendStatus Status { get; set; } = status;
        public bool Throws { get; init; }
        public string Name => name;
        public string CapabilitySlug => name;
        public TimeSpan HealthCheckInterval => TimeSpan.FromSeconds(30);

        public void Hang() => _hang = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Release() => _hang?.TrySetResult();

        public async Task<BackendStatus> GetStatusAsync(CancellationToken ct = default)
        {
            Interlocked.Increment(ref _calls);
            if (_hang is { } hang) await hang.Task;
            if (Throws) throw new InvalidOperationException("probe failed");
            return Status;
        }

        public Task<bool> StartAsync(CancellationToken ct = default) => Task.FromResult(true);
        public Task StopAsync(CancellationToken ct = default) => Task.CompletedTask;
        public string? GetProxyTargetUrl() => null;
        public Task<JobResult?> ExecuteAsync(JobRequest request, CancellationToken ct = default) => Task.FromResult<JobResult?>(null);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
