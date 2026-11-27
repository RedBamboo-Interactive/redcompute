using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace RedCompute.App;

/// <summary>Early, non-service entry points for the trusted launcher and Claude's local read guard.</summary>
internal static class NativeMcpHelper
{
    internal const string Launch = "--leaf-mcp-stdio";
    internal const string ReadGuard = "--leaf-mcp-read-guard";

    public static bool IsInvocation(string[] args) => args.Length != 0 && args[0] is Launch or ReadGuard;

    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            return args[0] switch
            {
                Launch when args.Length >= 3 => await RunChildAsync(args[1], args[2], args.Skip(3),
                    Console.OpenStandardInput(), Console.OpenStandardOutput(), Console.OpenStandardError()),
                ReadGuard => await GuardAsync(args.Skip(1).ToHashSet(StringComparer.Ordinal)),
                _ => 2,
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
            or InvalidOperationException or JsonException or System.ComponentModel.Win32Exception or CryptographicException)
        {
            // The caller can report a failed launch or guard. Do not emit paths, arguments or environment values.
            return 2;
        }
    }

    internal static ProcessStartInfo ChildStartInfo(string path, IEnumerable<string> args)
    {
        var start = new ProcessStartInfo(path)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(path)!,
        };
        start.Environment.Clear();
        if (OperatingSystem.IsWindows())
        {
            foreach (var name in new[] { "SystemRoot", "WINDIR" })
                if (Environment.GetEnvironmentVariable(name) is { } value)
                    start.Environment[name] = value;
        }
        foreach (var argument in args)
            start.ArgumentList.Add(argument);
        return start;
    }

    internal static async Task<int> RunChildAsync(string digest, string path, IEnumerable<string> args,
        Stream input, Stream output, Stream error)
    {
        if (!Path.IsPathFullyQualified(path) || !Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase))
            return 2;
        // Deny writes/replacement while verifying and starting the executable on Windows.
        using var executable = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (!string.Equals(Convert.ToHexString(SHA256.HashData(executable)), digest, StringComparison.OrdinalIgnoreCase))
            return 2;
        using var child = Process.Start(ChildStartInfo(path, args));
        if (child is null) return 2;
        executable.Dispose();

        _ = CopyInputAsync(input, child.StandardInput.BaseStream);
        await Task.WhenAll(child.StandardOutput.BaseStream.CopyToAsync(output),
            child.StandardError.BaseStream.CopyToAsync(error), child.WaitForExitAsync());
        return child.ExitCode;
    }

    private static async Task CopyInputAsync(Stream source, Stream destination)
    {
        using (source)
        using (destination)
        {
            try { await source.CopyToAsync(destination); }
            catch (IOException) { /* The child may have exited before the harness closes stdin. */ }
        }
    }

    internal static bool MayCall(JsonElement call, IReadOnlySet<string> allowed)
        => call.ValueKind == JsonValueKind.Object
            && call.TryGetProperty("hook_event_name", out var hook) && hook.ValueKind == JsonValueKind.String
            && hook.GetString() == "PreToolUse"
            && call.TryGetProperty("tool_name", out var tool) && tool.ValueKind == JsonValueKind.String
            && tool.GetString() is { } name && allowed.Contains(name);

    private static async Task<int> GuardAsync(IReadOnlySet<string> allowed)
    {
        using var input = Console.OpenStandardInput();
        using var request = await JsonDocument.ParseAsync(input);
        if (MayCall(request.RootElement, allowed))
        {
            await Console.Out.WriteAsync("{}");
            return 0;
        }
        await Console.Out.WriteAsync("""{"hookSpecificOutput":{"hookEventName":"PreToolUse","permissionDecision":"deny","permissionDecisionReason":"This native MCP tool is outside the approved read-only set."}}""");
        return 0;
    }
}
