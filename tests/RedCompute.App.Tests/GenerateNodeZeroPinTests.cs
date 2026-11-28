using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
using RedBamboo.AppHost.Discovery;
using RedCompute.App.Api.Endpoints;
using RedCompute.App.Data;
using RedCompute.App.Services;
using RedCompute.App.Services.Jobs;
using RedCompute.App.Services.Nodes;
using RedCompute.Core.Capabilities;
using RedCompute.Core.Configuration;
using RedCompute.Core.Discovery;
using RedCompute.Core.Jobs;
using RedCompute.Core.Providers;
using Xunit;
using Xunit.Abstractions;

namespace RedCompute.App.Tests;

/// <summary>
/// The pinned before/after request test for Phase 4 P7 (per-node providers and Automatic routing touch
/// <c>/{slug}/generate</c> and the job path). With node zero only, each request below must answer, reach the
/// provider and record its job exactly as it did at P6 (607f5c0): the goldens were captured there.
/// </summary>
public sealed class GenerateNodeZeroPinTests(ITestOutputHelper output) : IAsyncLifetime
{
    private const string NodeZero = "node_01JCZ0000000000000000PINS";

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "redcompute-generate-pin-tests", Guid.NewGuid().ToString("N"));
    private readonly RecordingProvider _provider = new();
    private JobTrackingService _jobs = null!;
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
        _jobs = new JobTrackingService(() => new RedComputeDbContext(database));
        var registry = new CapabilityRegistry();
        registry.Register("image-gen", new CapabilityDefinition { Slug = "image-gen", DisplayName = "Image", Description = "Image test" },
            new CapabilityConfig { Providers = { ["recording"] = new ProviderConfig { Type = "ComfyUI" } } },
            new Dictionary<string, IBackendProvider> { [_provider.Name] = _provider }, _provider.Name);
        var stopped = new RecordingProvider("stopped", "music-gen", BackendStatus.Stopped);
        registry.Register("music-gen", new CapabilityDefinition { Slug = "music-gen", DisplayName = "Music", Description = "Music test" },
            new CapabilityConfig { Providers = { ["stopped"] = new ProviderConfig { Type = "Suno" } } },
            new Dictionary<string, IBackendProvider> { [stopped.Name] = stopped }, stopped.Name);
        // What RelayServer's status poll does at start: /status answers from these readings.
        await registry.PollProviderStatusesAsync();
        var nodes = new NodeRegistry(
            new NodeIdentityResult(new NodeIdentity(NodeZero, DateTimeOffset.UtcNow.AddDays(-1), NodeZero), true, false, null),
            NodeRegistryTests.Platform(), "0.2.0", DateTimeOffset.UtcNow, () => DateTimeOffset.UtcNow);
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        // RelayServer's JSON options, so the answers are the ones production writes.
        builder.Services.ConfigureHttpJsonOptions(options =>
        {
            options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
            options.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
        });
        _app = builder.Build();
        var endpoints = new EndpointRegistry(_app);
        GenericCapabilityEndpoints.Map(_app, endpoints, registry, _jobs, (_, _) => { }, nodes: nodes);
        GlobalEndpoints.Map(endpoints, registry, _jobs, null!, nodes);
        _jobs.StampNodeId = nodes.StampNodeId;
        await _app.StartAsync();
        _client = new HttpClient { BaseAddress = new Uri(_app.Urls.Single()) };
        _client.DefaultRequestHeaders.Add("X-System-Reason", "generate pin test");
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _app.DisposeAsync();
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    /// <summary>Each request, as JSON, and the path it is posted to.</summary>
    public static TheoryData<string, string, string> Requests() => new()
    {
        { "no placement", "/image-gen/generate", """{"prompt":"a red leaf","seed":7}""" },
        { "placement null", "/image-gen/generate", """{"prompt":"a red leaf","placement":null}""" },
        { "nodeId null", "/image-gen/generate", """{"prompt":"a red leaf","placement":{"nodeId":null}}""" },
        { "nodeId local", "/image-gen/generate", """{"prompt":"a red leaf","placement":{"nodeId":"local"}}""" },
        { "nodeId node zero", "/image-gen/generate", """{"prompt":"a red leaf","placement":{"nodeId":"node_01JCZ0000000000000000PINS"}}""" },
        { "provider named", "/image-gen/generate", """{"prompt":"a red leaf","provider":"recording","placement":{"nodeId":"local"}}""" },
        { "provider unknown", "/image-gen/generate", """{"prompt":"a red leaf","provider":"nope"}""" },
        { "async", "/image-gen/generate?async=true", """{"prompt":"a red leaf","name":"pinned"}""" },
        { "unknown node", "/image-gen/generate", """{"prompt":"a red leaf","placement":{"nodeId":"node_01JD0000000000000000000099"}}""" },
        { "bad member", "/image-gen/generate", """{"prompt":"a red leaf","placement":{"workspace":{"agentId":"x"}}}""" },
        { "not running", "/music-gen/generate", """{"prompt":"a red leaf"}""" },
        { "not running on node zero", "/music-gen/generate", """{"prompt":"a red leaf","placement":{"nodeId":"local"}}""" },
    };

    [Theory]
    [MemberData(nameof(Requests))]
    public async Task Node_zero_generate_is_pinned_to_its_p6_answer(string name, string path, string body)
    {
        var actual = await RenderAsync(path, body);
        output.WriteLine($"{name} => {actual}");
        Assert.Equal(Goldens[name], actual);
    }

    /// <summary>
    /// /status with node zero only: everything but the uptime, as P6 wrote it, once the fields the status poll
    /// added (statusAsOf on each provider and the answer, statusAgeSeconds, statusPollSeconds) are taken out:
    /// the poll only added fields.
    /// </summary>
    [Fact]
    public async Task Node_zero_status_is_pinned_to_its_p6_answer()
    {
        var text = await _client.GetStringAsync("/status");
        var actual = Regex.Replace(text, "\"uptime\":[0-9.E+-]+", "\"uptime\":0");
        output.WriteLine($"status => {actual}");
        var added = new Regex(",\"statusAsOf\":\"[^\"]+\"|,\"statusAgeSeconds\":[0-9.E+-]+|,\"statusPollSeconds\":5");
        Assert.Equal(5, added.Matches(actual).Count); // two providers, and the answer's three
        Assert.Equal(Goldens["status"], added.Replace(actual, ""));
    }

    /// <summary>Status, body (job ids as {job}), what the provider got, and the job as recorded.</summary>
    private async Task<string> RenderAsync(string path, string body)
    {
        using var response = await _client.PostAsync(path, new StringContent(body, Encoding.UTF8, "application/json"));
        var text = await response.Content.ReadAsStringAsync();
        // An async job runs after the 202: wait for it to reach the provider.
        for (var i = 0; i < 200 && response.StatusCode == System.Net.HttpStatusCode.Accepted && _provider.Requests.Count == 0; i++)
            await Task.Delay(10);
        var builder = new StringBuilder();
        builder.Append((int)response.StatusCode).Append(' ').Append(Jobs().Replace(text, "{job}"));
        lock (_provider.Requests)
        {
            foreach (var request in _provider.Requests)
            {
                builder.Append(" | provider ").Append(JsonSerializer.Serialize(request.Parameters));
                var job = _jobs.GetJob(request.JobId!.Value)!;
                for (var i = 0; i < 200 && job.Status is JobStatus.Running or JobStatus.Queued; i++)
                {
                    Thread.Sleep(10);
                    job = _jobs.GetJob(request.JobId!.Value)!;
                }
                builder.Append(" | job ").Append(string.Join(';', job.CapabilitySlug, job.ProviderName, job.InputJson, job.NodeId,
                    job.PlacementWhy ?? "-", job.Status, job.Name ?? "-"));
            }
        }
        return builder.ToString();
    }

    private static Regex Jobs() => new("[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}");

    /// <summary>Captured at P6 (607f5c0). Never edit one to make a change pass.</summary>
    private static readonly Dictionary<string, string> Goldens = new()
    {
        ["no placement"] = """200 {"images":1} | provider {"prompt":"a red leaf","seed":7} | job image-gen;recording;{"prompt":"a red leaf","seed":7};node_01JCZ0000000000000000PINS;-;Completed;-""",
        ["placement null"] = """200 {"images":1} | provider {"prompt":"a red leaf"} | job image-gen;recording;{"prompt":"a red leaf"};node_01JCZ0000000000000000PINS;-;Completed;-""",
        ["nodeId null"] = """200 {"images":1} | provider {"prompt":"a red leaf"} | job image-gen;recording;{"prompt":"a red leaf"};node_01JCZ0000000000000000PINS;local;Completed;-""",
        ["nodeId local"] = """200 {"images":1} | provider {"prompt":"a red leaf"} | job image-gen;recording;{"prompt":"a red leaf"};node_01JCZ0000000000000000PINS;explicit;Completed;-""",
        ["nodeId node zero"] = """200 {"images":1} | provider {"prompt":"a red leaf"} | job image-gen;recording;{"prompt":"a red leaf"};node_01JCZ0000000000000000PINS;explicit;Completed;-""",
        ["provider named"] = """200 {"images":1} | provider {"prompt":"a red leaf"} | job image-gen;recording;{"prompt":"a red leaf"};node_01JCZ0000000000000000PINS;explicit;Completed;-""",
        ["provider unknown"] = """404 {"error":"provider_not_found","message":"Provider 'nope' not found for image-gen. Available: recording"}""",
        ["async"] = """202 {"jobId":"{job}","status":"running"} | provider {"prompt":"a red leaf"} | job image-gen;recording;{"prompt":"a red leaf"};node_01JCZ0000000000000000PINS;-;Completed;pinned""",
        ["unknown node"] = """404 {"nodeId":"node_01JD0000000000000000000099","error":"node_not_found","message":"No node has the id 'node_01JD0000000000000000000099'"}""",
        ["bad member"] = """422 {"error":"placement_member_not_supported","message":"placement.workspace is not supported on this route; only placement.nodeId is","fields":{"placement.workspace":"not supported on this route"}}""",
        ["not running"] = """503 {"error":"provider_not_running","message":"Backend for 'music-gen' is Stopped. Start via POST /control/start/music-gen"}""",
        ["not running on node zero"] = """503 {"error":"provider_not_running","message":"Backend for 'music-gen' is Stopped. Start via POST /control/start/music-gen"}""",
        ["status"] = """{"service":"RedCompute","uptime":0,"localNodeId":"node_01JCZ0000000000000000PINS","capabilities":[{"slug":"image-gen","displayName":"Image","type":"image-gen","status":"Running","provider":"recording","defaultProvider":"recording","providers":[{"name":"recording","status":"Running","nodeId":"node_01JCZ0000000000000000PINS"}],"executionMode":"provider","sleeping":false,"disabled":false,"description":"Image test","rerunnable":false},{"slug":"music-gen","displayName":"Music","type":"music-gen","status":"Stopped","provider":"stopped","defaultProvider":"stopped","providers":[{"name":"stopped","status":"Stopped","nodeId":"node_01JCZ0000000000000000PINS"}],"executionMode":"provider","sleeping":false,"disabled":false,"description":"Music test","rerunnable":false}]}""",
    };

    private sealed class RecordingProvider(string name = "recording", string slug = "image-gen", BackendStatus status = BackendStatus.Running) : IPluginProvider
    {
        public List<JobRequest> Requests { get; } = [];

        public string Name => name;
        public string CapabilitySlug => slug;
        public string DisplayName => "Recording";
        public string ProviderType => "ComfyUI";
        public Dictionary<string, ParameterSchema> InputParameters => new()
        {
            ["prompt"] = new ParameterSchema { Type = "string", Required = true },
            ["seed"] = new ParameterSchema { Type = "integer" },
        };
        public ReturnSchema OutputSchema => new() { ContentType = "application/json" };
        public bool IsProxy => false;
        public bool SupportsProgress => false;
        public bool SupportsRerun => false;
        public TimeSpan HealthCheckInterval => TimeSpan.FromSeconds(30);
        public Task<bool> StartAsync(CancellationToken ct = default) => Task.FromResult(true);
        public Task StopAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<BackendStatus> GetStatusAsync(CancellationToken ct = default) => Task.FromResult(status);
        public string? GetProxyTargetUrl() => null;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public Task<JobResult?> ExecuteAsync(JobRequest request, CancellationToken ct = default)
        {
            lock (Requests) Requests.Add(request);
            return Task.FromResult<JobResult?>(new JobResult { Success = true, ResultJson = """{"images":1}""" });
        }
    }
}
