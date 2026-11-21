using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
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

namespace RedCompute.App.Tests;

/// <summary>
/// <c>placement</c> on <c>/{slug}/generate</c> (design 2.1): <c>nodeId</c> only, checked against the
/// registry, and never part of what the provider or the job input sees. Since Phase 4 P1 the job
/// records the resolved node (design 3.3).
/// </summary>
public sealed class PlacementGenerateEndpointTests : IAsyncLifetime
{
    private const string NodeZero = "node_01JCZ0000000000000000PLAC";

    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "redcompute-placement-generate-tests", Guid.NewGuid().ToString("N"));
    private readonly RecordingProvider _provider = new();
    private JobTrackingService _jobs = null!;
    private NodeRegistry _nodes = null!;
    private CapabilityRegistry _registry = null!;
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

        var registry = _registry = new CapabilityRegistry();
        registry.Register("image-gen", new CapabilityDefinition
        {
            Slug = "image-gen", DisplayName = "Image", Description = "Image test",
        }, new CapabilityConfig(), new Dictionary<string, IBackendProvider> { [_provider.Name] = _provider }, _provider.Name);

        var nodes = _nodes = new NodeRegistry(
            new NodeIdentityResult(new NodeIdentity(NodeZero, DateTimeOffset.UtcNow.AddDays(-1), NodeZero), true, false, null),
            NodeRegistryTests.Platform(), "0.2.0", DateTimeOffset.UtcNow, () => DateTimeOffset.UtcNow);
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        _app = builder.Build();
        GenericCapabilityEndpoints.Map(_app, new EndpointRegistry(_app), registry, _jobs, (_, _) => { }, nodes: nodes);
        _jobs.StampNodeId = nodes.StampNodeId;
        await _app.StartAsync();
        _client = new HttpClient { BaseAddress = new Uri(_app.Urls.Single()) };
        _client.DefaultRequestHeaders.Add("X-System-Reason", "placement generate contract test");
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _app.DisposeAsync();
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    [Theory]
    [InlineData("local", "explicit")]
    [InlineData(NodeZero, "explicit")]
    [InlineData(null, "local")]
    public async Task A_node_is_accepted_the_provider_never_sees_it_and_the_job_records_it(string? nodeId, string why)
    {
        using var response = await _client.PostAsJsonAsync("/image-gen/generate",
            new { prompt = "a red leaf", placement = new { nodeId } });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var request = Assert.Single(_provider.Requests);
        Assert.Equal(["prompt"], request.Parameters.Keys);
        var job = _jobs.GetJob(request.JobId!.Value)!;
        Assert.Equal("""{"prompt":"a red leaf"}""", job.InputJson);
        // The executing node is node zero, stamped as it always was, and the job says why.
        Assert.Equal(NodeZero, job.NodeId);
        Assert.Equal(why, job.PlacementWhy);
        var details = JsonSerializer.SerializeToElement(JobApiResponse.Details(job, _directory, _nodes));
        var resolved = details.GetProperty("placement").GetProperty("resolved");
        Assert.Equal(NodeZero, resolved.GetProperty("nodeId").GetString());
        Assert.Equal(why, resolved.GetProperty("why").GetString());
    }

    [Fact]
    public async Task Without_placement_the_job_records_no_placement()
    {
        using var response = await _client.PostAsJsonAsync("/image-gen/generate", new { prompt = "a red leaf" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var job = _jobs.GetJob(Assert.Single(_provider.Requests).JobId!.Value)!;
        Assert.Equal(NodeZero, job.NodeId);
        Assert.Null(job.PlacementWhy);
        Assert.False(JobApiResponse.Details(job, _directory, _nodes).ContainsKey("placement"));
    }

    [Fact]
    public void Node_zero_keeps_its_stamp_even_when_named_and_its_identity_is_not_persisted()
    {
        var jobs = _jobs;
        jobs.StampNodeId = null;
        var job = jobs.CreateJob(new JobSubmission("image-gen", "recording", "{}",
            JobProvenance.DirectRedCompute("POST", "/image-gen/generate", new JobBeneficiary("user", "owner"), Guid.NewGuid().ToString("N"), null),
            Placement: new PlacementResolution(NodeZero, IsLocal: true, PlacementResolution.Explicit)));
        Assert.Null(jobs.GetJob(job.Id)!.NodeId);
        Assert.Equal("explicit", jobs.GetJob(job.Id)!.PlacementWhy);
    }

    [Fact]
    public void A_remote_resolution_stamps_the_remote_node_and_has_no_provider_yet()
    {
        var job = _jobs.CreateJob(new JobSubmission("image-gen", "recording", "{}",
            JobProvenance.DirectRedCompute("POST", "/image-gen/generate", new JobBeneficiary("user", "owner"), Guid.NewGuid().ToString("N"), null),
            Placement: new PlacementResolution("node_01JCZ000000000000000REMOT", IsLocal: false, PlacementResolution.Explicit)));
        Assert.Equal("node_01JCZ000000000000000REMOT", _jobs.GetJob(job.Id)!.NodeId);

        var entry = _registry.Get("image-gen")!;
        var (provider, error) = RedCompute.App.Api.ProviderResolver.Resolve(entry, null, "image-gen",
            new PlacementResolution("node_01JCZ000000000000000REMOT", IsLocal: false, PlacementResolution.Explicit));
        Assert.Null(provider);
        Assert.NotNull(error);
        Assert.Same(_provider, RedCompute.App.Api.ProviderResolver.Resolve(entry, null, "image-gen",
            new PlacementResolution(NodeZero, IsLocal: true, PlacementResolution.Explicit)).provider);
    }

    [Fact]
    public async Task An_unknown_node_is_404_and_nothing_runs()
    {
        using var response = await _client.PostAsJsonAsync("/image-gen/generate",
            new { prompt = "a red leaf", placement = new { nodeId = "node_01JCZ000000000000000OTHER" } });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("node_not_found", body.GetProperty("error").GetString());
        Assert.Equal("node_01JCZ000000000000000OTHER", body.GetProperty("nodeId").GetString());
        Assert.Empty(_provider.Requests);
    }

    [Theory]
    [InlineData("workspace", "placement.workspace")]
    [InlineData("scratch", "placement.scratch")]
    public async Task Any_other_member_is_not_supported_on_this_route(string member, string field)
    {
        object value = member == "workspace"
            ? new { agentId = "7e1e0000-0000-4000-8000-0000000000a1" }
            : new { owner = "nova", executionKey = "k" };
        using var response = await _client.PostAsJsonAsync("/image-gen/generate",
            new { prompt = "a red leaf", placement = new Dictionary<string, object> { ["nodeId"] = "local", [member] = value } });

        Assert.Equal((HttpStatusCode)422, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("placement_member_not_supported", body.GetProperty("error").GetString());
        Assert.True(body.GetProperty("fields").TryGetProperty(field, out _));
        Assert.Empty(_provider.Requests);
    }

    private sealed class RecordingProvider : IPluginProvider
    {
        public List<JobRequest> Requests { get; } = [];

        public string Name => "recording";
        public string CapabilitySlug => "image-gen";
        public string DisplayName => "Recording";
        public string ProviderType => "Recording";
        public Dictionary<string, ParameterSchema> InputParameters => new();
        public ReturnSchema OutputSchema => new() { ContentType = "application/json" };
        public bool IsProxy => false;
        public bool SupportsProgress => false;
        public bool SupportsRerun => false;
        public TimeSpan HealthCheckInterval => TimeSpan.FromSeconds(30);
        public Task<bool> StartAsync(CancellationToken ct = default) => Task.FromResult(true);
        public Task StopAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<BackendStatus> GetStatusAsync(CancellationToken ct = default) => Task.FromResult(BackendStatus.Running);
        public string? GetProxyTargetUrl() => null;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public Task<JobResult?> ExecuteAsync(JobRequest request, CancellationToken ct = default)
        {
            lock (Requests) Requests.Add(request);
            return Task.FromResult<JobResult?>(new JobResult { Success = true, ResultJson = "{}" });
        }
    }
}
