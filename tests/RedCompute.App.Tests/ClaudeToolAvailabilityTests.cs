using RedCompute.Plugin.ClaudeCode;
using Xunit;

namespace RedCompute.App.Tests;

public sealed class ClaudeToolAvailabilityTests
{
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
