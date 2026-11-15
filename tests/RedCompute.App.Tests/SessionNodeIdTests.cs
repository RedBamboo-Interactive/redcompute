using System.Net;
using System.Text;
using System.Text.Json;
using RedCompute.App.Api.Endpoints;
using RedCompute.App.Services;
using RedCompute.App.Services.Nodes;
using RedCompute.Core.Configuration;
using RedCompute.Core.Sessions;
using RedCompute.PluginSdk;
using Xunit;

namespace RedCompute.App.Tests;

/// <summary>
/// Node zero, C4 (design 2.2): a session's node comes from the provider or the ai-session mirror's
/// <c>node_id</c>, and the API never reports it as null: a session older than node ids is the
/// legacy node.
/// </summary>
public sealed class SessionNodeIdTests
{
    [Theory]
    [InlineData("node_mirrored", "node_mirrored")]
    [InlineData(null, null)]
    public async Task The_mirror_reader_takes_node_id_as_recorded(string? mirrored, string? expected)
    {
        var cachePath = Path.Combine(Path.GetTempPath(), $"redcompute-session-node-{Guid.NewGuid():N}.json");
        try
        {
            var config = new RedComputeConfig { RedLeafUrl = "http://redleaf" };
            var log = new Action<string, Guid?>((_, _) => { });
            var quality = new QualityModeService(config, log, new ProviderConfigService(config, log),
                new HttpClient(new MirrorHandler(null)), cachePath, TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(2));
            var reader = new RedLeafSessionReader(
                new HttpClient(new MirrorHandler(mirrored)) { BaseAddress = new Uri("http://redleaf/") }, quality);

            var (info, _) = await reader.GetSessionAsync("session-1", tail: 1);

            Assert.NotNull(info);
            Assert.Equal(expected, info!.NodeId);
        }
        finally
        {
            File.Delete(cachePath);
        }
    }

    [Fact]
    public void The_api_reports_an_unrecorded_node_as_the_legacy_node()
    {
        INodeRegistry nodes = new NodeRegistry(
            new NodeIdentityResult(new NodeIdentity("node_now", DateTimeOffset.UtcNow, "node_legacy"), true, false, null),
            NodeRegistryTests.Platform(), "0.2.0", DateTimeOffset.UtcNow);

        Assert.Equal("node_legacy", UnifiedSessionEndpoints.WithNode(Session(null), nodes).NodeId);
        Assert.Equal("node_elsewhere", UnifiedSessionEndpoints.WithNode(Session("node_elsewhere"), nodes).NodeId);
        // Without a registry (tests, tools) the recorded value passes through.
        Assert.Null(UnifiedSessionEndpoints.WithNode(Session(null), null).NodeId);
    }

    [Fact]
    public void The_snapshot_and_the_session_carry_the_field()
    {
        var snapshot = new AiSessionSnapshot { Provider = "claude-code", Id = "s", NodeId = "node_now" };
        Assert.Equal("node_now", snapshot.NodeId);
        Assert.Null(new AiSessionSnapshot { Provider = "claude-code", Id = "s" }.NodeId);
        Assert.Equal("node_now", JsonSerializer.SerializeToElement(Session("node_now"),
            new JsonSerializerOptions(JsonSerializerDefaults.Web)).GetProperty("nodeId").GetString());
    }

    private static UnifiedSessionInfo Session(string? nodeId) => new()
    {
        Id = "s", Provider = "claude-code", ProjectName = "p", ProjectPath = "/p", NodeId = nodeId,
    };

    private sealed class MirrorHandler(string? nodeId) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.PathAndQuery;
            var data = new Dictionary<string, object?>
            {
                ["session_id"] = "session-1",
                ["provider"] = "codex",
                ["status"] = "Idle",
                ["started_at"] = "2026-08-02T18:00:00Z",
            };
            if (nodeId is not null) data["node_id"] = nodeId;
            object body = path.StartsWith("/api/entities", StringComparison.Ordinal)
                ? new { items = new[] { new { id = "entity-1", name = "Node test", data = JsonSerializer.Serialize(data) } } }
                : new { items = Array.Empty<object>() };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
            });
        }
    }
}
