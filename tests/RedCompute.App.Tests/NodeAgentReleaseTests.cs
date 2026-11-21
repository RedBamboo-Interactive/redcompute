using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using RedBamboo.AppHost.Discovery;
using RedCompute.App.Api.Endpoints;
using RedCompute.App.Services;
using RedCompute.App.Services.Nodes;
using Xunit;

namespace RedCompute.App.Tests;

/// <summary>
/// GET /nodes/agent (Phase 3 R7): where the Nightly published leaf-node, read from the signed feed the way the
/// installers read it, or available=false so the "Add a node" dialog keeps its plain pairing text.
/// </summary>
public sealed class NodeAgentReleaseTests
{
    private const string Feed = "https://raw.githubusercontent.com/RedBamboo-Interactive/redleaf-releases/release-feed";
    private const string Release = "https://github.com/RedBamboo-Interactive/redleaf-releases/releases/download/redleaf-abc";
    private static readonly string Commit = new('a', 40);
    private static readonly string WindowsSha = new('1', 64);
    private static readonly string LinuxSha = new('2', 64);

    [Fact]
    public async Task A_published_agent_is_found_through_the_redcompute_record()
    {
        var feed = new FakeFeed();

        var download = await new NodeAgentRelease(feed, Feed).GetAsync(CancellationToken.None);

        Assert.True(download.Available, download.Reason);
        Assert.Equal("0.1.24", download.Version);
        Assert.Equal(Commit, download.SourceCommit);
        Assert.Equal("https://github.com/RedBamboo-Interactive/redleaf-releases/releases/tag/redleaf-abc", download.ReleaseUrl);
        Assert.Equal($"{Release}/leaf-node-manifest.v1.json", download.ManifestUrl);
        Assert.Equal($"{Release}/leaf-node-win-x64.zip", download.Windows!.ArchiveUrl);
        Assert.Equal(WindowsSha, download.Windows.Sha256);
        Assert.Equal(36_000_000, download.Windows.SizeBytes);
        Assert.Equal($"irm {Release}/leaf-node-install.ps1 | iex; Install-LeafNode", download.Windows.InstallCommand);
        Assert.Equal($"{Release}/leaf-node-linux-x64.tar.gz", download.Linux!.ArchiveUrl);
        Assert.Equal($"curl -fsSL {Release}/leaf-node-install.sh | sh", download.Linux.InstallCommand);
        // The install commands never pair: pairing is a separate step with the code.
        Assert.DoesNotContain("pair", download.Windows.InstallCommand + download.Linux.InstallCommand);
        Assert.Equal([$"{Feed}/components/redcompute/channels/nightly.json", $"{Feed}/components/redcompute/releases/redleaf-abc.json",
            $"{Release}/leaf-node-manifest.v1.json"], feed.Requests);
    }

    [Fact]
    public async Task A_release_without_leaf_node_is_not_available_and_says_so()
    {
        var feed = new FakeFeed { Manifest = null };

        var download = await new NodeAgentRelease(feed, Feed).GetAsync(CancellationToken.None);

        Assert.False(download.Available);
        Assert.Equal("RedCompute 0.1.24 was released without leaf-node.", download.Reason);
        Assert.Null(download.Windows);
        Assert.Null(download.Linux);
    }

    [Theory]
    [InlineData("record-hash", "The RedCompute record does not match the hash its pointer names.")]
    [InlineData("other-version", "The leaf-node manifest does not belong to RedCompute 0.1.24.")]
    [InlineData("other-commit", "The leaf-node manifest does not belong to RedCompute 0.1.24.")]
    [InlineData("missing-linux", "The leaf-node manifest of RedCompute 0.1.24 is incomplete.")]
    [InlineData("bad-hash", "The leaf-node manifest of RedCompute 0.1.24 is incomplete.")]
    public async Task Anything_that_does_not_check_out_is_not_offered(string tamper, string reason)
    {
        var feed = new FakeFeed();
        switch (tamper)
        {
            case "record-hash": feed.PointerSha = new string('f', 64); break;
            case "other-version": feed.Manifest!["version"] = "0.1.23"; break;
            case "other-commit": feed.Manifest!["sourceCommit"] = new string('b', 40); break;
            case "missing-linux": feed.Manifest!["artifacts"]!.AsArray().RemoveAt(1); break;
            case "bad-hash": feed.Manifest!["artifacts"]![0]!["sha256"] = "not-a-hash"; break;
        }

        var download = await new NodeAgentRelease(feed, Feed).GetAsync(CancellationToken.None);

        Assert.False(download.Available);
        Assert.Equal(reason, download.Reason);
        Assert.Null(download.Windows);
    }

    [Fact]
    public async Task A_feed_that_is_not_https_is_never_read()
    {
        var feed = new FakeFeed();

        var download = await new NodeAgentRelease(feed, "http://example.invalid/feed").GetAsync(CancellationToken.None);

        Assert.False(download.Available);
        Assert.Contains("https only", download.Reason);
        Assert.Empty(feed.Requests);
    }

    [Fact]
    public async Task The_answer_is_kept_ten_minutes_and_a_failure_one_minute()
    {
        var clock = new ManualClock();
        var feed = new FakeFeed();
        var release = new NodeAgentRelease(feed, Feed, clock);

        await release.GetAsync(CancellationToken.None);
        clock.Advance(TimeSpan.FromMinutes(9));
        await release.GetAsync(CancellationToken.None);
        Assert.Equal(3, feed.Requests.Count);
        clock.Advance(TimeSpan.FromMinutes(2));
        feed.Fail = true;
        Assert.False((await release.GetAsync(CancellationToken.None)).Available);
        Assert.Equal(4, feed.Requests.Count);
        feed.Fail = false;
        clock.Advance(TimeSpan.FromSeconds(30));
        Assert.False((await release.GetAsync(CancellationToken.None)).Available);
        clock.Advance(TimeSpan.FromSeconds(31));
        Assert.True((await release.GetAsync(CancellationToken.None)).Available);
    }

    [Fact]
    public async Task The_route_answers_with_the_download_and_is_described_in_discovery()
    {
        await using var app = WebApplication.CreateSlimBuilder().Build();
        var endpoints = new EndpointRegistry(app);
        var nodes = new NodeRegistry(new NodeIdentityResult(new NodeIdentity("node_01JCZ0000000000000000GOLD", DateTimeOffset.UtcNow,
            "node_01JCZ0000000000000000GOLD"), true, false, null), NodeRegistryTests.Platform(), "0.2.0", DateTimeOffset.UtcNow);
        NodeEndpoints.Map(endpoints, nodes, new CapabilityRegistry(), () => null, agentRelease: new NodeAgentRelease(new FakeFeed(), Feed));

        var endpoint = ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints).OfType<RouteEndpoint>()
            .Single(candidate => candidate.RoutePattern.RawText == "/nodes/agent");
        var context = new DefaultHttpContext { RequestServices = app.Services };
        context.Request.Method = "GET";
        context.Response.Body = new MemoryStream();
        await endpoint.RequestDelegate!(context);
        context.Response.Body.Position = 0;
        using var json = await JsonDocument.ParseAsync(context.Response.Body);

        Assert.Equal(200, context.Response.StatusCode);
        Assert.True(json.RootElement.GetProperty("available").GetBoolean());
        Assert.Equal("nightly", json.RootElement.GetProperty("channel").GetString());
        Assert.Equal(LinuxSha, json.RootElement.GetProperty("linux").GetProperty("sha256").GetString());
        Assert.Contains(endpoints.GetEndpoints(), described => described.Path == "/nodes/agent" && described.Description.Contains("leaf-node pair"));
    }

    /// <summary>The signed feed's three documents for RedCompute 0.1.24, in memory.</summary>
    private sealed class FakeFeed : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];
        public bool Fail { get; set; }
        public string? PointerSha { get; set; }

        public JsonObject? Manifest { get; set; } = new()
        {
            ["schemaVersion"] = 1, ["componentId"] = "leaf-node", ["version"] = "0.1.24", ["sourceCommit"] = Commit,
            ["artifacts"] = new JsonArray(
                new JsonObject { ["rid"] = "win-x64", ["operatingSystem"] = "windows", ["architecture"] = "x64",
                    ["name"] = "leaf-node-win-x64.zip", ["sha256"] = WindowsSha, ["sizeBytes"] = 36_000_000 },
                new JsonObject { ["rid"] = "linux-x64", ["operatingSystem"] = "linux", ["architecture"] = "x64",
                    ["name"] = "leaf-node-linux-x64.tar.gz", ["sha256"] = LinuxSha, ["sizeBytes"] = 34_000_000 }),
        };

        private static readonly byte[] Record = Encoding.UTF8.GetBytes(new JsonObject
        {
            ["schemaVersion"] = 1,
            ["payload"] = new JsonObject
            {
                ["componentId"] = "redcompute", ["version"] = "0.1.24", ["sourceCommit"] = Commit,
                ["targetPlatform"] = new JsonObject { ["operatingSystem"] = "windows", ["architecture"] = "x64" },
                ["artifact"] = new JsonObject { ["url"] = $"{Release}/redcompute-win-x64-0123.zip", ["sha256"] = new string('0', 64) },
            },
        }.ToJsonString());

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            Requests.Add(url);
            if (Fail) throw new HttpRequestException("offline");
            byte[]? body = url switch
            {
                $"{Feed}/components/redcompute/channels/nightly.json" => Encoding.UTF8.GetBytes(new JsonObject
                {
                    ["schemaVersion"] = 1,
                    ["payload"] = new JsonObject
                    {
                        ["record"] = new JsonObject
                        {
                            ["url"] = $"{Feed}/components/redcompute/releases/redleaf-abc.json",
                            ["sha256"] = PointerSha ?? Convert.ToHexStringLower(SHA256.HashData(Record)),
                        },
                    },
                }.ToJsonString()),
                $"{Feed}/components/redcompute/releases/redleaf-abc.json" => Record,
                $"{Release}/leaf-node-manifest.v1.json" => Manifest is null ? null : Encoding.UTF8.GetBytes(Manifest.ToJsonString()),
                _ => null,
            };
            return Task.FromResult(body is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) });
        }
    }

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 11, 21, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }
}
