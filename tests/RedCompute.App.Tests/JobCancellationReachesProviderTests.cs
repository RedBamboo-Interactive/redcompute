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
using RedCompute.Core.Capabilities;
using RedCompute.Core.Configuration;
using RedCompute.Core.Discovery;
using RedCompute.Core.Jobs;
using RedCompute.Core.Providers;
using Xunit;

namespace RedCompute.App.Tests;

/// <summary>
/// Cancelling a job has to stop the provider invocation doing its work, not only mark the record:
/// otherwise a ComfyUI prompt keeps the GPU after its job reads Cancelled.
/// </summary>
public sealed class JobCancellationReachesProviderTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "redcompute-job-cancel-tests", Guid.NewGuid().ToString("N"));
    private readonly BlockingProvider _provider = new();
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
        registry.Register("image-gen", new CapabilityDefinition
        {
            Slug = "image-gen", DisplayName = "Image", Description = "Image test",
        }, new CapabilityConfig(), new Dictionary<string, IBackendProvider>
        {
            [_provider.Name] = _provider,
        }, _provider.Name);

        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        _app = builder.Build();
        var endpoints = new EndpointRegistry(_app);
        GenericCapabilityEndpoints.Map(_app, endpoints, registry, _jobs, (_, _) => { });
        GlobalEndpoints.Map(endpoints, registry, _jobs, null!);
        await _app.StartAsync();
        _client = new HttpClient { BaseAddress = new Uri(_app.Urls.Single()) };
        _client.DefaultRequestHeaders.Add("X-System-Reason", "job cancellation contract test");
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _app.DisposeAsync();
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public async Task Deleting_an_async_job_cancels_the_running_provider_invocation()
    {
        using var accepted = await _client.PostAsJsonAsync("/image-gen/generate?async=true", new { prompt = "x" });
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        var jobId = (await accepted.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("jobId").GetGuid();
        await _provider.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));

        using var deleted = await _client.DeleteAsync($"/jobs/{jobId}");
        Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);

        await _provider.ObservedCancellation.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await WaitUntilAsync(() => !_jobs.HasActiveExecution(jobId));
        Assert.Equal(JobStatus.Cancelled, _jobs.GetJob(jobId)!.Status);
    }

    [Fact]
    public async Task Deleting_a_synchronous_job_cancels_the_provider_and_answers_with_the_cancellation()
    {
        var response = _client.PostAsJsonAsync("/image-gen/generate", new { prompt = "x" });
        var jobId = await _provider.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));

        using var deleted = await _client.DeleteAsync($"/jobs/{jobId}");
        Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);

        using var answer = await response.WaitAsync(TimeSpan.FromSeconds(10));
        await _provider.ObservedCancellation.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(HttpStatusCode.Conflict, answer.StatusCode);
        Assert.Equal(jobId.ToString(), answer.Headers.GetValues("X-Job-Id").Single());
        Assert.Equal(JobStatus.Cancelled, _jobs.GetJob(jobId)!.Status);
    }

    [Fact]
    public void Cancelling_one_job_leaves_other_executions_running()
    {
        var mine = _jobs.CreateJob(Submission());
        var other = _jobs.CreateJob(Submission());
        using var mineScope = _jobs.BeginExecution(mine.Id);
        using var otherScope = _jobs.BeginExecution(other.Id);

        _jobs.MarkCancelled(mine.Id);

        Assert.True(mineScope.Token.IsCancellationRequested);
        Assert.False(otherScope.Token.IsCancellationRequested);
    }

    [Fact]
    public void A_cancel_that_lands_before_execution_starts_still_stops_it()
    {
        var job = _jobs.CreateJob(Submission());
        _jobs.MarkCancelled(job.Id);

        using var scope = _jobs.BeginExecution(job.Id);

        Assert.True(scope.Token.IsCancellationRequested);
    }

    [Fact]
    public void An_ended_execution_is_forgotten_and_a_late_cancel_is_harmless()
    {
        var job = _jobs.CreateJob(Submission());
        var scope = _jobs.BeginExecution(job.Id);
        scope.Dispose();

        Assert.False(_jobs.HasActiveExecution(job.Id));
        _jobs.MarkCancelled(job.Id);
        Assert.Equal(JobStatus.Cancelled, _jobs.GetJob(job.Id)!.Status);
    }

    private static JobSubmission Submission() => new(
        "image-gen", "blocking", "{}", new JobProvenance(
            JobProvenance.CurrentSchemaVersion,
            new JobOrigin("redleaf", new JobAppReference("plugin", "test", null, "Test"),
                new JobEntrypoint("http", "/test", "POST")),
            new JobActor("agent", "Test Agent", Id: "agent-1"),
            new JobBeneficiary("user", "user-1", "User One"),
            [], new JobTrace(), JobProvenanceAssurance.Verified, DateTimeOffset.UtcNow));

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) Assert.Fail("Condition was not reached in time");
            await Task.Delay(20);
        }
    }

    private sealed class BlockingProvider : IPluginProvider
    {
        public TaskCompletionSource<Guid> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ObservedCancellation { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string Name => "blocking";
        public string CapabilitySlug => "image-gen";
        public string DisplayName => "Blocking";
        public string ProviderType => "Blocking";
        public Dictionary<string, ParameterSchema> InputParameters => new();
        public ReturnSchema OutputSchema => new() { ContentType = "image/png" };
        public bool IsProxy => false;
        public bool SupportsProgress => false;
        public bool SupportsRerun => false;
        public TimeSpan HealthCheckInterval => TimeSpan.FromSeconds(30);
        public Task<bool> StartAsync(CancellationToken ct = default) => Task.FromResult(true);
        public Task StopAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<BackendStatus> GetStatusAsync(CancellationToken ct = default)
            => Task.FromResult(BackendStatus.Running);
        public string? GetProxyTargetUrl() => null;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public async Task<JobResult?> ExecuteAsync(JobRequest request, CancellationToken ct = default)
        {
            Started.TrySetResult(request.JobId!.Value);
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }
            catch (OperationCanceledException)
            {
                ObservedCancellation.TrySetResult();
                throw;
            }
            return new JobResult { Success = true, ResultJson = "{}" };
        }
    }
}
