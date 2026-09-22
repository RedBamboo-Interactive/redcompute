using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using RedBamboo.AppHost.Auth;
using RedCompute.App.Api.Endpoints;
using RedCompute.App.Data;
using RedCompute.App.Services;
using RedCompute.App.Services.Jobs;
using RedCompute.Core.Capabilities;
using RedCompute.Core.Configuration;
using RedCompute.Core.Jobs;
using RedCompute.Core.Providers;
using RedCompute.PluginSdk;
using Xunit;

namespace RedCompute.App.Tests;

public sealed class DecisionValidationJobTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "redcompute-decision-validation-tests",
        Guid.NewGuid().ToString("N"));
    private readonly string _database;
    private readonly JobTrackingService _jobs;

    public DecisionValidationJobTests()
    {
        _database = Path.Combine(_directory, "jobs.db");
        Directory.CreateDirectory(_directory);
        using var db = Db();
        db.Database.EnsureCreated();
        db.MigrateSchema();
        _jobs = new JobTrackingService(Db);
    }

    [Fact]
    public async Task Successful_validation_is_one_provenanced_job_with_persisted_output()
    {
        ProviderValidationContext? received = null;
        var provider = new ValidationProvider((context, _) =>
        {
            received = context;
            return Task.FromResult(new ProviderValidationResult(
                true, """{"ok":true,"readiness":"inference-warmed"}"""));
        });
        var context = Context();

        var response = await GenericCapabilityEndpoints.RunProviderValidationAsync(
            context, "decision", Capability(provider), _jobs, (_, _) => { });
        var json = await ExecuteAsync(response, context);

        var jobId = json["jobId"]!.GetValue<Guid>();
        Assert.NotNull(received);
        Assert.Equal(jobId, received!.JobId);
        var job = _jobs.GetJob(jobId)!;
        Assert.Equal(JobStatus.Completed, job.Status);
        Assert.Equal("""{"ok":true,"readiness":"inference-warmed"}""", job.ResultJson);
        Assert.Equal("application/json", job.OutputContentType);
        Assert.Equal("/decision/validate", job.CreationProvenance!.Origin.Entrypoint.Route);
        Assert.Equal(JobProvenanceAssurance.Verified, job.CreationProvenance.Assurance);
        Assert.Equal(job.ResultJson, json["validation"]!.ToJsonString());
        Assert.Equal(job.QueuedAt, received.QueuedAt);
        Assert.Equal(job.StartedAt, received.InvocationStartedAt);
        Assert.Single(AllJobs());
        Assert.Equal(
            [JobEventKind.Created, JobEventKind.Started, JobEventKind.Completed],
            _jobs.GetJobEvents(jobId).Select(item => item.Kind).ToArray());
    }

    [Fact]
    public async Task Provider_failure_is_typed_and_persisted_as_failed_job()
    {
        var provider = new ValidationProvider((_, _) => Task.FromResult(
            new ProviderValidationResult(
                false,
                """{"ok":false,"error":"resource_busy","available":false}""",
                "resource_busy",
                "GPU capacity is unavailable",
                409)));
        var context = Context();

        var response = await GenericCapabilityEndpoints.RunProviderValidationAsync(
            context, "decision", Capability(provider), _jobs, (_, _) => { });
        var json = await ExecuteAsync(response, context);

        Assert.Equal(409, context.Response.StatusCode);
        Assert.Equal("resource_busy", json["error"]!.GetValue<string>());
        var jobId = json["jobId"]!.GetValue<Guid>();
        var job = _jobs.GetJob(jobId)!;
        Assert.Equal(JobStatus.Failed, job.Status);
        Assert.Equal("resource_busy", job.ErrorDetails);
        Assert.Equal("""{"ok":false,"error":"resource_busy","available":false}""",
            job.ResultJson);
        Assert.Single(AllJobs());
    }

    [Fact]
    public async Task Cancellation_wins_a_terminal_race_with_late_provider_success()
    {
        var entered = new TaskCompletionSource<ProviderValidationContext>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new ValidationProvider(async (validation, _) =>
        {
            entered.TrySetResult(validation);
            await release.Task;
            return new ProviderValidationResult(true, """{"ok":true}""");
        });
        var context = Context();

        var pending = GenericCapabilityEndpoints.RunProviderValidationAsync(
            context, "decision", Capability(provider), _jobs, (_, _) => { });
        var validation = await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        _jobs.MarkCancelled(validation.JobId);
        release.TrySetResult();

        var response = await pending;
        var json = await ExecuteAsync(response, context);

        Assert.Equal(409, context.Response.StatusCode);
        Assert.Equal("job_cancelled", json["error"]!.GetValue<string>());
        var job = _jobs.GetJob(validation.JobId)!;
        Assert.Equal(JobStatus.Cancelled, job.Status);
        Assert.Null(job.ResultJson);
        Assert.DoesNotContain(_jobs.GetJobEvents(job.Id),
            item => item.Kind is JobEventKind.Completed or JobEventKind.Failed);
    }

    [Fact]
    public async Task Malformed_pre_admission_body_creates_no_job()
    {
        var calls = 0;
        var provider = new ValidationProvider((_, _) =>
        {
            calls++;
            return Task.FromResult(new ProviderValidationResult(true, """{"ok":true}"""));
        });
        var context = Context();
        var bytes = Encoding.UTF8.GetBytes("{broken");
        context.Request.Body = new MemoryStream(bytes);
        context.Request.ContentLength = bytes.Length;

        var response = await GenericCapabilityEndpoints.RunProviderValidationAsync(
            context, "decision", Capability(provider), _jobs, (_, _) => { });
        _ = await ExecuteAsync(response, context);

        Assert.Equal(400, context.Response.StatusCode);
        Assert.Equal(0, calls);
        Assert.Empty(AllJobs());
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    private RedComputeDbContext Db() => new(_database);

    private IReadOnlyList<JobRecord> AllJobs()
    {
        using var db = Db();
        return db.Jobs.OrderBy(item => item.QueuedAt).ToList();
    }

    private static CapabilityEntry Capability(IBackendProvider provider) => new()
    {
        Definition = new CapabilityDefinition
        {
            Slug = "decision",
            DisplayName = "Decision",
            Description = "test",
        },
        Config = new CapabilityConfig(),
        Providers = new Dictionary<string, IBackendProvider>
        {
            [provider.Name] = provider,
        },
        DefaultProviderName = provider.Name,
    };

    private static DefaultHttpContext Context()
    {
        const string subject = "user-1";
        var identity = new ExecutionIdentity(
            ExecutionIdentity.CurrentSchemaVersion,
            Guid.NewGuid().ToString(),
            new ExecutionAppIdentity("nova", "Nova"),
            new ExecutionActorIdentity("agent", "nova", "Nova", "agent-nova"),
            new ExecutionBeneficiaryIdentity("user", subject),
            []);
        var identityJson = JsonSerializer.Serialize(
            identity, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim("sub", subject),
                new Claim("token_use", "execution"),
                new Claim("execution_identity", identityJson),
            ], "test")),
            RequestServices = new ServiceCollection()
                .AddLogging()
                .AddOptions()
                .BuildServiceProvider(),
        };
        context.Request.Method = "POST";
        context.Request.Path = "/decision/validate";
        context.Request.Body = Stream.Null;
        context.Response.Body = new MemoryStream();
        context.TraceIdentifier = "validation-request";
        return context;
    }

    private static async Task<JsonNode> ExecuteAsync(
        IResult result,
        DefaultHttpContext context)
    {
        await result.ExecuteAsync(context);
        context.Response.Body.Position = 0;
        return JsonNode.Parse(await new StreamReader(
            context.Response.Body).ReadToEndAsync())!;
    }

    private sealed class ValidationProvider(
        Func<ProviderValidationContext, CancellationToken,
            Task<ProviderValidationResult>> validate)
        : IBackendProvider, IProviderSelfValidator
    {
        public string Name => "validation-test";
        public string CapabilitySlug => "decision";
        public TimeSpan HealthCheckInterval => TimeSpan.FromSeconds(30);
        public Task<bool> StartAsync(CancellationToken ct = default)
            => Task.FromResult(true);
        public Task StopAsync(CancellationToken ct = default)
            => Task.CompletedTask;
        public Task<BackendStatus> GetStatusAsync(CancellationToken ct = default)
            => Task.FromResult(BackendStatus.Running);
        public string? GetProxyTargetUrl() => null;
        public Task<JobResult?> ExecuteAsync(
            JobRequest request,
            CancellationToken ct = default)
            => Task.FromResult<JobResult?>(null);
        public Task<ProviderValidationResult> ValidateProviderAsync(
            ProviderValidationContext context,
            CancellationToken ct = default)
            => validate(context, ct);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
