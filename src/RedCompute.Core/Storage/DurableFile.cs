using System.Runtime.InteropServices;
using System.Text;

namespace RedCompute.Core.Storage;

/// <summary>
/// The one way RedCompute replaces a file it must find intact after a power cut. The new bytes go to a temporary file
/// beside the target, reach the disk (write-through and a flush), and only then are renamed over the target; on Linux
/// the folder is flushed too. A power cut therefore never leaves the zero-filled file NTFS and ext4 leave after a rename
/// whose data was still in the cache (N23: a torn <c>long-lived-processes.json</c> rolled a data move back). Windows
/// does not promise that the rename itself reached the disk (RedLeaf N29: a marker's rename was lost on a real power
/// cut), so a cut can also leave the old file, or, for a first write, no file and the whole temporary beside it, which
/// a reader at start takes back with <see cref="TryRecover"/>.
/// </summary>
public static class DurableFile
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>Where a folder flush that failed is reported. Never throws; null: not reported.</summary>
    public static Action<string>? DiagnosticLog { get; set; }

    /// <summary>Replaces <paramref name="path"/> with <paramref name="contents"/> (UTF-8, no BOM), durably.</summary>
    public static void WriteAllText(string path, string contents, UnixFileMode? unixMode = null)
        => WriteAllBytes(path, Utf8.GetBytes(contents), unixMode);

    /// <summary>
    /// Replaces <paramref name="path"/> with <paramref name="bytes"/>, durably. <paramref name="unixMode"/> (Linux) is the mode
    /// the file is created with, so a secret is never readable by others even for a moment. Each call writes a temporary
    /// file of its own, so two writers at once each publish a whole file and the last rename wins. A failed write leaves no
    /// temporary file behind and the target as it was.
    /// </summary>
    public static void WriteAllBytes(string path, ReadOnlySpan<byte> bytes, UnixFileMode? unixMode = null)
    {
        var full = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(full)!;
        Directory.CreateDirectory(directory);
        var temp = TempPathFor(full);
        try
        {
            var options = new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                Options = FileOptions.WriteThrough,
            };
            if (unixMode is { } mode && !OperatingSystem.IsWindows()) options.UnixCreateMode = mode;
            using (var stream = new FileStream(temp, options))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            Publish(temp, full);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
        SyncDirectory(directory);
    }

    /// <summary>
    /// Publishes a file another writer produced (a database backup, say) as <paramref name="path"/>: its bytes are flushed to
    /// the disk first, then it is renamed over the target and the folder is flushed. <paramref name="temporary"/> is gone
    /// afterwards; on failure it is left for the caller to clean up.
    /// </summary>
    public static void Commit(string temporary, string path)
    {
        var full = Path.GetFullPath(path);
        using (var stream = new FileStream(temporary, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            stream.Flush(flushToDisk: true);
        Publish(temporary, full);
        SyncDirectory(Path.GetDirectoryName(full)!);
    }

    /// <summary>
    /// Moves a file that does not read out of the way, as <c>&lt;name&gt;.unreadable-&lt;UTC time&gt;</c> beside it, before
    /// anything saves over it: a bad read never destroys the only copy. Each call keeps its own copy. Returns where the
    /// file went, or null when there was no file; throws (<see cref="IOException"/>, <see cref="UnauthorizedAccessException"/>)
    /// when it could not be moved.
    /// </summary>
    public static string? SetAside(string path, DateTimeOffset now)
    {
        var full = Path.GetFullPath(path);
        if (!File.Exists(full)) return null;
        var stamp = now.UtcDateTime.ToString("yyyyMMdd'T'HHmmssfff'Z'", System.Globalization.CultureInfo.InvariantCulture);
        var aside = $"{full}.unreadable-{stamp}";
        for (var n = 2; File.Exists(aside); n++) aside = $"{full}.unreadable-{stamp}-{n}";
        File.Move(full, aside, overwrite: false);
        SyncDirectory(Path.GetDirectoryName(full)!);
        return aside;
    }

    /// <summary>
    /// <see cref="SetAside"/>, described for a log line: where the file went, or why it stays where it is (the next save
    /// then replaces it). Never throws for a file that cannot be moved.
    /// </summary>
    public static string SetAsideAndDescribe(string path, DateTimeOffset now)
    {
        try
        {
            var aside = SetAside(path, now);
            return aside is null ? "there was no file to keep" : $"it was set aside as {aside}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"it could not be set aside ({ex.Message}), so the next save replaces it";
        }
    }

    /// <summary>
    /// Flushes a folder's entries to the disk (Linux). Windows has no equivalent for a folder, and nothing there promises
    /// that a rename reached the disk: <see cref="TryRecover"/> covers the rename a cut lost. Best effort: a file system
    /// that refuses is reported to <see cref="DiagnosticLog"/> and left alone.
    /// </summary>
    public static void SyncDirectory(string directory)
    {
        if (OperatingSystem.IsWindows()) return;
        try
        {
            // O_RDONLY (0): a folder opens read-only without other flags. O_DIRECTORY differs between x86-64 and arm64.
            var fd = NativeOpen(directory, 0);
            if (fd < 0)
            {
                DiagnosticLog?.Invoke($"Could not open {directory} to flush it (error {Marshal.GetLastWin32Error()}); a power cut now could lose the last rename.");
                return;
            }
            try
            {
                if (NativeFsync(fd) != 0)
                    DiagnosticLog?.Invoke($"Flushing {directory} failed (error {Marshal.GetLastWin32Error()}); a power cut now could lose the last rename.");
            }
            finally { _ = NativeClose(fd); }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            DiagnosticLog?.Invoke($"Flushing {directory} is not possible here: {ex.Message}");
        }
    }

    /// <summary>
    /// For a reader at start, while this process is the only writer of <paramref name="path"/>: when the file is missing
    /// but a temporary of this writer is beside it (<c>&lt;name&gt;.&lt;32 hex&gt;.tmp</c>), the power cut lost the rename
    /// that would have published it. The newest temporary that is not torn and that <paramref name="isWhole"/> accepts
    /// (the caller's own parse) is published as the file. A present file is never replaced, and every other temporary is
    /// left exactly as it is. Never call it where another writer may still own a temporary: a temporary is complete only
    /// once its writer flushed it. Returns what was recovered, for the log, or null when nothing was. Never throws for a
    /// file that cannot be read or moved.
    /// </summary>
    public static string? TryRecover(string path, Func<byte[], bool> isWhole)
    {
        var full = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(full)!;
        var name = Path.GetFileName(full);
        List<FileInfo> temporaries;
        try
        {
            if (File.Exists(full) || !Directory.Exists(directory)) return null;
            temporaries = new DirectoryInfo(directory).EnumerateFiles(name + ".*.tmp")
                .Where(file => IsTemporaryOf(name, file.Name))
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }

        foreach (var temporary in temporaries)
        {
            byte[] bytes;
            try { bytes = File.ReadAllBytes(temporary.FullName); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            if (bytes.Length == 0 || !bytes.AsSpan().ContainsAnyExcept((byte)0)) continue;
            bool whole;
            // The caller's parse decides; a parse that throws has said no.
            try { whole = isWhole(bytes); }
            catch (Exception) { whole = false; }
            if (!whole) continue;
            try
            {
                File.Move(temporary.FullName, full, overwrite: false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null;
            }
            SyncDirectory(directory);
            var recovered = $"Recovered {full} from {temporary.Name}: a power cut lost the rename that would have put it in place.";
            DiagnosticLog?.Invoke(recovered);
            return recovered;
        }
        return null;
    }

    private static bool IsTemporaryOf(string name, string candidate)
    {
        const int Hex = 32;
        if (candidate.Length != name.Length + 1 + Hex + 4) return false;
        if (!candidate.StartsWith(name + ".", StringComparison.Ordinal) || !candidate.EndsWith(".tmp", StringComparison.Ordinal)) return false;
        foreach (var c in candidate.AsSpan(name.Length + 1, Hex))
            if (!char.IsAsciiHexDigitLower(c)) return false;
        return true;
    }

    private static string TempPathFor(string full) => $"{full}.{Guid.NewGuid():N}.tmp";

    // Windows refuses a replace while another writer's replace of the same name is in flight: a short retry, then the error.
    private static void Publish(string temp, string full)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                File.Move(temp, full, overwrite: true);
                return;
            }
            catch (Exception ex) when (attempt < 8 && ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(10 * (attempt + 1));
            }
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int NativeOpen([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

    [DllImport("libc", EntryPoint = "fsync", SetLastError = true)]
    private static extern int NativeFsync(int fd);

    [DllImport("libc", EntryPoint = "close", SetLastError = true)]
    private static extern int NativeClose(int fd);
}
