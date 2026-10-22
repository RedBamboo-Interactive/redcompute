using RedCompute.App.Services;
using RedCompute.Core.Configuration;
using RedCompute.Core.Jobs;
using Microsoft.Data.Sqlite;
using Xunit;

namespace RedCompute.App.Tests;

public sealed class SessionInputQueueStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "redcompute-input-queue-tests", Guid.NewGuid().ToString("N"));
    private readonly RedComputeConfig _config = new()
    {
        InputAttachmentTtlMinutes = 60,
        InputAttachmentMaxFileSizeBytes = 1024,
        InputAttachmentMaxCount = 4,
        InputAttachmentMaxTurnSizeBytes = 2048,
    };

    private (InputAttachmentStore Attachments, SessionInputQueueStore Queue) Stores()
    {
        Directory.CreateDirectory(_root);
        var attachments = new InputAttachmentStore(_config, Path.Combine(_root, "bytes"), Path.Combine(_root, "attachments.db"));
        return (attachments, new SessionInputQueueStore(_config, attachments));
    }

    [Fact]
    public async Task AdmissionPersistsAcrossStoreInstancesAndReservesAttachments()
    {
        var (attachments, queue) = Stores();
        var attachment = await attachments.UploadBytesAsync("hello"u8.ToArray(), "hello.txt", "text/plain", "owner-a");

        var admitted = await queue.EnqueueAsync(Submission("hello", "owner-a", [attachment.Id]));

        Assert.False(admitted.Existing);
        Assert.Equal(SessionInputQueueState.Pending, admitted.Item.State);
        var reserved = await attachments.GetAuthorizedAsync(attachment.Id, "owner-a");
        Assert.Null(reserved!.ExpiresAt);
        Assert.Equal("session-a", reserved.ClaimedSessionId);
        Assert.Equal(admitted.Item.MessageUid, reserved.ClaimedMessageUid);

        var (_, reopened) = Stores();
        var items = await reopened.ListAsync("session-a", "owner-a", includeTerminal: false);
        Assert.Single(items);
        Assert.Equal(admitted.Item.Id, items[0].Id);
    }

    [Fact]
    public async Task IdempotencyReturnsOriginalAndRejectsConflictingPayload()
    {
        var (_, queue) = Stores();
        var submission = Submission("hello", idempotencyKey: "client-message-1");

        var first = await queue.EnqueueAsync(submission);
        var replay = await queue.EnqueueAsync(Submission("hello", idempotencyKey: "client-message-1"));

        Assert.False(first.Existing);
        Assert.True(replay.Existing);
        Assert.Equal(first.Item.Id, replay.Item.Id);
        var error = await Assert.ThrowsAsync<SessionInputQueueStoreException>(() =>
            queue.EnqueueAsync(Submission("different", idempotencyKey: "client-message-1")));
        Assert.Equal("idempotency_conflict", error.Code);
    }

    [Fact]
    public async Task IdempotencyIgnoresRequestExecutionLineageButPreservesStableContext()
    {
        var (_, queue) = Stores();
        var firstProvenance = Provenance("test-app",
        [
            new JobContextReference("discussion", "discussion-1"),
            new JobContextReference("execution", "execution-1"),
            new JobContextReference("parent-execution", "parent-1"),
        ]);
        var retryProvenance = Provenance("test-app",
        [
            new JobContextReference("discussion", "discussion-1"),
            new JobContextReference("execution", "execution-2"),
            new JobContextReference("parent-execution", "parent-2"),
        ]);

        var first = await queue.EnqueueAsync(Submission("hello", idempotencyKey: "client-message-1",
            provenance: firstProvenance));
        var retry = await queue.EnqueueAsync(Submission("hello", idempotencyKey: "client-message-1",
            provenance: retryProvenance));

        Assert.True(retry.Existing);
        Assert.Equal(first.Item.Id, retry.Item.Id);

        var differentDiscussion = Provenance("test-app",
        [
            new JobContextReference("discussion", "discussion-2"),
            new JobContextReference("execution", "execution-3"),
        ]);
        var distinct = await queue.EnqueueAsync(Submission("hello", idempotencyKey: "client-message-1",
            provenance: differentDiscussion));
        Assert.False(distinct.Existing);
        Assert.NotEqual(first.Item.Id, distinct.Item.Id);
    }

    [Fact]
    public async Task IdempotencyRecognizesRowsWrittenWithTheLegacyVolatileScope()
    {
        var (attachments, queue) = Stores();
        var original = Provenance("test-app",
        [
            new JobContextReference("discussion", "discussion-1"),
            new JobContextReference("execution", "execution-1"),
        ]);
        var first = await queue.EnqueueAsync(Submission("hello", idempotencyKey: "legacy-key",
            provenance: original));

        await using (var connection = new SqliteConnection($"Data Source={attachments.DatabasePath};Pooling=False"))
        {
            await connection.OpenAsync();
            var command = connection.CreateCommand();
            command.CommandText = "UPDATE SessionQueuedInputs SET ProvenanceScope='legacy-scope', IdempotencyFingerprint='legacy-fingerprint' WHERE Id=$id";
            command.Parameters.AddWithValue("$id", first.Item.Id);
            await command.ExecuteNonQueryAsync();
        }

        var retry = Provenance("test-app",
        [
            new JobContextReference("discussion", "discussion-1"),
            new JobContextReference("execution", "execution-2"),
        ]);
        var replay = await queue.EnqueueAsync(Submission("hello", idempotencyKey: "legacy-key",
            provenance: retry));

        Assert.True(replay.Existing);
        Assert.Equal(first.Item.Id, replay.Item.Id);
    }

    [Fact]
    public async Task ClaimCoalescesOnlyConsecutiveMatchingProvenance()
    {
        var (_, queue) = Stores();
        var first = await queue.EnqueueAsync(Submission("one"));
        var second = await queue.EnqueueAsync(Submission("two"));
        await queue.EnqueueAsync(Submission("three", provenance: Provenance("other-app")));

        var batch = await queue.ClaimBatchAsync("session-a", "worker", TimeSpan.FromMinutes(1));

        Assert.Equal([first.Item.Id, second.Item.Id], batch.Select(item => item.Id));
        Assert.All(batch, item => Assert.Equal(SessionInputQueueState.Delivering, item.State));
    }

    [Fact]
    public async Task ClaimNeverCoalescesAcrossDeliveryPolicies()
    {
        var (_, queue) = Stores();
        var first = await queue.EnqueueAsync(Submission("one"));
        await queue.EnqueueAsync(Submission("steer", policy: SessionInputDeliveryPolicy.SteerCurrent));

        var batch = await queue.ClaimBatchAsync("session-a", "worker", TimeSpan.FromMinutes(1));

        Assert.Equal([first.Item.Id], batch.Select(item => item.Id));
    }

    [Fact]
    public async Task SteerClaimOvertakesWaitingAfterCurrentAndCoalescesOnlyConsecutiveSteers()
    {
        var (_, queue) = Stores();
        var waiting = await queue.EnqueueAsync(Submission("waiting"));
        var firstSteer = await queue.EnqueueAsync(Submission("steer-one", policy: SessionInputDeliveryPolicy.SteerCurrent));
        var secondSteer = await queue.EnqueueAsync(Submission("steer-two", policy: SessionInputDeliveryPolicy.SteerCurrent));
        await queue.EnqueueAsync(Submission("later-waiting"));
        await queue.EnqueueAsync(Submission("later-steer", policy: SessionInputDeliveryPolicy.SteerCurrent));

        var batch = await queue.ClaimSteerBatchAsync("session-a", "worker", TimeSpan.FromMinutes(1));

        Assert.Equal([firstSteer.Item.Id, secondSteer.Item.Id], batch.Select(item => item.Id));
        Assert.Equal(SessionInputQueueState.Pending,
            (await queue.GetAsync("session-a", waiting.Item.Id, "owner-a"))!.State);
    }

    [Fact]
    public async Task SteerPolicySurvivesRestartAndIdempotentReplay()
    {
        var (_, queue) = Stores();
        var first = await queue.EnqueueAsync(Submission("steer", idempotencyKey: "steer-key",
            policy: SessionInputDeliveryPolicy.SteerCurrent));

        var (_, reopened) = Stores();
        var replay = await reopened.EnqueueAsync(Submission("steer", idempotencyKey: "steer-key",
            policy: SessionInputDeliveryPolicy.SteerCurrent));
        var restored = await reopened.GetAsync("session-a", first.Item.Id, "owner-a");

        Assert.True(replay.Existing);
        Assert.Equal(first.Item.Id, replay.Item.Id);
        Assert.Equal(SessionInputDeliveryPolicy.SteerCurrent, restored!.DeliveryPolicy);
    }

    [Fact]
    public async Task CancelReleasesReservedAttachmentToDraft()
    {
        var (attachments, queue) = Stores();
        var attachment = await attachments.UploadBytesAsync("hello"u8.ToArray(), "hello.txt", "text/plain", "owner-a");
        var admitted = await queue.EnqueueAsync(Submission("hello", "owner-a", [attachment.Id]));

        var cancelled = await queue.CancelAsync("session-a", admitted.Item.Id, "owner-a");

        Assert.Equal(SessionInputQueueState.Cancelled, cancelled!.State);
        var draft = await attachments.GetAuthorizedAsync(attachment.Id, "owner-a");
        Assert.Null(draft!.ClaimedSessionId);
        Assert.NotNull(draft.ExpiresAt);
    }

    [Fact]
    public async Task CancellingSessionTerminatesEveryActiveItemAndReleasesAttachments()
    {
        var (attachments, queue) = Stores();
        var attachment = await attachments.UploadBytesAsync("hello"u8.ToArray(), "hello.txt", "text/plain", "owner-a");
        var first = await queue.EnqueueAsync(Submission("one", "owner-a", [attachment.Id]));
        var second = await queue.EnqueueAsync(Submission("two"));

        var cancelled = await queue.CancelSessionAsync("session-a");

        Assert.Equal([first.Item.Id, second.Item.Id], cancelled.Select(item => item.Id));
        Assert.All(cancelled, item => Assert.Equal(SessionInputQueueState.Cancelled, item.State));
        Assert.Empty(await queue.ListAsync("session-a", "owner-a", includeTerminal: false));
        var draft = await attachments.GetAuthorizedAsync(attachment.Id, "owner-a");
        Assert.Null(draft!.ClaimedSessionId);
        Assert.NotNull(draft.ExpiresAt);
    }

    [Fact]
    public async Task ExpiredDeliveryLeaseFailsClosedInsteadOfReplaying()
    {
        var (_, queue) = Stores();
        var admitted = await queue.EnqueueAsync(Submission("hello"));
        await queue.ClaimBatchAsync("session-a", "worker", TimeSpan.FromMilliseconds(-1));

        await queue.ClaimBatchAsync("session-a", "next-worker", TimeSpan.FromMinutes(1));

        var item = await queue.GetAsync("session-a", admitted.Item.Id, "owner-a");
        Assert.Equal(SessionInputQueueState.Failed, item!.State);
        Assert.Equal("delivery_outcome_unknown", item.ErrorCode);
        Assert.True(item.ErrorRetryable);
    }

    // A wall-clock jump (the 2026-10-08 jump to 24 December) can write a retry or lease time
    // months ahead. Nothing this queue schedules is more than 30 minutes out, so such a time is due.
    [Theory]
    [InlineData(5, false)]
    [InlineData(100, false)]
    [InlineData(60 * 24 * 63, true)]
    public async Task Retry_time_beyond_the_schedule_horizon_is_treated_as_due(int minutesAhead, bool due)
    {
        var (attachments, queue) = Stores();
        var admitted = await queue.EnqueueAsync(Submission("hello"));
        await SetAsync(attachments, admitted.Item.Id, "NextAttemptAt", DateTimeOffset.UtcNow.AddMinutes(minutesAhead));

        Assert.Equal(due, (await queue.GetRunnableSessionIdsAsync()).Contains("session-a"));
        var batch = await queue.ClaimBatchAsync("session-a", "worker", TimeSpan.FromMinutes(1));
        Assert.Equal(due ? 1 : 0, batch.Count);
    }

    [Fact]
    public async Task Delivery_lease_written_by_a_forward_clock_jump_is_treated_as_expired()
    {
        var (attachments, queue) = Stores();
        var admitted = await queue.EnqueueAsync(Submission("hello"));
        await queue.ClaimBatchAsync("session-a", "worker", TimeSpan.FromMinutes(1));
        await SetAsync(attachments, admitted.Item.Id, "LeaseExpiresAt", DateTimeOffset.UtcNow.AddDays(63));

        Assert.Equal(1, await queue.RecoverExpiredLeasesAsync());

        var item = await queue.GetAsync("session-a", admitted.Item.Id, "owner-a");
        Assert.Equal(SessionInputQueueState.Failed, item!.State);
        Assert.Equal("delivery_outcome_unknown", item.ErrorCode);
    }

    private static async Task SetAsync(InputAttachmentStore attachments, string itemId, string column, DateTimeOffset value)
    {
        await using var connection = new SqliteConnection($"Data Source={attachments.DatabasePath};Pooling=False");
        await connection.OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = $"UPDATE SessionQueuedInputs SET {column} = $value WHERE Id = $id";
        command.Parameters.AddWithValue("$value", value.ToString("O"));
        command.Parameters.AddWithValue("$id", itemId);
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }

    private static SessionInputQueueSubmission Submission(
        string text,
        string owner = "owner-a",
        IReadOnlyList<string>? attachments = null,
        string? idempotencyKey = null,
        JobProvenance? provenance = null,
        SessionInputDeliveryPolicy policy = SessionInputDeliveryPolicy.AfterCurrent) => new(
            "session-a",
            owner,
            [new QueuedSessionInputPart("text", text), .. (attachments ?? []).Select(id => new QueuedSessionInputPart("attachment", id))],
            text,
            null,
            provenance ?? Provenance("test-app"),
            policy,
            "m_" + Guid.NewGuid().ToString("N"),
            attachments ?? [],
            idempotencyKey);

    private static JobProvenance Provenance(string appId, IReadOnlyList<JobContextReference>? context = null) => new(
        JobProvenance.CurrentSchemaVersion,
        new JobOrigin("redcompute", new JobAppReference("app", appId, null, appId),
            new JobEntrypoint("http", "/test", "POST")),
        new JobActor("user", "Owner", Id: "owner-a"),
        new JobBeneficiary("user", "owner-a", "Owner"),
        context ?? [],
        new JobTrace(),
        JobProvenanceAssurance.Verified,
        DateTimeOffset.UtcNow);

    public void Dispose()
    {
        if (!Directory.Exists(_root)) return;
        foreach (var path in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
            File.SetAttributes(path, FileAttributes.Normal);
        Directory.Delete(_root, recursive: true);
    }
}
