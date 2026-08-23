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
