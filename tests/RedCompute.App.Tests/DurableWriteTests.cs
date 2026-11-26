using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using RedCompute.App.Rebase;
using RedCompute.App.Services;
using RedCompute.App.Services.Placement;
using RedCompute.Core.Sessions;
using RedCompute.Core.Storage;
using Xunit;

namespace RedCompute.App.Tests;

/// <summary>
/// N23: a write that replaces a state file reaches the disk before the rename that publishes it, so a power cut leaves
/// the old file or the new one and never a zero-filled one (seen on a real power cut: <c>long-lived-processes.json</c>
/// full of NUL bytes rolled a data move back). A file that does not read is set aside before anything saves over it:
/// a bad read never destroys the only copy. Only a store whose empty state is safe (the long-lived registry, the
/// shadow counts, a restart checkpoint) then starts empty; the move's own stores fail loudly.
/// </summary>
[Collection(nameof(PlannedRestartCheckpointCollection))]
public sealed class DurableWriteTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rc-durable", Guid.NewGuid().ToString("N"));

    public DurableWriteTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>What NTFS or ext4 leave after a rename whose data never reached the disk.</summary>
    private static readonly byte[] Torn = new byte[512];

    private static string[] SetAsideCopies(string path)
        => Directory.GetFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + ".unreadable-*");

    // ------------------------------------------------------------------ every replace goes through the durable writer

    /// <summary>
    /// The source of every RedCompute project: a temporary file moved over a state file is the durable writer's job, so no
    /// other file may replace one with <c>File.Move(..., overwrite: true)</c> or <c>File.Replace</c>. The exceptions are named
    /// with their reason.
    /// </summary>
    [Fact]
    public void Only_the_durable_writer_replaces_a_file()
    {
        var allowed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["src/RedCompute.Core/Storage/DurableFile.cs"] = "the durable writer itself",
            // NodeLink takes no dependency on Core; its two seq markers are written durably in place.
            ["src/RedCompute.NodeLink/Spool/SegmentFileSpool.cs"] = "durable markers, written in place",
            // Rotation renames finished logs; it writes no data.
            ["src/RedCompute.Node/NodeLog.cs"] = "log rotation, no data written",
        };
        var replace = new Regex(@"File\.Move\([^;]*overwrite:\s*true|File\.Replace\(", RegexOptions.Compiled);
        var root = RepoRoot();
        var hits = new[] { "src", "plugins" }
            .SelectMany(folder => Directory.EnumerateFiles(Path.Combine(root, folder), "*.cs", SearchOption.AllDirectories))
            .Select(file => Path.GetRelativePath(root, file).Replace('\\', '/'))
            .Where(file => !file.Contains("/obj/") && !file.Contains("/bin/") && !allowed.ContainsKey(file))
            .SelectMany(file => File.ReadLines(Path.Combine(root, file)).Select((line, index) => (file, line, number: index + 1)))
            .Where(hit => replace.IsMatch(hit.line))
            .Select(hit => $"  {hit.file}:{hit.number}: {hit.line.Trim()}")
            .ToList();

        Assert.True(hits.Count == 0, "Replace through RedCompute.Core.Storage.DurableFile instead:\n" + string.Join("\n", hits));
    }

    // ------------------------------------------------------------------ the durable writer

    private string[] Temporaries() => Directory.GetFiles(_root, "*.tmp", SearchOption.AllDirectories);

    [Fact]
    public void The_writer_replaces_a_file_whole_and_leaves_no_temporary()
    {
        var path = Path.Combine(_root, "nested", "state.json");

        DurableFile.WriteAllText(path, "first");
        DurableFile.WriteAllText(path, "second, longer than the first");

        Assert.Equal("second, longer than the first", File.ReadAllText(path));
        // UTF-8 without a byte-order mark, as File.WriteAllText writes it.
        Assert.Equal((byte)'s', File.ReadAllBytes(path)[0]);
        Assert.Empty(Temporaries());
    }

    [Fact]
    public void A_write_that_cannot_publish_leaves_the_target_and_no_temporary()
    {
        // A folder where the file should go: the rename can never succeed.
        var path = Path.Combine(_root, "taken");
        Directory.CreateDirectory(path);

        var error = Assert.ThrowsAny<Exception>(() => DurableFile.WriteAllText(path, "lost"));
        Assert.True(error is IOException or UnauthorizedAccessException, error.ToString());
        Assert.True(Directory.Exists(path));
        Assert.Empty(Temporaries());
    }

    [Fact]
    public void Writers_at_once_each_publish_a_whole_file()
    {
        var path = Path.Combine(_root, "contended.json");
        var payloads = Enumerable.Range(0, 6).Select(n => new string((char)('a' + n), 4096 * (n + 1))).ToArray();

        Parallel.For(0, payloads.Length, new ParallelOptions { MaxDegreeOfParallelism = payloads.Length }, n =>
        {
            for (var i = 0; i < 10; i++) DurableFile.WriteAllText(path, payloads[n]);
        });

        Assert.Contains(File.ReadAllText(path), payloads);
        Assert.Empty(Temporaries());
    }

    [Fact]
    public void Commit_publishes_a_file_another_writer_produced()
    {
        var path = Path.Combine(_root, "snapshot.db");
        File.WriteAllText(path, "old");
        var temporary = Path.Combine(_root, "snapshot.db.partial");
        File.WriteAllText(temporary, "new");

        DurableFile.Commit(temporary, path);

        Assert.Equal("new", File.ReadAllText(path));
        Assert.False(File.Exists(temporary));
    }

    [Fact]
    public void Setting_aside_twice_in_the_same_instant_keeps_both_copies()
    {
        var path = Path.Combine(_root, "state.json");
        var now = DateTimeOffset.UtcNow;
        File.WriteAllText(path, "one");
        var first = DurableFile.SetAside(path, now);
        File.WriteAllText(path, "two");
        var second = DurableFile.SetAside(path, now);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.NotEqual(first, second);
        Assert.Equal(["one", "two"], new[] { first!, second! }.Select(File.ReadAllText));
        Assert.False(File.Exists(path));
        Assert.Null(DurableFile.SetAside(path, now));
    }

    [Fact]
    public void A_secret_is_never_readable_by_others_on_linux()
    {
        if (OperatingSystem.IsWindows()) return; // The mode is a Linux concept; Windows inherits the folder's ACL.
        var path = Path.Combine(_root, "key.p8");

        DurableFile.WriteAllBytes(path, [1, 2, 3], UnixFileMode.UserRead | UnixFileMode.UserWrite);

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
    }

    // ------------------------------------------------------------------ a store that reads a bad file keeps it

    [Fact]
    public void The_long_lived_registry_sets_an_unreadable_file_aside_and_starts_empty()
    {
        var statePath = Path.Combine(_root, "long-lived-processes.json");
        File.WriteAllBytes(statePath, Torn);
        var log = new List<string>();

        using (var registry = new LongLivedProcessRegistry(_root, log.Add, monitor: false))
            Assert.Empty(registry.List());

        var aside = Assert.Single(SetAsideCopies(statePath));
        Assert.Equal(Torn, File.ReadAllBytes(aside));
        Assert.Contains(log, line => line.Contains(aside, StringComparison.Ordinal));
    }

    [Fact]
    public void A_restart_checkpoint_that_does_not_read_is_set_aside_and_no_session_is_continued()
    {
        var path = Path.Combine(_root, "planned-restart.json");
        File.WriteAllBytes(path, Torn);

        Assert.Null(PlannedRestartCheckpoint.LoadForStartup(path, DateTimeOffset.UtcNow));

        var aside = Assert.Single(SetAsideCopies(path));
        Assert.Equal(Torn, File.ReadAllBytes(aside));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void Two_unreadable_shadow_files_in_a_row_are_both_kept()
    {
        var store = new PlacementShadowStore(Path.Combine(_root, PlacementShadowStore.FileName));
        File.WriteAllBytes(store.Path, Torn);
        Assert.Null(store.Load((_, _) => { }));
        File.WriteAllText(store.Path, "{ not json");
        Assert.Null(store.Load((_, _) => { }));

        var copies = SetAsideCopies(store.Path).Select(File.ReadAllBytes).ToList();
        Assert.Equal(2, copies.Count);
        Assert.Contains(copies, bytes => bytes.SequenceEqual(Torn));
    }

    [Fact]
    public void An_unreadable_rebase_state_is_set_aside_and_named_in_the_log()
    {
        var storage = new ComputeStorage(_root, Path.Combine(_root, "suite"), isRelocated: true);
        var statePath = Path.Combine(storage.DataRoot, StoredPathRebaseRunner.StateFileName);
        File.WriteAllBytes(statePath, Torn);
        var log = new List<string>();

        var runner = new StoredPathRebaseRunner(storage, [], log.Add);

        Assert.Equal(RebaseStates.Completed, runner.Status.State);
        var aside = Assert.Single(SetAsideCopies(statePath));
        Assert.Equal(Torn, File.ReadAllBytes(aside));
        Assert.Contains(log, line => line.Contains(aside, StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------ the move: tolerant only where empty is safe

    [Fact]
    public void A_torn_long_lived_registry_does_not_fail_the_move()
    {
        var storage = new ComputeStorage(_root, Path.Combine(_root, "suite"), isRelocated: true);
        Directory.CreateDirectory(storage.LongLivedProcessesDirectory);
        var statePath = Path.Combine(storage.LongLivedProcessesDirectory, "long-lived-processes.json");
        File.WriteAllBytes(statePath, Torn);

        var result = new LongLivedProcessesRebaser(storage).Rebase(Map(storage));

        Assert.Equal((0, 0, 1), (result.Examined, result.Changed, result.Skipped));
        // Left exactly as it was: the registry sets it aside when it opens.
        Assert.Equal(Torn, File.ReadAllBytes(statePath));
    }

    [Fact]
    public void A_torn_config_still_fails_the_move_and_is_left_as_it_was()
    {
        var storage = new ComputeStorage(_root, Path.Combine(_root, "suite"), isRelocated: true);
        Directory.CreateDirectory(Path.GetDirectoryName(storage.ConfigFile)!);
        File.WriteAllBytes(storage.ConfigFile, Torn);

        Assert.ThrowsAny<Exception>(() => new ConfigRebaser(storage).Rebase(Map(storage)));
        Assert.Equal(Torn, File.ReadAllBytes(storage.ConfigFile));
    }

    private RebaseMap Map(ComputeStorage storage)
        => new(1, [new RebaseEntry(Path.Combine(_root, "old"), storage.DataRoot)]);

    private static string RepoRoot([CallerFilePath] string file = "")
    {
        var directory = new DirectoryInfo(Path.GetDirectoryName(file)!);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "RedCompute.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("RedCompute.sln not found above " + file);
    }
}
