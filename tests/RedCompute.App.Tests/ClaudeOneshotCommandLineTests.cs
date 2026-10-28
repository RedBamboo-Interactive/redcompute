using RedCompute.Plugin.ClaudeCode;
using Xunit;

namespace RedCompute.App.Tests;

public sealed class ClaudeOneshotCommandLineTests
{
    private const string ClaudePath =
        @"C:\Users\someone\AppData\Roaming\npm\node_modules\@anthropic-ai\claude-code\bin\claude.exe";

    [Fact]
    public void Short_prompt_rides_on_the_command_line()
    {
        Assert.True(ClaudeSessionService.UsePromptArgument(
            ClaudePath, "Reply with ok.", Options(system: "Be brief."), hasImages: false));
    }

    [Fact]
    public void Prompt_under_32_KiB_still_goes_to_stdin_when_the_system_prompt_fills_the_command_line()
    {
        // Release notes on 2026-10-28: a prompt below the old 32 KiB prompt-only threshold plus a
        // long system prompt exceeded Windows' 32,767-character command line.
        var prompt = new string('p', 24_000);
        var options = Options(system: new string('s', 9_000));

        Assert.False(ClaudeSessionService.UsePromptArgument(ClaudePath, prompt, options, hasImages: false));
        Assert.True(ClaudeSessionService.OneshotCommandLineLength(ClaudePath, [.. options, "-p", prompt])
            > ClaudeSessionService.MaxOneshotCommandLineChars);
    }

    [Fact]
    public void Quotes_and_backslashes_count_toward_the_limit()
    {
        var plain = ClaudeSessionService.OneshotCommandLineLength(ClaudePath, ["abc"]);
        var escaped = ClaudeSessionService.OneshotCommandLineLength(ClaudePath, ["a\"\\c"]);

        Assert.Equal(plain + 3, escaped);
    }

    [Fact]
    public void Images_always_use_stdin()
    {
        Assert.False(ClaudeSessionService.UsePromptArgument(
            ClaudePath, "Describe it.", Options(system: null), hasImages: true));
    }

    private static List<string> Options(string? system)
    {
        var options = new List<string>
        {
            "--output-format", "stream-json", "--verbose", "--no-session-persistence",
            "--tools", "", "--strict-mcp-config", "--mcp-config", "{\"mcpServers\":{}}",
            "--thinking-display", "summarized",
        };
        if (system is not null) options.AddRange(["--system-prompt", system]);
        return options;
    }
}
