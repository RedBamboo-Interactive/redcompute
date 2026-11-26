using System.Text;
using RedCompute.App.Rebase;
using RedCompute.Core.Sessions;
using RedCompute.Core.Storage;
using Xunit;

namespace RedCompute.App.Tests;

/// <summary>
/// RedLeaf N29 (LeafLab, 2026-11-26): on Windows nothing promises that a rename reached the disk. A power cut can keep a
/// written-through temporary and lose the rename that would have published it. For a first write that leaves no file and
/// the whole temporary beside it; a reader at start takes it back (<see cref="DurableFile.TryRecover"/>), and anything less
/// than whole is left exactly as it is.
/// </summary>
[Collection(nameof(PlannedRestartCheckpointCollection))]
public sealed class DurableRecoveryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rc-recovery", Guid.NewGuid().ToString("N"));

    public DurableRecoveryTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private static string TemporaryOf(string path) => $"{path}.{Guid.NewGuid():N}.tmp";

    /// <summary>A temporary the writer flushed, whose rename the cut lost.</summary>
    private static string LeaveTemporary(string path, byte[] bytes, DateTime? written = null)
    {
        var temporary = TemporaryOf(path);
        File.WriteAllBytes(temporary, bytes);
        if (written is { } at) File.SetLastWriteTimeUtc(temporary, at);
        return temporary;
    }

    private static readonly byte[] Whole = Encoding.UTF8.GetBytes("{\"whole\":true}");
    private static readonly byte[] Torn = new byte[512];

    private static bool ReadsWhole(byte[] bytes) => Encoding.UTF8.GetString(bytes).EndsWith('}');

    // ------------------------------------------------------------------ the recovery itself

    [Fact]
    public void A_first_write_whose_rename_was_lost_is_taken_back_from_its_temporary()
    {
        var path = Path.Combine(_root, "state.json");
        var temporary = LeaveTemporary(path, Whole);

        var recovered = DurableFile.TryRecover(path, ReadsWhole);

        Assert.NotNull(recovered);
        Assert.Contains(Path.GetFileName(temporary), recovered);
        Assert.Equal(Whole, File.ReadAllBytes(path));
        Assert.False(File.Exists(temporary));
    }

    [Fact]
    public void A_torn_temporary_is_left_as_it_is_and_nothing_is_published()
    {
        var path = Path.Combine(_root, "state.json");
        var temporary = LeaveTemporary(path, Torn);

        Assert.Null(DurableFile.TryRecover(path, _ => true));

        Assert.False(File.Exists(path));
        Assert.Equal(Torn, File.ReadAllBytes(temporary));
    }

    [Fact]
    public void A_temporary_the_caller_does_not_accept_is_left_as_it_is()
    {
        var path = Path.Combine(_root, "state.json");
        var half = Encoding.UTF8.GetBytes("{\"whole\":tr");
        var temporary = LeaveTemporary(path, half);

        Assert.Null(DurableFile.TryRecover(path, ReadsWhole));
        Assert.Null(DurableFile.TryRecover(path, _ => throw new FormatException("not mine")));

        Assert.False(File.Exists(path));
        Assert.Equal(half, File.ReadAllBytes(temporary));
    }

    [Fact]
    public void The_newest_whole_temporary_wins_and_the_others_are_left()
    {
        var path = Path.Combine(_root, "state.json");
        var now = DateTime.UtcNow;
        var older = LeaveTemporary(path, Encoding.UTF8.GetBytes("{\"n\":1}"), now.AddMinutes(-2));
        var whole = LeaveTemporary(path, Encoding.UTF8.GetBytes("{\"n\":2}"), now.AddMinutes(-1));
        var newestTorn = LeaveTemporary(path, Torn, now);

        Assert.NotNull(DurableFile.TryRecover(path, ReadsWhole));

        Assert.Equal("{\"n\":2}", File.ReadAllText(path));
        Assert.False(File.Exists(whole));
        Assert.True(File.Exists(older));
        Assert.Equal(Torn, File.ReadAllBytes(newestTorn));
    }

    [Fact]
    public void A_file_that_is_there_is_never_replaced()
    {
        // A lost rename over an existing file leaves the previous version: stale, not missing, and the caller's to judge.
        var path = Path.Combine(_root, "state.json");
        File.WriteAllText(path, "{\"previous\":true}");
        var temporary = LeaveTemporary(path, Whole);

        Assert.Null(DurableFile.TryRecover(path, ReadsWhole));

        Assert.Equal("{\"previous\":true}", File.ReadAllText(path));
        Assert.True(File.Exists(temporary));
    }

    [Fact]
    public void Only_this_files_own_temporaries_are_considered()
    {
        var path = Path.Combine(_root, "state.json");
        var other = TemporaryOf(Path.Combine(_root, "state.json.bak"));
        File.WriteAllBytes(other, Whole);
        var notOurs = path + ".backup.tmp";
        File.WriteAllBytes(notOurs, Whole);
        var upper = $"{path}.{Guid.NewGuid().ToString("N").ToUpperInvariant()}.tmp";
        File.WriteAllBytes(upper, Whole);

        Assert.Null(DurableFile.TryRecover(path, ReadsWhole));

        Assert.False(File.Exists(path));
        Assert.True(File.Exists(other) && File.Exists(notOurs) && File.Exists(upper));
    }

    [Fact]
    public void A_missing_folder_is_nothing_to_recover()
        => Assert.Null(DurableFile.TryRecover(Path.Combine(_root, "absent", "state.json"), _ => true));

    // ------------------------------------------------------------------ the readers at start

    [Fact]
    public void A_planned_restart_checkpoint_whose_rename_was_lost_still_continues_its_sessions()
    {
        // Every checkpoint is a first write (the previous one was consumed). Without recovery the planned restart would read as
        // an unplanned one: the drained sessions' jobs failed as orphans, and an idle-stopped session told to continue.
        var path = Path.Combine(_root, "planned-restart.json");
        var written = Path.Combine(_root, "written.json");
        var checkpoint = new PlannedRestartData("run-n29", DateTimeOffset.UtcNow,
            [new PlannedRestartSession("claude-code", "session-1", Guid.NewGuid())]);
        PlannedRestartCheckpoint.Write(checkpoint, written);
        var temporary = TemporaryOf(path);
        File.Move(written, temporary);
        var log = new List<string>();
        try
        {
            var loaded = PlannedRestartCheckpoint.LoadForStartup(path, DateTimeOffset.UtcNow, log: log.Add);

            Assert.NotNull(loaded);
            Assert.Equal("run-n29", loaded.RunId);
            Assert.Equal("session-1", Assert.Single(loaded.Sessions).SessionId);
            Assert.Contains(log, line => line.Contains("Recovered", StringComparison.Ordinal) && line.Contains(Path.GetFileName(temporary), StringComparison.Ordinal));
        }
        finally
        {
            PlannedRestartCheckpoint.Consume(path);
        }
    }

    [Fact]
    public void A_torn_checkpoint_temporary_continues_no_session_and_is_kept()
    {
        var path = Path.Combine(_root, "planned-restart.json");
        var temporary = LeaveTemporary(path, Torn);
        try
        {
            Assert.Null(PlannedRestartCheckpoint.LoadForStartup(path, DateTimeOffset.UtcNow));
            Assert.Equal(Torn, File.ReadAllBytes(temporary));
        }
        finally
        {
            PlannedRestartCheckpoint.Consume(path);
        }
    }

    [Fact]
    public void A_first_rebase_record_whose_rename_was_lost_is_not_run_again()
    {
        var storage = new ComputeStorage(_root, Path.Combine(_root, "suite"), isRelocated: true);
        var statePath = Path.Combine(storage.DataRoot, StoredPathRebaseRunner.StateFileName);
        Directory.CreateDirectory(storage.DataRoot);
        var temporary = LeaveTemporary(statePath, Encoding.UTF8.GetBytes("{\"generation\":3,\"state\":\"completed\",\"owners\":[]}"));
        var log = new List<string>();

        var runner = new StoredPathRebaseRunner(storage, [], log.Add);

        Assert.Equal(3L, runner.Status.Generation);
        Assert.Equal(RebaseStates.Completed, runner.Status.State);
        Assert.False(File.Exists(temporary));
        Assert.Contains(log, line => line.StartsWith("[Rebase] Recovered", StringComparison.Ordinal));
        runner.Run(new RebaseMap(3, []));
        Assert.Contains(log, line => line.Contains("Generation 3 already completed", StringComparison.Ordinal));
    }
}
