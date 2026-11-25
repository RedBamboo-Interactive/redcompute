using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
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
/// The pinned before/after request test for the generate and job path (Phase 4 P1). Each request's
/// whole observable result is captured: the HTTP answer, what the provider received, the stored job,
/// the job API's details and progress, the job record as the WebSocket broadcasts it, and the Created
/// lifecycle event. <see cref="Before"/> is what the build before P1 produced (captured at aaa25d6).
/// A request without <c>placement</c> must still produce exactly that. A request with one produces
/// it plus <c>placement.resolved</c>, and nothing else changes.
/// </summary>
public sealed class GenerateJobPathPinTests : IAsyncLifetime
{
    private const string NodeZero = "node_01JCZ0000000000000000PINS";

    private readonly ITestOutputHelper _output;
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "redcompute-generate-pin-tests", Guid.NewGuid().ToString("N"));
    private readonly RecordingProvider _provider = new();
    private JobTrackingService _jobs = null!;
    private NodeRegistry _nodes = null!;
    private WebApplication _app = null!;
    private HttpClient _client = null!;

    public GenerateJobPathPinTests(ITestOutputHelper output) => _output = output;

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
        registry.Register("image-gen", new CapabilityDefinition
        {
            Slug = "image-gen", DisplayName = "Image", Description = "Image test",
        }, new CapabilityConfig(), new Dictionary<string, IBackendProvider> { [_provider.Name] = _provider }, _provider.Name);

        _nodes = new NodeRegistry(
            new NodeIdentityResult(new NodeIdentity(NodeZero, DateTimeOffset.UtcNow.AddDays(-1), NodeZero), true, false, null),
            NodeRegistryTests.Platform(), "0.2.0", DateTimeOffset.UtcNow, () => DateTimeOffset.UtcNow);
        _jobs.StampNodeId = _nodes.StampNodeId;
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        _app = builder.Build();
        GenericCapabilityEndpoints.Map(_app, new EndpointRegistry(_app), registry, _jobs, (_, _) => { }, nodes: _nodes);
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

    /// <summary>Captured at aaa25d6, the build before P1, for each request below (all five were identical).</summary>
    private const string Before = """
status 200 application/json; charset=utf-8
x-job-id True
body {"ok":true}
provider ["prompt"]
job.nodeId node_01JCZ0000000000000000PINS
job.input {"prompt":"a red leaf"}
details {"terminal":true,"nodeId":"node_01JCZ0000000000000000PINS","stage":"completed","result":{"ok":true},"outputs":[],"links":{"self":"/jobs/<guid>","progress":"/image-gen/jobs/<guid>/progress","output":"/image-gen/jobs/<guid>/output","lifecycleEvents":"/jobs/<guid>/lifecycle-events"},"id":"<guid>","capability":"image-gen","providerName":"recording","status":"Completed","queuedAt":"<ts>","startedAt":"<ts>","completedAt":"<ts>","durationMs":<n>,"progress":1,"input":"{\u0022prompt\u0022:\u0022a red leaf\u0022}","outputLocation":null,"outputSizeBytes":null,"outputContentType":null,"resultJson":"{\u0022ok\u0022:true}","errorMessage":null,"errorDetails":null,"name":null,"rationale":null,"costUsd":null,"userId":null,"confidential":false,"keepResident":false,"supervisorSessionId":null,"userName":null,"userAvatarUrl":null,"externalExecution":false,"parentJobId":null,"attemptCount":0,"leaseOwner":null,"leaseExpiresAt":null,"creationProvenance":{"schemaVersion":1,"origin":{"service":"redcompute","app":{"kind":"direct-client","id":"direct-redcompute-api","entityId":null,"nameSnapshot":"Direct RedCompute client","iconSnapshot":null,"colorSnapshot":null},"entrypoint":{"kind":"http","route":"/image-gen/generate","method":"POST"}},"actor":{"kind":"app","nameSnapshot":"Direct RedCompute client","entityId":null,"id":"direct-redcompute-api","avatarSnapshot":null},"onBehalfOf":{"kind":"system","id":null,"nameSnapshot":null,"avatarSnapshot":null,"reason":"generate pin test"},"context":[],"trace":{"requestId":"<request>","correlationId":null,"parentJobId":null},"assurance":"asserted","capturedAt":"<ts>","cause":null},"auditComplete":true}
progress {"terminal":true,"nodeId":"node_01JCZ0000000000000000PINS","stage":"completed","result":{"ok":true},"outputs":[],"links":{"self":"/jobs/<guid>","progress":"/image-gen/jobs/<guid>/progress","output":"/image-gen/jobs/<guid>/output","lifecycleEvents":"/jobs/<guid>/lifecycle-events"},"id":"<guid>","status":"Completed","progress":1,"errorMessage":null,"durationMs":<n>,"resultJson":"{\u0022ok\u0022:true}"}
broadcast {"id":"<guid>","capabilitySlug":"image-gen","providerName":"recording","status":"Completed","queuedAt":"<ts>","startedAt":"<ts>","completedAt":"<ts>","inputJson":"{\u0022prompt\u0022:\u0022a red leaf\u0022}","progress":1,"resultJson":"{\u0022ok\u0022:true}","idempotencyScope":"<hex><hex>","idempotencyFingerprint":"<hex><hex>","externalExecution":false,"attemptCount":0,"creationProvenance":{"schemaVersion":1,"origin":{"service":"redcompute","app":{"kind":"direct-client","id":"direct-redcompute-api","nameSnapshot":"Direct RedCompute client"},"entrypoint":{"kind":"http","route":"/image-gen/generate","method":"POST"}},"actor":{"kind":"app","nameSnapshot":"Direct RedCompute client","id":"direct-redcompute-api"},"onBehalfOf":{"kind":"system","reason":"generate pin test"},"context":[],"trace":{"requestId":"<request>"},"assurance":"asserted","capturedAt":"<ts>"},"confidential":false,"keepResident":false,"nodeId":"node_01JCZ0000000000000000PINS","durationMs":<n>}
events Created:{"status":"Queued"},Started:{"status":"Running"},Completed:{}
""";

    /// <summary>Captured at aaa25d6 for the same requests with <c>?async=true</c> (all five identical).</summary>
    private const string BeforeAsync = """
status 202 application/json; charset=utf-8
x-job-id False
body {"jobId":"<guid>","status":"running"}
provider ["prompt"]
job.nodeId node_01JCZ0000000000000000PINS
job.input {"prompt":"a red leaf"}
details {"terminal":true,"nodeId":"node_01JCZ0000000000000000PINS","stage":"completed","result":{"ok":true},"outputs":[],"links":{"self":"/jobs/<guid>","progress":"/image-gen/jobs/<guid>/progress","output":"/image-gen/jobs/<guid>/output","lifecycleEvents":"/jobs/<guid>/lifecycle-events"},"id":"<guid>","capability":"image-gen","providerName":"recording","status":"Completed","queuedAt":"<ts>","startedAt":"<ts>","completedAt":"<ts>","durationMs":<n>,"progress":1,"input":"{\u0022prompt\u0022:\u0022a red leaf\u0022}","outputLocation":null,"outputSizeBytes":null,"outputContentType":null,"resultJson":"{\u0022ok\u0022:true}","errorMessage":null,"errorDetails":null,"name":null,"rationale":null,"costUsd":null,"userId":null,"confidential":false,"keepResident":false,"supervisorSessionId":null,"userName":null,"userAvatarUrl":null,"externalExecution":false,"parentJobId":null,"attemptCount":0,"leaseOwner":null,"leaseExpiresAt":null,"creationProvenance":{"schemaVersion":1,"origin":{"service":"redcompute","app":{"kind":"direct-client","id":"direct-redcompute-api","entityId":null,"nameSnapshot":"Direct RedCompute client","iconSnapshot":null,"colorSnapshot":null},"entrypoint":{"kind":"http","route":"/image-gen/generate","method":"POST"}},"actor":{"kind":"app","nameSnapshot":"Direct RedCompute client","entityId":null,"id":"direct-redcompute-api","avatarSnapshot":null},"onBehalfOf":{"kind":"system","id":null,"nameSnapshot":null,"avatarSnapshot":null,"reason":"generate pin test"},"context":[],"trace":{"requestId":"<request>","correlationId":null,"parentJobId":null},"assurance":"asserted","capturedAt":"<ts>","cause":null},"auditComplete":true}
progress {"terminal":true,"nodeId":"node_01JCZ0000000000000000PINS","stage":"completed","result":{"ok":true},"outputs":[],"links":{"self":"/jobs/<guid>","progress":"/image-gen/jobs/<guid>/progress","output":"/image-gen/jobs/<guid>/output","lifecycleEvents":"/jobs/<guid>/lifecycle-events"},"id":"<guid>","status":"Completed","progress":1,"errorMessage":null,"durationMs":<n>,"resultJson":"{\u0022ok\u0022:true}"}
broadcast {"id":"<guid>","capabilitySlug":"image-gen","providerName":"recording","status":"Completed","queuedAt":"<ts>","startedAt":"<ts>","completedAt":"<ts>","inputJson":"{\u0022prompt\u0022:\u0022a red leaf\u0022}","progress":1,"resultJson":"{\u0022ok\u0022:true}","idempotencyScope":"<hex><hex>","idempotencyFingerprint":"<hex><hex>","externalExecution":false,"attemptCount":0,"creationProvenance":{"schemaVersion":1,"origin":{"service":"redcompute","app":{"kind":"direct-client","id":"direct-redcompute-api","nameSnapshot":"Direct RedCompute client"},"entrypoint":{"kind":"http","route":"/image-gen/generate","method":"POST"}},"actor":{"kind":"app","nameSnapshot":"Direct RedCompute client","id":"direct-redcompute-api"},"onBehalfOf":{"kind":"system","reason":"generate pin test"},"context":[],"trace":{"requestId":"<request>"},"assurance":"asserted","capturedAt":"<ts>"},"confidential":false,"keepResident":false,"nodeId":"node_01JCZ0000000000000000PINS","durationMs":<n>}
events Created:{"status":"Queued"},Started:{"status":"Running"},Completed:{}
""";

    public static TheoryData<string, string?, string?> Requests() => new()
    {
        // name, placement JSON (null: no placement member), the recorded why (null: nothing recorded)
        { "no placement", null, null },
        { "nodeId local", """{"nodeId":"local"}""", "explicit" },
        { "nodeId null", """{"nodeId":null}""", "local" },
        { "nodeId node zero", $$"""{"nodeId":"{{NodeZero}}"}""", "explicit" },
        { "empty placement", "{}", "local" },
    };

    [Theory]
    [MemberData(nameof(Requests))]
    public async Task Sync_generate_is_the_capture_plus_only_placement_resolved(string name, string? placement, string? why)
    {
        var observed = await ObserveAsync(placement, async: false);
        _output.WriteLine($"{name}:\n{observed}");
        Assert.Equal(After(Before, why), observed);
    }

    [Theory]
    [MemberData(nameof(Requests))]
    public async Task Async_generate_is_the_capture_plus_only_placement_resolved(string name, string? placement, string? why)
    {
        var observed = await ObserveAsync(placement, async: true);
        _output.WriteLine($"{name}:\n{observed}");
        Assert.Equal(After(BeforeAsync, why), observed);
    }

    /// <summary>
    /// The capture with P1's one addition: with a <c>placement</c> member, the job API's details and
    /// progress carry <c>placement.resolved</c> right after <c>nodeId</c>, and the broadcast record its
    /// <c>placementWhy</c> right after <c>nodeId</c>. Without one, the capture itself.
    /// </summary>
    private static string After(string before, string? why)
    {
        // The source file's line endings are the checkout's; the observation joins with LF.
        before = before.ReplaceLineEndings("\n");
        if (why is null) return before;
        var api = $"\"nodeId\":\"{NodeZero}\",";
        var resolved = $"{api}\"placement\":{{\"resolved\":{{\"nodeId\":\"{NodeZero}\",\"why\":\"{why}\"}}}},";
        var lines = before.Split('\n').Select(line =>
            line.StartsWith("details ") || line.StartsWith("progress ") ? ReplaceOnce(line, api, resolved)
            : line.StartsWith("broadcast ") ? ReplaceOnce(line, $"\"nodeId\":\"{NodeZero}\",", $"\"nodeId\":\"{NodeZero}\",\"placementWhy\":\"{why}\",")
            : line);
        return string.Join("\n", lines);
    }

    private static string ReplaceOnce(string text, string old, string replacement)
    {
        var at = text.IndexOf(old, StringComparison.Ordinal);
        Assert.True(at >= 0 && text.IndexOf(old, at + 1, StringComparison.Ordinal) < 0, $"'{old}' is not in the capture exactly once");
        return string.Concat(text.AsSpan(0, at), replacement, text.AsSpan(at + old.Length));
    }

    private async Task<string> ObserveAsync(string? placement, bool async)
    {
        var body = placement is null
            ? """{"prompt":"a red leaf"}"""
            : $$"""{"prompt":"a red leaf","placement":{{placement}}}""";
        using var content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
        using var response = await _client.PostAsync(async ? "/image-gen/generate?async=true" : "/image-gen/generate", content);
        var answer = await response.Content.ReadAsStringAsync();
        // An async generate answers once the job is queued; the provider runs it afterwards. Wait for the job
        // the answer names before reading what the provider received, or a loaded machine finds it not yet called.
        if (async)
        {
            var queued = JsonDocument.Parse(answer).RootElement.GetProperty("jobId").GetGuid();
            Assert.True(await _jobs.WaitForTerminalAsync(queued, TimeSpan.FromSeconds(10)), $"job {queued} did not end within 10 s");
        }
        JobRequest request;
        lock (_provider.Requests) request = Assert.Single(_provider.Requests);
        var job = _jobs.GetJob(request.JobId!.Value)!;

        var web = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var broadcast = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
        };
        var events = _jobs.GetJobEvents(job.Id);
        var lines = new[]
        {
            $"status {(int)response.StatusCode} {response.Content.Headers.ContentType}",
            $"x-job-id {response.Headers.Contains("X-Job-Id")}",
            $"body {answer}",
            $"provider {JsonSerializer.Serialize(request.Parameters.Keys)}",
            $"job.nodeId {job.NodeId}",
            $"job.input {job.InputJson}",
            $"details {JsonSerializer.Serialize(JobApiResponse.Details(job, _directory, _nodes), web)}",
            $"progress {JsonSerializer.Serialize(JobApiResponse.Progress(job, _directory, _nodes), web)}",
            $"broadcast {JsonSerializer.Serialize(job, broadcast)}",
            $"events {string.Join(",", events.Select(e => $"{e.Kind}:{e.DataJson}"))}",
        };
        return Normalize(string.Join("\n", lines));
    }

    private static string Normalize(string text)
    {
        text = Regex.Replace(text, "[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}", "<guid>");
        text = Regex.Replace(text, @"\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d+)?(Z|[+-]\d{2}:\d{2})", "<ts>");
        text = Regex.Replace(text, "\"durationMs\":\\d+", "\"durationMs\":<n>");
        text = Regex.Replace(text, "[0-9a-f]{32}", "<hex>");
        text = Regex.Replace(text, "\"requestId\":\"[^\"]+\"", "\"requestId\":\"<request>\"");
        return text;
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
            return Task.FromResult<JobResult?>(new JobResult { Success = true, ResultJson = """{"ok":true}""" });
        }
    }
}
