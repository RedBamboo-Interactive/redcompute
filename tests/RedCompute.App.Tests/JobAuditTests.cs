using Microsoft.EntityFrameworkCore;
using RedBamboo.AppHost.Auth;
using RedBamboo.AppHost.Streams;
using RedCompute.App.Data;
using RedCompute.App.Services.Jobs;
using RedCompute.Core.Jobs;
using RedCompute.Core.Sessions;
using RedCompute.App.Api.Endpoints;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using System.Security.Claims;
using Xunit;

namespace RedCompute.App.Tests;

public sealed class JobAuditTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "redcompute-audit-tests", Guid.NewGuid().ToString("N"));
    private readonly string _database;
    private readonly JobTrackingService _jobs;

    public JobAuditTests()
    {
        _database = Path.Combine(_directory, "jobs.db");
        Directory.CreateDirectory(_directory);
        using var db = Db();
        db.Database.EnsureCreated();
        db.MigrateSchema();
        _jobs = new JobTrackingService(Db);
    }

    [Fact]
    public void Provenance_round_trips_snapshots_and_rejects_audit_gaps_for_new_jobs()
    {
        var provenance = Provenance("nova", "agent-nova", "user-1", "/api/apps/nova/discussions/d1/message");
        provenance.ValidateForNewJob();
        var roundTrip = JobProvenance.FromJson(provenance.ToJson())!;
        Assert.Equal(provenance.Origin, roundTrip.Origin);
        Assert.Equal("#C74B7A", roundTrip.Origin.App.ColorSnapshot);
        Assert.Equal(provenance.Actor, roundTrip.Actor);
        Assert.Equal(provenance.OnBehalfOf, roundTrip.OnBehalfOf);
        Assert.Equal(provenance.Context, roundTrip.Context);
        Assert.Equal(provenance.Trace, roundTrip.Trace);

        var localUser = provenance with { OnBehalfOf = new JobBeneficiary("user", "local-user") };
        Assert.Throws<JobProvenanceValidationException>(localUser.ValidateForNewJob);
        var systemWithoutReason = provenance with { OnBehalfOf = new JobBeneficiary("system") };
        Assert.Throws<JobProvenanceValidationException>(systemWithoutReason.ValidateForNewJob);
    }

    [Fact]
    public void Idempotency_is_scoped_and_conflicting_reuse_is_rejected()
    {
        var nova = Provenance("nova", "agent-nova", "user-1", "/nova/message");
        var first = _jobs.CreateJob(new JobSubmission("image-gen", "ComfyUI", "{\"seed\":1}", nova,
            IdempotencyKey: "render-1"));
        var reused = _jobs.CreateJob(new JobSubmission("image-gen", "ComfyUI", "{\"seed\":1}", nova,
            IdempotencyKey: "render-1"));
        Assert.Equal(first.Id, reused.Id);
        Assert.True(reused.IsIdempotencyReuse);
        var reuseEvent = Assert.Single(_jobs.GetJobEvents(first.Id), e => e.Kind == JobEventKind.Reused);
        Assert.Equal("/nova/message", reuseEvent.Provenance!.Origin.Entrypoint.Route);

        Assert.Throws<IdempotencyConflictException>(() => _jobs.CreateJob(new JobSubmission(
            "image-gen", "ComfyUI", "{\"seed\":2}", nova, IdempotencyKey: "render-1")));
        Assert.Throws<IdempotencyConflictException>(() => _jobs.CreateJob(new JobSubmission(
            "image-gen", "DifferentProvider", "{\"seed\":1}", nova, IdempotencyKey: "render-1")));

        var wallpaper = Provenance("wallpaper", "wallpaper", "user-1", "/wallpaper/generate", actorKind: "app");
        var independentlyScoped = _jobs.CreateJob(new JobSubmission(
            "image-gen", "ComfyUI", "{\"seed\":2}", wallpaper, IdempotencyKey: "render-1"));
        Assert.NotEqual(first.Id, independentlyScoped.Id);
    }

    [Fact]
    public void Explicit_idempotency_scope_prevents_duplicate_roots_across_capabilities()
    {
        var provenance = Provenance("redleaf", "automation-scheduler", "user-1", "/automations/tick",
            actorKind: "app");
        var scope = $"redleaf:automation:{Guid.NewGuid():N}";
        var key = "scheduled:638924256000000000";

        var legacy = _jobs.CreateJob(new JobSubmission(
            "automation", "RedLeaf automation worker", "{\"definition\":1}", provenance,
            IdempotencyKey: key, ExternalExecution: true, IdempotencyScope: scope));

        var conflict = Assert.Throws<IdempotencyConflictException>(() => _jobs.CreateJob(new JobSubmission(
            "workflow", "RedLeaf Workflow Engine", "{\"definition\":2}", provenance,
            IdempotencyKey: key, ExternalExecution: true, IdempotencyScope: scope)));

        Assert.Equal(legacy.Id, conflict.ExistingJobId);
    }

    [Fact]
    public void Every_work_start_has_immutable_invocation_provenance()
    {
        var createdWith = Provenance("nova", "agent-nova", "user-1", "/nova/create");
        var job = _jobs.CreateJob(new JobSubmission("ai-session", "Codex", "{}", createdWith));
        _jobs.StartInvocation(job.Id, createdWith with
        {
            Origin = createdWith.Origin with { Entrypoint = new JobEntrypoint("http", "/nova/message", "POST") },
        });
        var resume = createdWith with
        {
            Origin = createdWith.Origin with { Entrypoint = new JobEntrypoint("http", "/nova/resume", "POST") },
            Trace = new JobTrace(CorrelationId: "turn-2"),
        };
        _jobs.StartInvocation(job.Id, resume, JobEventKind.Resumed);
        _jobs.StartInvocation(job.Id, resume with { Trace = new JobTrace(CorrelationId: "retry-1") }, JobEventKind.Retried);
        _jobs.StartInvocation(job.Id, resume with { Trace = new JobTrace(ParentJobId: job.Id.ToString()) }, JobEventKind.Rerun);
        _jobs.MarkCompleted(job.Id, resultJson: "{}");

        var events = _jobs.GetJobEvents(job.Id);
        Assert.Equal([JobEventKind.Created, JobEventKind.Started, JobEventKind.Resumed,
            JobEventKind.Retried, JobEventKind.Rerun, JobEventKind.Completed], events.Select(e => e.Kind));
        Assert.All(events.Where(e => e.Kind is JobEventKind.Started or JobEventKind.Resumed
            or JobEventKind.Retried or JobEventKind.Rerun), e => Assert.NotNull(e.Provenance));

        events[1].DataJson = "mutated";
        Assert.NotEqual("mutated", _jobs.GetJobEvents(job.Id)[1].DataJson);

        using var db = Db();
        var persisted = db.JobEvents.Single(e => e.Id == events[1].Id);
        persisted.DataJson = "mutated";
        Assert.Throws<DbUpdateException>(() => db.SaveChanges());
    }

    [Fact]
    public void Structured_filters_are_role_specific_and_completeness_is_honest()
    {
        _jobs.CreateJob(new JobSubmission("tts", "Local", "{}",
            Provenance("nova", "agent-nova", "user-1", "/nova/speak") with
            {
                Context = [new JobContextReference("execution", "execution-1")],
            }));
        _jobs.CreateJob(new JobSubmission("tts", "Local", "{}",
            Provenance("codered", "codered", "user-2", "/codered/review", actorKind: "app")));

        Assert.Single(_jobs.GetJobs(originApp: "nova").Items);
        Assert.Single(_jobs.GetJobs(originApi: "/codered/review").Items);
        Assert.Single(_jobs.GetJobs(actor: "agent-nova").Items);
        Assert.Single(_jobs.GetJobs(beneficiary: "user-2").Items);
        Assert.Single(_jobs.GetJobs(executionId: "execution-1").Items);
        Assert.Empty(_jobs.GetJobs(executionId: "execution-missing").Items);
        Assert.Equal(2, _jobs.GetJobs(assurance: "Verified", complete: true).TotalCount);
    }

    [Fact]
    public void Conservative_backfill_keeps_exact_session_links_but_unknown_actor_visible()
    {
        using (var db = Db())
        {
            db.Jobs.Add(new JobRecord
            {
                Id = Guid.Parse("11111111-1111-1111-1111-111111111111"),
                CapabilitySlug = "ai-session", ProviderName = "Codex", InputJson = "{}",
                UserId = "local-user", CallerInfo = "Nova", QueuedAt = DateTimeOffset.UtcNow,
            });
            db.SaveChanges();
        }
        var session = new UnifiedSessionInfo
        {
            Id = "session-1", Provider = "codex", ProjectName = "nova", ProjectPath = "C:\\nova",
            Status = SessionStatus.Stopped, StartedAt = DateTimeOffset.UtcNow,
            JobId = Guid.Parse("11111111-1111-1111-1111-111111111111"), Source = "Nova",
            UserId = "user-1", UserName = "Laurent", UserAvatarUrl = "/avatar.png",
        };

        Assert.Equal(1, _jobs.BackfillLegacyJobs([session]));
        var job = _jobs.GetJob(session.JobId!.Value)!;
        Assert.Equal("session-1", job.CreationProvenance!.Context.Single(c => c.Kind == "session").Id);
        Assert.Equal("unknown", job.CreationProvenance.Actor.Kind);
        Assert.Equal(JobProvenanceAssurance.Unknown, job.CreationProvenance.Assurance);
        Assert.False(JobTrackingService.IsAuditComplete(job.CreationProvenance));
        Assert.Equal("user-1", job.UserId);
    }

    [Fact]
    public void RedLeaf_exact_evidence_enriches_only_legacy_jobs_and_records_field_assurance()
    {
        using (var db = Db())
        {
            db.Jobs.Add(new JobRecord
            {
                Id = Guid.Parse("22222222-2222-2222-2222-222222222222"),
                CapabilitySlug = "ai-session", ProviderName = "Codex", InputJson = "{}",
                CallerInfo = "Nova", QueuedAt = DateTimeOffset.UtcNow,
            });
            db.SaveChanges();
        }
        _jobs.BackfillLegacyJobs([]);
        var evidence = new JobProvenance(1,
            new JobOrigin("redleaf", new JobAppReference("plugin", "nova", null, "Nova"),
                new JobEntrypoint("legacy", "unknown")),
            new JobActor("agent", "Nova", "agent-entity", "nova", "/nova.png"),
            new JobBeneficiary("user", "user-1", "Laurent", "/avatar.png"),
            [new JobContextReference("discussion", "discussion-1", "discussion-entity")],
            new JobTrace(), JobProvenanceAssurance.BackfilledInferred, DateTimeOffset.UtcNow);

        Assert.True(_jobs.ApplyBackfillEvidence(Guid.Parse("22222222-2222-2222-2222-222222222222"),
            evidence, new Dictionary<string, string> { ["actor"] = "exact" }, "test-v1", "redleaf-test"));
        var job = _jobs.GetJob(Guid.Parse("22222222-2222-2222-2222-222222222222"))!;
        Assert.Equal("nova", job.CreationProvenance!.Origin.App.Id);
        Assert.Equal("agent-entity", job.CreationProvenance.Actor.EntityId);
        Assert.Contains(job.CreationProvenance.Context, c => c.Kind == "discussion");
        Assert.False(JobTrackingService.IsAuditComplete(job.CreationProvenance));
        Assert.Contains(_jobs.GetJobEvents(job.Id), e => e.Kind == JobEventKind.Backfilled);
        Assert.False(_jobs.ApplyBackfillEvidence(job.Id, evidence));
    }

    [Fact]
    public async Task Outbox_is_transactional_and_failed_delivery_remains_pending_for_retry()
    {
        var job = _jobs.CreateJob(new JobSubmission("tts", "Local", "{}",
            Provenance("nova", "agent-nova", "user-1", "/nova/speak")));
        using (var db = Db())
            Assert.Equal(2, db.JobOutbox.Count(m => m.JobId == job.Id));

        var jwt = new JwtService(new JwtOptions { SigningKey = new string('a', 64) });
        await using var client = new RedLeafStreamClient("http://127.0.0.1:1", "RedComputeTests", jwt);
        var worker = new JobAuditOutboxService(client, (_, _) => { }, Db);
        Assert.Equal(2, await worker.ProcessBatchAsync(CancellationToken.None));
        using var after = Db();
        var pending = after.JobOutbox.Where(m => m.JobId == job.Id).ToList();
        Assert.All(pending, m =>
        {
            Assert.Null(m.AcknowledgedAt);
            Assert.Equal(1, m.Attempts);
            Assert.NotNull(m.LastError);
            Assert.True(m.NextAttemptAt > m.CreatedAt);
        });
    }

    [Fact]
    public async Task Forwarded_identity_and_verified_provenance_require_service_claims()
    {
        var supplied = Provenance("nova", "agent-nova", "victim", "/nova/message");
        var untrusted = Context(
            [new Claim("sub", "actual-user"), new Claim("name", "Actual User"), new Claim("client_id", "other")]);
        untrusted.Request.Headers["X-User-Id"] = "victim";
        untrusted.Request.Headers[ProvenanceCapture.HeaderName] = supplied.ToJson();
        Assert.Equal("actual-user", UserInfoHelper.ResolveUserId(untrusted));
        var asserted = await ProvenanceCapture.ResolveAsync(untrusted, "/ai-session/execute");
        Assert.Equal(JobProvenanceAssurance.Asserted, asserted.Assurance);
        Assert.Equal("actual-user", asserted.OnBehalfOf.Id);

        var direct = Context([]);
        direct.Request.Headers[ProvenanceCapture.HeaderName] = supplied.ToJson();
        var directAsserted = await ProvenanceCapture.ResolveAsync(direct, "/ai-session/execute");
        Assert.Equal(JobProvenanceAssurance.Asserted, directAsserted.Assurance);
        Assert.Equal("victim", directAsserted.OnBehalfOf.Id);

        var trusted = Context(
            [new Claim("sub", "service:redleaf"), new Claim("client_id", "redleaf"),
             new Claim("compute_provenance", "true"), new Claim("compute_delegate_user", "true")]);
        trusted.Request.Headers["X-User-Id"] = "victim";
        trusted.Request.Headers[ProvenanceCapture.HeaderName] = supplied.ToJson();
        Assert.Equal("victim", UserInfoHelper.ResolveUserId(trusted));
        var verified = await ProvenanceCapture.ResolveAsync(trusted, "/ai-session/execute");
        Assert.Equal(JobProvenanceAssurance.Verified, verified.Assurance);
        Assert.Equal("victim", verified.OnBehalfOf.Id);

        trusted.Request.Headers[ProvenanceCapture.HeaderName] = (supplied with
            { Assurance = JobProvenanceAssurance.Asserted }).ToJson();
        var deliberatelyAsserted = await ProvenanceCapture.ResolveAsync(trusted, "/ai-session/execute");
        Assert.Equal(JobProvenanceAssurance.Asserted, deliberatelyAsserted.Assurance);
    }

    [Fact]
    public void External_attempt_has_one_idempotent_leased_timeline_and_queryable_children()
    {
        var now = new DateTimeOffset(2026, 8, 2, 10, 0, 0, TimeSpan.Zero);
        var jobs = new JobTrackingService(Db, () => now);
        var automationId = Guid.NewGuid();
        var provenance = Provenance("redleaf", "automation-worker", "user-1", "/api/automations/run",
            actorKind: "service") with
        {
            Context = [new JobContextReference("automation", automationId.ToString(), automationId.ToString(), "Morning coffee")],
            Trace = new JobTrace("request-1", "automation-correlation"),
        };
        var submission = new JobSubmission("automation", "RedLeaf automation worker",
            "{\"trigger\":\"schedule\"}", provenance, IdempotencyKey: "automation:morning:638897256000000000",
            Name: "Morning coffee", ExternalExecution: true);

        var parent = jobs.CreateJob(submission);
        jobs.AppendJobEvent(parent.Id, JobEventKind.Scheduled,
            new { scheduledFor = now, definitionVersion = "sha256:test" }, provenance);
        var reused = jobs.CreateJob(submission);
        Assert.Equal(parent.Id, reused.Id);
        Assert.True(reused.IsIdempotencyReuse);

        var lease = jobs.ClaimExternalJob(parent.Id, "redleaf:worker-1", TimeSpan.FromMinutes(5), provenance);
        Assert.Equal(1, lease.Attempt);
        Assert.Throws<JobLeaseConflictException>(() => jobs.ClaimExternalJob(
            parent.Id, "redleaf:worker-2", TimeSpan.FromMinutes(5), provenance));
        Assert.Throws<JobLeaseValidationException>(() => jobs.ReportExternalProgress(
            parent.Id, "wrong-token", 0.25));

        jobs.ReportExternalProgress(parent.Id, lease.LeaseToken, 0.25, "Preparing");
        jobs.ReportExternalRetry(parent.Id, lease.LeaseToken, 1, TimeSpan.FromSeconds(30), "Transient failure");

        var childProvenance = provenance with
        {
            Origin = provenance.Origin with
            {
                Entrypoint = new JobEntrypoint("sdk", "automation/child"),
            },
            Trace = provenance.Trace with { ParentJobId = parent.Id.ToString() },
        };
        var child = jobs.CreateJob(new JobSubmission("image-gen", "ComfyUI", "{}", childProvenance));
        jobs.LinkExternalChild(parent.Id, lease.LeaseToken, child.Id, "render");
        Assert.Equal(parent.Id, jobs.GetJob(child.Id)!.ParentJobId);
        Assert.Single(jobs.GetJobs(parentJobId: parent.Id).Items);

        var renewed = jobs.RenewExternalLease(parent.Id, lease.LeaseToken,
            TimeSpan.FromMinutes(5), provenance);
        Assert.Equal(lease.Attempt, renewed.Attempt);
        jobs.CompleteExternalJob(parent.Id, renewed.LeaseToken,
            resultJson: "{\"success\":true}", costUsd: 0.01);

        var completed = jobs.GetJob(parent.Id)!;
        Assert.Equal(JobStatus.Completed, completed.Status);
        Assert.Null(completed.LeaseOwner);
        Assert.Equal(1, completed.AttemptCount);
        Assert.Throws<JobLeaseConflictException>(() => jobs.ClaimExternalJob(
            parent.Id, "redleaf:worker-2", TimeSpan.FromMinutes(5), provenance));

        var kinds = jobs.GetJobEvents(parent.Id).Select(e => e.Kind).ToList();
        Assert.Contains(JobEventKind.Created, kinds);
        Assert.Contains(JobEventKind.Scheduled, kinds);
        Assert.Contains(JobEventKind.Reused, kinds);
        Assert.Contains(JobEventKind.Claimed, kinds);
        Assert.Contains(JobEventKind.Progress, kinds);
        Assert.Contains(JobEventKind.Retried, kinds);
        Assert.Contains(JobEventKind.ChildLinked, kinds);
        Assert.Contains(JobEventKind.LeaseRenewed, kinds);
        Assert.Contains(JobEventKind.Completed, kinds);
    }

    [Fact]
    public void Persistent_job_can_be_referenced_by_multiple_attempts_without_becoming_their_child()
    {
        var provenance = Provenance("redleaf", "automation-worker", "user-1",
            "/api/automations/run", actorKind: "service");
        var firstParent = _jobs.CreateJob(new JobSubmission("automation",
            "RedLeaf automation worker", "{}", provenance, ExternalExecution: true));
        var secondParent = _jobs.CreateJob(new JobSubmission("automation",
            "RedLeaf automation worker", "{\"tick\":2}", provenance,
            ExternalExecution: true));
        var persistentSession = _jobs.CreateJob(new JobSubmission("ai-session", "Codex",
            "{}", provenance with
            {
                Origin = provenance.Origin with
                {
                    Entrypoint = new JobEntrypoint("sdk", "heartbeat/session"),
                },
            }));
        var firstLease = _jobs.ClaimExternalJob(firstParent.Id, "redleaf:first",
            TimeSpan.FromMinutes(1), provenance);
        var secondLease = _jobs.ClaimExternalJob(secondParent.Id, "redleaf:second",
            TimeSpan.FromMinutes(1), provenance);

        _jobs.LinkExternalChild(firstParent.Id, firstLease.LeaseToken,
            persistentSession.Id, "reference");
        _jobs.LinkExternalChild(secondParent.Id, secondLease.LeaseToken,
            persistentSession.Id, "reference");

        Assert.Null(_jobs.GetJob(persistentSession.Id)!.ParentJobId);
        Assert.Empty(_jobs.GetJobs(parentJobId: firstParent.Id).Items);
        Assert.Contains(_jobs.GetJobEvents(firstParent.Id), e =>
            e.Kind == JobEventKind.ChildLinked
            && e.DataJson.Contains("\"relation\":\"reference\"", StringComparison.Ordinal));
        Assert.Contains(_jobs.GetJobEvents(secondParent.Id), e =>
            e.Kind == JobEventKind.ChildLinked
            && e.DataJson.Contains(persistentSession.Id.ToString(), StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Expired_external_lease_reclaims_the_same_attempt_and_rejects_the_old_token()
    {
        var now = new DateTimeOffset(2026, 8, 2, 11, 0, 0, TimeSpan.Zero);
        var jobs = new JobTrackingService(Db, () => now);
        var provenance = Provenance("redleaf", "automation-worker", "user-1", "/api/automations/run",
            actorKind: "service");
        var job = jobs.CreateJob(new JobSubmission("automation", "RedLeaf automation worker", "{}",
            provenance, ExternalExecution: true));
        var first = jobs.ClaimExternalJob(job.Id, "redleaf:old", TimeSpan.FromSeconds(15), provenance);

        now = now.AddSeconds(16);
        Assert.Throws<JobLeaseValidationException>(() => jobs.ReportExternalProgress(
            job.Id, first.LeaseToken, 0.5, "Late worker must not write after expiry"));
        Assert.Throws<JobLeaseValidationException>(() => jobs.CompleteExternalJob(
            job.Id, first.LeaseToken));
        var second = jobs.ClaimExternalJob(job.Id, "redleaf:new", TimeSpan.FromMinutes(1), provenance);
        Assert.Equal(job.Id, second.JobId);
        Assert.Equal(2, second.Attempt);
        Assert.NotEqual(first.LeaseToken, second.LeaseToken);
        Assert.Throws<JobLeaseValidationException>(() => jobs.CompleteExternalJob(job.Id, first.LeaseToken));

        jobs.TimeoutExternalJob(job.Id, second.LeaseToken, "Execution deadline exceeded");
        Assert.Equal(JobStatus.TimedOut, jobs.GetJob(job.Id)!.Status);
        Assert.Equal(2, jobs.GetJob(job.Id)!.AttemptCount);
        Assert.Equal(2, jobs.GetJobEvents(job.Id).Count(e =>
            e.Kind is JobEventKind.Claimed or JobEventKind.Retried));
        Assert.Contains(jobs.GetJobEvents(job.Id), e => e.Kind == JobEventKind.TimedOut);
    }

    [Fact]
    public void External_skip_is_a_terminal_outcome_not_a_fake_success()
    {
        var provenance = Provenance("redleaf", "automation-worker", "user-1", "/api/automations/run",
            actorKind: "service");
        var job = _jobs.CreateJob(new JobSubmission("automation", "RedLeaf automation worker", "{}",
            provenance, ExternalExecution: true));
        var lease = _jobs.ClaimExternalJob(job.Id, "redleaf:worker", TimeSpan.FromMinutes(1), provenance);

        _jobs.SkipExternalJob(job.Id, lease.LeaseToken, "Overlapping execution already owns the definition");

        Assert.Equal(JobStatus.Skipped, _jobs.GetJob(job.Id)!.Status);
        Assert.Contains(_jobs.GetJobEvents(job.Id), e => e.Kind == JobEventKind.Skipped);
    }

    [Fact]
    public void Historical_external_backfill_preserves_time_status_and_is_replay_safe()
    {
        var importedAt = new DateTimeOffset(2026, 8, 2, 12, 0, 0, TimeSpan.Zero);
        var jobs = new JobTrackingService(Db, () => importedAt);
        var happenedAt = new DateTimeOffset(2026, 6, 25, 7, 0, 0, TimeSpan.Zero);
        var id = Guid.Parse("aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee");
        var provenance = Provenance("nova", "automation-worker", "user-1", "automation-runs",
            actorKind: "application") with
        {
            Assurance = JobProvenanceAssurance.BackfilledInferred,
            CapturedAt = happenedAt,
            Context = [new JobContextReference("automation", "morning-greeting")],
        };
        var historical = new JobRecord
        {
            Id = id,
            CapabilitySlug = "automation",
            ProviderName = "Legacy RedLeaf automation worker",
            Status = JobStatus.Failed,
            QueuedAt = happenedAt,
            StartedAt = happenedAt,
            CompletedAt = happenedAt.AddSeconds(3),
            InputJson = "{\"legacyRecordId\":42}",
            ResultJson = "{\"summary\":\"failed\"}",
            ErrorMessage = "legacy failure",
            IdempotencyKey = "legacy:automation-runs:42",
            Name = "morning-greeting",
            CreationProvenance = provenance,
            ExternalExecution = true,
        };

        var restored = jobs.BackfillExternalJob(historical, happenedAt,
            "redleaf:automation-runs:42");
        var replay = jobs.BackfillExternalJob(new JobRecord
        {
            Id = id,
            CapabilitySlug = historical.CapabilitySlug,
            ProviderName = historical.ProviderName,
            Status = historical.Status,
            QueuedAt = historical.QueuedAt,
            StartedAt = historical.StartedAt,
            CompletedAt = historical.CompletedAt,
            InputJson = historical.InputJson,
            ResultJson = historical.ResultJson,
            ErrorMessage = historical.ErrorMessage,
            IdempotencyKey = historical.IdempotencyKey,
            Name = historical.Name,
            CreationProvenance = provenance,
            ExternalExecution = true,
        }, happenedAt, "redleaf:automation-runs:42");

        Assert.Equal(id, restored.Id);
        Assert.Equal(id, replay.Id);
        Assert.True(replay.IsIdempotencyReuse);
        Assert.Equal(JobStatus.Failed, restored.Status);
        Assert.Equal(happenedAt, restored.QueuedAt);
        Assert.Equal(happenedAt.AddSeconds(3), restored.CompletedAt);
        var events = jobs.GetJobEvents(id);
        Assert.Equal(happenedAt, events.Single(e => e.Kind == JobEventKind.Created).OccurredAt);
        Assert.Equal(happenedAt.AddSeconds(3), events.Single(e => e.Kind == JobEventKind.Failed).OccurredAt);
        Assert.Equal(importedAt, events.Single(e => e.Kind == JobEventKind.Backfilled).OccurredAt);
        Assert.Equal(5, events.Count);
    }

    private RedComputeDbContext Db() => new(_database);

    private static DefaultHttpContext Context(IEnumerable<Claim> claims)
    {
        var context = new DefaultHttpContext();
        context.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
        context.RequestServices = new ServiceCollection().BuildServiceProvider();
        return context;
    }

    private static JobProvenance Provenance(string appId, string actorId, string userId,
        string route, string actorKind = "agent") => new(
        1,
        new JobOrigin("redleaf", new JobAppReference("plugin", appId, null, appId, $"/{appId}.png", "#C74B7A"),
            new JobEntrypoint("http", route, "POST")),
        new JobActor(actorKind, actorId, actorKind == "agent" ? actorId : null, actorId, $"/{actorId}.png"),
        new JobBeneficiary("user", userId, userId, $"/{userId}.png"),
        [new JobContextReference("test", "context-1")], new JobTrace("request-1"),
        JobProvenanceAssurance.Verified, DateTimeOffset.UtcNow);

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch { }
    }
}
