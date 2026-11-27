using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RedCompute.Core.Jobs;
using RedCompute.Plugin.ClaudeCode;
using Xunit;

namespace RedCompute.App.Tests;

public sealed class ClaudeNativeMcpTests
{
    private static readonly Guid Agent = Guid.Parse("1209a868-bdc9-4c45-8bc7-9aa87003d83a");
    private static readonly Guid Server = Guid.Parse("1c09b63b-bdc9-4c45-8bc7-9aa87003d83a");
    private static readonly string Digest = new('a', 64);
    private static readonly string Revision = new('b', 64);

    [Theory]
    [InlineData("https://example.invalid", false)]
    [InlineData("http://localhost:18804", false)]
    [InlineData("http://127.0.0.1:18804/redirect", false)]
    [InlineData("http://127.0.0.1:18804", true)]
    public void Credential_destination_is_a_trusted_loopback_origin(string address, bool permitted)
        => Assert.Equal(permitted, ClaudeNativeMcp.TryPlanUri(address, out _));

    [Fact]
    public void Stale_or_ungranted_plans_never_produce_a_native_launch()
    {
        var plan = MakePlan();
        var registry = MakeRegistry();
        var provenance = Provenance();
        Assert.Empty(ClaudeNativeMcp.Select(plan with { Servers = [] }, registry, "session", provenance));
        Assert.Empty(ClaudeNativeMcp.Select(plan with { DiscussionId = "other" }, registry, "session", provenance));
        Assert.Empty(ClaudeNativeMcp.Select(plan with { OwnerId = "other" }, registry, "session", provenance));
        Assert.Empty(ClaudeNativeMcp.Select(plan with { AgentId = Guid.NewGuid().ToString() }, registry, "session", provenance));
        Assert.Empty(ClaudeNativeMcp.Select(plan, registry, "rotated-session", provenance));
        Assert.Empty(ClaudeNativeMcp.Select(plan, new(registry.ClaudeSha256,
            [registry.Registrations[0] with { Revision = new('c', 64) }]), "session", provenance));

        var args = new List<string> { "--strict-mcp-config", "--mcp-config", ClaudeSessionService.BuildSessionMcpConfig("http://127.0.0.1:18804/api/mcp") };
        var before = args.ToArray();
        Assert.Empty(ClaudeNativeMcp.Append(args, "trusted-helper.exe", []));
        Assert.Equal(before, args);
    }

    [Fact]
    public void Matching_plan_keeps_the_gateway_and_uses_a_fail_closed_read_guard()
    {
        var plan = MakePlan();
        var projected = ClaudeNativeMcp.Select(plan, MakeRegistry(), "session", Provenance());
        var args = new List<string> { "--strict-mcp-config", "--mcp-config", ClaudeSessionService.BuildSessionMcpConfig("http://127.0.0.1:18804/api/mcp") };
        var alias = Assert.Single(ClaudeNativeMcp.Append(args, "trusted-helper.exe", projected));
        var servers = JsonNode.Parse(args[2])!["mcpServers"]!;
        Assert.NotNull(servers["leaf"]);
        Assert.Equal("trusted-helper.exe", servers[alias]!["command"]!.GetValue<string>());
        Assert.Equal("--leaf-mcp-stdio", servers[alias]!["args"]![0]!.GetValue<string>());
        var guard = JsonNode.Parse(args[args.IndexOf("--settings") + 1])!["hooks"]!["PreToolUse"]![0]!["hooks"]![0]!;
        Assert.Equal("block", guard["onFailure"]!.GetValue<string>());
        var allowlist = new HashSet<string>(StringComparer.Ordinal) { guard["args"]![1]!.GetValue<string>() };
        using var allowed = JsonDocument.Parse(JsonSerializer.Serialize(new { hook_event_name = "PreToolUse", tool_name = $"mcp__{alias}__read_note" }));
        using var refused = JsonDocument.Parse(JsonSerializer.Serialize(new { hook_event_name = "PreToolUse", tool_name = $"mcp__{alias}__write_note" }));
        Assert.True(NativeMcpHelper.MayCall(allowed.RootElement, allowlist));
        Assert.False(NativeMcpHelper.MayCall(refused.RootElement, allowlist));
    }

    [WindowsFact("Uses the Windows console process and its real environment block")]
    public async Task Trusted_launcher_pins_the_target_and_gives_the_real_child_no_ambient_credentials()
    {
        const string canary = "LEAF_MCP_TEST_PARENT_SENTINEL";
        Environment.SetEnvironmentVariable(canary, "synthetic");
        try
        {
            var executable = Path.Combine(Environment.SystemDirectory, "cmd.exe");
            var arguments = new[] { "/d", "/c", $"if defined {canary} (exit /b 41) else if defined REDLEAF_EXECUTION_TOKEN (exit /b 42) else if defined ANTHROPIC_API_KEY (exit /b 43) else (echo isolated)" };
            Assert.DoesNotContain(canary, NativeMcpHelper.ChildStartInfo(executable, arguments).Environment.Keys);
            Assert.DoesNotContain("REDLEAF_EXECUTION_TOKEN", NativeMcpHelper.ChildStartInfo(executable, arguments).Environment.Keys);
            Assert.DoesNotContain("ANTHROPIC_API_KEY", NativeMcpHelper.ChildStartInfo(executable, arguments).Environment.Keys);

            using var childInput = new MemoryStream();
            using var output = new MemoryStream();
            using var error = new MemoryStream();
            Assert.Equal(2, await NativeMcpHelper.RunChildAsync(Digest, executable, arguments, childInput, output, error));
            Assert.Empty(output.ToArray());
            using var binary = File.OpenRead(executable);
            var sha = Convert.ToHexString(SHA256.HashData(binary));
            Assert.Equal(0, await NativeMcpHelper.RunChildAsync(sha, executable, arguments, childInput, output, error));
            Assert.Equal("isolated", Encoding.UTF8.GetString(output.ToArray()).Trim());
        }
        finally
        {
            Environment.SetEnvironmentVariable(canary, null);
        }
    }

    private static ClaudeNativeMcp.Plan MakePlan() => new(1, "session", Agent.ToString(), "owner", "discussion",
        [new(Server, new("fixture", Digest, Revision, ["read_note"]))]);

    private static ClaudeNativeMcp.HostRegistry MakeRegistry() => new(Digest,
        [new(Server, "fixture", Revision, Digest, Agent, "owner", "discussion", @"C:\trusted\fixture.exe", [])]);

    private static JobProvenance Provenance() => JobProvenance.DirectRedCompute(
        "POST", "/ai-session/sessions", new("user", "owner"), "request", null) with
    {
        Origin = new("redleaf", new("app", "nova", null, "Nova"), new("api", "/api/apps/nova/discussions")),
        Actor = new("agent", "Synthetic", Agent.ToString()),
        Context = [new("discussion", "discussion")],
    };
}
