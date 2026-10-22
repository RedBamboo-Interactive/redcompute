using RedCompute.Plugin.ClaudeCode;
using Xunit;

namespace RedCompute.App.Tests;

// A Claude session stopped by a restart before its first turn has no Claude conversation id.
// Accepted input queued for it (a Discord reply to an outreach-only session) must be able to
// relaunch it; a session that did run turns must never silently restart without its history.
public sealed class ClaudeResumeLaunchTests
{
    [Theory]
    [InlineData(null, 0)]
    [InlineData("", 0)]
    [InlineData("claude-conversation-1", 0)]
    [InlineData("claude-conversation-1", 12)]
    public void Resumable_sessions_launch(string? claudeSessionId, int messageCount)
        => Assert.Null(ClaudeSessionService.ResumeLaunchError(claudeSessionId, messageCount));

    // The shipped provider config carries "Model": "". Blank must mean "no --model flag", never
    // "--model ''", which fails every turn with "model: String should have at least 1 character".
    [Theory]
    [InlineData("claude-opus-5-5", "", "claude-opus-5-5")]
    [InlineData(" claude-opus-5-5 ", null, "claude-opus-5-5")]
    [InlineData("", "", null)]
    [InlineData(null, "", null)]
    [InlineData("  ", null, null)]
    [InlineData(null, "claude-sonnet-5-5", "claude-sonnet-5-5")]
    [InlineData("", "claude-sonnet-5-5", "claude-sonnet-5-5")]
    public void Blank_models_never_reach_the_cli(string? requested, string? configured, string? expected)
        => Assert.Equal(expected, ClaudeSessionService.LaunchModel(requested, configured));

    [Theory]
    [InlineData(null, 1)]
    [InlineData("", 7)]
    public void Session_with_turns_but_no_conversation_id_refuses_to_restart_blank(
        string? claudeSessionId, int messageCount)
        => Assert.Equal("Session has no Claude session ID to resume",
            ClaudeSessionService.ResumeLaunchError(claudeSessionId, messageCount));
}
