using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using RedCompute.PluginSdk.Hosting;
using RedCompute.PluginSdk.Hosting.Local;

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
                Launch when args.Length >= 3 => await RunChildAsync(LocalExecutionHost.Shared, args[1], args[2], args.Skip(3).ToArray(),
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

    internal static HostSpawnSpec BuildChildSpec(IExecutionHost host, string path, IReadOnlyList<string> args)
    {
        var environment = new Dictionary<string, string>();
        if (host.Platform.IsWindows)
            foreach (var name in new[] { "SystemRoot", "WINDIR" })
                if (host.Platform.GetEnvironmentVariable(name) is { } value)
                    environment[name] = value;

        return new HostSpawnSpec
        {
            FileName = path,
            ArgumentList = args,
            WorkingDirectory = host.Path(Path.GetDirectoryName(path)!),
            EnvironmentBase = EnvironmentBase.Empty,
            EnvironmentOverlay = environment,
            StandardInput = StdioMode.Pipe,
            StandardOutput = StdioMode.Pipe,
            StandardError = StdioMode.Pipe,
            CreateNoWindow = true,
            UseShellExecute = false,
            // Claude's existing session tree contains the helper and its descendants.
            Containment = ContainmentMode.None,
            OwnerLabel = "claude-code",
        };
    }

    internal static async Task<int> RunChildAsync(IExecutionHost host, string digest, string path, IReadOnlyList<string> args,
        Stream input, Stream output, Stream error)
    {
        if (!host.PathRules.IsPathFullyQualified(path) || !Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase))
            return 2;

        IHostProcess? started;
        // Deny writes/replacement while verifying and starting the executable on Windows.
        using (var executable = host.Files.OpenRead(host.Path(path), FileShare.Read))
        {
            if (!string.Equals(Convert.ToHexString(SHA256.HashData(executable)), digest, StringComparison.OrdinalIgnoreCase))
                return 2;
            started = host.Processes.Start(BuildChildSpec(host, path, args));
        }

        using var child = started;
        if (child is null) return 2;

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
