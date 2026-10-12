using System.Text.Json;
using RedCompute.Core.Jobs;
using RedCompute.Plugin.OpenAILive;
using RedCompute.PluginSdk;
using Xunit;

namespace RedCompute.App.Tests;

public sealed class OpenAILiveProviderContractTests
{
    [Fact]
    public void Creation_payload_uses_provider_history_roles_and_never_contains_provenance()
    {
        var provenance = new JobProvenance(JobProvenance.CurrentSchemaVersion,
            new JobOrigin("redleaf", new JobAppReference("app", "nova", null, "Nova"),
                new JobEntrypoint("http", "/voice", "POST")),
            new JobActor("agent", "Nova"), new JobBeneficiary("user", "user-1"), [],
            new JobTrace(), JobProvenanceAssurance.Verified, DateTimeOffset.UtcNow);
        var request = new VoiceSessionCreateRequest("voice-1", "offer", "gpt-live-1", "marin",
            "Speak naturally.", provenance,
            [new("user", "Earlier question"), new("assistant", "Earlier answer")]);

        var json = JsonSerializer.Serialize(OpenAILiveProvider.BuildCreatePayload(request));
        using var document = JsonDocument.Parse(json);
        var input = document.RootElement.GetProperty("session").GetProperty("input");

        Assert.Equal("input_text", input[0].GetProperty("content")[0].GetProperty("type").GetString());
        Assert.Equal("output_text", input[1].GetProperty("content")[0].GetProperty("type").GetString());
        Assert.DoesNotContain("user-1", json, StringComparison.Ordinal);
        Assert.DoesNotContain("provenance", json, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("session.input_transcript.delta")]
    [InlineData("session.output_transcript.delta")]
    public void Normalizes_timed_transcript_events(string type)
    {
        var evt = OpenAILiveProvider.NormalizeEvent($$"""
            {"type":"{{type}}","event_id":"evt_1","delta":"hello ","start_ms":10,"end_ms":20}
            """);

        Assert.NotNull(evt);
        Assert.Equal(type, evt!.Type);
        Assert.Equal("evt_1", evt.EventId);
        Assert.Equal("hello ", evt.Delta);
        Assert.Equal(10, evt.StartMs);
        Assert.Equal(20, evt.EndMs);
    }

    [Fact]
    public void Normalizes_client_delegation_without_inventing_task_text()
    {
        var evt = OpenAILiveProvider.NormalizeEvent("""
            {"type":"session.delegation.created","event_id":"evt_d","offset_ms":1234,
             "delegation":{"id":"item_123","type":"delegation","target":"client"}}
            """);

        Assert.NotNull(evt);
        Assert.Equal("item_123", evt!.DelegationId);
        Assert.Equal(1234, evt.OffsetMs);
        Assert.Null(evt.Delta);
    }

    [Fact]
    public void Ignores_unowned_provider_events()
        => Assert.Null(OpenAILiveProvider.NormalizeEvent("{\"type\":\"session.output_audio.delta\"}"));

    [Fact]
    public void Preview_command_is_server_side_commentary_without_a_delegation()
    {
        var json = JsonSerializer.Serialize(OpenAILiveProvider.BuildPreviewCommand(
            new VoicePreviewRequest("preview_1", "This is the preview.")));
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        Assert.Equal("session.commentary.append", root.GetProperty("type").GetString());
        Assert.Equal("preview_1", root.GetProperty("event_id").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("delegation_id").ValueKind);
        Assert.Equal("This is the preview.", root.GetProperty("content").GetString());
    }
}
