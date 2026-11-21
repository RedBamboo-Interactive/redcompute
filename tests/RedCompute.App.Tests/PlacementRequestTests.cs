using System.Text.Json;
using RedCompute.App.Services.Nodes;
using RedCompute.App.Services.Placement;
using RedCompute.PluginSdk;
using Xunit;

namespace RedCompute.App.Tests;

/// <summary><c>placement</c> parsing and validation (Phase 2 design, sections 2.1 and 2.4).</summary>
public sealed class PlacementRequestTests
{
    private const string Agent = "7e1e0000-0000-4000-8000-0000000000a1";
    private const string Repository = "7e1e0000-0000-4000-8000-000000000001";
    private const string Checkout = "7e1e0000-0000-4000-8000-0000000000c1";

    private static (bool Ok, PlacementRequest? Placement, PlacementError? Error) Parse(string json,
        PlacementRoute route = PlacementRoute.Session, bool conversationOnly = false)
    {
        using var document = JsonDocument.Parse(json);
        var ok = PlacementParser.TryParse(document.RootElement, route, conversationOnly, out var placement, out var error);
        return (ok, placement, error);
    }

    [Theory]
    [InlineData("""{"projectPath":"C:\\p"}""")]
    [InlineData("""{"placement":null}""")]
    [InlineData("[]")]
    public void No_placement_is_not_an_error(string body)
    {
        var (ok, placement, error) = Parse(body);

        Assert.True(ok);
        Assert.Null(placement);
        Assert.Null(error);
    }

    [Fact]
    public void A_full_placement_keeps_every_reference()
    {
        var (ok, placement, error) = Parse($$$"""
            {"placement":{"nodeId":"local","repository":{"repositoryId":"{{{Repository}}}","checkoutId":"{{{Checkout}}}","branch":"feature/x","subPath":"inner/repo"},
             "scratch":{"owner":"Nova","executionKey":"attempt-1/node run"},"scratchAs":"env"}}
            """, PlacementRoute.Execute);

        Assert.True(ok, error?.Message);
        Assert.Equal(new PlacementRequest("local", null,
            new RepositoryPlacement(Guid.Parse(Repository), Guid.Parse(Checkout), "feature/x", "inner/repo"),
            new ScratchPlacement("Nova", "attempt-1/node run")), placement);
        Assert.Equal($$"""
            {"nodeId":"local","repository":{"repositoryId":"{{Repository}}","checkoutId":"{{Checkout}}","branch":"feature/x","subPath":"inner/repo"},"scratch":{"owner":"Nova","executionKey":"attempt-1/node run"},"scratchAs":"env"}
            """.Trim(), placement!.ToJson().ToJsonString());
    }

    [Fact]
    public void A_workspace_placement_defaults_to_scratch_in_the_environment()
    {
        var (ok, placement, _) = Parse($$$$"""
            {"placement":{"nodeId":null,"workspace":{"agentId":"{{{{Agent.ToUpperInvariant()}}}}"},"scratch":{"owner":"nova","executionKey":"d1"}}}
            """);

        Assert.True(ok);
        Assert.Null(placement!.NodeId);
        Assert.Equal(Guid.Parse(Agent), placement.Workspace!.AgentId);
        Assert.Equal(PlacementRequest.ScratchAsEnv, placement.ScratchAs);
        Assert.Equal(Agent, placement.ToJson()["workspace"]!["agentId"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("""{"placement":"local"}""", "placement")]
    [InlineData("""{"placement":{"node":"local"}}""", "placement.node")]
    [InlineData("""{"placement":{"nodeId":7}}""", "placement.nodeId")]
    [InlineData("""{"placement":{"nodeId":" "}}""", "placement.nodeId")]
    [InlineData("""{"placement":{"workspace":"nova"}}""", "placement.workspace")]
    [InlineData("""{"placement":{"workspace":{}}}""", "placement.workspace.agentId")]
    [InlineData("""{"placement":{"workspace":{"agentId":"nova"}}}""", "placement.workspace.agentId")]
    [InlineData("""{"placement":{"workspace":{"agentId":"7e1e0000-0000-4000-8000-0000000000a1","path":"C:\\x"}}}""", "placement.workspace.path")]
    [InlineData("""{"placement":{"repository":{"branch":"main"}}}""", "placement.repository.repositoryId")]
    [InlineData("""{"placement":{"repository":{"repositoryId":"7e1e0000-0000-4000-8000-000000000001","checkoutId":"c1"}}}""", "placement.repository.checkoutId")]
    [InlineData("""{"placement":{"scratch":{"owner":"nova"}}}""", "placement.scratch.executionKey")]
    [InlineData("""{"placement":{"scratch":{"owner":"","executionKey":"k"}}}""", "placement.scratch.owner")]
    [InlineData("""{"placement":{"scratch":{"owner":"nova","executionKey":"a\u0000b"}}}""", "placement.scratch.executionKey")]
    [InlineData("""{"placement":{"scratchAs":"cwd"}}""", "placement.scratchAs")]
    [InlineData("""{"placement":{"scratch":{"owner":"nova","executionKey":"k"},"scratchAs":"tmp"}}""", "placement.scratchAs")]
    public void Malformed_members_are_422_naming_the_member(string body, string member)
    {
        var (ok, placement, error) = Parse(body);

        Assert.False(ok);
        Assert.Null(placement);
        Assert.Equal(422, error!.Status);
        Assert.Equal("validation_failed", error.Code);
        Assert.Equal(member, Assert.Single(error.Fields!).Key);
    }

    [Theory]
    [InlineData("../outside")]
    [InlineData("inner/../../outside")]
    [InlineData("..\\outside")]
    [InlineData("/rooted")]
    [InlineData("\\rooted")]
    [InlineData("C:\\elsewhere")]
    [InlineData("C:relative")]
    [InlineData(".")]
    [InlineData("./")]
    public void A_sub_path_must_stay_inside_the_checkout(string subPath)
    {
        var (ok, _, error) = Parse(JsonSerializer.Serialize(new
        {
            placement = new { repository = new { repositoryId = Repository, subPath } },
        }));

        Assert.False(ok);
        Assert.Equal("placement.repository.subPath", Assert.Single(error!.Fields!).Key);
    }

    [Theory]
    [InlineData("inner")]
    [InlineData("inner/repo")]
    [InlineData("inner\\repo")]
    [InlineData("./inner")]
    public void A_relative_sub_path_is_accepted(string subPath)
    {
        var (ok, placement, _) = Parse(JsonSerializer.Serialize(new
        {
            placement = new { repository = new { repositoryId = Repository, subPath } },
        }));

        Assert.True(ok);
        Assert.Equal(subPath, placement!.Repository!.SubPath);
    }

    [Fact]
    public void A_workspace_and_a_repository_together_are_refused()
    {
        var (ok, _, error) = Parse($$$$"""
            {"placement":{"workspace":{"agentId":"{{{{Agent}}}}"},"repository":{"repositoryId":"{{{{Repository}}}}"}}}
            """);

        Assert.False(ok);
        Assert.Equal("placement", Assert.Single(error!.Fields!).Key);
    }

    [Theory]
    [InlineData(PlacementRoute.Session, "add-dir", false)]
    [InlineData(PlacementRoute.Execute, "add-dir", true)]
    [InlineData(PlacementRoute.Session, "cwd", true)]
    [InlineData(PlacementRoute.Execute, "cwd", true)]
    [InlineData(PlacementRoute.Session, "env", true)]
    public void Scratch_use_depends_on_the_route(PlacementRoute route, string scratchAs, bool accepted)
    {
        var (ok, placement, error) = Parse(JsonSerializer.Serialize(new
        {
            placement = new { scratch = new { owner = "nova", executionKey = "k" }, scratchAs },
        }), route);

        Assert.Equal(accepted, ok);
        if (accepted) Assert.Equal(scratchAs, placement!.ScratchAs);
        else Assert.Equal("placement.scratchAs", Assert.Single(error!.Fields!).Key);
    }

    [Theory]
    [InlineData("workspace")]
    [InlineData("repository")]
    public void Scratch_as_the_working_directory_excludes_a_workspace_or_a_repository(string member)
    {
        var placement = new Dictionary<string, object>
        {
            ["scratch"] = new { owner = "nova", executionKey = "k" },
            ["scratchAs"] = "cwd",
            [member] = member == "workspace" ? new { agentId = Agent } : new { repositoryId = Repository },
        };
        var (ok, _, error) = Parse(JsonSerializer.Serialize(new { placement }), PlacementRoute.Execute);

        Assert.False(ok);
        Assert.Equal("placement.scratchAs", Assert.Single(error!.Fields!).Key);
    }

    [Theory]
    [InlineData("""{"placement":{"nodeId":"local"}}""", true)]
    [InlineData("""{"placement":{"nodeId":"local","workspace":null}}""", true)]
    [InlineData("""{"placement":{"workspace":{"agentId":"7e1e0000-0000-4000-8000-0000000000a1"}}}""", false)]
    [InlineData("""{"placement":{"scratch":{"owner":"nova","executionKey":"k"}}}""", false)]
    public void Conversation_only_sessions_take_a_node_only(string body, bool accepted)
    {
        var (ok, _, error) = Parse(body, conversationOnly: true);

        Assert.Equal(accepted, ok);
        if (!accepted) Assert.Equal("validation_failed", error!.Code);
    }

    [Theory]
    [InlineData("""{"placement":{"nodeId":"local"}}""", null)]
    [InlineData("""{"placement":{"nodeId":null}}""", null)]
    [InlineData("""{"placement":{"workspace":{"agentId":"7e1e0000-0000-4000-8000-0000000000a1"}}}""", "placement.workspace")]
    [InlineData("""{"placement":{"scratch":{"owner":"nova","executionKey":"k"}}}""", "placement.scratch")]
    [InlineData("""{"placement":{"scratchAs":"env"}}""", "placement.scratchAs")]
    public void Generate_takes_a_node_only(string body, string? refused)
    {
        var (ok, _, error) = Parse(body, PlacementRoute.Generate);

        Assert.Equal(refused is null, ok);
        if (refused is null) return;
        Assert.Equal(422, error!.Status);
        Assert.Equal("placement_member_not_supported", error.Code);
        Assert.Equal(refused, Assert.Single(error.Fields!).Key);
    }

    // ── nodes ─────────────────────────────────────────────────────────────────────────────────

    private const string NodeZero = "node_01JCZ0000000000000000PLAC";
    private const string Legacy = "node_01JCZ00000000000000LEGACY";

    private static NodeRegistry Nodes() => new(
        new NodeIdentityResult(new NodeIdentity(NodeZero, DateTimeOffset.UtcNow.AddDays(-1), Legacy), true, false, null),
        NodeRegistryTests.Platform(), "0.2.0", DateTimeOffset.UtcNow, () => DateTimeOffset.UtcNow);

    [Theory]
    [InlineData("local", NodeZero)]
    [InlineData("LOCAL", NodeZero)]
    [InlineData(NodeZero, NodeZero)]
    [InlineData(Legacy, NodeZero)]
    public void A_known_node_resolves_to_its_id(string requested, string expected)
    {
        var error = PlacementParser.ResolveNode(new PlacementRequest(requested, null, null, null), Nodes(), null, out var nodeId);

        Assert.Null(error);
        Assert.Equal(expected, nodeId);
    }

    [Theory]
    [InlineData("node_01JCZ000000000000000OTHER")]
    [InlineData("7e1e0000-0000-4000-8000-0000000000a1")]
    public void An_unknown_node_is_404_with_its_id(string requested)
    {
        var error = PlacementParser.ResolveNode(new PlacementRequest(requested, null, null, null), Nodes(), null, out var nodeId);

        Assert.Null(nodeId);
        Assert.Equal(404, error!.Status);
        Assert.Equal("node_not_found", error.Code);
        Assert.Equal(requested, error.NodeId);
    }

    [Fact]
    public void A_null_node_is_routed_later()
    {
        Assert.Null(PlacementParser.ResolveNode(new PlacementRequest(null, null, null, null), Nodes(), null, out var nodeId));
        Assert.Null(nodeId);
    }

    [Theory]
    [InlineData("local", true)]
    [InlineData(NodeZero, false)]
    public void Without_a_registry_only_the_local_alias_is_known(string requested, bool known)
    {
        var error = PlacementParser.ResolveNode(new PlacementRequest(requested, null, null, null), null, null, out var nodeId);

        Assert.Equal(known, error is null);
        Assert.Equal(known ? INodeRegistry.LocalAlias : null, nodeId);
    }
}
