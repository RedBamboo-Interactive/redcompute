using System.Net;
using System.Reflection;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Data.Sqlite;
using RedBamboo.AppHost.Auth;
using RedBamboo.AppHost.Discovery;
using RedBamboo.AppHost.Streams;
using RedBamboo.AppHost.WebSockets;
using RedCompute.App.Api.Endpoints;
using RedCompute.App.Services;
using RedCompute.Core.Capabilities;
using RedCompute.Core.Configuration;
using RedCompute.Core.Jobs;
using RedCompute.Core.Providers;
using RedCompute.Core.Sessions;
using RedCompute.PluginSdk;
using Xunit;

namespace RedCompute.App.Tests;

// The facts the RedLeaf and shared-client sides settled: the policy's exact wire shape, the
// change notification body, what the chat client reads from question / question_resolved, and
// flow nodes' unattended one-shots.
[Collection("Unified session endpoint mapping")]
public sealed class PermissionContractAlignmentTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Environment.GetEnvironmentVariable("REDLEAF_SCRATCH_DIR") ?? Path.GetTempPath(),
        "redcompute-permission-alignment", Guid.NewGuid().ToString("N"));

    public PermissionContractAlignmentTests() => Directory.CreateDirectory(_root);

    // ---- RedLeaf's GET /api/agent-permissions/policy (redleaf 3d7f8c2a, AgentAccessPolicy.ToContract) ----

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]   // accepted on an older warning text: RedLeaf already reports false
    public void Redleafs_exact_contract_is_read_with_enabled_taken_as_effective(bool enabled, bool fullAccess)
    {
        using var body = JsonDocument.Parse($$$"""
            {"version":1,"defaultLevel":"auto","agents":{"5f0c2f7e-0000-4000-8000-000000000001":"full"},
             "fullAccess":{"enabled":{{{(enabled ? "true" : "false")}}},"textVersion":1,"acknowledgedAt":"2026-11-09T10:00:00.0000000+00:00","acknowledgedBy":"owner-1"}}
            """);

        var policy = RedLeafSessionAccessPolicyClient.Parse(body.RootElement);

        Assert.Equal(1, policy.Version);
        Assert.Equal(SessionAccessLevels.Auto, policy.DefaultFor("someone"));
        Assert.Equal(SessionAccessLevels.Full, policy.DefaultFor("5f0c2f7e-0000-4000-8000-000000000001"));
        Assert.Equal(fullAccess, policy.FullAccessEnabled);
        Assert.Equal("owner-1", policy.FullAccess!.AcknowledgedBy);
    }

    [Fact]
    public void An_enabled_full_access_counts_whatever_text_version_it_names()
    {
        using var body = JsonDocument.Parse("""{"version":1,"defaultLevel":"ask","agents":{},"fullAccess":{"enabled":true,"textVersion":7,"acknowledgedAt":null,"acknowledgedBy":null}}""");

        Assert.True(RedLeafSessionAccessPolicyClient.Parse(body.RootElement).FullAccessEnabled);
    }

    // ---- POST /ai-session/access-policy/changed with RedLeaf's {version, revision} ----

    [Theory]
    [InlineData("""{"version":1,"revision":7}""", 7L)]
    [InlineData("", null)]
    [InlineData("not json", null)]
    public async Task Policy_change_accepts_redleafs_body(string json, long? revision)
    {
        var (status, body, _) = await PostPolicyChangedAsync(json, "infrastructure service token");

        Assert.Equal(200, status);
        Assert.True(body.GetProperty("refreshed").GetBoolean());
        if (revision is null) Assert.Equal(JsonValueKind.Null, body.GetProperty("revision").ValueKind);
        else Assert.Equal(revision, body.GetProperty("revision").GetInt64());
    }

    [Fact]
    public async Task Policy_change_from_a_derived_shell_token_is_accepted()
    {
        // RedLeaf's NotifyComputeAsync goes through ComputeGateway: with a caller execution token it
        // sends the derived token (one redcompute-call hop), otherwise the infrastructure token.
        var (status, _, reads) = await PostPolicyChangedAsync("""{"version":1,"revision":8}""", "redleaf shell through the kernel");

        Assert.Equal(200, status);
        Assert.True(reads >= 1, reads.ToString()); // refetched, never served from the cache
    }

    // ---- what the shared chat client reads from the live stream ----

    [Fact]
    public void A_permission_question_reaches_clients_with_kind_and_permission()
    {
        using var f = new StreamFixture();
        using var input = JsonDocument.Parse("""{"command":"npm test"}""");

        f.Pipeline.HandleLiveEvent("claude", "session-1", new UnifiedStreamEvent
        {
            Type = "question", Kind = PendingSessionQuestion.PermissionKind, RequestId = "req-1",
            ToolName = "Bash", MessageId = "toolu_1",
            Permission = new PendingPermissionRequest
            {
                ToolName = "Bash", ToolUseId = "toolu_1", Summary = "Run the tests", Input = input.RootElement.Clone(),
                Cwd = "C:\\repo", Options = SessionPermissionDecisions.All,
            },
        });

        var evt = f.SingleEvent();
        Assert.Equal("question", evt.GetProperty("type").GetString());
        Assert.Equal("permission", evt.GetProperty("kind").GetString());
        Assert.Equal("req-1", evt.GetProperty("requestId").GetString());
        Assert.Equal("toolu_1", evt.GetProperty("messageId").GetString());
        var permission = evt.GetProperty("permission");
        Assert.Equal("Bash", permission.GetProperty("toolName").GetString());
        Assert.Equal("toolu_1", permission.GetProperty("toolUseId").GetString());
        Assert.Equal("Run the tests", permission.GetProperty("summary").GetString());
        Assert.Equal("npm test", permission.GetProperty("input").GetProperty("command").GetString());
        Assert.Equal("C:\\repo", permission.GetProperty("cwd").GetString());
        Assert.Equal(["allow_once", "allow_session", "deny"], permission.GetProperty("options").EnumerateArray().Select(o => o.GetString()));
        Assert.True(evt.TryGetProperty("sequence", out _));
    }

    [Theory]
    [InlineData("allow_session", "allow_session")]
    [InlineData("deny", "timeout")]
    public void A_permission_resolution_reaches_clients_with_its_decision(string decision, string content)
    {
        using var f = new StreamFixture();

        f.Pipeline.HandleLiveEvent("claude", "session-1", new UnifiedStreamEvent
        {
            Type = "question_resolved", Kind = PendingSessionQuestion.PermissionKind, RequestId = "req-1",
            Content = content, Decision = decision,
        });

        var evt = f.SingleEvent();
        Assert.Equal(decision, evt.GetProperty("decision").GetString());
        Assert.Equal(content, evt.GetProperty("content").GetString());
        Assert.Equal("permission", evt.GetProperty("kind").GetString());
    }

    // ---- flow nodes' one-shots: unattended: true ----

    [Theory]
    [InlineData("true")]
    [InlineData("false")]
    [InlineData("null")]
    public async Task Execute_accepts_unattended_and_runs_non_interactively_at_the_resolved_level(string unattended)
    {
        var (status, provider, body) = await ExecuteAsync($$"""{"prompt":"summarize","suiteAccess":false,"unattended":{{unattended}}}""", "signed login");

        Assert.True(status == 200, body.ToString());
        var call = Assert.Single(provider.Calls);
        Assert.Equal(SessionAccessLevels.Ask, call["accessLevel"]);   // nothing requested: the policy default
        Assert.False(call.ContainsKey("unattended"));                  // never copied from the body
    }

    [Fact]
    public async Task Execute_refuses_a_malformed_unattended()
    {
        var (status, provider, body) = await ExecuteAsync("""{"prompt":"summarize","suiteAccess":false,"unattended":"yes"}""", "signed login");

        Assert.Equal(422, status);
        Assert.Equal("validation_failed", body.GetProperty("error").GetString());
        Assert.Empty(provider.Calls);
    }

    [Theory]
    [InlineData(SessionAccessLevels.Plan)]
    [InlineData(SessionAccessLevels.Ask)]
    [InlineData(SessionAccessLevels.Auto)]
    [InlineData(SessionAccessLevels.Full)]
    [InlineData(null)]
    public void A_claude_one_shot_never_has_a_channel_to_park_a_permission(string? level)
    {
        var args = new List<string>();
        RedCompute.Plugin.ClaudeCode.ClaudeSessionService.AddAgentArgs(args, null, null, 10, null, null, null, level);

        Assert.Contains("--print", args);
        Assert.DoesNotContain("--permission-prompt-tool", args);
        Assert.DoesNotContain("--input-format", args);
    }

    // ---- harnesses ----

    private static async Task<(int Status, JsonElement Body, int Reads)> PostPolicyChangedAsync(string json, string caller)
    {
        var registry = new CapabilityRegistry();
        var policy = new StaticSessionAccessPolicySource(SessionAccessPolicy.NoRecord);
        var access = new SessionAccessService(policy, () => [], _ => null, (_, _) => { });
        await using var app = WebApplication.CreateSlimBuilder().Build();
        SessionAccessEndpoints.Map(new EndpointRegistry(app), registry, new ClaudeBackgroundWorkTests.MemoryJobTracker(), access);
        var context = Context(app, "/ai-session/access-policy/changed", json);
        if (caller == "redleaf shell through the kernel")
            ShellThroughKernel(context);
        else
            SessionRollbackTests.Identify(context, caller);

        await Endpoint(app, "/ai-session/access-policy/changed").RequestDelegate!(context);
        return (context.Response.StatusCode, await Body(context), policy.Reads);
    }

    private static void ShellThroughKernel(DefaultHttpContext context)
    {
        var identity = new ExecutionIdentity(ExecutionIdentity.CurrentSchemaVersion, Guid.NewGuid().ToString(),
            new ExecutionAppIdentity("redleaf", "redleaf", "redleaf"),
            new ExecutionActorIdentity("app", "redleaf", "RedLeaf", "redleaf"),
            new ExecutionBeneficiaryIdentity("user", "owner", "Owner"),
            [new ExecutionContextReference("browser", Route: "/settings/agent-permissions"), new ExecutionContextReference("redcompute-call")],
            "browser-token");
        context.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("sub", "owner"), new Claim(ExecutionIdentityClaims.TokenUseClaim, ExecutionIdentityClaims.TokenUse)], "signed"));
        context.Items[ExecutionIdentityClaims.HttpContextItemKey] = identity;
    }

    private async Task<(int Status, ExecuteProvider Provider, JsonElement Body)> ExecuteAsync(string json, string caller)
    {
        using var f = new SessionRecoveryTests.Fixture("maintenance_restart");
        var proxy = DispatchProxy.Create<IExecuteTestProvider, ExecuteProvider>();
        var provider = (ExecuteProvider)(object)proxy;
        var registry = new CapabilityRegistry();
        registry.Register("ai-session", new CapabilityDefinition { Slug = "ai-session", DisplayName = "AI Session" },
            new CapabilityConfig(), new Dictionary<string, IBackendProvider> { ["test"] = proxy }, "test");
        await using var app = WebApplication.CreateSlimBuilder().Build();
        UnifiedSessionEndpoints.Map(new EndpointRegistry(app), registry, new ClaudeBackgroundWorkTests.MemoryJobTracker(), (_, _) => { },
            f.Config, attachmentStore: f.Attachments, inputQueue: f.Service());
        var context = Context(app, "/ai-session/execute", json);
        SessionRollbackTests.Identify(context, caller);
        await Endpoint(app, "/ai-session/execute").RequestDelegate!(context);
        return (context.Response.StatusCode, provider, await Body(context));
    }

    private static RouteEndpoint Endpoint(WebApplication app, string pattern)
        => ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints).OfType<RouteEndpoint>()
            .Single(item => item.RoutePattern.RawText == pattern
                && item.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods.Contains("POST"));

    private static DefaultHttpContext Context(WebApplication app, string pattern, string json)
    {
        var context = new DefaultHttpContext { RequestServices = app.Services };
        context.SetEndpoint(Endpoint(app, pattern));
        context.Request.Method = "POST";
        var bytes = Encoding.UTF8.GetBytes(json);
        context.Request.ContentType = "application/json";
        context.Request.ContentLength = bytes.Length;
        context.Request.Body = new MemoryStream(bytes);
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static async Task<JsonElement> Body(HttpContext context)
    {
        context.Response.Body.Position = 0;
        var text = await new StreamReader(context.Response.Body).ReadToEndAsync();
        return text.Length == 0 ? default : JsonSerializer.Deserialize<JsonElement>(text);
    }

    private sealed class StreamFixture : IDisposable
    {
        private readonly string _root = Path.Combine(
            Environment.GetEnvironmentVariable("REDLEAF_SCRATCH_DIR") ?? Path.GetTempPath(),
            "redcompute-permission-stream", Guid.NewGuid().ToString("N"));
        private readonly RedLeafStreamClient _client;
        private readonly SessionRollbackTests.RecordingWebSocket _socket = new();
        public SessionTranscriptPipeline Pipeline { get; }

        public StreamFixture()
        {
            Directory.CreateDirectory(_root);
            var broadcaster = new WebSocketBroadcaster();
            typeof(WebSocketBroadcaster).GetMethod("AddClient", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(broadcaster, ["test", _socket]);
            _client = new RedLeafStreamClient("http://127.0.0.1:9", "RedComputeTests",
                new JwtService(new JwtOptions { SigningKey = new string('a', 64) }));
            Pipeline = new SessionTranscriptPipeline(_client, broadcaster, (_, _) => { }, (_, _) => false,
                new TranscriptSequenceStore(Path.Combine(_root, "cursor.db")));
        }

        public JsonElement SingleEvent()
        {
            var frame = Assert.Single(_socket.Frames, f => f.Contains("\"session.stream\"", StringComparison.Ordinal));
            using var json = JsonDocument.Parse(frame);
            return json.RootElement.GetProperty("data").GetProperty("event").Clone();
        }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            _ = _client.DisposeAsync();
            try { Directory.Delete(_root, true); } catch { }
        }
    }

    public interface IExecuteTestProvider : ISessionProvider, IBackendProvider { }

    public class ExecuteProvider : DispatchProxy
    {
        internal readonly List<Dictionary<string, object?>> Calls = [];

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            switch (method!.Name)
            {
                case "get_ProviderId": return "test";
                case "get_ProviderDisplayName": return "Test";
                case "get_Capabilities": return SessionCapabilities.StatelessExecution;
                case "get_Name": return "Test";
                case "get_CapabilitySlug": return "ai-session";
                case "get_HealthCheckInterval": return TimeSpan.FromSeconds(10);
                case "get_SupportedAccessLevels": return SessionAccessLevels.All;
                case nameof(ISessionProvider.GetAvailableModels): return new List<ModelInfo>();
                case nameof(ISessionProvider.ExecuteAsync):
                    lock (Calls) Calls.Add(new Dictionary<string, object?>((Dictionary<string, object?>)args![7]!));
                    return Task.FromResult(new SessionExecuteResult(true, "done", null, "test-model", 1, 1, null, null));
                default: throw new NotSupportedException(method.Name);
            }
        }
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }
}
