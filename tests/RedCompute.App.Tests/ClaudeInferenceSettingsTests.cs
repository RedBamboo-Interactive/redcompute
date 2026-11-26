using System.Text.Json;
using RedCompute.Plugin.ClaudeCode;
using RedCompute.Core.Sessions;
using Xunit;

namespace RedCompute.App.Tests;

public sealed class ClaudeInferenceSettingsTests
{
    [Fact]
    public void Catalogue_resolves_aliases_and_preserves_auto_permissions()
    {
        using var document = JsonDocument.Parse("""
            {"models":[
                {"value":"default","resolvedModel":"claude-a","displayName":"Default","supportsAutoMode":true},
                {"value":"opus","resolvedModel":"claude-a","displayName":"Opus","supportsAutoMode":true,
                    "supportedEffortLevels":["low","high"]},
                {"value":"haiku","resolvedModel":"claude-b","displayName":"Haiku","supportsAutoMode":false,
                    "supportedEffortLevels":["medium"]},
                {"value":"missing","displayName":"No resolved model"}
            ]}
            """);
        var catalog = ClaudeSessionService.ParseInferenceModels(document.RootElement);
        var ordinary = ClaudeSessionService.InferenceOptions("opus", "low", catalog, SessionAccessLevels.Ask);
        Assert.Equal("claude-a", ordinary.Model);
        Assert.Equal(["claude-a", "claude-b"], ordinary.Models.Select(model => model.Id));
        Assert.Equal(["low", "high"], ordinary.Models[0].SupportedEfforts);

        var auto = ClaudeSessionService.InferenceOptions("opus", "high", catalog, SessionAccessLevels.Auto);
        var offered = Assert.Single(auto.Models);
        Assert.Equal("claude-a", offered.Id);
        Assert.Equal("Opus", offered.Name);
    }

    [Fact]
    public void Inference_controls_do_not_touch_the_model_for_an_effort_only_update()
    {
        var settings = ClaudeSessionService.InferenceFlagSettings("claude-a", "claude-a", "high");
        Assert.Single(settings);
        Assert.Equal("high", settings["effortLevel"]);

        settings = ClaudeSessionService.InferenceFlagSettings("claude-a", "claude-b", null);
        Assert.Equal("claude-b", settings["model"]);
        Assert.Contains("effortLevel", settings.Keys);
        Assert.Null(settings["effortLevel"]);
    }
}
