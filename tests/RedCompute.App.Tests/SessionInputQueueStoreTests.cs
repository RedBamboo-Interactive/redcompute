using RedCompute.App.Services;
using RedCompute.Core.Configuration;
using RedCompute.Core.Jobs;
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

    private static SessionInputQueueSubmission Submission(
        string text,
        string owner = "owner-a",
        IReadOnlyList<string>? attachments = null,
        string? idempotencyKey = null,
        JobProvenance? provenance = null) => new(
            "session-a",
            owner,
            [new QueuedSessionInputPart("text", text), .. (attachments ?? []).Select(id => new QueuedSessionInputPart("attachment", id))],
            text,
            null,
            provenance ?? Provenance("test-app"),
            SessionInputDeliveryPolicy.AfterCurrent,
            "m_" + Guid.NewGuid().ToString("N"),
            attachments ?? [],
            idempotencyKey);

    private static JobProvenance Provenance(string appId) => new(
        JobProvenance.CurrentSchemaVersion,
        new JobOrigin("redcompute", new JobAppReference("app", appId, null, appId),
            new JobEntrypoint("http", "/test", "POST")),
        new JobActor("user", "Owner", Id: "owner-a"),
        new JobBeneficiary("user", "owner-a", "Owner"),
        [],
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
