using System.Security.Cryptography;
using RedCompute.App.Services;
using RedCompute.Core.Sessions;
using Xunit;

namespace RedCompute.App.Tests;

public sealed class TranscriptSequenceStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), $"redcompute-transcript-{Guid.NewGuid():N}");

    [Fact]
    public void SequenceIsStrictAndDurableAcrossStoreInstances()
    {
        var path = Path.Combine(_directory, "cursor.db");
        var first = new TranscriptSequenceStore(path);
        var one = first.Next("codex", "session-1");
        var two = first.Next("codex", "session-1");
        var reopened = new TranscriptSequenceStore(path);
        var three = reopened.Next("codex", "session-1");

        Assert.Equal(1, one.Sequence);
        Assert.Equal(2, two.Sequence);
        Assert.Equal(3, three.Sequence);
        Assert.Equal(one.Epoch, three.Epoch);
    }

    [Fact]
    public void Remote_receipts_reuse_the_canonical_record_and_ack_only_persisted_prefixes_after_restart()
    {
        var path = Path.Combine(_directory, "remote.db");
        var binding = new RemoteTranscriptBinding("node-a", "runtime-a", 0);
        var one = RemoteRecord(1, "event-one", "first"u8);
        var two = RemoteRecord(2, "event-two", "second"u8);
        var store = new TranscriptSequenceStore(path);
        var local = store.Next("codex", "session");
        var reserved = store.ReserveRemote("codex", "session", binding, one);
        Assert.Equal(2, reserved.Stamp.Sequence);
        Assert.Equal(local.Epoch, reserved.Stamp.Epoch);
        Assert.False(reserved.Persisted);
        Assert.Equal(0, store.RemotePersistedThrough("codex", "session", binding));
        Assert.Throws<InvalidOperationException>(() => store.ReserveRemote("codex", "session", binding, two));

        // Simulate a process restart after the canonical append was sent, before its outcome was checkpointed.
        var reopened = new TranscriptSequenceStore(path);
        Assert.Equal(reserved, reopened.ReserveRemote("codex", "session", binding, one));
        Assert.Equal(0, reopened.RemotePersistedThrough("codex", "session", binding));
        Assert.Throws<InvalidOperationException>(() => reopened.CompleteRemote("codex", "session", binding, one, local));
        Assert.True(reopened.CompleteRemote("codex", "session", binding, one, reserved.Stamp));
        Assert.False(reopened.CompleteRemote("codex", "session", binding, one, reserved.Stamp));

        var reconnected = new TranscriptSequenceStore(path);
        var retry = reconnected.ReserveRemote("codex", "session", binding, one);
        Assert.True(retry.Persisted);
        Assert.Equal(reserved.Stamp, retry.Stamp);
        Assert.Equal(1, reconnected.RemotePersistedThrough("codex", "session", binding));
        var next = reconnected.ReserveRemote("codex", "session", binding, two);
        Assert.Equal(3, next.Stamp.Sequence);
        Assert.Equal(local.Epoch, next.Stamp.Epoch);
        Assert.Equal(1, reconnected.RemotePersistedThrough("codex", "session", binding));
        Assert.Throws<InvalidOperationException>(() =>
            reconnected.ReserveRemote("codex", "session", binding, one with { EventId = "different" }));
        Assert.Throws<InvalidOperationException>(() =>
            reconnected.ReserveRemote("codex", "session", binding, RemoteRecord(1, "event-one", "changed"u8)));
        Assert.Throws<InvalidOperationException>(() =>
            reconnected.ReserveRemote("codex", "session", binding, two with { EventId = one.EventId }));
    }

    [Fact]
    public void Remote_binding_generation_starts_a_new_source_prefix_in_the_same_canonical_epoch()
    {
        var store = new TranscriptSequenceStore(Path.Combine(_directory, "generation.db"));
        var before = new RemoteTranscriptBinding("node-a", "runtime-a", 0);
        var after = new RemoteTranscriptBinding("node-b", "runtime-b", 1);
        var one = RemoteRecord(1, "event-one", "first"u8);
        var original = store.ReserveRemote("codex", "session", before, one);
        Assert.True(store.CompleteRemote("codex", "session", before, one, original.Stamp));

        var next = store.ReserveRemote("codex", "session", after, one);
        Assert.Equal(original.Stamp.Epoch, next.Stamp.Epoch);
        Assert.Equal(original.Stamp.Sequence + 1, next.Stamp.Sequence);
        Assert.Equal(0, store.RemotePersistedThrough("codex", "session", after));
        Assert.Equal(1, store.RemotePersistedThrough("codex", "session", before));
    }

    private static RemoteTranscriptRecord RemoteRecord(long sequence, string eventId, ReadOnlySpan<byte> payload) =>
        new(sequence, eventId, Convert.ToHexString(SHA256.HashData(payload)));

    [Fact]
    public void SessionsHaveIndependentStableEpochsAndSequences()
    {
        var store = new TranscriptSequenceStore(Path.Combine(_directory, "independent.db"));
        var first = store.Next("codex", "session-1");
        var other = store.Next("codex", "session-2");

        Assert.Equal(1, first.Sequence);
        Assert.Equal(1, other.Sequence);
        Assert.NotEqual(first.Epoch, other.Epoch);
    }

    [Fact]
    public void ConcurrentAllocationNeverDuplicatesOrReordersASequence()
    {
        var store = new TranscriptSequenceStore(Path.Combine(_directory, "concurrent.db"));
        var stamps = Enumerable.Range(0, 64)
            .AsParallel()
            .Select(_ => store.Next("codex", "session-1"))
            .ToArray();

        Assert.Equal(64, stamps.Select(stamp => stamp.Sequence).Distinct().Count());
        Assert.Equal(Enumerable.Range(1, 64).Select(value => (long)value),
            stamps.Select(stamp => stamp.Sequence).Order());
        Assert.Single(stamps.Select(stamp => stamp.Epoch).Distinct());
    }

    [Fact]
    public void SnapshotCursorNeverAdvancesPastReturnedDurableRecords()
    {
        var history = new List<UnifiedMessageRecord>
        {
            Message("epoch", 4),
            Message("epoch", 7),
        };

        var cursor = TranscriptOrdering.Cursor("codex", "session-1", history);

        Assert.Equal("epoch", cursor.Epoch);
        Assert.Equal(7, cursor.ThroughSequence);
    }

    private static UnifiedMessageRecord Message(string epoch, long sequence) => new()
    {
        Id = sequence,
        SessionId = "session-1",
        Role = "assistant",
        EventType = "text",
        Epoch = epoch,
        Sequence = sequence,
        Timestamp = DateTimeOffset.UtcNow,
    };

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
