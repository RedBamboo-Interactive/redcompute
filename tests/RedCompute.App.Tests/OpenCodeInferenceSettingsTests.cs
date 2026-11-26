using System.Text.Json;
using RedCompute.Plugin.OpenCode;
using Xunit;

namespace RedCompute.App.Tests;

public sealed class OpenCodeInferenceSettingsTests
{
    [Fact]
    public void Acp_models_keep_only_native_efforts_observed_for_each_model()
    {
        using var first = JsonDocument.Parse("""
            {"configOptions":[
              {"id":"model","currentValue":"probe/model-a","options":[
                {"name":"Probe","options":[{"value":"probe/model-a","name":"A"},{"value":"probe/model-b","name":"B"}]}
              ]},
              {"id":"effort","currentValue":"high","options":[{"value":"high"},{"value":"default"}]}
            ]}
            """);
        var start = OpenCodeSessionService.ParseInferenceOptions(first.RootElement);
        Assert.NotNull(start);
        Assert.Equal("high", start.Effort);
        Assert.Equal(["high", "default"], start.Models[0].SupportedEfforts);
        Assert.Empty(start.Models[1].SupportedEfforts);

        using var second = JsonDocument.Parse("""
            {"configOptions":[
              {"id":"model","currentValue":"probe/model-b","options":[
                {"value":"probe/model-a","name":"A"},{"value":"probe/model-b","name":"B"}]},
              {"id":"effort","currentValue":"medium","options":[{"value":"medium"},{"value":"default"}]}
            ]}
            """);
        var changed = OpenCodeSessionService.ParseInferenceOptions(second.RootElement, start.Models);
        Assert.NotNull(changed);
        Assert.Equal("probe/model-b", changed.Model);
        Assert.Equal("medium", changed.Effort);
        Assert.Equal(["high", "default"], changed.Models[0].SupportedEfforts);
        Assert.Equal(["medium", "default"], changed.Models[1].SupportedEfforts);
    }

    [Fact]
    public void Missing_or_inconsistent_native_acknowledgement_cannot_confirm_a_selection()
    {
        using var missing = JsonDocument.Parse("{}");
        Assert.Null(OpenCodeSessionService.ParseInferenceOptions(missing.RootElement));
        using var invalid = JsonDocument.Parse("""
            {"configOptions":[
              {"id":"model","currentValue":"probe/model-a","options":[{"value":"probe/model-a"}]},
              {"id":"effort","currentValue":"high","options":[{"value":"low"}]}
            ]}
            """);
        Assert.Null(OpenCodeSessionService.ParseInferenceOptions(invalid.RootElement));
    }
}
