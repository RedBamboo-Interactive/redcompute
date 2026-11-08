using System.Diagnostics;
using System.IO;
using Xunit;

namespace RedCompute.Tests;

/// <summary>A fact that runs only on Windows; elsewhere it is skipped with the given reason.</summary>
internal sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute(string reason)
    {
        if (!OperatingSystem.IsWindows())
            Skip = $"Windows-only: {reason}";
    }
}

/// <summary>A theory that runs only on Windows; elsewhere it is skipped with the given reason.</summary>
internal sealed class WindowsTheoryAttribute : TheoryAttribute
{
    public WindowsTheoryAttribute(string reason)
    {
        if (!OperatingSystem.IsWindows())
            Skip = $"Windows-only: {reason}";
    }
}

/// <summary>
/// A fact that needs PowerShell: Windows PowerShell on Windows, pwsh on PATH elsewhere.
/// Skipped where neither exists.
/// </summary>
internal sealed class PowerShellFactAttribute : FactAttribute
{
    public PowerShellFactAttribute(string reason)
    {
        if (!OperatingSystem.IsWindows() && !OnPath("pwsh"))
            Skip = $"Needs PowerShell (pwsh is not on PATH): {reason}";
    }

    private static bool OnPath(string name) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "")
        .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
        .Any(directory => File.Exists(Path.Combine(directory, name)));
}

/// <summary>
/// One theory row that runs only on Windows; elsewhere that row alone is skipped with the
/// given reason (other rows of the same theory still run).
/// </summary>
internal sealed class WindowsInlineDataAttribute : Xunit.Sdk.DataAttribute
{
    private readonly object?[] _data;

    public WindowsInlineDataAttribute(string reason, params object?[] data)
    {
        _data = data;
        if (!OperatingSystem.IsWindows())
            Skip = $"Windows-only: {reason}";
    }

    public override IEnumerable<object?[]> GetData(System.Reflection.MethodInfo testMethod) => [_data];
}

/// <summary>Stand-in processes the session tests launch, chosen per OS.</summary>
internal static class TestProcesses
{
    /// <summary>
    /// A process that keeps running while its stdin is open and echoes what it reads to
    /// stdout: more.com on Windows, cat elsewhere.
    /// </summary>
    public static string StdinSink => OperatingSystem.IsWindows()
        ? Path.Combine(Environment.SystemDirectory, "more.com")
        : "/bin/cat";

    /// <summary>
    /// Appends every stdin line to the file named by <paramref name="pathVariable"/> in its
    /// environment, until stdin closes. Redirection flags are left to the caller.
    /// </summary>
    public static ProcessStartInfo AppendStdinTo(string path, string pathVariable)
    {
        if (OperatingSystem.IsWindows())
        {
            var start = new ProcessStartInfo { FileName = "powershell.exe" };
            start.Environment[pathVariable] = path;
            start.ArgumentList.Add("-NoProfile");
            start.ArgumentList.Add("-Command");
            start.ArgumentList.Add($"while ($null -ne ($line = [Console]::In.ReadLine())) {{ [IO.File]::AppendAllText($env:{pathVariable}, $line + [Environment]::NewLine) }}");
            return start;
        }
        return Sh($"cat >> \"${pathVariable}\"", env: (pathVariable, path));
    }

    /// <summary>
    /// Writes <paramref name="output"/> (passed through the <paramref name="variable"/>
    /// environment variable) to stdout without a trailing newline, then exits.
    /// </summary>
    public static ProcessStartInfo WriteToStdout(string variable, string output)
    {
        ProcessStartInfo start;
        if (OperatingSystem.IsWindows())
        {
            start = new ProcessStartInfo { FileName = "powershell.exe" };
            start.ArgumentList.Add("-NoProfile");
            start.ArgumentList.Add("-Command");
            start.ArgumentList.Add($"[Console]::Out.Write($env:{variable})");
        }
        else
        {
            start = Sh($"printf '%s' \"${variable}\"");
        }
        start.Environment[variable] = output;
        return start;
    }

    /// <summary>Prints <paramref name="file"/> on stdout, then behaves like <see cref="StdinSink"/>.</summary>
    public static ProcessStartInfo PrintThenEchoStdin(string file) => OperatingSystem.IsWindows()
        // Arguments verbatim: cmd.exe does not understand ArgumentList's \" escaping.
        ? new ProcessStartInfo { FileName = Path.Combine(Environment.SystemDirectory, "cmd.exe"), Arguments = $"/d /c type \"{file}\" & more" }
        : Sh("cat \"$PRINT_FILE\"; exec cat", env: ("PRINT_FILE", file));

    /// <summary>Writes everything read from stdin to <paramref name="file"/> (truncating it first).</summary>
    public static ProcessStartInfo WriteStdinTo(string file) => OperatingSystem.IsWindows()
        ? new ProcessStartInfo { FileName = Path.Combine(Environment.SystemDirectory, "cmd.exe"), Arguments = $"/d /c more > \"{file}\"" }
        : Sh("cat > \"$CAPTURE_FILE\"", env: ("CAPTURE_FILE", file));

    /// <summary>Reads one line from stdin, then exits.</summary>
    public static ProcessStartInfo ReadOneLine() => OperatingSystem.IsWindows()
        ? new ProcessStartInfo { FileName = Path.Combine(Environment.SystemDirectory, "cmd.exe"), Arguments = "/d /q /c set /p input=" }
        : Sh("read -r input");

    /// <summary>An unrelated process that sleeps for <paramref name="seconds"/>.</summary>
    public static ProcessStartInfo Sleep(int seconds)
    {
        if (!OperatingSystem.IsWindows())
            return new ProcessStartInfo { FileName = "/bin/sleep", ArgumentList = { seconds.ToString() } };
        return new ProcessStartInfo { FileName = "powershell.exe", ArgumentList = { "-NoProfile", "-Command", $"Start-Sleep -Seconds {seconds}" } };
    }

    private static ProcessStartInfo Sh(string script, params (string Name, string Value)[] env)
    {
        var start = new ProcessStartInfo { FileName = "/bin/sh" };
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add(script);
        foreach (var (name, value) in env) start.Environment[name] = value;
        return start;
    }
}

/// <summary>A fact about behaviour that exists only off Windows; skipped on Windows.</summary>
internal sealed class NonWindowsFactAttribute : FactAttribute
{
    public NonWindowsFactAttribute(string reason)
    {
        if (OperatingSystem.IsWindows())
            Skip = $"Non-Windows only: {reason}";
    }
}
