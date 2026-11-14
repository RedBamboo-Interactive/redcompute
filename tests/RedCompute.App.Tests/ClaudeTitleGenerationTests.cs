using RedCompute.Core.Configuration;
using RedCompute.Core.Sessions;
using RedCompute.Plugin.ClaudeCode;
using RedCompute.PluginSdk;
using Xunit;

namespace RedCompute.App.Tests;

public sealed class ClaudeTitleGenerationTests
{
    [Fact]
    public void ProviderConfigDefaultsTitlesToFastTier()
    {
        var config = new ProviderConfig { Type = "ClaudeCode" };

        Assert.Equal("fast", ClaudeCodeProvider.GetTitleQualityTier(config));
    }

    [Theory]
    [InlineData("off")]
    [InlineData("OFF")]
    [InlineData("")]
    [InlineData("   ")]
    public void ProviderConfigCanDisableSemanticTitles(string value)
    {
        var config = new ProviderConfig
        {
            Type = "ClaudeCode",
            Extra = new Dictionary<string, object?> { ["TitleQualityTier"] = value },
        };

        Assert.Null(ClaudeCodeProvider.GetTitleQualityTier(config));
    }

    [Fact]
    public void SharedTitleFormattingUsesTheHumanOpening()
    {
        var opening = new string('x', 900);

        var prompt = SessionTitleFormatter.BuildPrompt(opening);

        Assert.Contains("at most six words", prompt);
        Assert.EndsWith(new string('x', 800), prompt);
        Assert.DoesNotContain(new string('x', 801), prompt);
        Assert.Equal(
            "Fix Claude title updates",
            SessionTitleFormatter.DeriveFallback(
                "<nova-context>machine context</nova-context>Fix Claude title updates"));
    }

    private static IReadOnlyList<SessionInputPart> Opening(string? text, params InputAttachment[] attachments)
    {
        var parts = new List<SessionInputPart>();
        if (text is not null) parts.Add(SessionInputPart.TextPart(text));
        parts.AddRange(attachments.Select(SessionInputPart.AttachmentPart));
        return parts;
    }

    private static InputAttachment Attachment(string kind, string name, string mediaType) => new()
    {
        Id = "att_test", Kind = kind, Name = name, MediaType = mediaType, Size = 83_000,
        Sha256 = "00", StoredPath = "C:/attachments/att_test", DownloadUrl = "/attachments/att_test",
    };

    [Fact]
    public void TextOnlyOpeningKeepsTheExistingPromptAndFallback()
    {
        var attachments = SessionTitleFormatter.DescribeAttachments(Opening("Fix Claude title updates"));

        Assert.Empty(attachments);
        Assert.Equal(
            SessionTitleFormatter.BuildPrompt("Fix Claude title updates"),
            SessionTitleFormatter.BuildPrompt("Fix Claude title updates", attachments));
        Assert.DoesNotContain("attach", SessionTitleFormatter.BuildPrompt("Fix Claude title updates", attachments));
        Assert.Equal("Fix Claude title updates",
            SessionTitleFormatter.DeriveFallback("Fix Claude title updates", attachments));
    }

    [Fact]
    public void TextWithImageTellsTheTitleModelAnImageWasAttached()
    {
        var input = Opening("<nova-context>machine</nova-context>What is causing this: ?",
            Attachment("image", "image.png", "image/png"));
        var attachments = SessionTitleFormatter.DescribeAttachments(input);
        var opening = SessionTitleFormatter.StripContext(SessionInputFormatting.UserText(input));

        var prompt = SessionTitleFormatter.BuildPrompt(opening, attachments);

        Assert.Equal([new TitleAttachment("image", "image.png", "image/png")], attachments);
        Assert.Contains("attachments you cannot see: an image named \"image.png\" (image/png).", prompt);
        Assert.Contains("Never describe the message as empty, unclear", prompt);
        Assert.EndsWith("\n\nWhat is causing this: ?", prompt);
        Assert.DoesNotContain("machine", prompt);
        Assert.Equal("What is causing this:", SessionTitleFormatter.DeriveFallback(opening, attachments));
    }

    [Fact]
    public void ImageOnlyOpeningStillGetsAPromptAndAFallback()
    {
        var input = Opening(null, Attachment("image", "Screenshot 2026-11-14.png", "image/png"));
        var attachments = SessionTitleFormatter.DescribeAttachments(input);
        var opening = SessionTitleFormatter.StripContext(SessionInputFormatting.UserText(input));

        var prompt = SessionTitleFormatter.BuildPrompt(opening, attachments);

        Assert.Equal("", opening);
        Assert.Contains("an image named \"Screenshot 2026-11-14.png\" (image/png)", prompt);
        Assert.EndsWith("\n\n(no text, only the attachments)", prompt);
        Assert.Equal("Shared image", SessionTitleFormatter.DeriveFallback(opening, attachments));
        Assert.Null(SessionTitleFormatter.DeriveFallback(opening));
    }

    [Fact]
    public void AttachmentMetadataIsSanitisedAndCountedInFallbacks()
    {
        var attachments = SessionTitleFormatter.DescribeAttachments(Opening(null,
            Attachment("File", "re\"port\n<b>.pdf", "application/pdf"),
            Attachment("image", "a.png", "image/png")));
        var legacy = SessionTitleFormatter.DescribeAttachments(
            [SessionInputPart.LegacyImagePart(new RedCompute.Core.Sessions.ImageAttachment("image/jpeg", "AA=="))]);

        Assert.Equal("file", attachments[0].Kind);
        Assert.Equal("re port b .pdf", attachments[0].Name);
        Assert.Equal("Shared 2 attachments", SessionTitleFormatter.DeriveFallback("", attachments));
        Assert.Equal("Shared file re port b .pdf", SessionTitleFormatter.DeriveFallback("", [attachments[0]]));
        Assert.Equal([new TitleAttachment("image", "", "image/jpeg")], legacy);
        Assert.Contains("cannot see: an image (image/jpeg).", SessionTitleFormatter.BuildPrompt("", legacy));
    }

    [Theory]
    [InlineData("\"Claude Session Titles.\"", "Claude Session Titles")]
    [InlineData("preamble\n**Fix Claude Titles**", "Fix Claude Titles")]
    [InlineData("", null)]
    public void GeneratedTitleCleanupPreservesFallbackOnBadOutput(string raw, string? expected)
        => Assert.Equal(expected, SessionTitleFormatter.CleanGenerated(raw));
}
