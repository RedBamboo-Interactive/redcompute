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
        Assert.True(CodexProvider.DeclaredCapabilities.HasFlag(SessionCapabilities.FileAttachments));
    }

    [Fact]
    public void TypedAttachmentsLowerToLocalImageAndCompactFileReference()
    {
        var input = CodexInteractiveService.BuildTurnInput([
            SessionInputPart.TextPart("Review both"),
            SessionInputPart.AttachmentPart(new InputAttachment
            {
                Id = "att_image", Kind = "image", Name = "diagram.png", MediaType = "image/png",
                Size = 10, Sha256 = "abc", StoredPath = @"C:\attachments\image.bin", DownloadUrl = "/image",
            }),
            SessionInputPart.AttachmentPart(new InputAttachment
            {
                Id = "att_file", Kind = "file", Name = "proposal.pdf", MediaType = "application/pdf",
                Size = 20, Sha256 = "def", StoredPath = @"C:\attachments\file.bin", DownloadUrl = "/file",
            }),
        ]);

        var json = JsonSerializer.SerializeToElement(input);
        Assert.Equal("localImage", json[1].GetProperty("type").GetString());
        Assert.Equal(@"C:\attachments\image.bin", json[1].GetProperty("path").GetString());
        Assert.Equal("text", json[2].GetProperty("type").GetString());
        var reference = json[2].GetProperty("text").GetString();
        Assert.Contains("proposal.pdf", reference);
        Assert.Contains(@"C:\attachments\file.bin", reference);
        Assert.DoesNotContain("mention", json[2].GetRawText(), StringComparison.OrdinalIgnoreCase);
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
