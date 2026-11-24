using System.Text.Json;
using RedCompute.Plugin.ClaudeCode;
using RedCompute.Plugin.OpenCode;
using RedCompute.PluginSdk;
using Xunit;

namespace RedCompute.App.Tests;

// Every Leaf launch gets RedLeaf's MCP gateway as its only MCP server when it holds an execution token,
// and none without one; the token is referenced, never written into a command line.
public sealed class LeafMcpGatewayLaunchTests
{
    private static readonly Dictionary<string, string> WithToken = new(StringComparer.OrdinalIgnoreCase)
    {
        ["REDLEAF_EXECUTION_TOKEN"] = "secret-token-value-1234567890",
        ["REDLEAF_URL"] = "http://127.0.0.1:18804/",
        ["REDCOMPUTE_URL"] = "http://127.0.0.1:18800",
    };

    [Fact]
    public void The_gateway_url_needs_both_the_token_and_redleafs_url()
    {
        Assert.Equal("http://127.0.0.1:18804/api/mcp", LeafMcpGateway.UrlFor(WithToken));
        Assert.Null(LeafMcpGateway.UrlFor(null));
        Assert.Null(LeafMcpGateway.UrlFor(new Dictionary<string, string> { ["REDLEAF_EXECUTION_TOKEN"] = "" , ["REDLEAF_URL"] = "http://x" }));
        Assert.Null(LeafMcpGateway.UrlFor(new Dictionary<string, string> { ["REDLEAF_EXECUTION_TOKEN"] = "t" }));
    }

    [Fact]
    public void Claude_gets_leaf_with_the_token_as_a_variable_reference()
    {
        var config = ClaudeSessionService.BuildSessionMcpConfig(LeafMcpGateway.UrlFor(WithToken));

        Assert.DoesNotContain("secret-token-value", config);
        using var json = JsonDocument.Parse(config);
        var servers = json.RootElement.GetProperty("mcpServers");
        Assert.Equal(["leaf"], servers.EnumerateObject().Select(s => s.Name));
        var leaf = servers.GetProperty("leaf");
        Assert.Equal("http", leaf.GetProperty("type").GetString());
        Assert.Equal("http://127.0.0.1:18804/api/mcp", leaf.GetProperty("url").GetString());
        Assert.Equal("Bearer ${REDLEAF_EXECUTION_TOKEN}", leaf.GetProperty("headers").GetProperty("Authorization").GetString());
        Assert.Equal(ClaudeSessionService.EmptyMcpConfig, ClaudeSessionService.BuildSessionMcpConfig(null));
    }

    [Fact]
    public void Claude_flags_any_server_it_reports_that_leaf_did_not_configure()
    {
        using var init = JsonDocument.Parse("""{"mcp_servers":[{"name":"leaf","status":"connected"},{"name":"claude.ai Gmail","status":"needs-auth"}]}""");
        Assert.Equal(["claude.ai Gmail"], ClaudeSessionService.UnexpectedMcpServers(init.RootElement));
        using var clean = JsonDocument.Parse("""{"mcp_servers":[{"name":"leaf","status":"connected"}]}""");
        Assert.Empty(ClaudeSessionService.UnexpectedMcpServers(clean.RootElement));
    }

    [Fact]
    public void OpenCode_gets_leaf_over_http_in_the_acp_request_only_with_a_token()
    {
        var servers = JsonSerializer.SerializeToElement(OpenCodeSessionService.BuildSessionMcpServers(WithToken));
        var leaf = Assert.Single(servers.EnumerateArray());
        Assert.Equal("http", leaf.GetProperty("type").GetString());
        Assert.Equal("leaf", leaf.GetProperty("name").GetString());
        Assert.Equal("http://127.0.0.1:18804/api/mcp", leaf.GetProperty("url").GetString());
        var header = Assert.Single(leaf.GetProperty("headers").EnumerateArray());
        Assert.Equal(("Authorization", "Bearer secret-token-value-1234567890"),
            (header.GetProperty("name").GetString(), header.GetProperty("value").GetString()));

        Assert.Empty(OpenCodeSessionService.BuildSessionMcpServers(new Dictionary<string, string>()));
    }
}
