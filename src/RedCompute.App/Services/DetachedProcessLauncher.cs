using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace RedCompute.App.Services;

/// <summary>
/// Starts a process that no job RedCompute belongs to can take down. RedLeaf holds RedCompute in a
/// Job Object with kill-on-close, and every process RedCompute creates inherits that job, however it
/// detaches and whatever job it is later put in: nested jobs only add, never remove. So on 12 Nov a
/// RedLeaf deployment that stopped RedLeaf killed every launched long-lived server, while an adopted
/// one, started outside the job, lived (proven in memory/artifacts/909ca1ed). A server meant to
/// outlive RedCompute is therefore created with the desktop shell as its parent, exactly as the
/// suite's own deployer is (redbamboo-packages/dotnet/desktop-process.ps1): it inherits Explorer's
/// job (none), token and the user's registry environment, never RedCompute's job, credentials or
/// scratch TEMP. Without a desktop shell it breaks away from RedCompute's job, which works only when
/// every job above allows it; otherwise the launch is refused rather than promised and broken.
/// The process starts suspended, so the caller can put it in its own job before it runs anything.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class DetachedProcessLauncher
{
    private const uint CreateSuspended = 0x00000004;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint ExtendedStartupInfoPresent = 0x00080000;
    private const uint CreateBreakawayFromJob = 0x01000000;
    private const uint CreateNoWindow = 0x08000000;
    private const uint ProcessCreateProcess = 0x0080;
    private const int ErrorAccessDenied = 5;
    private static readonly IntPtr ParentProcessAttribute = new(0x00020000);

    /// <summary>A created, still suspended process. <see cref="Resume"/> or <see cref="Abort"/> it.</summary>
    internal sealed class Started : IDisposable
    {
        internal Started(int pid, SafeProcessHandle process, IntPtr thread, string via)
        {
            Pid = pid;
            Process = process;
            Thread = thread;
            Via = via;
        }

        public int Pid { get; }
        public SafeProcessHandle Process { get; }
        private IntPtr Thread { get; set; }

        /// <summary>"desktop" (parented to the shell) or "breakaway" (left RedCompute's job).</summary>
        public string Via { get; }

        public void Resume()
        {
            if (Thread == IntPtr.Zero) return;
            if (ResumeThread(Thread) == uint.MaxValue) throw new Win32Exception();
            CloseThread();
        }

        public void Abort()
        {
            TerminateProcess(Process, 1);
            CloseThread();
        }

        public void Dispose()
        {
            CloseThread();
            Process.Dispose();
        }

        private void CloseThread()
        {
            if (Thread == IntPtr.Zero) return;
            CloseHandle(Thread);
            Thread = IntPtr.Zero;
        }
    }

    /// <summary>Tests point this at nothing to exercise the shell-less path.</summary>
    internal static Func<IntPtr> ShellWindow { get; set; } = GetShellWindow;

    /// <summary>
    /// Create <paramref name="commandLine"/> suspended, without a window, outside RedCompute's jobs,
    /// with the user's environment plus <paramref name="overlay"/>.
    /// </summary>
    public static Started StartSuspended(string commandLine, string workingDirectory, IReadOnlyDictionary<string, string>? overlay)
    {
        var environment = BuildEnvironment(overlay);
        var shell = ShellWindow();
        if (shell != IntPtr.Zero)
        {
            GetWindowThreadProcessId(shell, out var shellPid);
            using var shellProcess = shellPid == 0 ? null : OpenProcess(ProcessCreateProcess, false, shellPid);
            if (shellProcess is { IsInvalid: false })
                return Create(commandLine, workingDirectory, environment, shellProcess, 0, "desktop");
        }
        try
        {
            return Create(commandLine, workingDirectory, environment, null, CreateBreakawayFromJob, "breakaway");
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorAccessDenied)
        {
            throw new InvalidOperationException(
                "no desktop shell is available and RedCompute's host job does not allow breakaway, so the process could not outlive its host");
        }
    }

    private static Started Create(string commandLine, string workingDirectory, string environment, SafeProcessHandle? parent,
        uint extraFlags, string via)
    {
        var attributeList = IntPtr.Zero;
        var parentValue = IntPtr.Zero;
        var environmentBlock = Marshal.StringToHGlobalUni(environment);
        try
        {
            var startup = new StartupInfoEx { StartupInfo = new StartupInfo { Size = Marshal.SizeOf<StartupInfoEx>() } };
            var flags = CreateSuspended | CreateUnicodeEnvironment | CreateNoWindow | extraFlags;
            if (parent is not null)
            {
                var size = IntPtr.Zero;
                InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
                attributeList = Marshal.AllocHGlobal(size);
                if (!InitializeProcThreadAttributeList(attributeList, 1, 0, ref size)) throw new Win32Exception();
                parentValue = Marshal.AllocHGlobal(IntPtr.Size);
                Marshal.WriteIntPtr(parentValue, parent.DangerousGetHandle());
                if (!UpdateProcThreadAttribute(attributeList, 0, ParentProcessAttribute, parentValue, IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
                    throw new Win32Exception();
                startup.AttributeList = attributeList;
                flags |= ExtendedStartupInfoPresent;
            }
            if (!CreateProcess(null, new StringBuilder(commandLine), IntPtr.Zero, IntPtr.Zero, false, flags,
                    environmentBlock, workingDirectory, ref startup, out var info))
                throw new Win32Exception();
            return new Started(info.ProcessId, new SafeProcessHandle(info.Process, ownsHandle: true), info.Thread, via);
        }
        finally
        {
            if (attributeList != IntPtr.Zero)
            {
                DeleteProcThreadAttributeList(attributeList);
                Marshal.FreeHGlobal(attributeList);
            }
            if (parentValue != IntPtr.Zero) Marshal.FreeHGlobal(parentValue);
            Marshal.FreeHGlobal(environmentBlock);
        }
    }

    /// <summary>
    /// The user's registry environment, as Explorer would give it, plus the request's own variables:
    /// never RedCompute's, which carries its host's settings and an agent's scratch TEMP.
    /// </summary>
    internal static string BuildEnvironment(IReadOnlyDictionary<string, string>? overlay)
    {
        var variables = UserEnvironment();
        foreach (var (key, value) in overlay ?? new Dictionary<string, string>())
            variables[key] = value;
        var block = new StringBuilder();
        foreach (var (key, value) in variables.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
            block.Append(key).Append('=').Append(value).Append('\0');
        return block.Append('\0').ToString();
    }

    internal static Dictionary<string, string> UserEnvironment()
    {
        if (!OpenProcessToken(GetCurrentProcess(), TokenQuery | TokenDuplicate, out var token)) throw new Win32Exception();
        try
        {
            if (!CreateEnvironmentBlock(out var block, token, false)) throw new Win32Exception();
            try
            {
                var variables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var offset = 0;
                while (true)
                {
                    var entry = Marshal.PtrToStringUni(IntPtr.Add(block, offset));
                    if (string.IsNullOrEmpty(entry)) break;
                    offset += (entry.Length + 1) * 2;
                    var separator = entry.IndexOf('=', 1);
                    if (separator > 0) variables[entry[..separator]] = entry[(separator + 1)..];
                }
                return variables;
            }
            finally
            {
                DestroyEnvironmentBlock(block);
            }
        }
        finally
        {
            CloseHandle(token);
        }
    }

    private const uint TokenQuery = 0x0008;
    private const uint TokenDuplicate = 0x0002;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int Size;
        public string? Reserved;
        public string? Desktop;
        public string? Title;
        public int X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
        public short ShowWindow, Reserved2;
        public IntPtr Reserved3, StdInput, StdOutput, StdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoEx
    {
        public StartupInfo StartupInfo;
        public IntPtr AttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr Process;
        public IntPtr Thread;
        public int ProcessId;
        public int ThreadId;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcess(string? application, StringBuilder commandLine, IntPtr processAttributes,
        IntPtr threadAttributes, bool inheritHandles, uint flags, IntPtr environment, string workingDirectory,
        ref StartupInfoEx startup, out ProcessInformation information);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref IntPtr size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attribute, IntPtr value, IntPtr size,
        IntPtr previous, IntPtr returnSize);

    [DllImport("kernel32.dll")]
    private static extern void DeleteProcThreadAttributeList(IntPtr list);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, uint pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint ResumeThread(IntPtr thread);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateProcess(SafeProcessHandle process, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    [DllImport("userenv.dll", SetLastError = true)]
    private static extern bool CreateEnvironmentBlock(out IntPtr block, IntPtr token, bool inherit);

    [DllImport("userenv.dll", SetLastError = true)]
    private static extern bool DestroyEnvironmentBlock(IntPtr block);

    [DllImport("user32.dll")]
    private static extern IntPtr GetShellWindow();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
}
