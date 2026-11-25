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

    // An Automation's ai-session run is an agent one-shot without a tools grant. It used to get no MCP flags
    // at all, so the CLI loaded the user's and the account's servers (Laurent's claude.ai connectors).
    [Fact]
    public void An_agent_oneshot_without_a_tools_grant_gets_the_strict_session_composition()
    {
        var bare = new List<string>();
        ClaudeSessionService.AddAgentArgs(bare, null, null, 50, tools: null, allowedTools: ["Read"], addDirs: null,
            accessLevel: "full");
        Assert.Contains("--strict-mcp-config", bare);
        Assert.Equal(ClaudeSessionService.EmptyMcpConfig, ValueAfter(bare, "--mcp-config"));

        var withToken = new List<string>();
        ClaudeSessionService.AddAgentArgs(withToken, null, null, 50, tools: null, allowedTools: ["Read"], addDirs: null,
            accessLevel: "full", leafMcpUrl: LeafMcpGateway.UrlFor(WithToken));
        Assert.Contains("--strict-mcp-config", withToken);
        Assert.Equal(ClaudeSessionService.BuildSessionMcpConfig("http://127.0.0.1:18804/api/mcp"), ValueAfter(withToken, "--mcp-config"));
        Assert.DoesNotContain(withToken, arg => arg.Contains("secret-token-value", StringComparison.Ordinal));
    }

    [Fact]
    public void A_tools_grant_keeps_the_agent_oneshot_empty_even_with_a_token()
    {
        var args = new List<string>();
        ClaudeSessionService.AddAgentArgs(args, null, null, 1, tools: ["Read"], allowedTools: null, addDirs: null,
            accessLevel: "full", leafMcpUrl: LeafMcpGateway.UrlFor(WithToken));

        Assert.Single(args, "--strict-mcp-config");
        Assert.Equal(ClaudeSessionService.EmptyMcpConfig, ValueAfter(args, "--mcp-config"));
    }

    private static string ValueAfter(List<string> args, string flag) => args[args.IndexOf(flag) + 1];

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
