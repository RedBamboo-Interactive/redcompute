using System.Reflection;
using System.Text.Json;
using RedCompute.App.Services;
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
        var runtime = OpenRuntime();

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

        var park = Grant(controller, SessionRuntimeOperation.Park, "park", resume);
        var parked = await runtime.ApplyAsync(SessionRuntimeOperation.Park, park);
        Assert.Equal(200, parked.Status);
        Assert.Equal(SessionStatus.Stopped, probe.Info.Status);
        Assert.Equal(parked.Body, (await OpenRuntime().ApplyAsync(SessionRuntimeOperation.Park, park)).Body);
        Assert.Equal(1, probe.Parks);

        RemoteSessionLifecycleRuntime OpenRuntime()
        {
            Directory.CreateDirectory(_root);
            var attachments = new InputAttachmentStore(_config, Path.Combine(_root, "bytes"), Path.Combine(_root, "input.db"));
            return new(new SessionInputQueueStore(_config, attachments),
                () => new("node", "sessions", controller.PublicKey),
                (_, _, _) => Task.FromResult<RemoteSessionRuntimeWorkspace?>(new("owner", "workspace", "abc123",
                    _root, "probe", provider, new(SessionAccessLevels.Ask, SessionAccessLevels.Ask))),
                TimeProvider.System);
        }

        SessionRuntimeGrant Grant(NoiseKeyPair key, SessionRuntimeOperation operation, string id, RemoteSessionLifecycleRequest payload) =>
            SessionRuntimeGrant.Issue(key, scope, operation, id, DateTimeOffset.UtcNow.AddSeconds(30),
                JsonSerializer.SerializeToUtf8Bytes(payload, Json));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    public interface IFixtureProvider : ISessionProvider, ISessionInferenceSettings, IIdleSessionParkingProvider;

    public class Provider : DispatchProxy
    {
        public UnifiedSessionInfo? Info { get; private set; }
        public int Starts { get; private set; }
        public int Resumes { get; private set; }
        public int Parks { get; private set; }
        public bool LoseStartReply { get; set; }

        protected override object? Invoke(MethodInfo? method, object?[]? args) => method?.Name switch
        {
            "get_ProviderId" => "codex",
            "get_Capabilities" => SessionCapabilities.PersistentSessions | SessionCapabilities.Resume,
            "get_SupportedAccessLevels" => SessionAccessLevels.All,
            nameof(ISessionProvider.GetSessionInfo) => (string?)args![0] == Info?.Id ? Info : null,
            nameof(ISessionProvider.StartSessionAsync) => Start(args!),
            nameof(ISessionProvider.ResumeSessionAsync) => Resume(),
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
