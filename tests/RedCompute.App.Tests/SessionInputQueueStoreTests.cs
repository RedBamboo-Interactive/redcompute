using RedCompute.App.Services;
using RedCompute.Core.Configuration;
using RedCompute.Core.Jobs;
using Microsoft.Data.Sqlite;
using RedCompute.NodeLink;
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
    public async Task Remote_start_reservation_and_exact_receipt_survive_reopening_the_queue()
    {
        var (_, first) = Stores();
        var start = RemoteLifecycleStart();
        var reserved = await first.ReserveRemoteLifecycleAsync(start);
        Assert.Equal(RemoteLifecycleState.Reserved, reserved.State);
        Assert.Null(reserved.Receipt);

        var (_, reopened) = Stores();
        Assert.Equal(RemoteLifecycleState.Pending, (await reopened.ReserveRemoteLifecycleAsync(start)).State);
        Assert.Equal(RemoteLifecycleState.Conflict,
            (await reopened.ReserveRemoteLifecycleAsync(start with { RequestId = "another-start" })).State);
        Assert.Equal(RemoteLifecycleState.Conflict,
            (await reopened.ReserveRemoteLifecycleAsync(start with { Payload = "changed"u8.ToArray() })).State);
        Assert.Equal(RemoteLifecycleState.Conflict,
            (await reopened.ReserveRemoteLifecycleAsync(start with { Scope = start.Scope with { OwnerId = "other" } })).State);

        var runtimeReceipt = new RemoteLifecycleReceipt(201, """{"runtimeSessionId":"durable","nativeThreadId":"native"}"""u8.ToArray());
        Assert.True(await reopened.CompleteRemoteLifecycleAsync(start, runtimeReceipt));
        Assert.False(await first.CompleteRemoteLifecycleAsync(start, new RemoteLifecycleReceipt(409, [])));

        var (_, restarted) = Stores();
        var replay = await restarted.ReserveRemoteLifecycleAsync(start with { ExpiresAtUnixSeconds = 1 });
        Assert.Equal(RemoteLifecycleState.Completed, replay.State);
        Assert.Equal(runtimeReceipt.Status, replay.Receipt!.Status);
        Assert.Equal(runtimeReceipt.Body, replay.Receipt.Body);
    }

    [Fact]
    public async Task Remote_lifecycle_receipts_cannot_cross_runtime_or_generation()
    {
        var (_, queue) = Stores();
        var start = RemoteLifecycleStart();
        Assert.Equal(RemoteLifecycleState.Reserved, (await queue.ReserveRemoteLifecycleAsync(start)).State);
        Assert.False(await queue.CompleteRemoteLifecycleAsync(start with { Scope = start.Scope with { RuntimeId = "two" } },
            new RemoteLifecycleReceipt(201, [])));
        Assert.Equal(RemoteLifecycleState.Reserved, (await queue.ReserveRemoteLifecycleAsync(
            start with { Scope = start.Scope with { Generation = 1 } })).State);
        Assert.Equal(RemoteLifecycleState.Conflict, (await queue.ReserveRemoteLifecycleAsync(
            start with { Operation = SessionRuntimeOperation.Resume })).State);
        Assert.Equal(RemoteLifecycleState.Reserved, (await queue.ReserveRemoteLifecycleAsync(
            start with { RequestId = "resume", Operation = SessionRuntimeOperation.Resume })).State);
    }

    private static SessionRuntimeGrant RemoteLifecycleStart() =>
        new(new SessionRuntimeScope("node-a", "runtime-a", "logical-a", 0L, "owner-a", "workspace-a"),
            SessionRuntimeOperation.Start, "start", 0L, "{}"u8.ToArray(), []);


    [Fact]
    public async Task Remote_input_replays_the_same_receipt_after_the_queue_row_expires()
    {
        var (_, queue) = Stores();
        var first = RemoteInput("input-one", "hello");
        var second = RemoteInput("input-two", "follow-up");

        var receipt = await queue.EnqueueRemoteInputAsync(first.Grant, first.Submission);
        var next = await queue.EnqueueRemoteInputAsync(second.Grant, second.Submission);
        Assert.Equal("input-one", receipt.TurnId);
        Assert.Equal("input-two", next.TurnId);
        Assert.NotEqual(receipt.QueueItemId, next.QueueItemId);
        Assert.Equal(SessionInputQueueState.Pending,
            (await queue.ReadRemoteInputAsync(first.Grant.Scope, first.Grant.RequestId))!.DeliveryState);

        var batch = await queue.ClaimBatchAsync("session-a", "worker", TimeSpan.FromMinutes(1));
        Assert.Equal(receipt.QueueItemId, Assert.Single(batch).Id);
        Assert.Equal(receipt.TurnId, batch[0].TurnId);
        await queue.MarkDeliveredAsync(batch, receipt.TurnId);
        Assert.Equal(1, await queue.CleanupTerminalAsync(TimeSpan.Zero));

        var (_, reopened) = Stores();
        Assert.Null(await reopened.GetAsync("session-a", receipt.QueueItemId, "owner-a"));
        var replay = await reopened.EnqueueRemoteInputAsync(first.Grant with { ExpiresAtUnixSeconds = 1 }, first.Submission);
        Assert.Equal(receipt, replay);
        var inspected = await reopened.ReadRemoteInputAsync(first.Grant.Scope, first.Grant.RequestId);
        Assert.NotNull(inspected);
        Assert.Equal(receipt, inspected.Receipt);
        Assert.Equal(SessionInputQueueState.Delivered, inspected.DeliveryState);
        Assert.Null(await reopened.ReadRemoteInputAsync(first.Grant.Scope with { OwnerId = "other" }, first.Grant.RequestId));
        Assert.Null(await reopened.ReadRemoteInputAsync(first.Grant.Scope with { Generation = 9 }, first.Grant.RequestId));
        var error = await Assert.ThrowsAsync<SessionInputQueueStoreException>(() => reopened.EnqueueRemoteInputAsync(
            first.Grant with { Payload = "changed"u8.ToArray() }, first.Submission));
        Assert.Equal("idempotency_conflict", error.Code);
        Assert.Equal(next.QueueItemId, Assert.Single(await reopened.ListAsync("session-a", "owner-a", true)).Id);
    }

    [Fact]
    public async Task Persistence_failure_rolls_back_the_remote_queue_item_and_receipt()
    {
        var (attachments, queue) = Stores();
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = attachments.DatabasePath, Pooling = false }.ToString());
        await connection.OpenAsync();
        await using var fault = connection.CreateCommand();
        fault.CommandText = """
            CREATE TRIGGER RejectRemoteInputReceipt BEFORE INSERT ON RemoteSessionInputReceipts
            BEGIN SELECT RAISE(ABORT, 'fixture receipt write failed'); END;
            """;
        await fault.ExecuteNonQueryAsync();

        var input = RemoteInput("input-one", "hello");
        await Assert.ThrowsAsync<SqliteException>(() => queue.EnqueueRemoteInputAsync(input.Grant, input.Submission));
        Assert.Empty(await queue.ListAsync("session-a", "owner-a", true));
        Assert.Null(await queue.ReadRemoteInputAsync(input.Grant.Scope, input.Grant.RequestId));

        fault.CommandText = "DROP TRIGGER RejectRemoteInputReceipt";
        await fault.ExecuteNonQueryAsync();
        var receipt = await queue.EnqueueRemoteInputAsync(input.Grant, input.Submission);
        Assert.Equal(receipt.QueueItemId, Assert.Single(await queue.ListAsync("session-a", "owner-a", true)).Id);
    }

    private static (SessionRuntimeGrant Grant, SessionInputQueueSubmission Submission) RemoteInput(string uid, string text)
    {
        var scope = new SessionRuntimeScope("node-a", "runtime-a", "logical-a", 0, "owner-a", "workspace-a");
        return (new(scope, SessionRuntimeOperation.SendInput, uid, 0, System.Text.Encoding.UTF8.GetBytes(text), []),
            Submission(text) with { MessageUid = uid, IdempotencyKey = uid });
    }


    [Fact]
    public async Task Remote_event_reconnect_replays_ordered_records_and_ack_deduplicates_after_reopening()
    {
        var (_, queue) = Stores();
        var scope = RemoteInput("input-one", "hello").Grant.Scope;
        var one = new RemoteSessionEvent("event-one", "input-one", "input-one", "first"u8.ToArray());
        var two = new RemoteSessionEvent("event-two", "input-one", "input-one", "second"u8.ToArray());
        Assert.Equal(1, (await queue.AppendRemoteEventAsync(scope, one)).Sequence);
        Assert.Equal(2, (await queue.AppendRemoteEventAsync(scope, two)).Sequence);
        Assert.Equal(1, (await queue.AppendRemoteEventAsync(scope, one)).Sequence);

        var (_, reopened) = Stores();
        var firstPage = await reopened.ReadRemoteEventsAsync(scope, 0, 1);
        Assert.False(firstPage.Gap);
        Assert.Equal(0, firstPage.AcknowledgedThrough);
        Assert.Equal(2, firstPage.LastSequence);
        Assert.Equal("event-one", Assert.Single(firstPage.Events).Event.EventId);
        Assert.Equal((long?)1, await reopened.AcknowledgeRemoteEventsAsync(scope, 1));
        Assert.Null(await reopened.AcknowledgeRemoteEventsAsync(scope, 3));
        Assert.Equal((long?)1, await reopened.AcknowledgeRemoteEventsAsync(scope, 0));

        var (_, reconnected) = Stores();
        Assert.True((await reconnected.ReadRemoteEventsAsync(scope, 0, 10)).Gap);
        var replay = await reconnected.ReadRemoteEventsAsync(scope, 1, 10);
        Assert.Equal(1, replay.AcknowledgedThrough);
        Assert.Equal("event-two", Assert.Single(replay.Events).Event.EventId);
        Assert.Equal(1, (await reconnected.AppendRemoteEventAsync(scope, one)).Sequence);
        Assert.Equal(2, (await reconnected.ReadRemoteEventsAsync(scope, 1, 10)).LastSequence);
        var conflict = await Assert.ThrowsAsync<SessionInputQueueStoreException>(() =>
            reconnected.AppendRemoteEventAsync(scope, one with { Body = "changed"u8.ToArray() }));
        Assert.Equal("remote_event_conflict", conflict.Code);
        Assert.Empty((await reconnected.ReadRemoteEventsAsync(scope with { OwnerId = "other" }, 0, 10)).Events);
    }


    [Fact]
    public async Task Remote_event_spool_bounds_pages_and_releases_capacity_only_after_ack()
    {
        var (_, queue) = Stores();
        var scope = RemoteInput("input-one", "hello").Grant.Scope;
        var body = new byte[256 * 1024];
        for (var i = 1; i <= 16; i++)
            Assert.Equal(i, (await queue.AppendRemoteEventAsync(scope, new($"event-{i}", null, null, body))).Sequence);

        var overflow = await Assert.ThrowsAsync<SessionInputQueueStoreException>(() =>
            queue.AppendRemoteEventAsync(scope, new("event-17", null, null, body)));
        Assert.Equal("remote_event_backpressure", overflow.Code);
        var page = await queue.ReadRemoteEventsAsync(scope, 0, 100);
        Assert.Equal(new long[] { 1, 2, 3, 4 }, page.Events.Select(e => e.Sequence));
        Assert.Equal(16, page.LastSequence);
        Assert.Equal((long?)1, await queue.AcknowledgeRemoteEventsAsync(scope, 1));
        Assert.Equal(17, (await queue.AppendRemoteEventAsync(scope, new("event-17", null, null, body))).Sequence);
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

    // Discussion 258167da: four completion events queued for one parent session each carried their
    // own event context, so each was its own scope and they reached the model one turn at a time.
    [Fact]
    public async Task Queued_events_from_one_caller_claim_as_one_batch()
    {
        var (_, queue) = Stores();
        var admitted = new List<string>();
        foreach (var uid in new[] { "event-1", "event-2", "event-3", "event-4", "event-5" })
            admitted.Add((await queue.EnqueueAsync(Event(uid))).Item.Id);

        var batch = await queue.ClaimBatchAsync("session-a", "worker", TimeSpan.FromMinutes(1));

        Assert.Equal(admitted, batch.Select(item => item.Id));
        // The stored provenance still names each event.
        Assert.Equal(["event-1", "event-2", "event-3", "event-4", "event-5"],
            batch.Select(item => item.Provenance.Context.Single(c => c.Kind == "event").Id));
    }

    [Fact]
    public async Task An_event_and_an_ordinary_message_never_share_a_batch()
    {
        var (_, queue) = Stores();
        var first = await queue.EnqueueAsync(Event("event-1"));
        var second = await queue.EnqueueAsync(Event("event-2"));
        var message = await queue.EnqueueAsync(Message("message-1"));
        var third = await queue.EnqueueAsync(Event("event-3"));

        var events = await queue.ClaimBatchAsync("session-a", "worker", TimeSpan.FromMinutes(1));
        Assert.Equal([first.Item.Id, second.Item.Id], events.Select(item => item.Id));
        await queue.MarkDeliveredAsync(events, events[0].MessageUid);

        var messages = await queue.ClaimBatchAsync("session-a", "worker", TimeSpan.FromMinutes(1));
        Assert.Equal([message.Item.Id], messages.Select(item => item.Id));
        await queue.MarkDeliveredAsync(messages, messages[0].MessageUid);

        var later = await queue.ClaimBatchAsync("session-a", "worker", TimeSpan.FromMinutes(1));
        Assert.Equal([third.Item.Id], later.Select(item => item.Id));
    }

    [Fact]
    public async Task Distinct_events_admit_separately_and_a_keyed_retry_dedupes()
    {
        var (_, queue) = Stores();
        var first = await queue.EnqueueAsync(Event("event-1", idempotent: true));
        var second = await queue.EnqueueAsync(Event("event-2", idempotent: true));
        Assert.False(first.Existing); Assert.False(second.Existing);
        Assert.NotEqual(first.Item.Id, second.Item.Id);

        // A redelivery arrives with a fresh signed execution and the same event identity.
        var retry = await queue.EnqueueAsync(Event("event-1", idempotent: true));
        Assert.True(retry.Existing);
        Assert.Equal(first.Item.Id, retry.Item.Id);

        // Unkeyed events never dedupe, even with identical text.
        var unkeyedA = await queue.EnqueueAsync(Event("event-3") with { DisplayContent = "same" });
        var unkeyedB = await queue.EnqueueAsync(Event("event-4") with { DisplayContent = "same" });
        Assert.False(unkeyedB.Existing);
        Assert.NotEqual(unkeyedA.Item.Id, unkeyedB.Item.Id);
        Assert.Equal(4, (await queue.ListAsync("session-a", "owner-a", includeTerminal: false)).Count);
    }

    // An event admitted by a build whose scope still hashed the event ID, retried after the upgrade.
    [Fact]
    public async Task Event_admitted_under_the_old_scope_dedupes_its_retry_after_the_change()
    {
        var (attachments, queue) = Stores();
        var first = await queue.EnqueueAsync(Event("event-1", idempotent: true));
        await using (var connection = new SqliteConnection($"Data Source={attachments.DatabasePath};Pooling=False"))
        {
            await connection.OpenAsync();
            var command = connection.CreateCommand();
            command.CommandText = "UPDATE SessionQueuedInputs SET ProvenanceScope='scope-with-event-id', IdempotencyFingerprint='old-fingerprint' WHERE Id=$id";
            command.Parameters.AddWithValue("$id", first.Item.Id);
            await command.ExecuteNonQueryAsync();
        }

        var retry = await queue.EnqueueAsync(Event("event-1", idempotent: true));

        Assert.True(retry.Existing);
        Assert.Equal(first.Item.Id, retry.Item.Id);
        Assert.Single(await queue.ListAsync("session-a", "owner-a", includeTerminal: false));
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

    // The context Nova's EventInjector sends for a discussion event, as stored on live queue rows.
    private static SessionInputQueueSubmission Event(string uid, bool idempotent = false) =>
        Submission(uid, idempotencyKey: idempotent ? $"nova-event:{uid}" : null, provenance: Provenance("nova",
        [
            new JobContextReference("discussion", "discussion-1"),
            new JobContextReference("session", "session-a"),
            new JobContextReference("event", uid),
            new JobContextReference("origin-entrypoint", Route: "/api/apps/nova/discussions/discussion-1/event"),
            new JobContextReference("execution", Guid.NewGuid().ToString()),
        ])) with { MessageUid = uid };

    private static SessionInputQueueSubmission Message(string uid) =>
        Submission(uid, idempotencyKey: $"q-{uid}", provenance: Provenance("nova",
        [
            new JobContextReference("discussion", "discussion-1"),
            new JobContextReference("session", "session-a"),
            new JobContextReference("origin-entrypoint", Route: "/api/apps/nova/discussions/discussion-1/message"),
            new JobContextReference("execution", Guid.NewGuid().ToString()),
            new JobContextReference("parent-execution", Guid.NewGuid().ToString()),
        ])) with { MessageUid = uid };

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
