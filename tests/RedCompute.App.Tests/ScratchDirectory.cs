using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Data.Sqlite;

namespace RedCompute.App.Tests;

/// <summary>
/// Removes a test's scratch directory. The product's SQLite connections are unpooled and closed with their use (proved by
/// 3000 enqueue/read/dispose cycles that never kept <c>input.db</c>), so a handle still on a file when a test ends is held
/// by something outside the test: a virus scanner or indexer that opened the freshly written database. That ends within
/// moments, so the removal waits for it for a bounded time. A handle that does not go away fails the test, and the failure names
/// the processes holding the file (Windows Restart Manager) instead of reporting a bare "in use".
/// </summary>
internal static class ScratchDirectory
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(15);

    public static void Delete(string root, TimeSpan? patience = null)
    {
        SqliteConnection.ClearAllPools();
        if (!Directory.Exists(root)) return;
        var clock = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                // Staged attachments are stored read-only; clear that so the tree can go.
                foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                    File.SetAttributes(file, FileAttributes.Normal);
                Directory.Delete(root, recursive: true);
                return;
            }
            catch (DirectoryNotFoundException)
            {
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (clock.Elapsed < (patience ?? Patience))
                {
                    Thread.Sleep(50);
                    continue;
                }
                throw new IOException($"{ex.Message} Still held after {(int)clock.Elapsed.TotalSeconds} s. Holders: {Holders(root)}", ex);
            }
        }
    }

    private static string Holders(string root)
    {
        if (!OperatingSystem.IsWindows()) return "unknown (not Windows)";
        try
        {
            var files = Directory.Exists(root) ? Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).ToArray() : [];
            if (files.Length == 0) return "no files left";
            var found = new List<string>();
            foreach (var file in files)
            {
                var names = HoldersOf(file);
                if (names.Count > 0) found.Add($"{Path.GetRelativePath(root, file)} <- {string.Join(", ", names)}");
            }
            return found.Count == 0
                ? $"none reported by Restart Manager for {files.Length} file(s) (this process: {Environment.ProcessId})"
                : string.Join("; ", found);
        }
        catch (Exception ex)
        {
            return $"could not be determined ({ex.GetType().Name}: {ex.Message})";
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RmUniqueProcess
    {
        public int ProcessId;
        public System.Runtime.InteropServices.ComTypes.FILETIME ProcessStartTime;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct RmProcessInfo
    {
        public RmUniqueProcess Process;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string AppName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string ServiceShortName;
        public int ApplicationType;
        public uint AppStatus;
        public uint TsSessionId;
        [MarshalAs(UnmanagedType.Bool)] public bool Restartable;
    }

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmStartSession(out uint session, int flags, string key);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmEndSession(uint session);

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmRegisterResources(uint session, uint files, string[] fileNames, uint apps, RmUniqueProcess[]? processes, uint services, string[]? serviceNames);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmGetList(uint session, out uint needed, ref uint count, [In, Out] RmProcessInfo[]? info, out uint reasons);

    private static List<string> HoldersOf(string file)
    {
        var names = new List<string>();
        var key = Guid.NewGuid().ToString("N");
        if (RmStartSession(out var session, 0, key) != 0) return names;
        try
        {
            if (RmRegisterResources(session, 1, [file], 0, null, 0, null) != 0) return names;
            uint count = 0;
            var result = RmGetList(session, out var needed, ref count, null, out _);
            if (result != 234 /* ERROR_MORE_DATA */ || needed == 0) return names;
            var info = new RmProcessInfo[needed];
            count = needed;
            if (RmGetList(session, out _, ref count, info, out _) != 0) return names;
            for (var i = 0; i < count; i++) names.Add($"{info[i].AppName} (pid {info[i].Process.ProcessId})");
        }
        finally
        {
            RmEndSession(session);
        }
        return names;
    }
}
