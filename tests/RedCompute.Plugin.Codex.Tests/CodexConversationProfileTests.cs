using System.Text.Json;
using RedCompute.Core.Sessions;
using RedCompute.PluginSdk;
using Xunit;

namespace RedCompute.Plugin.Codex.Tests;

public sealed class CodexConversationProfileTests
{
    [Fact]
    public void ThreadStartUsesRestrictedConversationDefaults()
    {
        var info = Session(SessionExecutionProfile.ConversationOnly);

        var value = JsonSerializer.SerializeToElement(
            CodexInteractiveService.BuildThreadStartParams(info));

        Assert.Equal("never", value.GetProperty("approvalPolicy").GetString());
        Assert.Equal("read-only", value.GetProperty("sandbox").GetString());
        Assert.Equal("disabled", value.GetProperty("config").GetProperty("web_search").GetString());
        Assert.Equal(CodexInteractiveService.ConversationBaseInstructions,
            value.GetProperty("baseInstructions").GetString());
        Assert.Equal("trusted SHIN instructions", value.GetProperty("developerInstructions").GetString());
    }

    [Fact]
    public void ThreadResumeRetainsRestrictedConversationDefaults()
    {
        var info = Session(SessionExecutionProfile.ConversationOnly);

        var value = JsonSerializer.SerializeToElement(
            CodexInteractiveService.BuildThreadResumeParams(info));

        Assert.Equal("thread-123", value.GetProperty("threadId").GetString());
        Assert.Equal("never", value.GetProperty("approvalPolicy").GetString());
        Assert.Equal("read-only", value.GetProperty("sandbox").GetString());
        Assert.Equal("disabled", value.GetProperty("config").GetProperty("web_search").GetString());
        Assert.Equal(CodexInteractiveService.ConversationBaseInstructions,
            value.GetProperty("baseInstructions").GetString());
    }

    [Fact]
    public void DefaultThreadDoesNotOverrideExistingCodexPolicy()
    {
        var value = JsonSerializer.SerializeToElement(
            CodexInteractiveService.BuildThreadStartParams(Session(SessionExecutionProfile.Default)));

        Assert.Equal(JsonValueKind.Null, value.GetProperty("approvalPolicy").ValueKind);
        Assert.Equal(JsonValueKind.Null, value.GetProperty("sandbox").ValueKind);
        Assert.Equal(JsonValueKind.Null, value.GetProperty("baseInstructions").ValueKind);
        Assert.Equal(JsonValueKind.Null, value.GetProperty("config").ValueKind);
    }

    [Fact]
    public void ConversationInputAllowsTextAndRejectsAttachments()
    {
        Assert.True(CodexInteractiveService.IsConversationInputAllowed([
            SessionInputPart.TextPart("status report"),
        ]));
        Assert.False(CodexInteractiveService.IsConversationInputAllowed([
            SessionInputPart.TextPart("inspect this"),
            SessionInputPart.AttachmentPart(new InputAttachment
            {
                Id = "attachment-1",
                Kind = "file",
                StoredPath = "C:\\isolated\\secret.txt",
                Name = "secret.txt",
                MediaType = "text/plain",
                Size = 10,
                Sha256 = "abc123",
                DownloadUrl = "/ai-session/input-attachments/attachment-1",
            }),
        ]));
    }

    private static CodexSessionInfo Session(string executionProfile) => new()
    {
        Id = "session-123",
        ProjectName = "conversation",
        ProjectPath = "C:\\isolated\\conversation",
        StartedAt = DateTimeOffset.UtcNow,
        ThreadId = "thread-123",
        DeveloperInstructions = "trusted SHIN instructions",
        ExecutionProfile = executionProfile,
    };
}
