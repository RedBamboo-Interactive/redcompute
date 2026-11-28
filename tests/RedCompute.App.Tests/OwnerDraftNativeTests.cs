using System.Collections.Concurrent;
using System.Net;
using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using RedBamboo.AppHost.Auth;
using Xunit.Abstractions;
using RedBamboo.AppHost.WebSockets;
using RedCompute.App.Services;
using RedCompute.App.Api.Endpoints;
using RedCompute.Core.Configuration;
using RedCompute.Core.Capabilities;
using RedCompute.Core.Jobs;
using RedCompute.Core.Providers;
using RedCompute.Core.Sessions;
using RedCompute.Plugin.Codex;
using RedCompute.PluginSdk;
using RedCompute.PluginSdk.Hosting;
using RedCompute.PluginSdk.Hosting.Local;
using Xunit;

namespace RedCompute.App.Tests;

public sealed class OwnerDraftNativeTests(ITestOutputHelper output)
{
    private static readonly string Owner = "9f91254a-29db-4313-a46c-0a35030861aa";
    private static readonly string Agent = "1ca0439d-88d7-4af6-8a26-537c85a39363";
    private static readonly Guid Binding = Guid.Parse("c5390a54-7549-4f76-81e0-a905a87f7064");
    private const string Tool = "leaf_submit_outreach_draft";
    private const string Discussion = "fixture-live";
    private const string Composer = "/api/apps/nova/discussions/fixture-live/message";
    private static readonly JsonSerializerOptions Wire = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task Native_codex_draft_is_bound_to_the_exact_delivered_owner_input_after_resume()
    {
        var executable = Environment.GetEnvironmentVariable("REDCOMPUTE_NATIVE_OWNER_DRAFT_CODEX");
        if (string.IsNullOrWhiteSpace(executable)) return;
        var enabled = Environment.GetEnvironmentVariable("REDCOMPUTE_NATIVE_OWNER_DRAFT_RED") != "1";
        var root = Path.Combine(Path.GetTempPath(), $"owner-draft-native-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            await ExerciseNativeAsync(root, executable, enabled);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Owner_draft_client_does_not_follow_a_redirect_to_another_loopback_recipient()
    {
        await using var recipient = await Lab.StartAsync();
        await using var redirectTarget = await Lab.StartAsync();
        recipient.DraftRedirect = redirectTarget.BaseUrl + OwnerDraftClient.Route;
        using var client = new OwnerDraftClient(new Uri(recipient.BaseUrl), recipient.Jwt);
        var payload = new OwnerDraftArguments(Binding, 1, "Synthetic held content", null, []);
        var draft = new OwnerDraftAdmission(1, "discord.outreach.draft", "fixture-intent",
            OwnerDraftClient.Digest(payload), DateTimeOffset.UtcNow.AddMinutes(1), Discussion, "fixture-session",
            Owner, Agent, "fixture-input", "fixture-message", "fixture-connection", "fixture-thread",
            "fixture-turn", "fixture-call", payload);

        var result = await client.AdmitAsync(draft, CancellationToken.None);

        Assert.Equal("owner_draft_recipient_unavailable", result.Status);
        Assert.Single(recipient.Admissions);
        Assert.Equal(0, redirectTarget.ReceivedRequests);
    }

    private async Task ExerciseNativeAsync(string root, string executable, bool enabled)
    {
        await using var lab = await Lab.StartAsync();
        var home = Directory.CreateDirectory(Path.Combine(root, "codex")).FullName;
        var project = Directory.CreateDirectory(Path.Combine(root, "project")).FullName;
        await File.WriteAllTextAsync(Path.Combine(home, "config.toml"), $$"""
            model = "gpt-5.2"
            model_provider = "fixture"
            web_search = "disabled"
            check_for_update_on_startup = false
            [analytics]
            enabled = false
            [model_providers.fixture]
            name = "Loopback fixture"
            base_url = "{{lab.BaseUrl}}v1"
            wire_api = "responses"
            env_key = "LEAF_FIXTURE_API_KEY"
            request_max_retries = 0
            stream_max_retries = 0
            """);
        var tracker = new Jobs();
        var config = new CodexConfig { CodexPath = executable, Model = "gpt-5.2" };
        var nativeStore = new NativeStore();
        var logs = new ConcurrentQueue<string>();
        void Log(string message, Guid? _) => logs.Enqueue(message);
        await using var codex = new CodexInteractiveService(config, nativeStore, new CodexModelCatalog(config, Log),
            new CodexSessionService(config, tracker, nativeStore, Log), tracker,
            new CodexAccountUsageService(config, Log), new NoQualityModes(), () => null, Log)
        {
            EnableOwnerDraftTool = enabled,
            LaunchAppServerProcess = spec => LocalExecutionHost.Shared.Processes.Start(spec with
            {
                EnvironmentBase = EnvironmentBase.Empty,
                EnvironmentOverlay = FixtureEnvironment(home),
            }),
        };
        var opened = await codex.StartSessionAsync(project, "gpt-5.2", Owner, null, null, "low", null,
            null, null, Provenance("launch", JobCauses.Agent));
        Assert.True(opened is not null, string.Join("\n", logs));
        var id = opened!.Id;
        var original = opened.ThreadId;
        Assert.True(await codex.SendMessageAsync(id, "warmup"));
        await WaitIdleAsync(codex, id, logs);
        Assert.True(await codex.TryParkIdleSessionAsync(id, SessionStopReasons.MaintenanceRestart));
        var resumed = await codex.ResumeSessionAsync(id, Provenance(id, JobCauses.Agent,
            new JobContextReference("automation", "fixture-heartbeat-launch")));
        Assert.Equal(original, resumed?.ThreadId);

        var attachments = new InputAttachmentStore(new RedComputeConfig(), Path.Combine(root,"bytes"), Path.Combine(root,"input.db"));
        var receipts = new SessionInputQueueStore(new RedComputeConfig(), attachments);
        var proxy = DispatchProxy.Create<SessionInputQueueEventTests.ITestProvider, NativeProvider>();
        var native = (NativeProvider)(object)proxy;
        native.Codex = codex;
        var registry = new CapabilityRegistry();
        registry.Register("ai-session", new CapabilityDefinition { Slug = "ai-session", DisplayName = "Fixture" },
            new CapabilityConfig(), new Dictionary<string, IBackendProvider> { ["codex"] = proxy }, "codex");
        var origin = new OwnerDraftOrigin(Discussion, id, Owner, Agent, "live", false, true);
        using var client = new OwnerDraftClient(new Uri(lab.BaseUrl), lab.Jwt);
        var issuer = new ExecutionTokenIssuer(lab.Jwt, lab.Options);
        using var services = new ServiceCollection().AddSingleton(lab.Options)
            .AddSingleton<IExecutionTokenIssuer>(issuer).BuildServiceProvider();
        TaskCompletionSource? beforeAdmission = null;
        var broker = new OwnerDraftBroker(receipts, async (_, ct) =>
        {
            if (beforeAdmission is { } entered)
            {
                entered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }
            return origin;
        }, client);
        var queue = new SessionInputQueueService(receipts, attachments, registry, tracker, new WebSocketBroadcaster(), Log)
            { OwnerDrafts = broker };

        async Task<SessionInputQueueItem> DeliverAsync(string uid, JobProvenance source, bool authenticated = true)
        {
            var submission = Submission(id, uid, source);
            SessionInputAdmissionResult admission;
            if (authenticated)
            {
                var identity = new ExecutionIdentity(ExecutionIdentity.CurrentSchemaVersion, Guid.NewGuid().ToString(),
                    new("nova", "Nova"), new("agent", "nova", "Nova", Agent), new("user", Owner),
                    [.. source.Context.Where(reference => reference.Kind != "execution")
                        .Select(r => new ExecutionContextReference(r.Kind, r.Id, r.EntityId, r.NameSnapshot, r.Route)),
                        new("job-cause", source.Cause)]);
                var token = issuer.Issue(identity, new ExecutionPrincipal(Owner, "fixture@example.invalid", "Fixture", []));
                var context = new DefaultHttpContext { User = lab.Jwt.ValidateToken(token.AccessToken)!, RequestServices = services };
                context.Request.Method = "POST";
                Assert.True(ExecutionIdentityClaims.TryRead(context.User, out var acceptedIdentity, out _));
                Assert.NotNull(acceptedIdentity);
                using var scope = ExecutionContextScope.Push(acceptedIdentity);
                Assert.True(ExecutionJobProvenance.TryResolve(context, "/ai-session/sessions/{id}/message", out var captured));
                admission = await UnifiedSessionEndpoints.AdmitWithCurrentCallerAsync(context, proxy, native.GetInfo(id),
                    submission with { Provenance = captured! }, queue, CancellationToken.None);
            }
            else
            {
                admission = await queue.AdmitAsync(submission);
            }
            await WaitIdleAsync(codex, id, logs);
            Assert.Equal(SessionInputQueueState.Delivered, admission.Item.State);
            return admission.Item;
        }

        var accepted = await DeliverAsync("owner", Provenance(id));
        Assert.True(lab.SawToolAfterResume, string.Join("\n", logs));
        var draft = Assert.Single(lab.Admissions);
        Assert.Equal(accepted.Id, draft.QueueItemId);
        Assert.Equal(accepted.MessageUid, draft.MessageUid);
        Assert.Equal(original, draft.ThreadId);
        Assert.Equal(nativeStore.GetUserTurnId(id, "owner"), draft.TurnId);
        Assert.Equal(Owner, draft.OwnerId);
        Assert.Equal("discord.outreach.draft", draft.Operation);
        Assert.Equal(OwnerDraftClient.Digest(draft.Draft), draft.PayloadSha256);

        await DeliverAsync("forge", Provenance(id));
        await DeliverAsync("callback", Provenance(id, JobCauses.Agent, new JobContextReference("event","fixture")));
        await DeliverAsync("automation", Provenance(id, JobCauses.Agent, new JobContextReference("automation","fixture")));
        await DeliverAsync("external", Provenance(id, JobCauses.Beneficiary, new JobContextReference("external-conversation","fixture")));
        await DeliverAsync("unsigned", Provenance(id, JobCauses.Beneficiary, new JobContextReference("redcompute-owner-draft-input", id)),
            authenticated: false);
        origin = origin with { Type = "heartbeat" };
        await DeliverAsync("heartbeat", Provenance(id));
        origin = origin with { Type = "live", Confidential = true };
        await DeliverAsync("confidential", Provenance(id));
        origin = origin with { Confidential = false, OwnerId = Agent };
        await DeliverAsync("wrong-owner", Provenance(id));
        origin = origin with { OwnerId = Owner };

        var first = Submission(id, "merged-first", OwnerDraftBroker.ClassifyAdmission(Provenance(id), id, Owner, true));
        var second = Submission(id, "merged-second", first.Provenance);
        var firstItem = (await receipts.EnqueueAsync(first)).Item;
        var secondItem = (await receipts.EnqueueAsync(second)).Item;
        await queue.ProcessSessionAsync(id, CancellationToken.None);
        await WaitIdleAsync(codex, id, logs);
        Assert.Equal("merged-first", (await receipts.GetAsync(id,firstItem.Id,Owner))?.DeliveredMessageUid);
        Assert.Equal("merged-first", (await receipts.GetAsync(id,secondItem.Id,Owner))?.DeliveredMessageUid);
        Assert.Equal(new[] { "warmup", "owner", "forge", "callback", "automation", "external", "unsigned", "heartbeat",
            "confidential", "wrong-owner", "merged-first" }, nativeStore.GetMessages(id)
            .Where(row => row.Role == "user")
            .Select(row => row.Content!.Split('\n')[0]));
        Assert.Single(lab.Admissions);

        // A real native callback stalled in the source read loses its authority when steering starts.
        beforeAdmission = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var steered = DeliverAsync("steered", Provenance(id));
        await beforeAdmission.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await codex.TrySteerInputAsync(id, [SessionInputPart.TextPart("agent steer")],
            messageUid: "agent-steer").WaitAsync(TimeSpan.FromSeconds(10));
        await steered;
        beforeAdmission = null;
        Assert.Single(lab.Admissions);

        // A late/retired callback cannot dispatch, and concurrent identical intents share one submission.
        var retryItem = (await receipts.EnqueueAsync(Submission(id,"retry",
            OwnerDraftBroker.ClassifyAdmission(Provenance(id),id,Owner,true)))).Item;
        receipts.CloseTurn(id, new SessionTurnEnd("Idle",null,null,null,0));
        var retryBatch = await receipts.ClaimBatchAsync(id,"fixture",TimeSpan.FromMinutes(1));
        var info = native.GetInfo(id);
        var delivery = broker.Prepare(info,retryBatch);
        Assert.NotNull(delivery);
        await receipts.MarkDeliveredAsync(retryBatch,retryItem.MessageUid);
        delivery!.Settle(true);
        var arguments = new OwnerDraftArguments(Binding,1,"synthetic draft",null,[]);
        var nativeCall = new NativeOwnerDraftCall("connection",original!,"turn","call-1",arguments);
        Assert.Equal("owner_input_not_delivered_or_retired",
            (await delivery.InvokeAsync(nativeCall,()=>false,CancellationToken.None)).Status);
        lab.HoldNextReply();
        var attempt = delivery.InvokeAsync(nativeCall,()=>true,CancellationToken.None);
        try
        {
            await lab.DraftEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var concurrent = delivery.InvokeAsync(nativeCall with { CallId = "call-2" },()=>true,CancellationToken.None);
            Assert.Equal(2,lab.Admissions.Count);
            lab.ReleaseDraftReply();
            Assert.All(await Task.WhenAll(attempt,concurrent), response =>
                Assert.Equal("owner_draft_recipient_unavailable",response.Status));
            var recovered = await delivery.InvokeAsync(nativeCall,()=>true,CancellationToken.None);
            Assert.Equal("held",recovered.Status);
            Assert.Equal(3,lab.Admissions.Count);
            Assert.Equal(lab.Admissions.ElementAt(1).IntentId, lab.Admissions.ElementAt(2).IntentId);
            var conflict = await delivery.InvokeAsync(nativeCall with { Draft = arguments with { Content = "changed" } },
                ()=>true,CancellationToken.None);
            Assert.Equal("owner_draft_conflict",conflict.Status);
            Assert.Equal(2,lab.UniqueIntents.Count);
        }
        finally { lab.ReleaseDraftReply(); }

        // A legacy thread starts with no tool and the later opt-in resume leaves that catalog alone.
        await codex.StopSessionAsync(id);
        await using var legacyStarter = new CodexInteractiveService(config,nativeStore,new CodexModelCatalog(config,Log),
            new CodexSessionService(config,tracker,nativeStore,Log),tracker,
            new CodexAccountUsageService(config,Log),new NoQualityModes(),()=>null,Log)
        { LaunchAppServerProcess = codex.LaunchAppServerProcess };
        var legacy = await legacyStarter.StartSessionAsync(project,"gpt-5.2",Owner,null,null,"low",null,null,null,Provenance("legacy"));
        Assert.NotNull(legacy);
        Assert.True(await legacyStarter.SendMessageAsync(legacy.Id,"warmup"));
        await WaitIdleAsync(legacyStarter,legacy.Id,logs);
        var legacyThread = legacy.ThreadId;
        Assert.True(await legacyStarter.TryParkIdleSessionAsync(legacy.Id,SessionStopReasons.MaintenanceRestart));
        Assert.Equal(legacyThread,(await codex.ResumeSessionAsync(legacy.Id,Provenance("legacy",JobCauses.Agent)))?.ThreadId);
        Assert.True(await codex.SendMessageAsync(legacy.Id,"legacy"));
        await WaitIdleAsync(codex,legacy.Id,logs);
        Assert.False(lab.LegacySawTool);
        Assert.Equal(3,lab.Admissions.Count);
        output.WriteLine($"owner={accepted.Id}/{accepted.MessageUid}, logical={id}, native={draft.ThreadId}, "
            + $"turn={draft.TurnId}, call={draft.CallId}, intent={draft.IntentId}, legacy={legacyThread}");
        output.WriteLine("Native admissions: 1. Restricted fixtures: callback, automation, Heartbeat, external, "
            + "confidential, wrong owner, forged argument, unsigned marker, merged inputs, steered turn and legacy: 0.");
        output.WriteLine("Concurrent recipients: 1 request per attempt. Retry reused intent. Fake ledger: 2 intents.");
    }

    private static Dictionary<string, string> FixtureEnvironment(string home)
    {
        var names = new[] { "SystemRoot", "WINDIR", "COMSPEC", "PATHEXT", "PATH", "TEMP", "TMP",
            "USERPROFILE", "APPDATA", "LOCALAPPDATA", "HOMEDRIVE", "HOMEPATH" };
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in names)
            if (Environment.GetEnvironmentVariable(name) is { } value) environment[name] = value;
        environment["CODEX_HOME"] = home;
        environment["LEAF_FIXTURE_API_KEY"] = "synthetic";
        environment["NO_PROXY"] = "127.0.0.1";
        return environment;
    }

    private static async Task WaitIdleAsync(CodexInteractiveService codex, string id, ConcurrentQueue<string> logs)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (!timeout.IsCancellationRequested)
        {
            if (codex.GetSession(id).Info?.Status == "Idle") return;
            await Task.Delay(10);
        }
        Assert.Fail(string.Join("\n",logs));
    }

    private static SessionInputQueueSubmission Submission(string id,string uid,JobProvenance provenance)
        => new(id,Owner,[new("text",uid)],uid,null,provenance,SessionInputDeliveryPolicy.AfterCurrent,uid,[],uid);

    private static JobProvenance Provenance(string id,string cause = JobCauses.Beneficiary,params JobContextReference[] references)
        => new(JobProvenance.CurrentSchemaVersion,
            new("redcompute",new("app","nova",null,"Nova"),new("http","/ai-session/sessions/{id}/message","POST")),
            new("agent","Nova",Agent,"nova"),new("user",Owner,"Owner"),
            [new("discussion",Discussion),new("session",id),new("origin-entrypoint",Route: Composer),
                new("execution",Guid.NewGuid().ToString()),..references],
            new(),JobProvenanceAssurance.Verified,DateTimeOffset.UtcNow) { Cause = cause };

    public class NativeProvider : DispatchProxy
    {
        internal CodexInteractiveService Codex = null!;
        internal UnifiedSessionInfo GetInfo(string id)
        {
            var info = Codex.GetSession(id).Info!;
            return new()
            {
                Id = id,Provider = "codex",ProjectName = info.ProjectName,ProjectPath = info.ProjectPath,
                Status = Enum.Parse<SessionStatus>(info.Status),UserId = info.UserId,OwnerAgentId = Agent,
                JobId = info.JobId,ProviderSessionId = info.ThreadId,
            };
        }

        protected override object? Invoke(MethodInfo? method,object?[]? args)
            => SessionProviderProxy.Route(method,args,(m,a) => m!.Name switch
            {
                "get_ProviderId" => "codex",
                "get_ProviderDisplayName" => "Codex",
                nameof(ISessionProvider.GetSession) => (GetInfo((string)a![0]!),new List<UnifiedMessageRecord>()),
                nameof(ISessionProvider.TrySendInputAsync) => SendAsync(a!),
                _ => throw new NotSupportedException(m.Name),
            });

        private async Task<SessionInputDeliveryResult> SendAsync(object?[] args)
            => await Codex.SendInputAsync((string)args[0]!, (IReadOnlyList<SessionInputPart>)args[1]!,
                (string?)args[2],(string?)args[3],(string?)args[4])
                ? SessionInputDeliveryResult.Accepted() : SessionInputDeliveryResult.Busy();
    }

    private sealed class NoQualityModes : IProviderQualityModeResolver
    {
        public bool TryResolveRequested(string qualityTier,string? preferredProvider,out ProviderQualityMode? resolved)
        {
            resolved = null;
            return false;
        }
    }

    private sealed class Jobs : IJobTracker
    {
        private readonly ConcurrentDictionary<Guid,JobRecord> _jobs = [];
        public JobRecord CreateJob(JobSubmission submission)
        {
            var job = new JobRecord { CapabilitySlug = submission.CapabilitySlug, ProviderName = submission.ProviderName,
                CreationProvenance = submission.Provenance };
            _jobs[job.Id] = job;
            return job;
        }
        public JobRecord? GetJob(Guid jobId) => _jobs.GetValueOrDefault(jobId);
        public void MarkRunning(Guid jobId) { }
        public void MarkCompleted(Guid jobId,string? outputLocation = null,long? outputSizeBytes = null,
            string? contentType = null,string? resultJson = null,double? costUsd = null) { }
        public void MarkFailed(Guid jobId,string errorMessage,string? errorDetails = null,string? resultJson = null) { }
        public void MarkCancelled(Guid jobId) { }
        public void UpdateName(Guid jobId,string name) { }
    }

    private sealed class NativeStore : ICodexSessionStore
    {
        private readonly ConcurrentDictionary<string,CodexSessionRecord> _sessions = [];
        private readonly ConcurrentQueue<CodexMessageRecord> _messages = [];
        private readonly ConcurrentDictionary<(string,string),string> _turns = [];
        public CodexSessionRecord? FindSession(string sessionId) => _sessions.GetValueOrDefault(sessionId);
        public CodexSessionRecord? FindSessionByJobId(Guid jobId) => _sessions.Values.FirstOrDefault(row=>row.JobId == jobId);
        public List<CodexSessionRecord> GetActiveSessions() => [];
        public List<CodexSessionRecord> GetSessionsWithStopReason(string status,string stopReason) => [];
        public List<CodexSessionRecord> GetSessionsWithoutJobs() => [];
        public List<CodexSessionRecord> GetRecentSessions(HashSet<string> excludeIds,int limit = 20,bool includeDismissed = false) => [];
        public void SaveSession(CodexSessionRecord record) => _sessions[record.Id] = record;
        public void DismissSession(string sessionId) { }
        public void AddMessage(CodexMessageRecord message) => _messages.Enqueue(message);
        public void AddMessages(List<CodexMessageRecord> messages) { foreach(var row in messages) _messages.Enqueue(row); }
        public List<CodexMessageRecord> GetMessages(string sessionId,int limit = 50_000)
            => _messages.Where(row => row.SessionId == sessionId).TakeLast(limit).ToList();
        public void SetUserTurnId(string sessionId,string messageUid,string turnId) => _turns[(sessionId,messageUid)] = turnId;
        public string? GetUserTurnId(string sessionId,string messageUid) => _turns.GetValueOrDefault((sessionId,messageUid));
        public Dictionary<Guid,string> GetSessionStatusesByJobIds(IEnumerable<Guid> jobIds) => [];
    }

    private sealed class Lab(WebApplication app) : IAsyncDisposable
    {
        private int _response;
        private int _receivedRequests;
        public int ReceivedRequests => Volatile.Read(ref _receivedRequests);
        public string? DraftRedirect { get; set; }
        private TaskCompletionSource? _hold;
        public string BaseUrl => app.Urls.Single().TrimEnd('/') + "/";
        public JwtOptions Options { get; } = new() { SigningKey = new string('f', 64) };
        private JwtService? _jwt;
        public JwtService Jwt => _jwt ??= new(Options);
        public ConcurrentQueue<OwnerDraftAdmission> Admissions { get; } = [];
        public ConcurrentDictionary<string,string> UniqueIntents { get; } = [];
        public TaskCompletionSource DraftEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool SawToolAfterResume { get; private set; }
        public bool LegacySawTool { get; private set; }
        public void HoldNextReply() => _hold = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void ReleaseDraftReply() => _hold?.TrySetResult();

        public static async Task<Lab> StartAsync()
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(options=>options.Listen(IPAddress.Loopback,0));
            var app = builder.Build();
            var lab = new Lab(app);
            app.MapGet("/v1/models",()=>Results.Json(new { data = Array.Empty<object>(), models = Array.Empty<object>() }));
            app.MapPost("/v1/responses",lab.RespondAsync);
            app.MapPost("/" + OwnerDraftClient.Route,lab.ReceiveAsync);
            await app.StartAsync();
            return lab;
        }

        private async Task ReceiveAsync(HttpContext ctx)
        {
            Interlocked.Increment(ref _receivedRequests);
            var auth = ctx.Request.Headers.Authorization.ToString();
            Assert.StartsWith("Bearer ", auth);
            var principal = Jwt.ValidateToken(auth["Bearer ".Length..]);
            Assert.NotNull(principal);
            Assert.Equal("service:redcompute", principal.FindFirst("sub")?.Value);
            Assert.Equal("redcompute", principal.FindFirst("client_id")?.Value);
            Assert.Contains("service", ExecutionIdentityClaims.ReadRoles(principal));
            Assert.DoesNotContain("admin", ExecutionIdentityClaims.ReadRoles(principal));
            Assert.True(string.IsNullOrEmpty(ctx.Request.Headers.Origin));
            var draft = (await ctx.Request.ReadFromJsonAsync<OwnerDraftAdmission>(Wire))!;
            Assert.Equal(draft.IntentId,ctx.Request.Headers["Idempotency-Key"].ToString());
            Assert.InRange(draft.ExpiresAt,DateTimeOffset.UtcNow,DateTimeOffset.UtcNow.AddMinutes(2));
            UniqueIntents.TryAdd(draft.IntentId,draft.PayloadSha256);
            Assert.Equal(UniqueIntents[draft.IntentId],draft.PayloadSha256);
            Admissions.Enqueue(draft);
            if (DraftRedirect is { } destination)
            {
                ctx.Response.StatusCode = StatusCodes.Status307TemporaryRedirect;
                ctx.Response.Headers.Location = destination;
                return;
            }
            if (_hold is { } hold && !hold.Task.IsCompleted)
            {
                DraftEntered.TrySetResult();
                await hold.Task.WaitAsync(TimeSpan.FromSeconds(10));
                ctx.Response.StatusCode = 503;
                return;
            }
            await ctx.Response.WriteAsJsonAsync(new OwnerDraftResult("held","held-" + draft.IntentId[..10]));
        }

        private async Task RespondAsync(HttpContext ctx)
        {
            using var request = await JsonDocument.ParseAsync(ctx.Request.Body);
            var root = request.RootElement;
            var tools = root.GetProperty("tools");
            var hasTool = tools.EnumerateArray().Any(tool => tool.TryGetProperty("name",out var name) && name.GetString() == Tool);
            var last = root.GetProperty("input").EnumerateArray().Last();
            var followup = last.GetProperty("type").GetString() == "function_call_output";
            var input = last.GetRawText();
            if (!followup && input.Contains("owner",StringComparison.Ordinal)) SawToolAfterResume = hasTool;
            if (!followup && input.Contains("legacy",StringComparison.Ordinal)) LegacySawTool = hasTool;
            var id = "fixture_response_" + Interlocked.Increment(ref _response);
            var response = new { id, @object = "response", status = "completed", model = "gpt-5.2",
                output = Array.Empty<object>(), usage = new { input_tokens = 1, output_tokens = 1, total_tokens = 2 } };
            var args = new Dictionary<string,object> { ["bindingId"] = Binding, ["bindingGeneration"] = 1, ["content"] = "Synthetic held content" };
            if (input.Contains("forge",StringComparison.Ordinal)) args["messageUid"] = "forged-user-input";
            var item = hasTool && !followup && !input.Contains("warmup",StringComparison.Ordinal)
                ? (object)new { type = "function_call", id = "fc_" + id, call_id = "call_" + id, name = Tool,
                    arguments = JsonSerializer.Serialize(args), status = "completed" }
                : new { type = "message", id = "msg_" + id, role = "assistant", status = "completed",
                    content = new[] { new { type = "output_text", text = "fixture complete", annotations = Array.Empty<object>() } } };
            ctx.Response.ContentType = "text/event-stream";
            await SendAsync("response.created",new { type = "response.created", response });
            await SendAsync("response.output_item.added",new { type = "response.output_item.added", output_index = 0, item });
            await SendAsync("response.output_item.done",new { type = "response.output_item.done", output_index = 0, item });
            await SendAsync("response.completed",new { type = "response.completed", response = response with { output = [item] } });
            async Task SendAsync(string kind,object value)
                => await ctx.Response.WriteAsync($"event: {kind}\ndata: {JsonSerializer.Serialize(value)}\n\n");
        }

        public async ValueTask DisposeAsync()
        {
            ReleaseDraftReply();
            await app.DisposeAsync();
        }
    }
}
