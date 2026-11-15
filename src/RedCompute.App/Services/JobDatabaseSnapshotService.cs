using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using RedCompute.Core.Storage;

namespace RedCompute.App.Services;

/// <summary>What a finished snapshot is, as the endpoint reports it.</summary>
public sealed record JobDatabaseSnapshotResult(
    string Path, long SizeBytes, string Sha256, long PageCount, long PageSize,
    double DurationSeconds, string QuickCheck, DateTimeOffset CreatedAt);

/// <summary>The snapshot failed; <see cref="QuickCheck"/> is set when the copy was made but did not verify.</summary>
public sealed class JobDatabaseSnapshotException(string message, string? quickCheck = null, Exception? inner = null)
    : Exception(message, inner)
{
    public string? QuickCheck { get; } = quickCheck;
}

/// <summary>
/// A consistent copy of the job database (<c>redcompute.db</c>) while RedCompute keeps running, for the
/// RedLeaf off-site backup. SQLite's online backup API reads one snapshot of the source: the database
/// runs in WAL mode, so that read never blocks a writer (it only holds the checkpoint back, and the WAL
/// grows meanwhile). The copy keeps the source's page layout, so a block-deduplicating backup stores only
/// the pages that changed since the last night (<c>VACUUM INTO</c> would rewrite them all).
/// <para>Every path resolves through <see cref="ComputeStorage"/>. The snapshot is one fixed file in
/// <see cref="SnapshotDirectory"/>, written under a temporary name and renamed once verified, so the fixed
/// name is always either absent or a complete, checked copy. One at a time.</para>
/// </summary>
public sealed class JobDatabaseSnapshotService
{
    public const string SnapshotFileName = "redcompute-snapshot.db";
    private const string TempPrefix = SnapshotFileName + ".tmp-";

    private readonly ComputeStorage _storage;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public JobDatabaseSnapshotService(ComputeStorage storage) => _storage = storage;

    /// <summary><c>backups\</c> under the data root: RedCompute's own folder for files it hands to a backup.</summary>
    public string SnapshotDirectory => Path.Combine(_storage.DataRoot, "backups");

    public string SnapshotPath => Path.Combine(SnapshotDirectory, SnapshotFileName);

    public bool IsRunning => _gate.CurrentCount == 0;

    /// <summary>Test seam: runs on the snapshot thread after the copy, before verification.</summary>
    internal Action<string>? AfterCopy { get; set; }

    /// <summary>Null when a snapshot is already running; otherwise the finished snapshot (or throws).</summary>
    public async Task<JobDatabaseSnapshotResult?> TryCreateAsync()
    {
        if (!await _gate.WaitAsync(0).ConfigureAwait(false)) return null;
        try
        {
            var completion = new TaskCompletionSource<JobDatabaseSnapshotResult>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            // A dedicated below-normal thread: RedCompute's request handling keeps the CPU. Background, so
            // a shutdown never waits for a 12 GB copy (the next snapshot clears the temp file it leaves).
            var thread = new Thread(() =>
            {
                try { completion.SetResult(Create()); }
                catch (Exception ex) { completion.SetException(ex); }
            })
            {
                IsBackground = true,
                Priority = ThreadPriority.BelowNormal,
                Name = "job-database-snapshot",
            };
            thread.Start();
            return await completion.Task.ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    /// <summary>Removes the snapshot. Null when one is running (it would be deleted under the writer).</summary>
    public bool? TryDelete()
    {
        if (!_gate.Wait(0)) return null;
        try
        {
            var existed = File.Exists(SnapshotPath);
            DeleteDatabaseFiles(SnapshotPath);
            return existed;
        }
        finally { _gate.Release(); }
    }

    private JobDatabaseSnapshotResult Create()
    {
        var clock = Stopwatch.StartNew();
        var source = _storage.Database;
        if (!File.Exists(source))
            throw new JobDatabaseSnapshotException($"The job database does not exist at {source}.");

        Directory.CreateDirectory(SnapshotDirectory);
        // We hold the gate, so any temp file here is a leftover of a killed run.
        foreach (var stale in Directory.EnumerateFiles(SnapshotDirectory, TempPrefix + "*"))
            TryDeleteFile(stale);

        var temp = Path.Combine(SnapshotDirectory, TempPrefix + Guid.NewGuid().ToString("N"));
        try
        {
            long pageCount, pageSize;
            string quickCheck;

            using (var from = new SqliteConnection(
                new SqliteConnectionStringBuilder
                {
                    DataSource = source, Mode = SqliteOpenMode.ReadOnly, Pooling = false, DefaultTimeout = 60,
                }.ToString()))
            using (var to = new SqliteConnection(
                new SqliteConnectionStringBuilder
                {
                    DataSource = temp, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false, DefaultTimeout = 60,
                }.ToString()))
            {
                from.Open();
                to.Open();
                // One backup step over the whole file = one read snapshot: consistent whatever the writers do.
                // Stepping in chunks would restart every time another connection commits, and on a database
                // that is written all day it would never finish.
                from.BackupDatabase(to);

                AfterCopy?.Invoke(temp);

                // One file, no side files: leave WAL mode in the copy (a header flag, one page).
                Scalar(to, "PRAGMA journal_mode=DELETE");
                quickCheck = Scalar(to, "PRAGMA quick_check") ?? "";
                pageCount = long.Parse(Scalar(to, "PRAGMA page_count") ?? "0");
                pageSize = long.Parse(Scalar(to, "PRAGMA page_size") ?? "0");
            }

            if (!string.Equals(quickCheck, "ok", StringComparison.Ordinal))
                throw new JobDatabaseSnapshotException(
                    "The snapshot did not pass PRAGMA quick_check, so it was discarded.", quickCheck);

            DeleteSideFiles(temp);
            var size = new FileInfo(temp).Length;
            var sha = Hash(temp);

            File.Move(temp, SnapshotPath, overwrite: true);
            DeleteSideFiles(SnapshotPath);
            return new JobDatabaseSnapshotResult(
                SnapshotPath, size, sha, pageCount, pageSize,
                Math.Round(clock.Elapsed.TotalSeconds, 2), quickCheck, DateTimeOffset.UtcNow);
        }
        catch (JobDatabaseSnapshotException) { throw; }
        catch (Exception ex)
        {
            throw new JobDatabaseSnapshotException($"The snapshot failed: {ex.Message}", inner: ex);
        }
        finally
        {
            // Success moved the file; on any failure nothing of the attempt stays behind.
            DeleteDatabaseFiles(temp);
        }
    }

    private static string? Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string Hash(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            1 << 20, FileOptions.SequentialScan);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static void DeleteDatabaseFiles(string path)
    {
        TryDeleteFile(path);
        DeleteSideFiles(path);
    }

    private static void DeleteSideFiles(string path)
    {
        foreach (var suffix in new[] { "-wal", "-shm", "-journal" }) TryDeleteFile(path + suffix);
    }

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
