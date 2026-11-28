using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using RedCompute.Core.Jobs;
using RedCompute.Plugin.ClaudeCode;
using RedCompute.PluginSdk.Hosting;
using RedCompute.PluginSdk.Hosting.Local;
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
            var host = new LocalExecutionHost("mcp-fixture");
            var starts = 0;
            host.StartProcess = (start, containment, _) =>
            {
                starts++;
                Assert.Equal(ContainmentMode.None, containment);
                Assert.All(start.Environment.Keys, name => Assert.Contains(name, new[] { "SystemRoot", "WINDIR" }));
                Assert.False(start.UseShellExecute);
                Assert.True(start.RedirectStandardInput && start.RedirectStandardOutput && start.RedirectStandardError);
                return System.Diagnostics.Process.Start(start);
            };

            using var childInput = new MemoryStream();
            using var output = new MemoryStream();
            using var error = new MemoryStream();
            Assert.Equal(2, await NativeMcpHelper.RunChildAsync(host, Digest, executable, arguments, childInput, output, error));
            Assert.Equal(0, starts);
            Assert.Empty(output.ToArray());
            using var binary = File.OpenRead(executable);
            var sha = Convert.ToHexString(SHA256.HashData(binary));
            Assert.Equal(0, await NativeMcpHelper.RunChildAsync(host, sha, executable, arguments, childInput, output, error));
            Assert.Equal(1, starts);
            Assert.Equal("isolated", Encoding.UTF8.GetString(output.ToArray()).Trim());
        }
        finally
        {
            Environment.SetEnvironmentVariable(canary, null);
        }
    }

    [Fact]
    public async Task Scoped_plan_reads_only_the_loopback_origin_without_redirect_proxy_or_cookies()
    {
        const string bearer = "synthetic-mcp-plan-token";
        var requests = new System.Collections.Concurrent.ConcurrentQueue<(string Path, string Token, string Cookie)>();
        var reply = "redirect";
        var redirects = 0;
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        app.MapGet("/api/mcp/native-plan", async (HttpContext context) =>
        {
            requests.Enqueue((context.Request.Path, context.Request.Headers.Authorization.ToString(), context.Request.Headers.Cookie.ToString()));
            switch (reply)
            {
                case "redirect":
                    context.Response.Redirect("/redirect-target");
                    break;
                case "empty":
                    await context.Response.WriteAsJsonAsync(MakePlan() with { Servers = [] });
                    break;
                case "foreign-empty":
                    await context.Response.WriteAsJsonAsync(MakePlan() with { Servers = [], DiscussionId = "other" });
                    break;
                case "invalid-revision":
                    var valid = MakePlan();
                    await context.Response.WriteAsJsonAsync(valid with
                    {
                        Servers = [valid.Servers[0] with { Declaration = valid.Servers[0].Declaration with { Revision = "truncated" } }]
                    });
                    break;
                case "unauthorized":
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    break;
                case "oversize":
                    await context.Response.WriteAsync(new string('x', 128 * 1024 + 1));
                    break;
                case "malformed":
                    await context.Response.WriteAsync("{");
                    break;
                default:
                    await context.Response.WriteAsJsonAsync(MakePlan());
                    break;
            }
        });
        app.MapGet("/redirect-target", () => Interlocked.Increment(ref redirects));
        await app.StartAsync();
        Assert.True(ClaudeNativeMcp.TryPlanUri(app.Urls.Single(), out var uri));
        var network = new PlanTestNetwork();

        Assert.Null(await ClaudeNativeMcp.FetchPlanAsync(network, uri, bearer, default));
        reply = "valid";
        var plan = await ClaudeNativeMcp.FetchPlanAsync(network, uri, bearer, default);
        Assert.NotNull(plan);
        Assert.Equal(Server, Assert.Single(plan.Servers).Id);
        reply = "unauthorized";
        Assert.Null(await ClaudeNativeMcp.FetchPlanAsync(network, uri, bearer, default));
        reply = "oversize";
        await Assert.ThrowsAsync<HttpRequestException>(() => ClaudeNativeMcp.FetchPlanAsync(network, uri, bearer, default));
        reply = "malformed";
        await Assert.ThrowsAnyAsync<JsonException>(() => ClaudeNativeMcp.FetchPlanAsync(network, uri, bearer, default));

        Assert.Equal(0, redirects);
        Assert.Equal(5, requests.Count);
        Assert.All(requests, request =>
        {
            Assert.Equal("/api/mcp/native-plan", request.Path);
            Assert.Equal("Bearer " + bearer, request.Token);
            Assert.Empty(request.Cookie);
        });
        Assert.Equal(0, network.ProxyCalls);
        Assert.Equal(5, network.Origins.Count);
        Assert.All(network.Origins, origin => Assert.Equal(uri, origin));

        var host = LocalExecutionHost.Shared;
        var scope = ClaudeNativeMcp.Scope.From("session", Provenance())!;
        var installed = new ClaudeNativeMcp.Generation(scope, ["fixture"], MakePlan().Servers);
        var environment = new Dictionary<string, string>
        {
            ["REDLEAF_EXECUTION_TOKEN"] = bearer,
            ["REDLEAF_URL"] = app.Urls.Single()
        };
        reply = "valid";
        var current = await ClaudeNativeMcp.ReadCurrentPlanAsync(host, scope, environment, default);
        Assert.True(ClaudeNativeMcp.StillAuthorized(installed, Assert.IsType<ClaudeNativeMcp.Plan>(current)));
        reply = "empty";
        var empty = Assert.IsType<ClaudeNativeMcp.Plan>(
            await ClaudeNativeMcp.ReadCurrentPlanAsync(host, scope, environment, default));
        Assert.Empty(empty.Servers);
        Assert.False(ClaudeNativeMcp.StillAuthorized(installed, empty));
        foreach (var invalid in new[] { "foreign-empty", "invalid-revision", "unauthorized", "malformed" })
        {
            reply = invalid;
            Assert.Null(await ClaudeNativeMcp.ReadCurrentPlanAsync(host, scope, environment, default));
        }
        Assert.Equal(0, redirects);
    }

    private sealed class PlanTestNetwork : IHostNetwork
    {
        private readonly IHostNetwork local = LocalExecutionHost.Shared.Network;
        private readonly ProbeProxy proxy = new();

        public int ProxyCalls => proxy.Calls;
        public List<Uri> Origins { get; } = [];

        public HttpMessageHandler CreateServiceHandler(Uri hostLocalUri)
        {
            Origins.Add(hostLocalUri);
            var handler = Assert.IsType<HttpClientHandler>(local.CreateServiceHandler(hostLocalUri));
            handler.Proxy = proxy;
            handler.CookieContainer.SetCookies(hostLocalUri, "sentinel=forbidden; Path=/");
            return handler;
        }

        public bool IsListening(int port) => local.IsListening(port);
        public Uri ResolveServiceUri(Uri hostLocalUri) => local.ResolveServiceUri(hostLocalUri);
        public Task<IHostPortForward> ReverseForwardAsync(Uri uri, CancellationToken ct = default) => local.ReverseForwardAsync(uri, ct);
        public Task<WebSocket> ConnectWebSocketAsync(Uri uri, CancellationToken ct = default) => local.ConnectWebSocketAsync(uri, ct);

        private sealed class ProbeProxy : IWebProxy
        {
            public int Calls;
            public ICredentials? Credentials { get; set; }

            public Uri GetProxy(Uri destination)
            {
                Interlocked.Increment(ref Calls);
                return new Uri("http://127.0.0.1:1");
            }

            public bool IsBypassed(Uri host)
            {
                Interlocked.Increment(ref Calls);
                return false;
            }
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
