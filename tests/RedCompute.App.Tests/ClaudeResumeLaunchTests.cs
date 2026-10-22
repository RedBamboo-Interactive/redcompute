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

    [Theory]
    [InlineData(null, 1)]
    [InlineData("", 7)]
    public void Session_with_turns_but_no_conversation_id_refuses_to_restart_blank(
        string? claudeSessionId, int messageCount)
        => Assert.Equal("Session has no Claude session ID to resume",
            ClaudeSessionService.ResumeLaunchError(claudeSessionId, messageCount));
}
