using RedCompute.Plugin.ClaudeCode;
using Xunit;

namespace RedCompute.App.Tests;

public sealed class ClaudeToolAvailabilityTests
{
    [Fact]
    public void ResolveClaudePath_finds_newest_Claude_Desktop_bundle()
    {
        using var scratch = new ScratchDirectory();
        var older = Path.Combine(scratch.Path, "Claude", "claude-code", "2.1.99", "claude.exe");
        var newer = Path.Combine(scratch.Path, "Claude", "claude-code", "2.1.156", "claude.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(older)!);
        Directory.CreateDirectory(Path.GetDirectoryName(newer)!);
        File.WriteAllText(older, "fixture");
        File.WriteAllText(newer, "fixture");

        var resolved = ClaudeSessionService.ResolveClaudePath(null, scratch.Path, null);

        Assert.Equal(newer, resolved);
    }

    [Fact]
    public void Explicit_empty_tools_disables_builtins_and_ambient_mcp()
    {
        var args = new List<string>();

        ClaudeSessionService.AddAgentArgs(
            args, model: null, effort: null, maxTurns: 1,
            tools: [], allowedTools: [], addDirs: []);

        Assert.Equal("", ValueAfter(args, "--tools"));
        Assert.Contains("--bare", args);
        Assert.Contains("--strict-mcp-config", args);
        Assert.Equal("{}", ValueAfter(args, "--mcp-config"));
    }

    [Fact]
    public void Omitted_tools_preserves_existing_agent_behavior()
    {
        var args = new List<string>();

        ClaudeSessionService.AddAgentArgs(
            args, model: null, effort: null, maxTurns: 1,
            tools: null, allowedTools: ["Read"], addDirs: null);

        Assert.DoesNotContain("--tools", args);
        Assert.DoesNotContain("--bare", args);
        Assert.Equal("Read", ValueAfter(args, "--allowed-tools"));
    }

    private static string ValueAfter(IReadOnlyList<string> values, string key)
    {
        var index = values.IndexOf(key);
        Assert.InRange(index, 0, values.Count - 2);
        return values[index + 1];
    }

    private sealed class ScratchDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"redcompute-claude-path-{Guid.NewGuid():N}");

        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }
}

internal static class ReadOnlyListIndex
{
    public static int IndexOf<T>(this IReadOnlyList<T> values, T item)
    {
        for (var index = 0; index < values.Count; index++)
            if (EqualityComparer<T>.Default.Equals(values[index], item)) return index;
        return -1;
    }
}
