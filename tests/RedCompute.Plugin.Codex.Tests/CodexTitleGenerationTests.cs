using RedCompute.Core.Configuration;
using RedCompute.Plugin.Codex;
using RedCompute.PluginSdk;
using Xunit;

namespace RedCompute.Plugin.Codex.Tests;

public sealed class CodexTitleGenerationTests
{
    [Fact]
    public void ProviderConfigDefaultsTitlesToFastTier()
    {
        var config = new ProviderConfig { Type = "Codex" };

        Assert.Equal("fast", CodexProvider.GetTitleQualityTier(config));
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
            Type = "Codex",
            Extra = new Dictionary<string, object?> { ["TitleQualityTier"] = value },
        };

        Assert.Null(CodexProvider.GetTitleQualityTier(config));
    }

    [Fact]
    public void ProviderConfigRetainsExplicitQualityTier()
    {
        var config = new ProviderConfig
        {
            Type = "Codex",
            Extra = new Dictionary<string, object?> { ["TitleQualityTier"] = "standard" },
        };

        Assert.Equal("standard", CodexProvider.GetTitleQualityTier(config));
    }

    [Fact]
    public void TitlePromptUsesHumanOpeningAndBoundsItsInput()
    {
        var opening = new string('x', 900);

        var prompt = CodexInteractiveService.BuildTitlePrompt(opening);

        Assert.Contains("at most six words", prompt);
        Assert.EndsWith(new string('x', 800), prompt);
        Assert.DoesNotContain(new string('x', 801), prompt);
    }

    [Fact]
    public void TitlePromptAndFallbackKnowAboutAttachedImages()
    {
        var attachments = new[] { new TitleAttachment("image", "image.png", "image/png") };

        var prompt = CodexInteractiveService.BuildTitlePrompt("What is causing this: ?", attachments);

        Assert.Contains("an image named \"image.png\" (image/png)", prompt);
        Assert.EndsWith("What is causing this: ?", prompt);
        Assert.Equal("Shared image", CodexInteractiveService.DeriveTitle("", attachments));
        Assert.Equal(CodexInteractiveService.BuildTitlePrompt("Fix it", []),
            CodexInteractiveService.BuildTitlePrompt("Fix it"));
    }

    [Theory]
    [InlineData("\"Restoring Semantic Discussion Titles.\"", "Restoring Semantic Discussion Titles")]
    [InlineData("preamble\n**Callback Delivery Regression**", "Callback Delivery Regression")]
    [InlineData("", null)]
    public void GeneratedTitleCleanupPreservesFallbackOnBadOutput(string raw, string? expected)
        => Assert.Equal(expected, CodexInteractiveService.CleanGeneratedTitle(raw));
}
