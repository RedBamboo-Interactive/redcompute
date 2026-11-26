using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using RedBamboo.AppHost.WebSockets;
using RedCompute.App.Services;
using RedCompute.App.Services.SessionBindings;
using RedCompute.Core.Providers;
using RedCompute.App.Services.Nodes;
using RedCompute.Core.Configuration;
using RedCompute.Core.Sessions;
using RedCompute.NodeLink;
using RedCompute.NodeLink.Noise;
using RedCompute.PluginSdk;
using Xunit;

namespace RedCompute.App.Tests;

public sealed class RemoteSessionLifecycleRuntimeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "redcompute-remote-runtime-" + Guid.NewGuid().ToString("N"));
    private readonly RedComputeConfig _config = new();
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task A_signed_start_is_recoverable_and_resume_preserves_its_native_thread()
    {
        using var controller = NoiseKeyPair.Generate();
        using var stranger = NoiseKeyPair.Generate();
        var provider = DispatchProxy.Create<IFixtureProvider, Provider>();
        var probe = (Provider)(object)provider;
        var scope = new SessionRuntimeScope("node", "sessions", "logical", 0, "owner", "workspace");
        var start = new RemoteSessionLifecycleRequest("abc123", "probe", "probe-model", "low");
        var paired = true;
        var runtime = OpenRuntime();

        var preview = await runtime.ApplyAsync(SessionRuntimeOperation.Inspect,
            Grant(controller, SessionRuntimeOperation.Inspect, "preview", new("abc123", "probe")));
        Assert.Equal(200, preview.Status);
        using var placement = JsonDocument.Parse(preview.Body);
        Assert.Equal("codex", placement.RootElement.GetProperty("providerId").GetString());
        Assert.Equal("abc123", placement.RootElement.GetProperty("revision").GetString());
        Assert.Equal("ask", placement.RootElement.GetProperty("maxAccessLevel").GetString());
        Assert.False(placement.RootElement.TryGetProperty("projectPath", out _));
        Assert.Equal(0, probe.Starts);

        var unknown = await runtime.ApplyAsync(SessionRuntimeOperation.Start, Grant(stranger, SessionRuntimeOperation.Start, "start", start));
        Assert.Equal(403, unknown.Status);
        var stale = await runtime.ApplyAsync(SessionRuntimeOperation.Start,
            Grant(controller, SessionRuntimeOperation.Start, "start", start with { Revision = "stale" }));
        Assert.Equal(422, stale.Status);
        Assert.Equal(0, probe.Starts);

        var command = Grant(controller, SessionRuntimeOperation.Start, "start", start);
        probe.LoseStartReply = true;
        await Assert.ThrowsAsync<IOException>(() => runtime.ApplyAsync(SessionRuntimeOperation.Start, command));
        Assert.Equal(1, probe.Starts);
        runtime = OpenRuntime();
        var accepted = await runtime.ApplyAsync(SessionRuntimeOperation.Start, command);
        Assert.Equal(201, accepted.Status);
        var thread = JsonSerializer.Deserialize<RemoteSessionLifecycleThread>(accepted.Body, Json)!;
        Assert.Equal(probe.Info!.Id, thread.RuntimeSessionId);
        Assert.Equal("fixture-native-thread", thread.NativeThreadId);
        Assert.Equal(1, probe.Starts);

        var replay = await runtime.ApplyAsync(SessionRuntimeOperation.Start,
            Grant(controller, SessionRuntimeOperation.Start, "start", start));
        Assert.Equal(accepted.Body, replay.Body);
        var secondStart = await runtime.ApplyAsync(SessionRuntimeOperation.Start,
            Grant(controller, SessionRuntimeOperation.Start, "start-2", start));
        Assert.Equal(409, secondStart.Status);

        probe.Info.Status = SessionStatus.Stopped;
        probe.Info.StopReason = SessionStopReasons.MaintenanceRestart;
        var resume = new RemoteSessionLifecycleRequest("abc123", "probe",
            RuntimeSessionId: thread.RuntimeSessionId, NativeThreadId: thread.NativeThreadId);
        var wrong = await runtime.ApplyAsync(SessionRuntimeOperation.Resume,
            Grant(controller, SessionRuntimeOperation.Resume, "resume-1", resume with { NativeThreadId = "wrong-thread" }));
        Assert.Equal(409, wrong.Status);
        Assert.Equal(0, probe.Resumes);

        var fenced = SessionRuntimeGrant.Issue(controller, scope with { Generation = 1 }, SessionRuntimeOperation.Resume,
            "resume-3", DateTimeOffset.UtcNow.AddSeconds(30), JsonSerializer.SerializeToUtf8Bytes(resume, Json));
        Assert.Equal(409, (await runtime.ApplyAsync(SessionRuntimeOperation.Resume, fenced)).Status);
        Assert.Equal(0, probe.Resumes);

        var reopened = await runtime.ApplyAsync(SessionRuntimeOperation.Resume,
            Grant(controller, SessionRuntimeOperation.Resume, "resume-2", resume));
        Assert.Equal(200, reopened.Status);
        Assert.Equal(thread, JsonSerializer.Deserialize<RemoteSessionLifecycleThread>(reopened.Body, Json));
        Assert.Equal(1, probe.Starts);
        Assert.Equal(1, probe.Resumes);

        var input = TurnCommand(SessionRuntimeOperation.SendInput, "prompt-one", new
        {
            revision = "abc123", providerInstanceId = "probe", runtimeSessionId = thread.RuntimeSessionId,
            nativeThreadId = thread.NativeThreadId, inputUid = "prompt-one", text = "One prompt",
        });
        var admitted = await runtime.ApplyAsync(SessionRuntimeOperation.SendInput, input);
        Assert.Equal(202, admitted.Status);
        Assert.Equal(admitted.Body, (await OpenRuntime().ApplyAsync(SessionRuntimeOperation.SendInput, input)).Body);
        var receipt = JsonSerializer.Deserialize<RemoteInputReceipt>(admitted.Body, Json)!;
        Assert.Equal("prompt-one", receipt.TurnId);

        var queue = Store();
        var registry = new CapabilityRegistry();
        var delivery = new SessionInputQueueService(queue, Attachments(), registry, null!, new WebSocketBroadcaster(), (_, _) => { })
        {
            BindingCommands = new SessionBindingCommands(registry, new SessionBindingTranscriptRoutes(queue)),
            RemoteRuntimeLookup = runtime.FindLocalSession,
        };
        var queued = Assert.Single(await queue.ListAsync(thread.RuntimeSessionId, "owner", true));
        await queue.RequireRecoveryAsync(queued, CancellationToken.None);
        await delivery.ProcessSessionAsync(thread.RuntimeSessionId, CancellationToken.None);
        Assert.Equal(0, probe.Inputs);
        Assert.Equal(admitted.Body, (await runtime.ApplyAsync(SessionRuntimeOperation.SendInput, input)).Body);
        await delivery.ProcessSessionAsync(thread.RuntimeSessionId, CancellationToken.None);
        Assert.Equal(1, probe.Inputs);
        Assert.Equal("prompt-one", probe.LastInputUid);
        var inspect = TurnCommand(SessionRuntimeOperation.Inspect, "inspect", new
        {
            revision = "abc123", providerInstanceId = "probe", runtimeSessionId = thread.RuntimeSessionId,
            nativeThreadId = thread.NativeThreadId, inputUid = "prompt-one",
        });
        queue.MarkTurnActive(thread.RuntimeSessionId);
        queue.CloseTurn(thread.RuntimeSessionId, new SessionTurnEnd("Stopped", SessionStopReasons.MaintenanceRestart,
            SessionTurnOutcomes.Interrupted, DateTimeOffset.UtcNow, 0));
        var inspected = await runtime.ApplyAsync(SessionRuntimeOperation.Inspect, inspect);
        Assert.Equal(200, inspected.Status);
        using var state = JsonDocument.Parse(inspected.Body);
        Assert.Equal("delivered", state.RootElement.GetProperty("state").GetString());
        var turn = state.RootElement.GetProperty("turn");
        Assert.Equal("prompt-one", turn.GetProperty("turnId").GetString());
        Assert.Equal(SessionTurnOutcomes.Interrupted, turn.GetProperty("outcome").GetString());

        probe.Record(1, "user", "prompt-one", "One prompt");
        probe.Record(2, "assistant", "reply-one", "One reply");
        var readEvents = TurnCommand(SessionRuntimeOperation.ReadEvents, "events", new
        {
            revision = "abc123", providerInstanceId = "probe", runtimeSessionId = thread.RuntimeSessionId,
            nativeThreadId = thread.NativeThreadId, afterSeq = 0, limit = 1,
        });
        var page = await runtime.ApplyAsync(SessionRuntimeOperation.ReadEvents, readEvents);
        Assert.Equal(200, page.Status);
        Assert.Equal(1, Assert.Single(JsonSerializer.Deserialize<RemoteSessionEventPage>(page.Body, Json)!.Events).Sequence);
        var acknowledge = TurnCommand(SessionRuntimeOperation.AcknowledgeEvents, "ack", new
        {
            revision = "abc123", providerInstanceId = "probe", runtimeSessionId = thread.RuntimeSessionId,
            nativeThreadId = thread.NativeThreadId, throughSeq = 1,
        });
        Assert.Equal(200, (await runtime.ApplyAsync(SessionRuntimeOperation.AcknowledgeEvents, acknowledge)).Status);
        Assert.Equal(409, (await runtime.ApplyAsync(SessionRuntimeOperation.ReadEvents, readEvents)).Status);
        var continued = TurnCommand(SessionRuntimeOperation.ReadEvents, "events-after-reconnect", new
        {
            revision = "abc123", providerInstanceId = "probe", runtimeSessionId = thread.RuntimeSessionId,
            nativeThreadId = thread.NativeThreadId, afterSeq = 1, limit = 10,
        });
        var resumedPage = await OpenRuntime().ApplyAsync(SessionRuntimeOperation.ReadEvents, continued);
        var item = Assert.Single(JsonSerializer.Deserialize<RemoteSessionEventPage>(resumedPage.Body, Json)!.Events);
        Assert.Equal(2, item.Sequence);
        Assert.Equal("prompt-one", item.Event.InputUid);
        var message = JsonSerializer.Deserialize<UnifiedMessageRecord>(item.Event.Body, Json)!;
        Assert.Equal("One reply", message.Content);

        var park = Grant(controller, SessionRuntimeOperation.Park, "park", resume);
        var parked = await runtime.ApplyAsync(SessionRuntimeOperation.Park, park);
        Assert.Equal(200, parked.Status);
        Assert.Equal(SessionStatus.Stopped, probe.Info.Status);
        Assert.Equal(parked.Body, (await OpenRuntime().ApplyAsync(SessionRuntimeOperation.Park, park)).Body);
        Assert.Equal(1, probe.Parks);

        await using var host = await RemoteSessionRuntimeHost.StartAsync(runtime, () => paired, 0, CancellationToken.None);
        Assert.Equal("127.0.0.1", host.Address.Host);
        using var http = new HttpClient { BaseAddress = host.Address };
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync(SessionRuntimeProtocol.HealthPath)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync("/settings")).StatusCode);
        using var wireResume = await PostAsync(Grant(controller, SessionRuntimeOperation.Resume, "resume-4", resume));
        Assert.Equal(HttpStatusCode.OK, wireResume.StatusCode);
        Assert.Equal(thread, JsonSerializer.Deserialize<RemoteSessionLifecycleThread>(await wireResume.Content.ReadAsByteArrayAsync(), Json));
        paired = false;
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await http.GetAsync(SessionRuntimeProtocol.HealthPath)).StatusCode);
        using var forbidden = await PostAsync(Grant(controller, SessionRuntimeOperation.Resume, "resume-5", resume));
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);

        async Task<HttpResponseMessage> PostAsync(SessionRuntimeGrant body)
        {
            using var request = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(body));
            request.Headers.ContentType = new("application/json");
            return await http.PostAsync(SessionRuntimeProtocol.PathFor(body.Operation), request);
        }

        InputAttachmentStore Attachments()
        {
            Directory.CreateDirectory(_root);
            return new(_config, Path.Combine(_root, "bytes"), Path.Combine(_root, "input.db"));
        }

        SessionInputQueueStore Store() => new(_config, Attachments());

        RemoteSessionRuntimeWorkspace Workspace() => new("owner", "workspace", "abc123",
            _root, "probe", provider, new(SessionAccessLevels.Ask, SessionAccessLevels.Ask));

        RemoteSessionLifecycleRuntime OpenRuntime()
        {
            return new(Store(),
                () => paired ? new("node", "sessions", controller.PublicKey) : null,
                (_, _, _) => Task.FromResult<RemoteSessionRuntimeWorkspace?>(Workspace()),
                TimeProvider.System, configuredWorkspace: (_, _) => Workspace());
        }

        SessionRuntimeGrant TurnCommand(SessionRuntimeOperation operation, string id, object payload) =>
            SessionRuntimeGrant.Issue(controller, scope, operation, id, DateTimeOffset.UtcNow.AddSeconds(30),
                JsonSerializer.SerializeToUtf8Bytes(payload, Json));

        SessionRuntimeGrant Grant(NoiseKeyPair key, SessionRuntimeOperation operation, string id, RemoteSessionLifecycleRequest payload) =>
            SessionRuntimeGrant.Issue(key, scope, operation, id, DateTimeOffset.UtcNow.AddSeconds(30),
                JsonSerializer.SerializeToUtf8Bytes(payload, Json));
    }

    [Fact]
    public async Task Owner_configuration_requires_the_registered_workspace_head_and_current_pin()
    {
        Directory.CreateDirectory(_root);
        var workspace = Path.Combine(_root, "workspace");
        Directory.CreateDirectory(workspace);
        var host = RedCompute.PluginSdk.Hosting.Local.LocalExecutionHost.Shared;
        Assert.NotNull(await HostGit.RunAsync(host, workspace, ["init", "-q"], CancellationToken.None));
        Assert.NotNull(await HostGit.RunAsync(host, workspace,
            ["-c", "user.name=Fixture", "-c", "user.email=fixture@example.invalid", "-c", "commit.gpgsign=false",
                "commit", "--allow-empty", "-m", "fixture"], CancellationToken.None));
        var revision = Assert.Single((await HostGit.RunAsync(host, workspace,
            ["rev-parse", "--verify", "HEAD"], CancellationToken.None))!);

        using var controller = NoiseKeyPair.Generate();
        var pin = NodeLinkKeyInfo.For(controller.PublicKey);
        var state = Path.Combine(_root, "node.json");
        await File.WriteAllBytesAsync(state, JsonSerializer.SerializeToUtf8Bytes(new
        {
            pairing = new { nodeId = "node", linkPin = pin },
        }, Json));
        var provider = DispatchProxy.Create<IFixtureProvider, Provider>();
        var configured = new RemoteSessionRuntimeConfiguration(new()
        {
            Port = 18809,
            RuntimeId = "sessions",
            PairingStatePath = state,
            Workspaces = [new("workspace", "owner", workspace, "probe", SessionAccessLevels.Ask)],
        }, instance => instance == "probe" ? provider : null);
        var scope = new SessionRuntimeScope("node", "sessions", "logical", 0, "owner", "workspace");
        var request = new RemoteSessionLifecycleRequest(revision, "probe");
        Assert.True(configured.IsReady());
        Assert.Equal(controller.PublicKey, configured.CurrentPairing()!.PinnedControllerKey);
        var resolved = await configured.ResolveAsync(scope, request, CancellationToken.None);
        Assert.NotNull(resolved);
        Assert.Equal(workspace, resolved.ProjectPath);
        Assert.Null(await configured.ResolveAsync(scope, request with { Revision = new string('0', 40) }, CancellationToken.None));
        Assert.Null(await configured.ResolveAsync(scope with { OwnerId = "other" }, request, CancellationToken.None));
        Assert.Null(await configured.ResolveAsync(scope with { AccessScope = "elsewhere" }, request, CancellationToken.None));

        await File.WriteAllTextAsync(state, """{"ended":{"reason":"revoked"}}""");
        Assert.Null(configured.CurrentPairing());
        Assert.False(configured.IsReady());
    }


    public void Dispose()
    {
        if (!Directory.Exists(_root)) return;
        var objects = Path.Combine(_root, "workspace", ".git", "objects");
        if (OperatingSystem.IsWindows() && Directory.Exists(objects))
        {
            foreach (var file in Directory.EnumerateFiles(objects, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, File.GetAttributes(file) & ~FileAttributes.ReadOnly);
        }
        Directory.Delete(_root, recursive: true);
    }

    public interface IFixtureProvider : ISessionProvider, ISessionInferenceSettings, IIdleSessionParkingProvider;

    public class Provider : DispatchProxy
    {
        public UnifiedSessionInfo? Info { get; private set; }
        public int Starts { get; private set; }
        public int Resumes { get; private set; }
        public int Parks { get; private set; }
        public int Inputs { get; private set; }
        public string? LastInputUid { get; private set; }
        private readonly List<UnifiedMessageRecord> _messages = [];

        public void Record(long id, string role, string uid, string text) => _messages.Add(new()
        {
            Id = id, SessionId = Info!.Id, Role = role, EventType = "text", MessageUid = uid,
            Content = text, Timestamp = DateTimeOffset.UtcNow,
        });
        public bool LoseStartReply { get; set; }

        protected override object? Invoke(MethodInfo? method, object?[]? args) => method?.Name switch
        {
            "get_ProviderId" => "codex",
            "get_Capabilities" => SessionCapabilities.PersistentSessions | SessionCapabilities.Resume,
            "get_SupportedAccessLevels" => SessionAccessLevels.All,
            nameof(ISessionProvider.GetSessionInfo) => (string?)args![0] == Info?.Id ? Info : null,
            nameof(ISessionProvider.GetSession) => (Info, _messages.ToList()),
            nameof(ISessionProvider.StartSessionAsync) => Start(args!),
            nameof(ISessionProvider.ResumeSessionAsync) => Resume(),
            nameof(ISessionProvider.TrySendInputAsync) => Send((string?)args![3]),
            nameof(IIdleSessionParkingProvider.TryParkIdleSessionAsync) => Park((string)args![1]!),
            _ => throw new InvalidOperationException(method?.Name),
        };

        private Task<UnifiedSessionInfo?> Start(object?[] args)
        {
            Starts++;
            Info = new UnifiedSessionInfo
            {
                Id = SessionHandoffScope.Current!.RuntimeSessionId,
                Provider = "codex", ProviderEntity = (string?)args[10],
                ProjectName = "fixture", ProjectPath = (string)args[0]!, UserId = (string?)args[2],
                ProviderSessionId = "fixture-native-thread", Status = SessionStatus.Idle,
                AccessLevel = SessionAccessScope.Current!.AccessLevel, MaxAccessLevel = SessionAccessScope.Current.MaxAccessLevel,
            };
            if (!LoseStartReply) return Task.FromResult<UnifiedSessionInfo?>(Info);
            LoseStartReply = false;
            return Task.FromException<UnifiedSessionInfo?>(new IOException("simulated lost reply after durable provider start"));
        }

        private Task<SessionInputDeliveryResult> Send(string? uid)
        {
            Inputs++;
            LastInputUid = uid;
            return Task.FromResult(SessionInputDeliveryResult.Accepted());
        }

        private Task<bool> Park(string reason)
        {
            Parks++;
            Info!.Status = SessionStatus.Stopped;
            Info.StopReason = reason;
            return Task.FromResult(true);
        }

        private Task<UnifiedSessionInfo?> Resume()
        {
            Resumes++;
            Info!.Status = SessionStatus.Idle;
            Info.StopReason = null;
            return Task.FromResult<UnifiedSessionInfo?>(Info);
        }
    }
}
