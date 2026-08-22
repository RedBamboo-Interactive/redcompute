using System.Text.Json;
using RedCompute.Plugin.Codex;
using Xunit;

namespace RedCompute.Plugin.Codex.Tests;

public sealed class CodexEventMapperPhaseTests
{
    [Fact]
    public void AgentMessageDeltaInheritsPhaseFromItemStart()
    {
        var phases = new Dictionary<string, string>();
        using var started = JsonDocument.Parse("""
            {"item":{"type":"agentMessage","id":"msg-commentary","text":"","phase":"commentary"}}
            """);
        using var delta = JsonDocument.Parse("""
            {"itemId":"msg-commentary","delta":"Checking the transcript."}
            """);

        Assert.Empty(CodexEventMapper.Map("item/started", started.RootElement, phases));
        var mapped = Assert.Single(CodexEventMapper.Map(
            "item/agentMessage/delta", delta.RootElement, phases));

        Assert.Equal("text", mapped.Type);
        Assert.True(mapped.IsPartial);
        Assert.Equal("commentary", mapped.Phase);
    }

    [Theory]
    [InlineData("commentary")]
    [InlineData("final_answer")]
    public void CompletedAgentMessageCarriesAndReleasesPhase(string phase)
    {
        var phases = new Dictionary<string, string>();
        using var started = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            item = new { type = "agentMessage", id = "msg-1", text = "", phase },
        }));
        using var completed = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            item = new { type = "agentMessage", id = "msg-1", text = "Complete text", phase },
        }));

        CodexEventMapper.Map("item/started", started.RootElement, phases);
        var mapped = Assert.Single(CodexEventMapper.Map("item/completed", completed.RootElement, phases));

        Assert.Equal(phase, mapped.Phase);
        Assert.Empty(phases);
    }

    [Fact]
    public void UnknownPhasePreservesLegacyBehavior()
    {
        using var completed = JsonDocument.Parse("""
            {"item":{"type":"agentMessage","id":"msg-1","text":"Complete text","phase":"other"}}
            """);

        var mapped = Assert.Single(CodexEventMapper.Map("item/completed", completed.RootElement));

        Assert.Null(mapped.Phase);
    }
}
