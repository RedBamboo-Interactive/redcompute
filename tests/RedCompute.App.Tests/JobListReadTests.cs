using Microsoft.EntityFrameworkCore;
using RedCompute.App.Data;
using RedCompute.App.Services.Jobs;
using RedCompute.Core.Jobs;
using Xunit;

namespace RedCompute.App.Tests;

/// <summary>
/// 2026-11-28: listing the jobs of one discussion read 1.0 to 1.45 GB and took 1 to 4 s. The list loaded the summary
/// of every job queued in the range to find the few that belong to the context, and the columns it filters on sit
/// behind each job's input and result in the row. It now filters, authorizes, counts and sorts on candidates read
/// from one index, and opens the rows of the returned page only. What it returns is unchanged.
/// </summary>
public sealed class JobListReadTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 11, 28, 17, 0, 0, TimeSpan.Zero);
    private const string Discussion = "061a3c79";
    private const string DiscussionEntity = "cae23c97-a78c-41b8-bfed-e2e737822618";

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "redcompute-job-list-tests", Guid.NewGuid().ToString("N"));
    private readonly string _database;

    public JobListReadTests()
    {
        _database = Path.Combine(_directory, "jobs.db");
        Directory.CreateDirectory(_directory);
        using var db = Db();
        db.Database.EnsureCreated();
        db.MigrateSchema();
    }

    [Fact]
    public void A_context_list_returns_the_same_jobs_in_the_same_order_with_the_same_summary()
    {
        // Oldest first. Everything a real table mixes: other discussions, the same id under another kind, rows from
        // before provenance, a row written with other letter case, a confidential job and a clock jump.
        var oldest = Add(-29, "tts", InDiscussion(), name: "oldest", input: Large('a'));
        Add(-28, "image-gen", In("discussion", "3a4cb22f"), name: "another discussion", input: Large('b'));
        var byEntity = Add(-27, "ai-session", In("discussion", "other-slug", DiscussionEntity), name: "by entity id");
        Add(-26, "tts", In("session", Discussion), name: "same id, another kind");
        AddRaw(-25, "legacy without provenance", provenanceJson: null);
        var otherCase = AddRaw(-24, "legacy, other letter case", provenanceJson: InDiscussion().ToJson()
            .Replace("\"context\"", "\"Context\"").Replace("\"kind\":\"discussion\"", "\"Kind\":\"DISCUSSION\"")
            .Replace($"\"id\":\"{Discussion}\"", $"\"Id\":\"{Discussion.ToUpperInvariant()}\""));
        var confidential = Add(-23, "ai-session", InDiscussion(), name: "confidential", confidential: true);
        var completed = Add(-22, "workflow", InDiscussion(), name: "completed");
        new JobTrackingService(Db, () => Now).MarkCompleted(completed, resultJson: "{\"large\":\"" + Large('r') + "\"}");
        var newest = Add(-1, "tts", InDiscussion(), name: "newest");
        var clockJump = Add(+40, "tts", InDiscussion(), name: "clock jump");
        Add(-40, "tts", InDiscussion(), name: "before the range");
        var jobs = new JobTrackingService(Db, () => Now);

        var all = jobs.GetJobs(contextKind: "discussion", contextId: Discussion, limit: 50,
            canRead: job => !job.Confidential, publicJobsAlwaysReadable: true, queuedSince: Now.AddDays(-30));
        // Newest first, the implausibly future one last, the confidential one never counted.
        Assert.Equal([newest, completed, otherCase, oldest, clockJump], all.Items.Select(j => j.Id));
        Assert.Equal(5, all.TotalCount);

        var byEntityId = jobs.GetJobs(contextKind: "discussion", contextId: DiscussionEntity.ToUpperInvariant(),
            queuedSince: Now.AddDays(-30));
        Assert.Equal([newest, completed, confidential, otherCase, byEntity, oldest, clockJump], byEntityId.Items.Select(j => j.Id));

        var page = jobs.GetJobs(contextKind: "discussion", contextId: Discussion, limit: 2, offset: 1,
            canRead: job => !job.Confidential, publicJobsAlwaysReadable: true, queuedSince: Now.AddDays(-30));
        Assert.Equal([completed, otherCase], page.Items.Select(j => j.Id));
        Assert.Equal(5, page.TotalCount);
        Assert.Empty(jobs.GetJobs(contextKind: "discussion", contextId: Discussion, limit: 2, offset: 5,
            queuedSince: Now.AddDays(-30), queuedUntil: Now).Items);

        // The summary is the list's own: everything it showed, and no input or result.
        var summary = page.Items[0];
        Assert.Equal("completed", summary.Name);
        Assert.Equal("workflow", summary.CapabilitySlug);
        Assert.Equal("Local", summary.ProviderName);
        Assert.Equal(JobStatus.Completed, summary.Status);
        Assert.Equal(Now.AddDays(-22), summary.QueuedAt);
        Assert.NotNull(summary.CompletedAt);
        Assert.Equal("user-1", summary.UserId);
        Assert.Contains(summary.CreationProvenance!.Context, c => c.Kind == "discussion" && c.Id == Discussion);
        Assert.Equal("{}", summary.InputJson);
        Assert.Null(summary.ResultJson);

        // The other provenance filters read the same candidates.
        Assert.Equal(1, jobs.GetJobs(contextKind: "session", queuedSince: Now.AddDays(-30)).TotalCount);
        Assert.Equal([newest], jobs.GetJobs(executionId: "execution-newest").Items.Select(j => j.Id));
        Assert.Equal(1, jobs.GetJobs(complete: false).TotalCount);
        Assert.Equal(5, jobs.GetJobs(complete: true, capabilitySlug: "tts", status: JobStatus.Queued).TotalCount);
        Assert.Equal(10, jobs.GetJobs(originApp: "nova", limit: 500).TotalCount);
    }

    [Fact]
    public void A_page_longer_than_one_statement_holds_is_loaded_whole_and_in_order()
    {
        var ids = Enumerable.Range(0, 1203).Select(i => Add(-10, "tts", InDiscussion(), minutes: i)).ToList();
        ids.Reverse();

        var listed = new JobTrackingService(Db, () => Now).GetJobs(contextKind: "discussion", contextId: Discussion,
            limit: int.MaxValue, offset: 2);

        Assert.Equal(1203, listed.TotalCount);
        Assert.Equal(ids.Skip(2), listed.Items.Select(j => j.Id));
    }

    [Fact]
    public void The_candidates_of_a_historical_lookup_are_read_from_the_list_index_and_never_from_the_rows()
    {
        Add(-29, "tts", InDiscussion(), input: Large('a'));
        using var db = Db();
        var since = Now.AddDays(-30);
        var until = Now;

        // The statements GET /jobs?contextKind=..&contextId=..&since=.. runs before it knows which jobs match:
        // bounded below, bounded on both sides, with the list's own column filters, and unbounded.
        Assert.Equal("SEARCH j USING COVERING INDEX IX_Jobs_List (QueuedAt>?)",
            Plan(db, JobTrackingService.ListCandidates(db.Jobs.Where(j => j.QueuedAt >= since))));
        Assert.Equal("SEARCH j USING COVERING INDEX IX_Jobs_List (QueuedAt>? AND QueuedAt<?)",
            Plan(db, JobTrackingService.ListCandidates(db.Jobs.Where(j => j.QueuedAt >= since).Where(j => j.QueuedAt < until))));
        Assert.Equal("SEARCH j USING COVERING INDEX IX_Jobs_List (QueuedAt>? AND QueuedAt<?)",
            Plan(db, JobTrackingService.ListCandidates(db.Jobs.Where(j => j.QueuedAt >= since).Where(j => j.QueuedAt < until)
                .Where(j => j.ExternalExecution == false))));
        Assert.Equal("SCAN j USING COVERING INDEX IX_Jobs_List",
            Plan(db, JobTrackingService.ListCandidates(db.Jobs)));
    }

    [Fact]
    public void The_list_index_is_built_once_and_holds_every_column_the_candidates_read()
    {
        var directory = Path.Combine(_directory, "fresh");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "jobs.db");
        using (var db = new RedComputeDbContext(path))
        {
            db.Database.EnsureCreated();
            db.MigrateSchema();
            Assert.NotNull(db.JobListIndexBuiltIn);
        }
        using (var db = new RedComputeDbContext(path))
        {
            db.MigrateSchema();
            Assert.Null(db.JobListIndexBuiltIn);

            db.Database.OpenConnection();
            using var command = db.Database.GetDbConnection().CreateCommand();
            command.CommandText = $"SELECT name FROM pragma_index_info('{RedComputeDbContext.JobListIndex}') ORDER BY seqno";
            var columns = new List<string>();
            using (var reader = command.ExecuteReader())
                while (reader.Read()) columns.Add(reader.GetString(0));
            Assert.Equal(["QueuedAt", "Id", "CapabilitySlug", "Status", "ExternalExecution", "ParentJobId", "Confidential",
                "UserId", "CreationProvenanceJson"], columns);
        }
    }

    private static string Plan(RedComputeDbContext db, IQueryable<JobRecord> query)
    {
        db.Database.OpenConnection();
        using var command = query.CreateDbCommand();
        command.CommandText = "EXPLAIN QUERY PLAN " + command.CommandText;
        var steps = new List<string>();
        using (var reader = command.ExecuteReader())
            while (reader.Read()) steps.Add(reader.GetString(3));
        return string.Join(" | ", steps);
    }

    private Guid Add(int days, string capability, JobProvenance provenance, string? name = null, string input = "{}",
        bool confidential = false, int minutes = 0)
    {
        var at = Now.AddDays(days).AddMinutes(minutes);
        if (name == "newest")
            provenance = provenance with { Context = [.. provenance.Context, new JobContextReference("execution", "execution-newest")] };
        return new JobTrackingService(Db, () => at).CreateJob(new JobSubmission(capability, "Local",
            "{\"prompt\":\"" + input + "\"}", provenance, Name: name, Confidential: confidential)).Id;
    }

    // A row as an older RedCompute left it: written straight to the table.
    private Guid AddRaw(int days, string name, string? provenanceJson)
    {
        using var db = Db();
        var job = new JobRecord
        {
            CapabilitySlug = "tts", ProviderName = "Local", Status = JobStatus.Completed, Name = name,
            QueuedAt = Now.AddDays(days), CreationProvenanceJson = provenanceJson,
        };
        db.Jobs.Add(job);
        db.SaveChanges();
        return job.Id;
    }

    private static JobProvenance InDiscussion() => In("discussion", Discussion, DiscussionEntity);

    private static JobProvenance In(string kind, string id, string? entityId = null) => new(
        1,
        new JobOrigin("redleaf", new JobAppReference("plugin", "nova", null, "Nova", "/nova.png", "#C74B7A"),
            new JobEntrypoint("http", "/api/apps/nova/discussions/x/message", "POST")),
        new JobActor("agent", "agent-nova", "agent-nova", "Nova", "/nova.png"),
        new JobBeneficiary("user", "user-1", "user-1", "/user-1.png"),
        [new JobContextReference(kind, id, entityId), new JobContextReference("origin-entrypoint", Route: "/x")],
        new JobTrace("request-1"), JobProvenanceAssurance.Verified, Now);

    private static string Large(char fill) => new(fill, 60_000);

    private RedComputeDbContext Db() => new(_database);

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch { }
    }
}
