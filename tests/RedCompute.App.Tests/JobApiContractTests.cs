using System.Diagnostics;
using System.Net;
using System.Security.Claims;
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

public sealed class JobApiContractTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "redcompute-job-api-tests", Guid.NewGuid().ToString("N"));
    private JobTrackingService _jobs = null!;
    private WebApplication _app = null!;
    private HttpClient _client = null!;
    private CapabilityRegistry _registry = null!;
    private EndpointRegistry _endpoints = null!;

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

        var provider = new FakeProvider();
        _registry = new CapabilityRegistry();
        _registry.Register("image-gen", new CapabilityDefinition
        {
            Slug = "image-gen", DisplayName = "Image", Description = "Image test",
        }, new CapabilityConfig(), new Dictionary<string, IBackendProvider>
        {
            [provider.Name] = provider,
        }, provider.Name);

        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        _app = builder.Build();
        _app.Use(async (context, next) =>
        {
            if (context.Request.Headers.ContainsKey("X-Test-Trusted"))
                context.User = new ClaimsPrincipal(new ClaimsIdentity([
                    new Claim("client_id", "redleaf"),
                    new Claim("compute_provenance", "true"),
                ], "test"));
            await next();
        });
        _endpoints = new EndpointRegistry(_app);
        GenericCapabilityEndpoints.Map(_app, _endpoints, _registry, _jobs, (_, _) => { });
        GlobalEndpoints.Map(_endpoints, _registry, _jobs, null!);
        await _app.StartAsync();
        _client = new HttpClient { BaseAddress = new Uri(_app.Urls.Single()) };
    }

    [Fact]
    public async Task Discovery_and_openapi_document_wait_and_agent_response_fields()
    {
        var descriptor = new RedComputeServiceDescriptor(
            new RedComputeConfig { ApiPort = 18800 }, _registry, null, _endpoints);
        var capabilities = await descriptor.GetCapabilitiesAsync();
        var image = Assert.Single(capabilities, capability => capability.Slug == "image-gen");
        var progress = Assert.Single(image.Endpoints!, endpoint => endpoint.Path.EndsWith("/progress"));
        Assert.Contains(progress.Parameters!, parameter => parameter.Name == "waitSeconds");
        var response = JsonSerializer.Serialize(progress.Response);
        Assert.Contains("terminal", response);
        Assert.Contains("outputs", response);
        Assert.Contains("timed_out", response);

        var openApi = JsonSerializer.Serialize(await OpenApiGenerator.GenerateAsync(descriptor));
        Assert.Contains("waitSeconds", openApi);
        Assert.Contains("lifecycleEvents", openApi);
        Assert.Contains("executing", openApi);
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _app.DisposeAsync();
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public async Task Progress_and_detail_add_agent_fields_without_removing_compatibility_fields()
    {
        var output = Path.Combine(_directory, "result.png");
        await File.WriteAllBytesAsync(output, [1, 2, 3, 4]);
        var completed = CreateJob();
        _jobs.StartInvocation(completed.Id, Provenance());
        _jobs.MarkCompleted(completed.Id, output, 4, "image/png", "{\"answer\":42}");

        var failed = CreateJob();
        _jobs.StartInvocation(failed.Id, Provenance());
        _jobs.MarkFailed(failed.Id, "real failure");

        var running = CreateJob();
        _jobs.StartInvocation(running.Id, Provenance());

        using var progressResponse = await _client.GetAsync($"/image-gen/jobs/{completed.Id}/progress");
        using var detailResponse = await _client.GetAsync($"/jobs/{completed.Id}");
        using var failedResponse = await _client.GetAsync($"/image-gen/jobs/{failed.Id}/progress");
        using var runningResponse = await _client.GetAsync($"/jobs/{running.Id}");

        Assert.Equal(HttpStatusCode.OK, progressResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, detailResponse.StatusCode);
        using var progress = JsonDocument.Parse(await progressResponse.Content.ReadAsStringAsync());
        using var detail = JsonDocument.Parse(await detailResponse.Content.ReadAsStringAsync());
        using var failure = JsonDocument.Parse(await failedResponse.Content.ReadAsStringAsync());
        using var active = JsonDocument.Parse(await runningResponse.Content.ReadAsStringAsync());

        Assert.Equal("Completed", progress.RootElement.GetProperty("status").GetString());
        Assert.True(progress.RootElement.GetProperty("terminal").GetBoolean());
        Assert.Equal("completed", progress.RootElement.GetProperty("stage").GetString());
        Assert.Equal(42, progress.RootElement.GetProperty("result").GetProperty("answer").GetInt32());
        Assert.Equal("{\"answer\":42}", progress.RootElement.GetProperty("resultJson").GetString());
        var outputItem = Assert.Single(progress.RootElement.GetProperty("outputs").EnumerateArray());
        Assert.Equal($"/image-gen/jobs/{completed.Id}/output", outputItem.GetProperty("url").GetString());
        Assert.Equal("image/png", outputItem.GetProperty("contentType").GetString());
        Assert.Equal(4, outputItem.GetProperty("sizeBytes").GetInt64());
        Assert.False(outputItem.ToString().Contains(_directory, StringComparison.OrdinalIgnoreCase));
        Assert.Equal($"/jobs/{completed.Id}", progress.RootElement.GetProperty("links").GetProperty("self").GetString());

        Assert.Equal("image-gen", detail.RootElement.GetProperty("capability").GetString());
        Assert.True(detail.RootElement.TryGetProperty("outputLocation", out _));
        Assert.True(detail.RootElement.GetProperty("terminal").GetBoolean());
        Assert.Equal("real failure", failure.RootElement.GetProperty("errorMessage").GetString());
        Assert.True(failure.RootElement.GetProperty("terminal").GetBoolean());
        Assert.Empty(failure.RootElement.GetProperty("outputs").EnumerateArray());
        Assert.False(active.RootElement.GetProperty("terminal").GetBoolean());
        Assert.Equal("waiting", active.RootElement.GetProperty("stage").GetString());
        Assert.Empty(active.RootElement.GetProperty("outputs").EnumerateArray());

        _jobs.UpdateProgress(running.Id, 0);
        using var executingResponse = await _client.GetAsync($"/image-gen/jobs/{running.Id}/progress");
        using var executing = JsonDocument.Parse(await executingResponse.Content.ReadAsStringAsync());
        Assert.Equal("executing", executing.RootElement.GetProperty("stage").GetString());
    }

    [Fact]
    public void Multiple_artifacts_use_service_relative_selector_urls_and_never_paths()
    {
        var job = CreateJob();
        var primary = Path.Combine(_directory, "primary.png");
        File.WriteAllBytes(primary, [1]);
        JobArtifactStore.Save(job.Id, _directory, primary, "image/png", null, "primary.png",
        [
            new JobOutputPart
            {
                Name = "variation",
                Suffix = "_variation",
                ContentType = "image/png",
                Data = new MemoryStream([2, 3]),
                FileName = "variation.png",
            },
        ]);
        _jobs.MarkCompleted(job.Id, primary, 1, "image/png", "{}");

        var response = JobApiResponse.Progress(_jobs.GetJob(job.Id)!, _directory);
        var outputs = Assert.IsType<object[]>(response["outputs"]);
        var json = JsonSerializer.Serialize(outputs);

        Assert.Equal(2, outputs.Length);
        Assert.Contains($"/image-gen/jobs/{job.Id}/output", json);
        Assert.Contains("?artifact=variation", json);
        Assert.DoesNotContain(_directory, json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Long_poll_returns_early_on_completion_times_out_and_is_immediate_when_terminal()
    {
        var early = CreateJob();
        _jobs.StartInvocation(early.Id, Provenance());
        var watch = Stopwatch.StartNew();
        var pending = _client.GetAsync($"/image-gen/jobs/{early.Id}/progress?waitSeconds=5");
        Assert.True(SpinWait.SpinUntil(() => _jobs.ActiveTerminalWaiterCount == 1,
            TimeSpan.FromSeconds(2)));
        _jobs.MarkCompleted(early.Id, resultJson: "{}");
        using var earlyResponse = await pending;
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(3));
        using var earlyBody = JsonDocument.Parse(await earlyResponse.Content.ReadAsStringAsync());
        Assert.True(earlyBody.RootElement.GetProperty("terminal").GetBoolean());

        var running = CreateJob();
        _jobs.StartInvocation(running.Id, Provenance());
        watch.Restart();
        using var timeoutResponse = await _client.GetAsync($"/jobs/{running.Id}?waitSeconds=1");
        Assert.InRange(watch.Elapsed, TimeSpan.FromMilliseconds(700), TimeSpan.FromSeconds(3));
        using var timeoutBody = JsonDocument.Parse(await timeoutResponse.Content.ReadAsStringAsync());
        Assert.False(timeoutBody.RootElement.GetProperty("terminal").GetBoolean());

        watch.Restart();
        using var immediate = await _client.GetAsync($"/jobs/{early.Id}?waitSeconds=60");
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1));
        Assert.Equal(0, _jobs.ActiveTerminalWaiterCount);
    }

    [Fact]
    public async Task Long_poll_clamps_rejects_bad_input_and_releases_cancelled_waiters()
    {
        var context = new DefaultHttpContext();
        context.Request.QueryString = new QueryString("?waitSeconds=999");
        Assert.True(JobLongPoll.TryParse(context, out var maximum, out _));
        Assert.Equal(60, maximum);
        context.Request.QueryString = new QueryString("?waitSeconds=-4");
        Assert.True(JobLongPoll.TryParse(context, out var minimum, out _));
        Assert.Equal(0, minimum);

        var job = CreateJob();
        _jobs.StartInvocation(job.Id, Provenance());
        using var invalid = await _client.GetAsync($"/image-gen/jobs/{job.Id}/progress?waitSeconds=nope");
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);

        using var cancellation = new CancellationTokenSource();
        var waiting = _jobs.WaitForTerminalAsync(job.Id, TimeSpan.FromSeconds(30), cancellation.Token);
        Assert.True(SpinWait.SpinUntil(() => _jobs.ActiveTerminalWaiterCount == 1,
            TimeSpan.FromSeconds(2)));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        Assert.Equal(0, _jobs.ActiveTerminalWaiterCount);

        Assert.False(await _jobs.WaitForTerminalAsync(job.Id, TimeSpan.FromMilliseconds(30)));
        Assert.Equal(0, _jobs.ActiveTerminalWaiterCount);
    }

    [Fact]
    public async Task Progress_route_applies_the_job_access_policy()
    {
        var confidential = _jobs.CreateJob(new JobSubmission(
            "image-gen", "fake", "{}", Provenance(), Confidential: true));

        using var denied = await _client.GetAsync($"/image-gen/jobs/{confidential.Id}/progress");
        using var trustedRequest = new HttpRequestMessage(HttpMethod.Get,
            $"/image-gen/jobs/{confidential.Id}/progress");
        trustedRequest.Headers.Add("X-Test-Trusted", "true");
        using var allowed = await _client.SendAsync(trustedRequest);

        Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
    }

    [Fact]
    public async Task Invocation_owned_progress_callbacks_do_not_cross_wire_concurrent_requests()
    {
        var provider = new ConcurrentProgressProvider();
        var first = CreateJob();
        var second = CreateJob();
        _jobs.StartInvocation(first.Id, Provenance());
        _jobs.StartInvocation(second.Id, Provenance());
        var firstRequest = GenericCapabilityEndpoints.CreateProviderRequest(first, "image-gen",
            new Dictionary<string, object?> { ["progress"] = 0.25 }, null, Provenance(), provider, _jobs);
        var secondRequest = GenericCapabilityEndpoints.CreateProviderRequest(second, "image-gen",
            new Dictionary<string, object?> { ["progress"] = 0.75 }, null, Provenance(), provider, _jobs);

        await Task.WhenAll(provider.ExecuteAsync(firstRequest), provider.ExecuteAsync(secondRequest));

        Assert.Equal(0.25, _jobs.GetJob(first.Id)!.Progress);
        Assert.Equal(0.75, _jobs.GetJob(second.Id)!.Progress);
        Assert.NotNull(_jobs.GetJob(first.Id)!.ProviderStartedAt);
        Assert.NotNull(_jobs.GetJob(second.Id)!.ProviderStartedAt);
    }

    private JobRecord CreateJob() => _jobs.CreateJob(new JobSubmission(
        "image-gen", "fake", "{}", Provenance()));

    private static JobProvenance Provenance() => new(
        JobProvenance.CurrentSchemaVersion,
        new JobOrigin("redleaf", new JobAppReference("plugin", "test", null, "Test"),
            new JobEntrypoint("http", "/test", "POST")),
        new JobActor("agent", "Test Agent", Id: "agent-1"),
        new JobBeneficiary("user", "user-1", "User One"),
        [], new JobTrace(), JobProvenanceAssurance.Verified, DateTimeOffset.UtcNow);

    private class FakeProvider : IPluginProvider
    {
        public string Name => "fake";
        public string CapabilitySlug => "image-gen";
        public string DisplayName => "Fake";
        public string ProviderType => "Fake";
        public Dictionary<string, ParameterSchema> InputParameters => new();
        public ReturnSchema OutputSchema => new() { ContentType = "image/png" };
        public bool IsProxy => false;
        public bool SupportsProgress => true;
        public bool SupportsRerun => false;
        public TimeSpan HealthCheckInterval => TimeSpan.FromSeconds(30);
        public Task<bool> StartAsync(CancellationToken ct = default) => Task.FromResult(true);
        public Task StopAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<BackendStatus> GetStatusAsync(CancellationToken ct = default)
            => Task.FromResult(BackendStatus.Running);
        public string? GetProxyTargetUrl() => null;
        public virtual Task<JobResult?> ExecuteAsync(JobRequest request, CancellationToken ct = default)
            => Task.FromResult<JobResult?>(new JobResult { Success = true, ResultJson = "{}" });
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ConcurrentProgressProvider : FakeProvider
    {
        private readonly TaskCompletionSource _bothStarted = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private int _started;

        public override async Task<JobResult?> ExecuteAsync(
            JobRequest request, CancellationToken ct = default)
        {
            if (Interlocked.Increment(ref _started) == 2) _bothStarted.TrySetResult();
            await _bothStarted.Task.WaitAsync(ct);
            request.ProgressCallback!(Convert.ToDouble(request.Parameters["progress"]));
            return new JobResult { Success = true, ResultJson = "{}" };
        }
    }
}
