using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace RedCompute.PluginSdk;

/// <summary>
/// Makes a session's whole process tree die with the session. A PPID walk (<see
/// cref="Process.Kill(bool)"/>, the harness's TaskStop) cannot find a process whose launching shell
/// already exited: <c>nohup x &amp;</c>, <c>(x &amp;)</c>, <c>start /b</c> and <c>Start-Process</c>
/// from a foreground tool call all return at once and leave the server parented to a dead PID
/// (proven 2026-11-12, memory/artifacts/7e8aa955). So every session root goes into its own Windows
/// Job Object, which every descendant inherits however it detaches, with kill-on-close so a
/// RedCompute crash takes the tree down too.
/// <para>
/// Some processes start outside every job anyway: Windows activates a packaged app (the Store
/// PowerShell 7 that the agent's PowerShell tool runs) outside its caller's job, and everything it
/// starts follows it out. So the tree also tracks descendants by parentage. Every member and every
/// tracked descendant has its handle held from first sighting until its exit has been examined, so
/// its PID cannot be reused in between: a live process whose parent PID is a tracked process and
/// which was created after it is provably its child. Scans run every 100 ms for a few seconds after
/// any job activity (a tool call starts a member), then every 2 s. These escapees are written to a
/// ledger for a restart and reaped with the tree.
/// </para>
/// <para>
/// The root's exit is the end of the session, whatever caused it: stop, park, forced interrupt,
/// rollback, the CLI exiting on its own. A server meant to outlive its session must be started
/// through RedCompute's long-lived process registry, outside every session job; <see
/// cref="IsProtected"/> lets that registry also shield an adopted escapee.
/// </para>
/// </summary>
public static class SessionProcessContainment
{
    private static readonly ConcurrentDictionary<nuint, ContainedProcessTree> Trees = new();
    private static readonly object PortGate = new();
    private static SafeFileHandle? _port;
    private static Thread? _scanner;
    private static long _nextKey;
    private static long _ticks;
    private static int _scanning;
    private static readonly TimeSpan ScanInterval = TimeSpan.FromMilliseconds(100);
    private const int ColdScanEveryTicks = 20;

    /// <summary>Diagnostics sink; RedCompute points it at its log.</summary>
    public static Action<string>? Log { get; set; }

    /// <summary>
    /// True when (pid, creation time UTC ticks) is a registered long-lived process that a reap
    /// must leave alone.
    /// </summary>
    public static Func<int, long, bool>? IsProtected { get; set; }

    /// <summary>Where escapee ledgers survive a RedCompute restart.</summary>
    public static string LedgerDirectory { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RedCompute", "session-processes");

    /// <summary>
    /// <see cref="Process.Start(ProcessStartInfo)"/>, then contain the new process. Containment
    /// failing never fails the launch: the session runs exactly as it did before this existed.
    /// </summary>
    public static Process? Start(ProcessStartInfo startInfo, string owner)
    {
        var process = Process.Start(startInfo);
        if (process is not null) Contain(process, owner);
        return process;
    }

    /// <summary>Contain an already started root. Null off Windows or when the OS refuses.</summary>
    public static ContainedProcessTree? Contain(Process root, string owner)
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            return ContainCore(root, owner, keepLedger: null);
        }
        catch (Exception ex)
        {
            Log?.Invoke($"[Containment] Could not contain {owner} PID {SafeId(root)}: {ex.Message}");
            return null;
        }
    }

    /// <summary>True when a live session job holds this process.</summary>
    public static bool IsSessionMember(int pid)
        => OperatingSystem.IsWindows() && Trees.Values.Any(tree => !tree.IsKept && tree.Contains(pid));

    /// <summary>The live contained tree whose root is this PID, if any.</summary>
    public static ContainedProcessTree? FindByRoot(int rootPid)
        => Trees.Values.FirstOrDefault(tree => tree.RootPid == rootPid && !tree.IsReaped);

    /// <summary>
    /// Boot: reap the escapees a previous RedCompute recorded. Its jobs closed with it, so their
    /// members are already gone; only processes matching a recorded identity exactly are killed.
    /// </summary>
    public static int SweepLedgers()
    {
        if (!OperatingSystem.IsWindows() || !Directory.Exists(LedgerDirectory)) return 0;
        var killed = 0;
        foreach (var file in Directory.EnumerateFiles(LedgerDirectory, "*.json"))
        {
            try
            {
                var ledger = JsonSerializer.Deserialize<EscapeeLedger>(File.ReadAllText(file));
                if (ledger is not null && ledger.WriterPid == Writer.Pid && ledger.WriterCreatedUtcTicks == Writer.CreatedUtcTicks)
                    continue;
                if (ledger is not null)
                    foreach (var escapee in ledger.Escapees)
                        if (KillIdentity(escapee.Pid, escapee.CreatedUtcTicks, ledger.Owner)) killed++;
                File.Delete(file);
            }
            catch (Exception ex)
            {
                Log?.Invoke($"[Containment] Ledger {Path.GetFileName(file)} unreadable: {ex.Message}");
            }
        }
        if (killed > 0) Log?.Invoke($"[Containment] Boot sweep reaped {killed} escaped process tree(s)");
        return killed;
    }

    /// <summary>The process creation time as UTC ticks, the identity the reaper and registry share.</summary>
    public static long CreationUtcTicks(Process process)
    {
        try
        {
            return OperatingSystem.IsWindows()
                ? Native.CreationTicks(process.SafeHandle)
                : process.StartTime.ToUniversalTime().Ticks;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// Keep a deliberately long-lived tree: its own job without kill-on-close, tracked by parentage
    /// like a session, but never reaped when its root exits (a launcher that returns at once is
    /// normal). Every live identity is written to <paramref name="ledgerPath"/>, so a later
    /// RedCompute can <see cref="Resume"/> it. It ends only through <see cref="ContainedProcessTree.Reap"/>.
    /// </summary>
    public static ContainedProcessTree? Keep(Process root, string owner, string ledgerPath)
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            return ContainCore(root, owner, ledgerPath);
        }
        catch (Exception ex)
        {
            Log?.Invoke($"[Containment] Could not keep {owner} PID {SafeId(root)}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Resume a kept tree from its ledger after a restart: only processes whose PID and creation
    /// time both still match are taken back. Null when none of them is alive.
    /// </summary>
    public static ContainedProcessTree? Resume(string owner, string ledgerPath)
    {
        if (!OperatingSystem.IsWindows() || !File.Exists(ledgerPath)) return null;
        try
        {
            var ledger = JsonSerializer.Deserialize<EscapeeLedger>(File.ReadAllText(ledgerPath));
            return ledger is null ? null : ResumeCore(owner, ledgerPath, ledger.Escapees.Select(e => (e.Pid, e.CreatedUtcTicks)));
        }
        catch (Exception ex)
        {
            Log?.Invoke($"[Containment] Could not resume {owner} from {Path.GetFileName(ledgerPath)}: {ex.Message}");
            return null;
        }
    }

    /// <summary>Resume a kept tree from known identities (a record written before ledgers existed).</summary>
    public static ContainedProcessTree? Resume(string owner, string ledgerPath, IEnumerable<(int Pid, long CreatedUtcTicks)> identities)
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            return ResumeCore(owner, ledgerPath, identities);
        }
        catch (Exception ex)
        {
            Log?.Invoke($"[Containment] Could not resume {owner}: {ex.Message}");
            return null;
        }
    }

    [SupportedOSPlatform("windows")]
    private static ContainedProcessTree? ResumeCore(string owner, string ledgerPath, IEnumerable<(int Pid, long CreatedUtcTicks)> identities)
    {
        var alive = new List<(int Pid, IntPtr Handle, long Created)>();
        foreach (var (pid, created) in identities)
        {
            var handle = Native.OpenForIdentity(pid);
            if (handle == IntPtr.Zero) continue;
            if (!Native.HasExited(handle) && Native.Times(handle).Created == created) alive.Add((pid, handle, created));
            else Native.Close(handle);
        }
        if (alive.Count == 0)
        {
            try { File.Delete(ledgerPath); } catch { }
            return null;
        }

        var port = EnsurePort();
        var job = Native.CreateJobObject(IntPtr.Zero, null);
        if (job.IsInvalid) throw new Win32Exception();
        var key = (nuint)Interlocked.Increment(ref _nextKey);
        var tree = new ContainedProcessTree(job, key, owner, alive[0].Pid, alive[0].Created, ledgerPath);
        Native.AssociatePort(job, port, key);
        Trees[key] = tree;
        foreach (var (pid, handle, created) in alive)
        {
            var member = false;
            try
            {
                using var process = Process.GetProcessById(pid);
                member = Native.AssignProcessToJobObject(job, process.SafeHandle);
            }
            catch { }
            tree.Seed(pid, handle, created, member);
        }
        tree.Scan(Native.Snapshot());
        Log?.Invoke($"[Containment] Resumed {owner}: {alive.Count} live process(es)");
        return tree;
    }

    [SupportedOSPlatform("windows")]
    private static ContainedProcessTree ContainCore(Process root, string owner, string? keepLedger)
    {
        var port = EnsurePort();
        var job = Native.CreateJobObject(IntPtr.Zero, null);
        if (job.IsInvalid) throw new Win32Exception();
        var key = (nuint)Interlocked.Increment(ref _nextKey);
        var tree = new ContainedProcessTree(job, key, owner, root.Id, Native.CreationTicks(root.SafeHandle), keepLedger);
        try
        {
            // A session dies with RedCompute; a kept server must not.
            if (keepLedger is null) Native.SetKillOnClose(job);
            Native.AssociatePort(job, port, key);
            Trees[key] = tree;
            // A kept process may sit in a job that refuses nesting; parentage still tracks it.
            if (!Native.AssignProcessToJobObject(job, root.SafeHandle) && keepLedger is null) throw new Win32Exception();
            // A child the root spawned before assignment is not a member; adopt it by ancestry.
            tree.AdoptPreAssignmentChildren();
        }
        catch
        {
            Trees.TryRemove(key, out _);
            tree.Abandon();
            throw;
        }
        return tree;
    }

    [SupportedOSPlatform("windows")]
    private static SafeFileHandle EnsurePort()
    {
        lock (PortGate)
        {
            if (_port is not null) return _port;
            var port = Native.CreateIoCompletionPort(new IntPtr(-1), IntPtr.Zero, UIntPtr.Zero, 1);
            if (port.IsInvalid) throw new Win32Exception();
            var thread = new Thread(() => Pump(port)) { IsBackground = true, Name = "Session process containment" };
            thread.Start();
            // Its own thread, not the thread pool: a starved pool would delay scans past the life of
            // a short-lived packaged pwsh, and its children would then be unprovable.
            _scanner = new Thread(() =>
            {
                while (true)
                {
                    Thread.Sleep(ScanInterval);
                    ScanTick();
                }
            }) { IsBackground = true, Name = "Session process scan" };
            _scanner.Start();
            _port = port;
            return port;
        }
    }

    [SupportedOSPlatform("windows")]
    private static void Pump(SafeFileHandle port)
    {
        while (true)
        {
            if (!Native.GetQueuedCompletionStatus(port, out var message, out var key, out var overlapped, uint.MaxValue))
            {
                if (port.IsClosed) return;
                continue;
            }
            if (!Trees.TryGetValue(key, out var tree)) continue;
            var pid = unchecked((int)overlapped.ToInt64());
            try
            {
                switch (message)
                {
                    case Native.MsgNewProcess: tree.OnMemberAdded(pid); break;
                    case Native.MsgExitProcess:
                    case Native.MsgAbnormalExitProcess:
                        tree.OnMemberExited(pid);
                        break;
                    case Native.MsgActiveProcessZero:
                        if (tree.IsReaped && Trees.TryRemove(key, out _)) tree.Abandon();
                        break;
                }
            }
            catch (Exception ex)
            {
                Log?.Invoke($"[Containment] {tree.Owner} PID {tree.RootPid}: {ex.Message}");
            }
        }
    }

    [SupportedOSPlatform("windows")]
    private static void ScanTick()
    {
        if (Interlocked.Exchange(ref _scanning, 1) == 1) return;
        try
        {
            var tick = Interlocked.Increment(ref _ticks);
            var due = Trees.Values.Where(tree => !tree.IsReaped && (tree.IsHot || tick % ColdScanEveryTicks == 0)).ToArray();
            if (due.Length == 0) return;
            var snapshot = Native.Snapshot();
            foreach (var tree in due)
            {
                try { tree.Scan(snapshot); }
                catch (Exception ex) { Log?.Invoke($"[Containment] {tree.Owner} PID {tree.RootPid} scan: {ex.Message}"); }
            }
        }
        finally
        {
            Volatile.Write(ref _scanning, 0);
        }
    }

    internal static void Forget(ContainedProcessTree tree) => Trees.TryRemove(tree.Key, out _);

    /// <summary>Kill a process tree only when PID and creation time both still match.</summary>
    internal static bool KillIdentity(int pid, long createdUtcTicks, string owner, bool ignoreProtection = false)
    {
        if (!ignoreProtection && IsProtected?.Invoke(pid, createdUtcTicks) == true)
        {
            Log?.Invoke($"[Containment] Leaving PID {pid} ({owner}): registered long-lived process");
            return false;
        }
        try
        {
            using var process = Process.GetProcessById(pid);
            if (Native.CreationTicks(process.SafeHandle) != createdUtcTicks) return false;
            var name = process.ProcessName;
            process.Kill(entireProcessTree: true);
            Log?.Invoke($"[Containment] Reaped escaped {name} PID {pid} of {owner}");
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string SafeId(Process process)
    {
        try { return process.Id.ToString(); } catch { return "?"; }
    }

    internal sealed record EscapeeIdentity(int Pid, long CreatedUtcTicks);

    internal sealed record EscapeeLedger(string Owner, int RootPid, long RootCreatedUtcTicks, List<EscapeeIdentity> Escapees,
        int WriterPid = 0, long WriterCreatedUtcTicks = 0);

    /// <summary>This RedCompute process, so a sweep never touches the ledgers of its own live trees.</summary>
    internal static (int Pid, long CreatedUtcTicks) Writer { get; } = CurrentWriter();

    private static (int, long) CurrentWriter()
    {
        using var current = Process.GetCurrentProcess();
        return (current.Id, CreationUtcTicks(current));
    }

    [SupportedOSPlatform("windows")]
    internal static class Native
    {
        // JOB_OBJECT_MSG_* completion-port message ids.
        public const uint MsgActiveProcessZero = 4;
        public const uint MsgNewProcess = 6;
        public const uint MsgExitProcess = 7;
        public const uint MsgAbnormalExitProcess = 8;
        private const uint QueryLimitedInformation = 0x1000;
        private const uint Synchronize = 0x00100000;
        private const uint KillOnJobClose = 0x2000;

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern SafeFileHandle CreateJobObject(IntPtr attributes, string? name);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool AssignProcessToJobObject(SafeFileHandle job, SafeProcessHandle process);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool TerminateJobObject(SafeFileHandle job, uint exitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetInformationJobObject(SafeFileHandle job, int infoClass, IntPtr info, uint length);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool IsProcessInJob(IntPtr process, SafeFileHandle job, out bool result);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern SafeFileHandle CreateIoCompletionPort(IntPtr file, IntPtr existingPort, UIntPtr key, uint threads);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool GetQueuedCompletionStatus(SafeFileHandle port, out uint bytes, out nuint key, out IntPtr overlapped, uint milliseconds);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetProcessTimes(IntPtr process, out long creation, out long exit, out long kernel, out long user);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool Process32FirstW(IntPtr snapshot, ref ProcessEntry entry);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool Process32NextW(IntPtr snapshot, ref ProcessEntry entry);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct ProcessEntry
        {
            public uint Size;
            public uint Usage;
            public uint ProcessId;
            public IntPtr DefaultHeapId;
            public uint ModuleId;
            public uint Threads;
            public uint ParentProcessId;
            public int PriorityClassBase;
            public uint Flags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string ExeFile;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BasicLimit
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize;
            public UIntPtr MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ExtendedLimit
        {
            public BasicLimit Basic;
            public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
            public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
            public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct AssociateCompletionPort
        {
            public nuint CompletionKey;
            public IntPtr CompletionPort;
        }

        public static void SetKillOnClose(SafeFileHandle job) => SetLimits(job, KillOnJobClose);

        public static void SetLimits(SafeFileHandle job, uint flags)
        {
            var info = new ExtendedLimit { Basic = new BasicLimit { LimitFlags = flags } };
            SetInfo(job, 9, info);
        }

        public static void AssociatePort(SafeFileHandle job, SafeFileHandle port, nuint key)
            => SetInfo(job, 7, new AssociateCompletionPort { CompletionKey = key, CompletionPort = port.DangerousGetHandle() });

        private static void SetInfo<T>(SafeFileHandle job, int infoClass, T info) where T : struct
        {
            var length = Marshal.SizeOf<T>();
            var buffer = Marshal.AllocHGlobal(length);
            try
            {
                Marshal.StructureToPtr(info, buffer, false);
                if (!SetInformationJobObject(job, infoClass, buffer, (uint)length)) throw new Win32Exception();
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        /// <summary>A query-and-wait handle; holding it keeps the PID from being reused.</summary>
        public static IntPtr OpenForIdentity(int pid) => OpenProcess(QueryLimitedInformation | Synchronize, false, pid);

        public static void Close(IntPtr handle)
        {
            if (handle != IntPtr.Zero) CloseHandle(handle);
        }

        public static (long Created, long Exited) Times(IntPtr process)
            => GetProcessTimes(process, out var created, out var exited, out _, out _)
                ? (FileTimeToUtcTicks(created), exited == 0 ? 0 : FileTimeToUtcTicks(exited))
                : (0, 0);

        public static long CreationTicks(SafeProcessHandle process)
            => GetProcessTimes(process.DangerousGetHandle(), out var created, out _, out _, out _)
                ? FileTimeToUtcTicks(created)
                : 0;

        public static bool InJob(IntPtr process, SafeFileHandle job)
            => IsProcessInJob(process, job, out var result) && result;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

        public static bool HasExited(IntPtr process) => WaitForSingleObject(process, 0) == 0;

        private static long FileTimeToUtcTicks(long fileTime) => DateTime.FromFileTimeUtc(fileTime).Ticks;

        /// <summary>(pid, parent pid) of every process now.</summary>
        public static List<(int Pid, int ParentPid)> Snapshot()
        {
            var result = new List<(int, int)>();
            var snapshot = CreateToolhelp32Snapshot(0x2, 0);
            if (snapshot == new IntPtr(-1)) return result;
            try
            {
                var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>() };
                if (!Process32FirstW(snapshot, ref entry)) return result;
                do result.Add(((int)entry.ProcessId, (int)entry.ParentProcessId));
                while (Process32NextW(snapshot, ref entry));
            }
            finally
            {
                CloseHandle(snapshot);
            }
            return result;
        }
    }
}

/// <summary>
/// One session root's job and everything proven to descend from it: job members and the
/// descendants that started outside the job. Reaped when the root exits.
/// </summary>
public sealed class ContainedProcessTree
{
    private static readonly long HotTicks = TimeSpan.FromSeconds(5).Ticks;
    private readonly object _gate = new();
    private readonly SafeFileHandle _job;
    private readonly Dictionary<int, Tracked> _tracked = new();
    private readonly Dictionary<int, long> _escapees = new();
    private readonly string? _keepLedger;
    private long _lastActivity = DateTime.UtcNow.Ticks;
    private string _persisted = "";
    private bool _reaped;

    internal ContainedProcessTree(SafeFileHandle job, nuint key, string owner, int rootPid, long rootCreatedUtcTicks,
        string? keepLedger = null)
    {
        _job = job;
        _keepLedger = keepLedger;
        Key = key;
        Owner = owner;
        RootPid = rootPid;
        RootCreatedUtcTicks = rootCreatedUtcTicks;
    }

    internal nuint Key { get; }
    public string Owner { get; }
    public int RootPid { get; }
    public long RootCreatedUtcTicks { get; }

    public bool IsReaped
    {
        get { lock (_gate) return _reaped; }
    }

    /// <summary>A registered long-lived tree, not a session: never reaped when its root exits.</summary>
    public bool IsKept => _keepLedger is not null;

    /// <summary>Every tracked process still running (pid, creation UTC ticks).</summary>
    public IReadOnlyList<(int Pid, long CreatedUtcTicks)> LiveProcesses
    {
        get
        {
            if (!OperatingSystem.IsWindows()) return [];
            lock (_gate)
                return _tracked.Where(pair => !SessionProcessContainment.Native.HasExited(pair.Value.Handle))
                    .Select(pair => (pair.Key, pair.Value.Created)).ToArray();
        }
    }

    /// <summary>True when this exact identity is, or was until its exit was examined, part of the tree.</summary>
    public bool Tracks(int pid, long createdUtcTicks)
    {
        lock (_gate) return !_reaped && _tracked.TryGetValue(pid, out var tracked) && tracked.Created == createdUtcTicks;
    }

    /// <summary>Recently active: a member started or ended within the last few seconds.</summary>
    internal bool IsHot => DateTime.UtcNow.Ticks - Interlocked.Read(ref _lastActivity) < HotTicks;

    /// <summary>Raised once, after the root exited and the tree was reaped.</summary>
    public event Action<ContainedProcessTree>? Reaped;

    /// <summary>Live descendants outside the job, tracked by parentage (pid, creation UTC ticks).</summary>
    public IReadOnlyList<(int Pid, long CreatedUtcTicks)> Escapees
    {
        get { lock (_gate) return _escapees.Select(pair => (pair.Key, pair.Value)).ToArray(); }
    }

    internal bool Contains(int pid)
    {
        lock (_gate) return !_reaped && _tracked.TryGetValue(pid, out var tracked) && tracked.Member;
    }

    private string LedgerPath => _keepLedger ?? Path.Combine(SessionProcessContainment.LedgerDirectory,
        $"{Sanitize(Owner)}-{RootPid}-{RootCreatedUtcTicks}.json");

    /// <summary>Take back a live process after a restart, its handle already open.</summary>
    internal void Seed(int pid, IntPtr handle, long created, bool member)
    {
        lock (_gate)
        {
            if (_reaped || !_tracked.TryAdd(pid, new Tracked(handle, created, member)))
            {
                if (OperatingSystem.IsWindows()) SessionProcessContainment.Native.Close(handle);
                return;
            }
            if (!member) _escapees[pid] = created;
        }
    }

    private void Touch() => Interlocked.Exchange(ref _lastActivity, DateTime.UtcNow.Ticks);

    /// <summary>
    /// Track the root at once (its NEW_PROCESS notification is asynchronous), and the children it
    /// may have started before it was assigned.
    /// </summary>
    [SupportedOSPlatform("windows")]
    internal void AdoptPreAssignmentChildren()
    {
        OnMemberAdded(RootPid);
        Scan(SessionProcessContainment.Native.Snapshot());
    }

    [SupportedOSPlatform("windows")]
    internal void OnMemberAdded(int pid)
    {
        Touch();
        lock (_gate)
        {
            if (_reaped) return;
            if (_tracked.TryGetValue(pid, out var known))
            {
                if (!known.Member) _tracked[pid] = known with { Member = true };
                _escapees.Remove(pid);
                return;
            }
        }
        var handle = SessionProcessContainment.Native.OpenForIdentity(pid);
        if (handle == IntPtr.Zero) return;
        var (created, _) = SessionProcessContainment.Native.Times(handle);
        lock (_gate)
        {
            if (_reaped || !_tracked.TryAdd(pid, new Tracked(handle, created, Member: true)))
            {
                SessionProcessContainment.Native.Close(handle);
                return;
            }
        }
        if (IsKept) PersistLedger();
    }

    [SupportedOSPlatform("windows")]
    internal void OnMemberExited(int pid)
    {
        Touch();
        if (pid == RootPid && !IsKept)
        {
            Reap();
            return;
        }
        // Examine its children while its handle still reserves the PID; the scan then releases it.
        Scan(SessionProcessContainment.Native.Snapshot());
    }

    /// <summary>
    /// Track every live process whose parent is tracked and which was created after that parent,
    /// then release processes that had already exited before the snapshot: their children, if
    /// any are alive, are in it.
    /// </summary>
    [SupportedOSPlatform("windows")]
    internal void Scan(IReadOnlyList<(int Pid, int ParentPid)> snapshot)
    {
        var changed = false;
        lock (_gate)
        {
            if (_reaped) return;
            var exitedBeforeSnapshot = _tracked.Where(pair => SessionProcessContainment.Native.HasExited(pair.Value.Handle))
                .Select(pair => pair.Key).ToHashSet();
            // A fresh snapshot after the exit check, so an exited parent's children are all in it.
            if (exitedBeforeSnapshot.Count > 0) snapshot = SessionProcessContainment.Native.Snapshot();

            bool grew;
            do
            {
                grew = false;
                foreach (var (pid, parentPid) in snapshot)
                {
                    if (pid == parentPid || _tracked.ContainsKey(pid) || !_tracked.TryGetValue(parentPid, out var parent)) continue;
                    var handle = SessionProcessContainment.Native.OpenForIdentity(pid);
                    if (handle == IntPtr.Zero) continue;
                    var (created, _) = SessionProcessContainment.Native.Times(handle);
                    // The parent's handle is held, so its PID is its own: a later-created process
                    // naming it as parent is its child.
                    if (created < parent.Created)
                    {
                        SessionProcessContainment.Native.Close(handle);
                        continue;
                    }
                    var member = SessionProcessContainment.Native.InJob(handle, _job);
                    _tracked[pid] = new Tracked(handle, created, member);
                    if (!member) _escapees[pid] = created;
                    changed |= !member || IsKept;
                    grew = true;
                }
            } while (grew);

            foreach (var pid in exitedBeforeSnapshot)
            {
                // A session's root is released by its reap; a kept tree's root is just another process.
                if ((pid == RootPid && !IsKept) || !_tracked.Remove(pid, out var gone)) continue;
                SessionProcessContainment.Native.Close(gone.Handle);
                changed |= _escapees.Remove(pid) || IsKept;
            }
        }
        if (changed) PersistLedger();
    }

    /// <summary>
    /// Kill every member and every tracked escapee, leaving registered long-lived processes.
    /// Idempotent; runs on the root's exit.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public void Reap()
    {
        Scan(SessionProcessContainment.Native.Snapshot());
        (int Pid, long Created)[] escapees;
        (int Pid, long Created)[] unjoined;
        int members;
        lock (_gate)
        {
            if (_reaped) return;
            _reaped = true;
            members = _tracked.Count(pair => pair.Value.Member && pair.Key != RootPid
                && !SessionProcessContainment.Native.HasExited(pair.Value.Handle));
            escapees = _escapees.Select(pair => (pair.Key, pair.Value)).ToArray();
            // A kept process whose assignment the OS refused is a "member" outside the job.
            unjoined = _tracked.Where(pair => pair.Value.Member && !SessionProcessContainment.Native.InJob(pair.Value.Handle, _job))
                .Select(pair => (pair.Key, pair.Value.Created)).ToArray();
        }

        SessionProcessContainment.Native.TerminateJobObject(_job, 1);
        var killed = escapees.Count(escapee =>
            SessionProcessContainment.KillIdentity(escapee.Pid, escapee.Created, $"{Owner} root {RootPid}", ignoreProtection: IsKept));
        foreach (var (pid, created) in unjoined)
            SessionProcessContainment.KillIdentity(pid, created, $"{Owner} root {RootPid}", ignoreProtection: IsKept);
        try { File.Delete(LedgerPath); } catch { }
        ReleaseHandles();

        if (members > 0 || escapees.Length > 0)
            SessionProcessContainment.Log?.Invoke(
                $"[Containment] {Owner} root {RootPid} ended: reaped {members} job member(s) and {killed} of {escapees.Length} escaped process(es)");
        Reaped?.Invoke(this);
    }

    /// <summary>Release everything once the job is empty, or when containment never took.</summary>
    internal void Abandon()
    {
        ReleaseHandles();
        _job.Dispose();
        SessionProcessContainment.Forget(this);
    }

    private void ReleaseHandles()
    {
        Tracked[] tracked;
        lock (_gate)
        {
            tracked = _tracked.Values.ToArray();
            _tracked.Clear();
        }
        if (OperatingSystem.IsWindows())
            foreach (var entry in tracked) SessionProcessContainment.Native.Close(entry.Handle);
    }

    private void PersistLedger()
    {
        SessionProcessContainment.EscapeeLedger ledger;
        lock (_gate)
        {
            if (_reaped) return;
            // A session records what escaped its job (for the boot sweep); a kept tree records every
            // live process (for its resume).
            var identities = (IsKept
                    ? _tracked.Select(pair => (pair.Key, pair.Value.Created))
                    : _escapees.Select(pair => (pair.Key, pair.Value)))
                .OrderBy(entry => entry.Item1).ToList();
            var fingerprint = string.Join(",", identities.Select(entry => $"{entry.Item1}:{entry.Item2}"));
            if (fingerprint == _persisted) return;
            _persisted = fingerprint;
            if (identities.Count == 0 && !IsKept)
            {
                try { File.Delete(LedgerPath); } catch { }
                return;
            }
            ledger = new SessionProcessContainment.EscapeeLedger(Owner, RootPid, RootCreatedUtcTicks,
                identities.Select(entry => new SessionProcessContainment.EscapeeIdentity(entry.Item1, entry.Item2)).ToList(),
                SessionProcessContainment.Writer.Pid, SessionProcessContainment.Writer.CreatedUtcTicks);
        }
        try
        {
            Directory.CreateDirectory(SessionProcessContainment.LedgerDirectory);
            var temp = LedgerPath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(ledger));
            File.Move(temp, LedgerPath, overwrite: true);
        }
        catch (Exception ex)
        {
            SessionProcessContainment.Log?.Invoke($"[Containment] Could not write escapee ledger: {ex.Message}");
        }
    }

    private static string Sanitize(string value)
        => string.Concat(value.Select(ch => char.IsLetterOrDigit(ch) || ch == '-' ? ch : '_'));

    private sealed record Tracked(IntPtr Handle, long Created, bool Member);
}
