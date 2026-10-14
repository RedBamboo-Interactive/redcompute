using RedCompute.Core.Configuration;
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

    [Theory]
    [InlineData("\"Claude Session Titles.\"", "Claude Session Titles")]
    [InlineData("preamble\n**Fix Claude Titles**", "Fix Claude Titles")]
    [InlineData("", null)]
    public void GeneratedTitleCleanupPreservesFallbackOnBadOutput(string raw, string? expected)
        => Assert.Equal(expected, SessionTitleFormatter.CleanGenerated(raw));
}
