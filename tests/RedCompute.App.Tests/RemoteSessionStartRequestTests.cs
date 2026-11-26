using System.Text.Json;
using RedCompute.App.Api.Endpoints;
using RedCompute.App.Services.Nodes;
using RedCompute.App.Services.Placement;
using Xunit;

namespace RedCompute.App.Tests;

public sealed class RemoteSessionStartRequestTests
{
    private const string RemotePlacement = """
        "placement": {"nodeId":"remote-one","sessionRuntime":{"id":"sessions","workspace":"probe-workspace","revision":"a0b1c2","providerInstanceId":"probe"}}
        """;

    [Fact]
    public void Explicit_remote_start_and_validate_share_the_same_target()
    {
        using var start = JsonDocument.Parse("{" + RemotePlacement + """, "provider":"codex","model":"probe-model","effort":"low","validateOnly":true}""");
        Assert.True(RemoteSessionStartRequest.IsRequested(start.RootElement));
        var request = RemoteSessionStartRequest.Parse(start.RootElement, "stable-key");
        Assert.Equal("stable-key", request.IdempotencyKey);
        Assert.True(request.ValidateOnly);
        Assert.Equal(new RemoteSessionPlacement("remote-one", "sessions", "probe-workspace", "a0b1c2",
            "codex", "probe", "probe-model", "low"), request.Placement);
    }

    [Theory]
    [InlineData(PlacementRoute.Execute)]
    [InlineData(PlacementRoute.Generate)]
    public void A_session_runtime_target_cannot_be_used_for_generic_routes(PlacementRoute route)
    {
        using var doc = JsonDocument.Parse("{" + RemotePlacement + "}");
        Assert.False(PlacementParser.TryParse(doc.RootElement, route, false, out _, out _));
    }

    [Theory]
    [InlineData(", \"projectPath\": \"C:/raw\"")]
    [InlineData(", \"endpointUrl\": \"https://example.invalid\"")]
    [InlineData(", \"executionProfile\": \"conversation-only\"")]
    [InlineData(", \"validateOnly\": \"yes\"")]
    public void Remote_start_refuses_local_or_unsupported_options(string extra)
    {
        using var doc = JsonDocument.Parse("{" + RemotePlacement + ", \"provider\":\"codex\"" + extra + "}");
        var error = Assert.Throws<ControllerRemoteSessionException>(() => RemoteSessionStartRequest.Parse(doc.RootElement, "start"));
        Assert.Equal(422, error.Status);
    }

    [Fact]
    public void Runtime_marker_does_not_silently_fall_back_for_a_local_node()
    {
        using var doc = JsonDocument.Parse("{" + RemotePlacement.Replace("remote-one", "local") + ", \"provider\":\"codex\"}");
        Assert.True(RemoteSessionStartRequest.IsRequested(doc.RootElement));
        var error = Assert.Throws<ControllerRemoteSessionException>(() => RemoteSessionStartRequest.Parse(doc.RootElement, "start"));
        Assert.Equal(422, error.Status);
    }
}
