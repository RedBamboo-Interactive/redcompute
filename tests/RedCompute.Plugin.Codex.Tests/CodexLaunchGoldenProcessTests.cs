using System.Text.Json.Nodes;
using RedCompute.Core.Jobs;
using RedCompute.Plugin.Codex;
using RedCompute.PluginSdk;
using Xunit;

namespace RedCompute.Plugin.Codex.Tests;

/// <summary>
/// Layer B launch goldens for Codex (node-zero design, section 4): the real services launch the probe
/// CLI as codex.exe through their production launch path; the probe records what the OS started and
/// the containment it landed in. Real processes, Windows only.
/// </summary>
[Collection(CodexLaunchGoldenCollection.Name)]
public sealed class CodexLaunchGoldenProcessTests : IDisposable
{
    private const string Token = "golden-execution-token";

    private static readonly JsonObject AppServer = new()
    {
        ["rpc"] = new JsonObject
        {
            ["results"] = new JsonObject
            {
                ["thread/start"] = new JsonObject { ["thread"] = new JsonObject { ["id"] = "thread-golden" } },
                ["model/list"] = new JsonObject { ["data"] = new JsonArray() },
            },
        },
    };

    private readonly ProbeHarness _probe = new("codex");
    private readonly string _project;
    private readonly string _scratch;

    public CodexLaunchGoldenProcessTests()
    {
        _project = Path.Combine(_probe.Root, "project");
        _scratch = Path.Combine(_probe.Root, "scratch");
        Directory.CreateDirectory(_project);
        Directory.CreateDirectory(_scratch);
    }

    [WindowsFact("containment is a Windows Job Object and the probe replaces codex.exe")]
    public async Task Codex_app_server_session()
    {
        var config = Config((JsonObject)AppServer.DeepClone());
        var jobs = new GoldenJobTracker();
        var store = new GoldenStore();
        await using var interactive = new CodexInteractiveService(
            config, store, new CodexModelCatalog(config, (_, _) => { }),
            new CodexSessionService(config, jobs, store, (_, _) => { }), jobs,
            new CodexAccountUsageService(config, (_, _) => { }), new NoQualityModes(),
            () => null, (_, _) => { });
        CodexSessionInfo? info = null;
        var launches = await _probe.RunAsync(async () =>
        {
            using var _ = SessionScratch.PushExecutionToken(Token);
            info = await interactive.StartSessionAsync(_project, null, "owner", null, null, null, null, null, null,
                Provenance(), _scratch);
        });
        Assert.NotNull(info);
        _probe.Normalizer.Add(info!.Id, "<session-id>");
        LaunchGoldens.Verify("B-codex-app-server-session", Canonical(launches));
    }

    [WindowsFact("containment is a Windows Job Object and the probe replaces codex.exe")]
    public async Task Codex_exec()
    {
        var config = Config(new JsonObject
        {
            ["onEof"] = new JsonArray(
                "{\"type\":\"thread.started\",\"thread_id\":\"thread-golden\"}",
                "{\"type\":\"item.completed\",\"item\":{\"id\":\"item-1\",\"type\":\"agent_message\",\"text\":\"ok\"}}",
                "{\"type\":\"turn.completed\",\"usage\":{\"input_tokens\":1,\"output_tokens\":1}}"),
        });
        var service = new CodexSessionService(config, new GoldenJobTracker(), new GoldenStore(), (_, _) => { });
        CodexSessionService.ExecuteResult? result = null;
        var launches = await _probe.RunAsync(async () =>
        {
            result = await service.ExecuteExecAsync("golden prompt", null, _project, "gpt-golden", "read-only", 60,
                CancellationToken.None, env: new Dictionary<string, string> { ["GOLDEN_EXEC_ENV"] = "1" });
        });
        Assert.NotNull(result);
        LaunchGoldens.Verify("B-codex-exec", Canonical(launches));
    }

    [WindowsFact("containment is a Windows Job Object and the probe replaces codex.exe")]
    public async Task Codex_model_list_app_server()
    {
        var config = Config((JsonObject)AppServer.DeepClone());
        var catalog = new CodexModelCatalog(config, (_, _) => { });
        var launches = await _probe.RunAsync(async () => await catalog.GetDefaultModelIdAsync());
        LaunchGoldens.Verify("B-codex-model-list", Canonical(launches));
    }

    private JsonNode Canonical(IReadOnlyList<(ProbeLaunch Launch, string Containment)> launches)
    {
        Assert.NotEmpty(launches);
        return new JsonArray(launches.Select(l => (JsonNode)l.Launch.Canonical(_probe.Normalizer, l.Containment)).ToArray());
    }

    private CodexConfig Config(JsonObject script) => new() { CodexPath = _probe.Install("codex", script) };

    private static JobProvenance Provenance() => JobProvenance.DirectRedCompute(
        "POST", "/ai-session/sessions", new JobBeneficiary("user", "owner"), "golden-request", null);

    public void Dispose() => _probe.Dispose();

    private sealed class NoQualityModes : IProviderQualityModeResolver
    {
        public bool TryResolveRequested(string qualityTier, string? preferredProvider, out ProviderQualityMode? resolved)
        {
            resolved = null;
            return false;
        }
    }

    private sealed class GoldenJobTracker : IJobTracker
    {
        private readonly Dictionary<Guid, JobRecord> _jobs = [];
        public JobRecord CreateJob(JobSubmission submission)
        {
            var job = new JobRecord
            {
                CapabilitySlug = submission.CapabilitySlug, ProviderName = submission.ProviderName,
                CreationProvenance = submission.Provenance,
            };
            lock (_jobs) _jobs[job.Id] = job;
            return job;
        }
        public void MarkRunning(Guid jobId) { }
        public void MarkCompleted(Guid jobId, string? outputLocation = null, long? outputSizeBytes = null,
            string? contentType = null, string? resultJson = null, double? costUsd = null) { }
        public void MarkFailed(Guid jobId, string errorMessage, string? errorDetails = null, string? resultJson = null) { }
        public void MarkCancelled(Guid jobId) { }
        public void UpdateName(Guid jobId, string name) { }
        public JobRecord? GetJob(Guid jobId) { lock (_jobs) return _jobs.GetValueOrDefault(jobId); }
    }

    private sealed class GoldenStore : ICodexSessionStore
    {
        private readonly Dictionary<string, CodexSessionRecord> _sessions = [];
        public CodexSessionRecord? FindSession(string sessionId) { lock (_sessions) return _sessions.GetValueOrDefault(sessionId); }
        public CodexSessionRecord? FindSessionByJobId(Guid jobId) { lock (_sessions) return _sessions.Values.FirstOrDefault(s => s.JobId == jobId); }
        public List<CodexSessionRecord> GetActiveSessions() => [];
        public List<CodexSessionRecord> GetSessionsWithStopReason(string status, string stopReason) => [];
        public List<CodexSessionRecord> GetSessionsWithoutJobs() => [];
        public List<CodexSessionRecord> GetRecentSessions(HashSet<string> excludeIds, int limit = 20, bool includeDismissed = false) => [];
        public void SaveSession(CodexSessionRecord record) { lock (_sessions) _sessions[record.Id] = record; }
        public void DismissSession(string sessionId) { }
        public void AddMessage(CodexMessageRecord message) { }
        public void AddMessages(List<CodexMessageRecord> messages) { }
        public List<CodexMessageRecord> GetMessages(string sessionId, int limit = 50_000) => [];
        public Dictionary<Guid, string> GetSessionStatusesByJobIds(IEnumerable<Guid> jobIds) => [];
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class CodexLaunchGoldenCollection
{
    public const string Name = "Codex launch goldens";
}
