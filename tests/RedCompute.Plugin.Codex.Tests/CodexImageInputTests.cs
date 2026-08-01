using System.Text.Json;
using RedCompute.Core.Sessions;
using RedCompute.Plugin.Codex;
using Xunit;

namespace RedCompute.Plugin.Codex.Tests;

public sealed class CodexImageInputTests
{
    [Fact]
    public void ProviderAdvertisesImageAttachments()
    {
        Assert.True(CodexProvider.DeclaredCapabilities.HasFlag(SessionCapabilities.ImageAttachments));
    }

    [Fact]
    public void TurnInputKeepsTextThenAllImagesInOrder()
    {
        var input = CodexInteractiveService.BuildTurnInput("Compare these", [
            new ImageAttachment("image/png", "cG5n"),
            new ImageAttachment("image/jpeg", "anBlZw=="),
        ]);

        var json = JsonSerializer.SerializeToElement(input);
        Assert.Equal(3, json.GetArrayLength());
        Assert.Equal("text", json[0].GetProperty("type").GetString());
        Assert.Equal("Compare these", json[0].GetProperty("text").GetString());
        Assert.Equal("data:image/png;base64,cG5n", json[1].GetProperty("url").GetString());
        Assert.Equal("data:image/jpeg;base64,anBlZw==", json[2].GetProperty("url").GetString());
    }

    [Fact]
    public void TurnInputSupportsImageOnlyMessages()
    {
        var input = CodexInteractiveService.BuildTurnInput("", [
            new ImageAttachment("image/webp", "d2VicA=="),
        ]);

        var json = JsonSerializer.SerializeToElement(input);
        Assert.Equal(1, json.GetArrayLength());
        Assert.Equal("image", json[0].GetProperty("type").GetString());
        Assert.Equal("data:image/webp;base64,d2VicA==", json[0].GetProperty("url").GetString());
    }

    [Fact]
    public void TextOnlyModelIsRejectedBeforeTurnStart()
    {
        var model = new CodexModel(
            "gpt-text", "Text", null, false, false, null, [], [], ["text"]);

        var support = CodexInteractiveService.GetImageAttachmentSupport("gpt-text", [model]);

        Assert.False(support.Supported);
        Assert.Contains("gpt-text", support.Reason);
    }

}
