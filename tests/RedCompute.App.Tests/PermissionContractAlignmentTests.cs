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

    // ---- RedLeaf's GET /api/agent-permissions/policy, exactly as redleaf 88de9e40 sends it ----
    //
    // AgentAccessPolicy.ToContract() builds a JsonObject (version, defaultLevel, agents sorted by id,
    // fullAccess {enabled = Effective, textVersion, acknowledgedAt = "O" format, acknowledgedBy}) and
    // the endpoint returns it with Results.Json, so the default web serializer writes nulls and
    // escapes '+'. The change notification is JsonContent.Create(new { version = 1, revision }).

    /// <summary>No stored policy: AgentAccessPolicy.None.</summary>
    private const string RedLeafNoRecord =
        """{"version":1,"defaultLevel":"ask","agents":{},"fullAccess":{"enabled":false,"textVersion":null,"acknowledgedAt":null,"acknowledgedBy":null}}""";

    /// <summary>Full access accepted on the current text (1), default auto, one Agent at full.</summary>
    private static readonly string RedLeafFullOn = Wire(
        """{"version":1,"defaultLevel":"auto","agents":{"0b9e1d2c-5a4f-4e3b-9c8d-7f6a5b4c3d2e":"full","5f0c2f7e-0000-4000-8000-000000000001":"plan"},"fullAccess":{"enabled":true,"textVersion":1,"acknowledgedAt":"2026-11-09T10:00:00.0000000+00:00","acknowledgedBy":"7c1d4e2a-9b3f-4a8e-b6d5-0e1f2a3b4c5d"}}""");

    /// <summary>Accepted on an older warning text (0): stored Enabled, but Effective is false.</summary>
    private static readonly string RedLeafStaleAcceptance = Wire(
        """{"version":1,"defaultLevel":"ask","agents":{},"fullAccess":{"enabled":false,"textVersion":0,"acknowledgedAt":"2026-10-01T08:30:00.0000000+00:00","acknowledgedBy":"7c1d4e2a-9b3f-4a8e-b6d5-0e1f2a3b4c5d"}}""");

    /// <summary>The default web encoder escapes '+' as a backslash-u002B sequence, as RedLeaf's response does.</summary>
    private static string Wire(string json) => json.Replace("+", @"\u002B", StringComparison.Ordinal);

    /// <summary>
    /// GET /api/agent-permissions (the Settings view) on an install with no stored policy, trimmed to
    /// the fields RedCompute reads plus their neighbours: ViewAsync writes the compact policy under
    /// <c>policy</c> and the stored revision (0 for AgentAccessPolicy.None) beside it.
    /// </summary>
    private const string RedLeafViewNoRecord =
        """{"policy":{"version":1,"defaultLevel":"ask","agents":{},"fullAccess":{"enabled":false,"textVersion":null,"acknowledgedAt":null,"acknowledgedBy":null}},"levels":["plan","ask","auto","full"],"revision":0,"updatedAt":null,"fullAccess":{"enabled":false,"effective":false,"needsReacceptance":false,"textVersion":null,"acknowledgedAt":null,"acknowledgedBy":null,"acknowledgedByName":null,"revokedAt":null},"agents":[],"canChange":false,"blockedReason":"agent_permissions_require_person","blockedMessage":"x"}""";

    /// <summary>The same view after the owner saved a plain Ask once: same compact policy, revision 1.</summary>
    private const string RedLeafViewSavedAsk =
        """{"policy":{"version":1,"defaultLevel":"ask","agents":{},"fullAccess":{"enabled":false,"textVersion":null,"acknowledgedAt":null,"acknowledgedBy":null}},"levels":["plan","ask","auto","full"],"revision":1,"updatedAt":"2026-11-09T18:00:00+00:00","agents":[]}""";

    /// <summary>NotifyComputeAsync's body after a write.</summary>
    private const string RedLeafChangeNotification = """{"version":1,"revision":3}""";

    [Fact]
    public void The_literals_are_what_redleafs_serialization_writes()
    {
        // ToContract's code, serialized the way Results.Json and JsonContent.Create do (web defaults).
        static string Contract(string defaultLevel, (string Id, string Level)[] agents, bool effective, int? textVersion,
            DateTimeOffset? acknowledgedAt, string? acknowledgedBy)
        {
            var map = new System.Text.Json.Nodes.JsonObject();
            foreach (var (id, level) in agents.OrderBy(pair => pair.Id, StringComparer.Ordinal)) map[id] = level;
            var contract = new System.Text.Json.Nodes.JsonObject
            {
                ["version"] = 1,
                ["defaultLevel"] = defaultLevel,
                ["agents"] = map,
                ["fullAccess"] = new System.Text.Json.Nodes.JsonObject
                {
                    ["enabled"] = effective,
                    ["textVersion"] = textVersion,
                    ["acknowledgedAt"] = acknowledgedAt?.ToString("O"),
                    ["acknowledgedBy"] = acknowledgedBy,
                },
            };
            return JsonSerializer.Serialize(contract, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        }

        Assert.Equal(RedLeafNoRecord, Contract("ask", [], false, null, null, null));
        Assert.Equal(RedLeafFullOn, Contract("auto",
            [("5f0c2f7e-0000-4000-8000-000000000001", "plan"), ("0b9e1d2c-5a4f-4e3b-9c8d-7f6a5b4c3d2e", "full")],
            true, 1, DateTimeOffset.Parse("2026-11-09T10:00:00Z"), "7c1d4e2a-9b3f-4a8e-b6d5-0e1f2a3b4c5d"));
        Assert.Equal(RedLeafStaleAcceptance, Contract("ask", [], false, 0,
            DateTimeOffset.Parse("2026-10-01T08:30:00Z"), "7c1d4e2a-9b3f-4a8e-b6d5-0e1f2a3b4c5d"));
        Assert.Equal(RedLeafChangeNotification,
            JsonSerializer.Serialize(new { version = 1, revision = 3 }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    }

    [Fact]
    public async Task The_client_reads_redleafs_policy_with_full_access_on()
    {
        var policy = await ReadThroughClientAsync(RedLeafFullOn);

        Assert.True(policy.Available);
        Assert.Equal(1, policy.Version);
        Assert.True(policy.FullAccessEnabled);
        Assert.Equal(SessionAccessLevels.Auto, policy.DefaultFor("someone-else"));
        Assert.Equal(SessionAccessLevels.Auto, policy.DefaultFor(null));
        Assert.Equal(SessionAccessLevels.Full, policy.DefaultFor("0b9e1d2c-5a4f-4e3b-9c8d-7f6a5b4c3d2e"));
        Assert.Equal(SessionAccessLevels.Plan, policy.DefaultFor("5f0c2f7e-0000-4000-8000-000000000001"));
        Assert.Equal(1, policy.FullAccess!.TextVersion);
        Assert.Equal(DateTimeOffset.Parse("2026-11-09T10:00:00Z"), policy.FullAccess.AcknowledgedAt);
        Assert.Equal("7c1d4e2a-9b3f-4a8e-b6d5-0e1f2a3b4c5d", policy.FullAccess.AcknowledgedBy);
        Assert.Equal(SessionAccessLevels.Full, SessionAccessResolver.ResolveStart(SessionAccessLevels.Full, null, true, policy,
            SessionAccessLevels.All, null).Level);
    }

    [Fact]
    public async Task The_client_reads_redleafs_no_record_as_auto_with_full_off()
    {
        // Krhom's install, 2026-11-09: RedLeaf on main, nobody ever saved Agent permissions.
        var policy = await ReadThroughClientAsync(RedLeafNoRecord, RedLeafViewNoRecord);

        Assert.True(policy.Available);
        Assert.False(policy.Stored);
        Assert.False(policy.FullAccessEnabled);
        Assert.Equal(SessionAccessLevels.Auto, policy.DefaultFor("anyone"));
        Assert.Equal(SessionAccessLevels.Auto, SessionAccessResolver.ResolveStart(null, "anyone", false, policy,
            SessionAccessLevels.All, null).Level);
        Assert.Equal(SessionAccessLevels.Ask, SessionAccessResolver.ResolveStart(null, "anyone", false, policy,
            [SessionAccessLevels.Ask, SessionAccessLevels.Full], null).Level);
    }

    [Fact]
    public async Task The_client_reads_a_saved_plain_ask_as_ask()
    {
        var policy = await ReadThroughClientAsync(RedLeafNoRecord, RedLeafViewSavedAsk);

        Assert.True(policy.Stored);
        Assert.Equal(SessionAccessLevels.Ask, policy.DefaultFor("anyone"));
    }

    [Fact]
    public async Task The_client_trusts_redleafs_effective_enabled_on_a_stale_acceptance()
    {
        var policy = await ReadThroughClientAsync(RedLeafStaleAcceptance);

        Assert.False(policy.FullAccessEnabled);
        Assert.Equal(0, policy.FullAccess!.TextVersion);
        Assert.Equal(SessionAccessLevels.Auto, SessionAccessResolver.ResolveStart(SessionAccessLevels.Full, null, true, policy,
            SessionAccessLevels.All, null).Level);
    }

    private static async Task<SessionAccessPolicy> ReadThroughClientAsync(string json, string? view = null)
    {
        var http = new HttpClient(new FixedResponse(json, view)) { BaseAddress = new Uri("http://redleaf/") };
        return await new RedLeafSessionAccessPolicyClient(http, (_, _) => { }).GetAsync();
    }

    /// <param name="view">The Settings view, served only to a client that asks for it; null fails the test if it does.</param>
    private sealed class FixedResponse(string json, string? view) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            var path = request.RequestUri!.AbsolutePath;
            var body = json;
            if (path == "/api/agent-permissions")
            {
                Assert.NotNull(view);
                body = view!;
            }
            else Assert.Equal("/api/agent-permissions/policy", path);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }

    // ---- POST /ai-session/access-policy/changed with RedLeaf's {version, revision} ----

    [Theory]
    [InlineData(RedLeafChangeNotification, 3L)]
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
        var (status, _, reads) = await PostPolicyChangedAsync(RedLeafChangeNotification, "redleaf shell through the kernel");

        Assert.Equal(200, status);
        Assert.True(reads >= 1, reads.ToString()); // refetched, never served from the cache
    }

    [Theory]
    [MemberData(nameof(LocalDefaultCallers))]
    public async Task Policy_change_refuses_localdefault_identities(string caller)
    {
        var (status, body, reads) = await PostPolicyChangedAsync(RedLeafChangeNotification, caller);

        Assert.Equal(403, status);
        Assert.Equal("service_caller_required", body.GetProperty("error").GetString());
        Assert.Equal(0, reads);
    }

    public static TheoryData<string> LocalDefaultCallers => new(SessionRollbackTests.LocalDefaultCallers);

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
        Assert.Equal(SessionAccessLevels.Auto, call["accessLevel"]);  // nothing requested, no policy saved: auto
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
