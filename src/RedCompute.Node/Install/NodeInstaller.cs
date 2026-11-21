using System.Runtime.Versioning;
using System.Text.Json.Nodes;
using RedCompute.PluginSdk.Hosting;

namespace RedCompute.Node.Install;

/// <summary>
/// What install and uninstall change outside the program and data folders (design 2.3): autostart
/// (the HKCU Run value, or the systemd user unit), the "installed app" entry (the HKCU Uninstall key
/// on Windows), and linger on Linux. Tests replace it; the real ones touch only the current user.
/// </summary>
public interface INodeInstallPlatform
{
    /// <summary>Starts <paramref name="executable"/> <c>run</c> at every logon, and now.</summary>
    void EnableAutostart(string executable, bool keepRunningWhenLoggedOut);

    void DisableAutostart();

    void RegisterApp(string executable, string installLocation, string version);

    void UnregisterApp();

    /// <summary>What still exists of the above (empty after a clean uninstall).</summary>
    IReadOnlyList<string> Leftovers();
}

/// <summary>
/// <c>leaf-node install</c> and <c>uninstall</c> (design 2.3). Program files live side by side per
/// version with <c>current.json</c> (the last two versions are kept); Linux gets a stable shim on PATH.
/// Uninstall removes everything: autostart, the app entry, data, program files and the shim. (Unpairing
/// and deleting the key come first, in <see cref="NodeCommands"/>.)
/// </summary>
public sealed class NodeInstaller(NodePaths paths, IExecutionHost host, INodeInstallPlatform platform, NodeLog log)
{
    public const string CurrentFile = "current.json";
    public const string ShimName = "leaf-node";
    private const int VersionsKept = 2;

    public static string ExecutableName => OperatingSystem.IsWindows() ? "leaf-node.exe" : "leaf-node";

    /// <summary>
    /// Copies the running build into <c>Programs/&lt;version&gt;</c> (unless it already runs from there),
    /// points <c>current.json</c> at it, writes the Linux shim, registers autostart and the app.
    /// Returns the installed executable.
    /// </summary>
    public string Install(string sourceFolder, string version, bool keepRunningWhenLoggedOut)
    {
        var target = Path.Combine(paths.Programs, version);
        if (!SameFolder(sourceFolder, target))
        {
            if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
            CopyFolder(sourceFolder, target);
            log.Info($"Installed {version} into {target}.");
        }
        var executable = Path.Combine(target, ExecutableName);
        if (!File.Exists(executable))
            throw new NodeCommandException("bad_install", $"{executable} is missing: the program folder is incomplete.");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(executable, File.GetUnixFileMode(executable) | UnixFileMode.UserExecute);

        host.Files.ReplaceAtomically(host.Path(Path.Combine(paths.Programs, CurrentFile)),
            new JsonObject { ["version"] = version, ["path"] = target }.ToJsonString());
        PruneVersions(version);
        foreach (var folder in new[] { paths.Data, paths.Logs, paths.Audit, paths.Spool, paths.Compute })
            Directory.CreateDirectory(folder);

        var launch = executable;
        if (paths.Bin is { } bin)
        {
            launch = WriteShim(bin);
            log.Info($"Linked {Path.Combine(bin, ShimName)}.");
        }
        platform.RegisterApp(executable, paths.Programs, version);
        platform.EnableAutostart(launch, keepRunningWhenLoggedOut);
        return executable;
    }

    /// <summary>Removes autostart, the app entry, the shim, data and program files; returns what is left (should be nothing).</summary>
    public UninstallResult Uninstall(string runningFolder)
    {
        var removed = new List<string>();
        var scheduled = new List<string>();
        platform.DisableAutostart();
        platform.UnregisterApp();
        if (paths.Bin is { } bin)
        {
            var shim = Path.Combine(bin, ShimName);
            if (File.Exists(shim) || IsLink(shim))
            {
                File.Delete(shim);
                removed.Add(shim);
            }
        }
        foreach (var folder in paths.OwnedFolders())
        {
            if (!Directory.Exists(folder)) continue;
            try
            {
                Directory.Delete(folder, recursive: true);
                removed.Add(folder);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // Windows will not delete the running program: delete it once this process exits.
                if (OperatingSystem.IsWindows() && IsUnder(runningFolder, folder))
                {
                    ScheduleDelete(folder);
                    scheduled.Add(folder);
                }
                else
                {
                    throw new NodeCommandException("uninstall_incomplete", $"Could not delete {folder}: {error.Message}");
                }
            }
        }
        return new UninstallResult(removed, scheduled, platform.Leftovers());
    }

    private string WriteShim(string bin)
    {
        var script = Path.Combine(paths.Programs, ShimName);
        // The stable shim reads current.json, so an update only swaps that file.
        host.Files.ReplaceAtomically(host.Path(script), """
            #!/bin/sh
            here="$(dirname "$(readlink -f "$0")")"
            version="$(sed -n 's/.*"version"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' "$here/current.json")"
            exec "$here/$version/leaf-node" "$@"

            """.ReplaceLineEndings("\n"));
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                                     | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        Directory.CreateDirectory(bin);
        var link = Path.Combine(bin, ShimName);
        if (File.Exists(link) || IsLink(link)) File.Delete(link);
        File.CreateSymbolicLink(link, script);
        return link;
    }

    private void PruneVersions(string current)
    {
        var versions = Directory.EnumerateDirectories(paths.Programs)
            .Select(Path.GetFileName)
            .Where(name => name is not null && RedCompute.NodeLink.AgentVersions.IsValid(name))
            .Select(name => name!)
            .OrderByDescending(name => name, Comparer<string>.Create(RedCompute.NodeLink.AgentVersions.Compare))
            .ToList();
        foreach (var old in versions.Where(v => v != current).Skip(VersionsKept - 1))
        {
            try
            {
                Directory.Delete(Path.Combine(paths.Programs, old), recursive: true);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                log.Warn($"Could not remove version {old}: {error.Message}");
            }
        }
    }

    private void ScheduleDelete(string folder)
    {
        host.Processes.Start(new HostSpawnSpec
        {
            FileName = "cmd.exe",
            RawArguments = $"/d /c \"ping -n 4 127.0.0.1 >nul & rmdir /s /q \"{folder}\"\"",
            CreateNoWindow = true,
            Containment = ContainmentMode.None,
            OwnerLabel = "leaf-node-uninstall",
        })?.Dispose();
    }

    private static void CopyFolder(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            var destination = Path.Combine(target, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, overwrite: true);
        }
    }

    private static bool IsLink(string path) => new FileInfo(path).LinkTarget is not null;

    private static bool SameFolder(string a, string b)
        => string.Equals(Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static bool IsUnder(string path, string folder)
    {
        var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var root = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return full.StartsWith(root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }
}

public sealed record UninstallResult(IReadOnlyList<string> Removed, IReadOnlyList<string> ScheduledAfterExit,
    IReadOnlyList<string> Leftovers);

/// <summary>Windows: the HKCU Run value <c>LeafNode</c> and the HKCU Uninstall key <c>LeafNode</c>; no admin rights.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsInstallPlatform(IExecutionHost host) : INodeInstallPlatform
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\LeafNode";
    private const string ValueName = "LeafNode";

    public void EnableAutostart(string executable, bool keepRunningWhenLoggedOut)
    {
        using (var run = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKey))
            run.SetValue(ValueName, $"\"{executable}\" run");
        IsolatedInstallPlatform.StartDetached(host, executable);
    }

    public void DisableAutostart()
    {
        using var run = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        run?.DeleteValue(ValueName, throwOnMissingValue: false);
    }

    public void RegisterApp(string executable, string installLocation, string version)
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(UninstallKey);
        key.SetValue("DisplayName", "Leaf node");
        key.SetValue("DisplayVersion", version);
        key.SetValue("Publisher", "RedBamboo");
        key.SetValue("InstallLocation", installLocation);
        key.SetValue("DisplayIcon", executable);
        key.SetValue("UninstallString", $"\"{executable}\" uninstall");
        key.SetValue("NoModify", 1);
        key.SetValue("NoRepair", 1);
    }

    public void UnregisterApp() => Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, throwOnMissingSubKey: false);

    public IReadOnlyList<string> Leftovers()
    {
        var left = new List<string>();
        using (var run = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey))
            if (run?.GetValue(ValueName) is not null) left.Add(@"HKCU\" + RunKey + @"\" + ValueName);
        using (var app = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(UninstallKey))
            if (app is not null) left.Add(@"HKCU\" + UninstallKey);
        return left;
    }
}

/// <summary>
/// An agent that <c>LEAF_NODE_HOME</c> isolates (tests, a second agent for development, an installer
/// dry run). The Run value, the Uninstall key and the systemd user unit name the user's one real
/// agent, so this one never writes them: it records the same facts as files in its own home
/// (<c>registrations/</c>, removed with it) and starts the agent directly.
/// </summary>
public sealed class IsolatedInstallPlatform(NodePaths paths, IExecutionHost host, Action<string>? start = null)
    : INodeInstallPlatform
{
    private string Folder => Path.Combine(paths.IsolatedHome!, "registrations");
    private string AutostartFile => Path.Combine(Folder, "autostart.json");
    private string AppFile => Path.Combine(Folder, "app.json");

    public void EnableAutostart(string executable, bool keepRunningWhenLoggedOut)
    {
        Write(AutostartFile, new JsonObject
        {
            ["command"] = new JsonArray(executable, "run"),
            ["keepRunningWhenLoggedOut"] = keepRunningWhenLoggedOut,
        });
        (start ?? (path => StartDetached(host, path)))(executable);
    }

    public void DisableAutostart() => Delete(AutostartFile);

    public void RegisterApp(string executable, string installLocation, string version) => Write(AppFile, new JsonObject
    {
        ["displayName"] = "Leaf node",
        ["displayVersion"] = version,
        ["installLocation"] = installLocation,
        ["uninstallString"] = $"\"{executable}\" uninstall",
    });

    public void UnregisterApp() => Delete(AppFile);

    public IReadOnlyList<string> Leftovers() => new[] { AutostartFile, AppFile }.Where(File.Exists).ToArray();

    /// <summary>
    /// Starts <c>leaf-node run</c> in the background, holding none of this process's handles: the
    /// console or pipe an installer reads <c>install</c>'s output from closes when <c>install</c> exits.
    /// Windows starts it through the shell (no inherited handles); elsewhere its stdio are fresh pipes.
    /// </summary>
    internal static void StartDetached(IExecutionHost host, string executable)
    {
        var windows = OperatingSystem.IsWindows();
        host.Processes.Start(new HostSpawnSpec
        {
            FileName = executable,
            ArgumentList = ["run"],
            CreateNoWindow = !windows,
            UseShellExecute = windows,
            StandardInput = windows ? StdioMode.NotRedirected : StdioMode.Pipe,
            StandardOutput = windows ? StdioMode.NotRedirected : StdioMode.Pipe,
            StandardError = windows ? StdioMode.NotRedirected : StdioMode.Pipe,
            Containment = ContainmentMode.None,
            OwnerLabel = "leaf-node",
        })?.Dispose();
    }

    private void Write(string file, JsonObject value)
    {
        host.Files.CreateDirectory(host.Path(Folder));
        host.Files.ReplaceAtomically(host.Path(file), value.ToJsonString());
    }

    private void Delete(string file)
    {
        if (File.Exists(file)) File.Delete(file);
    }
}

/// <summary>
/// Linux: the user unit <c>~/.config/systemd/user/leaf-node.service</c>, enabled and started with
/// <c>systemctl --user</c>; linger only on request (a headless box, design 2.2, D2).
/// </summary>
public sealed class LinuxInstallPlatform(NodePaths paths, IExecutionHost host, NodeLog log) : INodeInstallPlatform
{
    public const string UnitName = "leaf-node.service";

    private string UnitFile => Path.Combine(paths.SystemdUnits!, UnitName);

    /// <summary>The template <c>Install/leaf-node.service</c> (embedded): its comment lines dropped, the shim in ExecStart.</summary>
    public static string Unit(string executable)
    {
        using var stream = typeof(LinuxInstallPlatform).Assembly.GetManifestResourceStream(UnitName)
                           ?? throw new InvalidOperationException($"The {UnitName} template is not embedded.");
        using var reader = new StreamReader(stream);
        var lines = reader.ReadToEnd().ReplaceLineEndings("\n").Split('\n')
            .Where(line => !line.StartsWith('#'));
        return string.Join('\n', lines).Replace("@EXECUTABLE@", executable, StringComparison.Ordinal);
    }

    public void EnableAutostart(string executable, bool keepRunningWhenLoggedOut)
    {
        host.Files.CreateDirectory(host.Path(paths.SystemdUnits!));
        host.Files.ReplaceAtomically(host.Path(UnitFile), Unit(executable));
        Run("systemctl", "--user", "daemon-reload");
        Run("systemctl", "--user", "enable", "--now", UnitName);
        if (keepRunningWhenLoggedOut) Run("loginctl", "enable-linger");
    }

    public void DisableAutostart()
    {
        if (host.Files.FileExists(host.Path(UnitFile)))
        {
            Run("systemctl", "--user", "disable", "--now", UnitName);
            host.Files.DeleteFile(host.Path(UnitFile));
            Run("systemctl", "--user", "daemon-reload");
        }
    }

    public void RegisterApp(string executable, string installLocation, string version)
    {
        // No app registry to join: `leaf-node uninstall` is the way out (design 2.3).
    }

    public void UnregisterApp()
    {
    }

    public IReadOnlyList<string> Leftovers() => host.Files.FileExists(host.Path(UnitFile)) ? [UnitFile] : [];

    private void Run(string file, params string[] arguments)
    {
        using var process = host.Processes.Start(new HostSpawnSpec
        {
            FileName = file,
            ArgumentList = arguments,
            StandardOutput = StdioMode.Pipe,
            StandardError = StdioMode.Pipe,
            Containment = ContainmentMode.None,
            OwnerLabel = "leaf-node-install",
        });
        if (process is null) throw new NodeCommandException("install_failed", $"{file} did not start.");
        var stderr = process.StandardError.ReadToEndAsync();
        _ = process.StandardOutput.ReadToEndAsync();
        if (!process.WaitForExit(60_000))
        {
            process.Kill(entireProcessTree: true);
            throw new NodeCommandException("install_failed", $"{file} {string.Join(' ', arguments)} did not finish.");
        }
        if (process.ExitCode != 0)
        {
            var message = $"{file} {string.Join(' ', arguments)} failed ({process.ExitCode}): {stderr.Result.Trim()}";
            if (file == "loginctl") log.Warn(message); // linger is a nicety; the unit still runs while logged in
            else throw new NodeCommandException("install_failed", message);
        }
    }
}
