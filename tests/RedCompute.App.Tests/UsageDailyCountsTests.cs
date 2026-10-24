using System.Data.Common;
using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using RedBamboo.AppHost.Discovery;
using RedCompute.App.Api.Endpoints;
using RedCompute.App.Data;
using RedCompute.App.Services.Jobs;
using RedCompute.Core.Jobs;
using Xunit;

namespace RedCompute.App.Tests;

public sealed class UsageDailyCountsTests : IDisposable
{
    private const string User = "user-1";
    private static readonly DateTimeOffset Now = new(2026, 10, 24, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Yesterday = new(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);

    private static readonly string[] ForbiddenColumns =
    [
        "Name", "InputJson", "ResultJson", "OutputLocation", "ErrorMessage", "ErrorDetails",
        "Rationale", "UserName", "CallerInfo", "IdempotencyKey",
    ];

    private static readonly string[] AllowedColumns =
    [
        "CapabilitySlug", "ProviderName", "Status", "QueuedAt", "ProviderStartedAt", "CompletedAt",
        "OutputContentType", "UserId", "Confidential", "ParentJobId", "CreationProvenanceJson",
    ];

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "redcompute-usage-tests", Guid.NewGuid().ToString("N"));
    private readonly string _database;
    private readonly List<string> _commands = [];

    public UsageDailyCountsTests()
    {
        _database = Path.Combine(_directory, "jobs.db");
        Directory.CreateDirectory(_directory);
        using var db = Db();
        db.Database.EnsureCreated();
        db.MigrateSchema();
    }

    [Fact]
    public void Groups_by_normalised_dimensions_and_sums_execution_seconds()
    {
        Seed(
            // Two ComfyUI app-initiated images: 10.5s + 5.9s floors to 16 only after summing.
            Job("image-gen", "ComfyUI", JobStatus.Completed, Yesterday, "image/png", Actor("app", "provider-comfyui"),
                execution: TimeSpan.FromSeconds(10.5)),
            Job("image-gen", "ComfyUI", JobStatus.Completed, Yesterday.AddMinutes(5), "image/png; charset=binary",
                Actor("app", "spriteforge"), execution: TimeSpan.FromSeconds(5.9)),
            Job("image-gen", "ComfyUI", JobStatus.Completed, Yesterday, "video/mp4", Actor("agent", "agent-nova"),
                execution: TimeSpan.FromSeconds(30)),
            Job("image-gen", "ComfyUI", JobStatus.Completed, Yesterday, "image/webp", Actor("plugin", "pixoo")),
            // Completed without a provider start, and a clock-skewed negative duration, add nothing.
            Job("image-gen", "ComfyUI", JobStatus.Completed, Yesterday, "image/png", Actor("user", User)),
            Job("image-gen", "ComfyUI", JobStatus.Completed, Yesterday, "image/png", Actor("user", "someone-else", entityId: User),
                execution: TimeSpan.FromSeconds(-20)),
            Job("image-gen", "ComfyUI", JobStatus.Completed, Yesterday, "image/png", Actor("user", "someone-else"),
                execution: TimeSpan.FromSeconds(4)),
            Job("ai-session", "Claude Code", JobStatus.Failed, Yesterday, null, Actor("agent", "agent-nova"),
                execution: TimeSpan.FromSeconds(60)),
            Job("ai-session", "Open Code", JobStatus.TimedOut, Yesterday, null, Actor("agent", "agent-nova")),
            Job("ai-session", "OpenCode", JobStatus.Cancelled, Yesterday, null, Actor("agent", "agent-nova")),
            Job("ai-session", "Codex", JobStatus.Skipped, Yesterday, null, Actor("agent", "agent-nova")),
            Job("ai-session", "Codex", JobStatus.Running, Yesterday, null, Actor("agent", "agent-nova")),
            Job("music", "Suno", JobStatus.Completed, Yesterday, "audio/mpeg", null, execution: TimeSpan.FromSeconds(2)),
            // UTC bucketing: 01:00+05:00 on the 23rd is the 22nd in UTC; 23:30-05:00 on the 23rd is today.
            Job("image-gen", "ComfyUI", JobStatus.Completed, new DateTimeOffset(2026, 10, 23, 1, 0, 0, TimeSpan.FromHours(5)),
                "image/png", Actor("app", "outfits")),
            Job("image-gen", "ComfyUI", JobStatus.Completed, new DateTimeOffset(2026, 10, 23, 23, 30, 0, TimeSpan.FromHours(-5)),
                "image/png", Actor("app", "outfits")));

        var counts = Service().GetDailyCounts("2026-10-22", "2026-10-23", User, null).Counts!;

        Assert.Equal(["2026-10-22", "2026-10-23"], counts.Days.Select(d => d.Day));
        Assert.Equal([new UsageGroup("image-gen", "comfyui", "succeeded", "image", "you", 1, 0)], counts.Days[0].Groups);
        Assert.Equal(
        [
            new UsageGroup("ai-session", "claude-code", "failed", "none", "agent", 1, 0),
            new UsageGroup("ai-session", "codex", "other", "none", "agent", 2, 0),
            new UsageGroup("ai-session", "opencode", "cancelled", "none", "agent", 1, 0),
            new UsageGroup("ai-session", "opencode", "failed", "none", "agent", 1, 0),
            new UsageGroup("image-gen", "comfyui", "succeeded", "animation", "other", 1, 0),
            new UsageGroup("image-gen", "comfyui", "succeeded", "image", "other", 1, 4),
            new UsageGroup("image-gen", "comfyui", "succeeded", "image", "you", 4, 16),
            new UsageGroup("image-gen", "comfyui", "succeeded", "video", "agent", 1, 30),
            new UsageGroup("music", "other", "succeeded", "audio", "other", 1, 2),
        ], counts.Days[1].Groups);
    }

    [Fact]
    public void Excludes_confidential_child_other_beneficiary_and_future_jobs()
    {
        var parent = Job("image-gen", "ComfyUI", JobStatus.Completed, Yesterday, "image/png", Actor("app", "outfits"));
        var child = Job("image-gen", "ComfyUI", JobStatus.Completed, Yesterday, "image/png", Actor("app", "outfits"));
        child.ParentJobId = parent.Id;
        var confidential = Job("image-gen", "ComfyUI", JobStatus.Completed, Yesterday, "image/png", Actor("agent", "agent-nova"));
        confidential.Confidential = true;
        var otherUser = Job("image-gen", "ComfyUI", JobStatus.Completed, Yesterday, "image/png", Actor("app", "outfits"));
        otherUser.UserId = "user-2";
        var futureStamped = Job("image-gen", "ComfyUI", JobStatus.Completed, Now.AddDays(30), "image/png", Actor("app", "outfits"));
        Seed(parent, child, confidential, otherUser, futureStamped);

        var counts = Service().GetDailyCounts("2026-10-23", "2026-10-23", User, "image-gen").Counts!;

        var group = Assert.Single(Assert.Single(counts.Days).Groups);
        Assert.Equal(1, group.Jobs);
        Assert.Equal("you", group.Initiator);
        // The future-stamped job's day can never be requested: it is not before today.
        Assert.Equal("window_includes_today",
            Service().GetDailyCounts("2026-11-23", "2026-11-23", User, "image-gen").Error);
    }

    [Fact]
    public void Aggregation_drops_jobs_queued_after_now()
    {
        var rows = new[]
        {
            Row(Yesterday),
            Row(Now.AddMinutes(1)),
        };

        // A host clock running behind real time sees the second job in the future.
        var counts = UsageCountsService.Aggregate(rows, User, new DateOnly(2026, 10, 23), new DateOnly(2026, 10, 24), Now);

        Assert.Equal(1, counts.Days.Sum(d => d.Groups.Sum(g => g.Jobs)));
    }

    [Fact]
    public void Capability_filter_narrows_the_result()
    {
        Seed(
            Job("image-gen", "ComfyUI", JobStatus.Completed, Yesterday, "image/png", Actor("app", "outfits")),
            Job("ai-session", "Codex", JobStatus.Completed, Yesterday, null, Actor("agent", "agent-nova")));

        var counts = Service().GetDailyCounts("2026-10-23", "2026-10-23", User, "ai-session").Counts!;

        Assert.Equal("ai-session", Assert.Single(Assert.Single(counts.Days).Groups).Capability);
    }

    [Theory]
    [InlineData("2026-10-20", "2026-10-24", User, "window_includes_today")]
    [InlineData("2026-10-24", "2026-10-24", User, "window_includes_today")]
    [InlineData("2026-08-21", "2026-10-23", User, "window_too_large")]
    [InlineData("2026-10-22", "2026-10-21", User, "invalid_window")]
    [InlineData("2026-10-1", "2026-10-21", User, "invalid_day")]
    [InlineData(null, "2026-10-21", User, "invalid_day")]
    [InlineData("2026-10-20", "2026-10-21T00:00:00Z", User, "invalid_day")]
    [InlineData("2026-10-20", "2026-10-21", null, "beneficiary_required")]
    [InlineData("2026-10-20", "2026-10-21", " ", "beneficiary_required")]
    public void Rejects_invalid_windows(string? since, string? until, string? beneficiary, string error)
    {
        var result = Service().GetDailyCounts(since, until, beneficiary, null);

        Assert.Null(result.Counts);
        Assert.Equal(error, result.Error);
    }

    [Fact]
    public void Accepts_a_window_exactly_62_days_apart_ending_yesterday()
    {
        var counts = Service().GetDailyCounts("2026-08-22", "2026-10-23", User, null).Counts!;

        Assert.Equal(63, counts.Days.Count);
        Assert.All(counts.Days, d => Assert.Empty(d.Groups));
    }

    [Fact]
    public void Generated_sql_reads_only_safe_columns_and_actor_paths()
    {
        Seed(Job("image-gen", "ComfyUI", JobStatus.Completed, Yesterday, "image/png", Actor("app", "outfits"),
            execution: TimeSpan.FromSeconds(3)));

        var counts = Service().GetDailyCounts("2026-10-23", "2026-10-23", User, "image-gen").Counts!;
        Assert.Equal(1, Assert.Single(Assert.Single(counts.Days).Groups).Jobs);

        var sql = Assert.Single(_commands, c => c.Contains("FROM \"Jobs\"", StringComparison.Ordinal));
        foreach (var column in ForbiddenColumns)
            Assert.DoesNotContain($"\"{column}\"", sql, StringComparison.Ordinal);

        var referenced = Regex.Matches(sql, "\"\\w+\"\\.\"(\\w+)\"").Select(m => m.Groups[1].Value).Distinct().ToList();
        Assert.All(referenced, column => Assert.Contains(column, AllowedColumns));
        Assert.Contains("ProviderStartedAt", referenced);

        // The provenance snapshot is reachable only through the three actor scalars.
        var allowedProvenance = new Regex(
            "json_extract\\(\"\\w+\"\\.\"CreationProvenanceJson\", '\\$\\.actor\\.(kind|id|entityId)'\\)");
        Assert.Equal(3, allowedProvenance.Matches(sql).Count);
        Assert.DoesNotContain("CreationProvenanceJson", allowedProvenance.Replace(sql, ""), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("anonymous")]
    [InlineData("local-user")]
    [InlineData("redleaf-without-provenance")]
    [InlineData("other-service")]
    [InlineData("execution-token")]
    public async Task Non_service_callers_are_forbidden(string caller)
    {
        Claim[]? claims = caller switch
        {
            "anonymous" => null,
            "local-user" => new[] { new Claim("sub", "local-user") },
            "redleaf-without-provenance" => ServiceClaims("redleaf", provenance: false),
            "other-service" => ServiceClaims("nova", provenance: true),
            _ => [.. ServiceClaims("redleaf", provenance: true), new Claim("token_use", "execution")],
        };

        var (status, _) = await Invoke(claims, "since=2026-10-23&until=2026-10-23&beneficiary=user-1");

        Assert.Equal(StatusCodes.Status403Forbidden, status);
    }

    [Fact]
    public async Task Service_token_reads_counts_and_invalid_windows_are_bad_requests()
    {
        Seed(Job("image-gen", "ComfyUI", JobStatus.Completed, Yesterday, "image/png", Actor("app", "outfits"),
            execution: TimeSpan.FromSeconds(7)));
        var service = ServiceClaims("redleaf", provenance: true);

        var (status, body) = await Invoke(service, "since=2026-10-23&until=2026-10-23&beneficiary=user-1&capability=image-gen");

        Assert.Equal(StatusCodes.Status200OK, status);
        using var json = JsonDocument.Parse(body);
        var day = Assert.Single(json.RootElement.GetProperty("days").EnumerateArray().ToList());
        Assert.Equal("2026-10-23", day.GetProperty("day").GetString());
        var group = Assert.Single(day.GetProperty("groups").EnumerateArray().ToList());
        Assert.Equal(
            ["capability", "provider", "status", "media", "initiator", "jobs", "executionSeconds"],
            group.EnumerateObject().Select(p => p.Name));
        Assert.Equal(7, group.GetProperty("executionSeconds").GetInt64());

        var (todayStatus, todayBody) = await Invoke(service, "since=2026-10-24&until=2026-10-24&beneficiary=user-1");
        Assert.Equal(StatusCodes.Status400BadRequest, todayStatus);
        Assert.Contains("window_includes_today", todayBody);
    }

    private async Task<(int Status, string Body)> Invoke(Claim[]? claims, string query)
    {
        var builder = WebApplication.CreateSlimBuilder();
        await using var app = builder.Build();
        var registry = new EndpointRegistry(app);
        UsageEndpoints.Map(registry, Service());
        var endpoint = ((IEndpointRouteBuilder)app).DataSources.SelectMany(d => d.Endpoints).OfType<RouteEndpoint>()
            .Single(e => e.RoutePattern.RawText == "/usage/daily-counts");
        Assert.Contains(registry.GetEndpoints(), e => e.Path == "/usage/daily-counts" && e.Method == "GET");

        var context = new DefaultHttpContext { RequestServices = app.Services };
        if (claims != null) context.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "signed"));
        context.Request.Method = "GET";
        context.Request.QueryString = new QueryString("?" + query);
        context.Response.Body = new MemoryStream();
        await endpoint.RequestDelegate!(context);
        context.Response.Body.Position = 0;
        return (context.Response.StatusCode, await new StreamReader(context.Response.Body).ReadToEndAsync());
    }

    private static Claim[] ServiceClaims(string clientId, bool provenance) =>
    [
        new("sub", $"service:{clientId}"),
        new("client_id", clientId),
        new("compute_provenance", provenance ? "true" : "false"),
    ];

    private UsageCountsService Service() => new(Db, () => Now);

    private RedComputeDbContext Db() => new CapturingDbContext(_database, _commands);

    private void Seed(params JobRecord[] jobs)
    {
        using var db = Db();
        db.Jobs.AddRange(jobs);
        db.SaveChanges();
    }

    private static JobRecord Job(string capability, string provider, JobStatus status, DateTimeOffset queuedAt,
        string? contentType, JobActor? actor, TimeSpan? execution = null)
    {
        var job = new JobRecord
        {
            CapabilitySlug = capability,
            ProviderName = provider,
            Status = status,
            QueuedAt = queuedAt,
            OutputContentType = contentType,
            UserId = User,
            Name = "private job name",
            InputJson = "{\"prompt\":\"private\"}",
        };
        if (execution is { } duration)
        {
            job.ProviderStartedAt = queuedAt.AddSeconds(1);
            job.CompletedAt = job.ProviderStartedAt + duration;
        }
        else if (status is not (JobStatus.Queued or JobStatus.Running))
        {
            job.CompletedAt = queuedAt.AddSeconds(1);
        }
        if (actor != null) job.CreationProvenance = Provenance(actor);
        return job;
    }

    private static JobActor Actor(string kind, string id, string? entityId = null)
        => new(kind, $"{kind} snapshot", entityId, id);

    private static JobProvenance Provenance(JobActor actor) => new(
        JobProvenance.CurrentSchemaVersion,
        new JobOrigin("redleaf", new JobAppReference("app", "outfits", null, "Outfits"),
            new JobEntrypoint("http", "/image-gen/generate", "POST")),
        actor,
        new JobBeneficiary("user", User, "Private Name"),
        [],
        new JobTrace(),
        JobProvenanceAssurance.Verified,
        Yesterday);

    private static UsageJobRow Row(DateTimeOffset queuedAt) => new(
        "image-gen", "ComfyUI", JobStatus.Completed, queuedAt, null, null, "image/png", "app", "outfits", null);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_directory, recursive: true); } catch { }
    }

    private sealed class CapturingDbContext(string dbPath, List<string> commands) : RedComputeDbContext(dbPath)
    {
        protected override void OnConfiguring(DbContextOptionsBuilder options)
        {
            base.OnConfiguring(options);
            options.AddInterceptors(new CommandCapture(commands));
        }
    }

    private sealed class CommandCapture(List<string> commands) : DbCommandInterceptor
    {
        public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            commands.Add(command.CommandText);
            return base.ReaderExecuting(command, eventData, result);
        }
    }
}
