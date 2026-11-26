using System.Runtime.InteropServices;
using System.Text;

namespace RedCompute.Core.Storage;

/// <summary>
/// The one way RedCompute replaces a file it must find intact after a power cut. The new bytes go to a temporary file
/// beside the target, reach the disk (write-through and a flush), and only then are renamed over the target; on Linux
/// the folder is flushed too, so the rename itself survives. A power cut therefore leaves the old file or the new one,
/// never the zero-filled file NTFS and ext4 leave after a rename whose data was still in the cache (N23: a torn
/// <c>long-lived-processes.json</c> rolled a data move back).
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
    /// Flushes a folder's entries to the disk (Linux). Windows makes the rename itself durable, so there is nothing to do
    /// there. Best effort: a file system that refuses is reported to <see cref="DiagnosticLog"/> and left alone.
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
