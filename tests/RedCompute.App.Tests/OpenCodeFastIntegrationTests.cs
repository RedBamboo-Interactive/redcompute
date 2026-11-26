using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using RedBamboo.AppHost.Discovery;
using RedCompute.App.Api.Endpoints;
using RedCompute.App.Services;
using RedCompute.Core.Capabilities;
using RedCompute.Core.Configuration;
using RedCompute.Core.Providers;
using RedCompute.Core.Jobs;
using RedCompute.Core.Sessions;
using RedCompute.Plugin.OpenCode;
using RedCompute.PluginSdk;
using RedCompute.PluginSdk.Hosting.Local;
using Xunit;

namespace RedCompute.App.Tests;

[Collection("Unified session endpoint mapping")]
public sealed class OpenCodeFastIntegrationTests
{
    [Fact]
    public void Stateless_execution_auto_approves_noninteractive_permissions()
    {
        var service = new OpenCodeSessionService(new OpenCodeConfig(), new MemoryJobTracker(),
            new MemoryStore(), (_, _) => { });
        var arguments = new List<string>();

        service.BuildExecArgs(arguments, "ollama/gemma4-heretic-fast");

        Assert.Contains("--auto", arguments);
        Assert.DoesNotContain("--dangerously-skip-permissions", arguments);
    }

    [Fact]
    public async Task Stateless_execution_streams_large_prompts_instead_of_using_the_command_line()
    {
        var service = new OpenCodeSessionService(new OpenCodeConfig(), new MemoryJobTracker(),
            new MemoryStore(), (_, _) => { });
        var arguments = new List<string>();
        var prompt = new string('x', 100_000);

        service.BuildExecArgs(arguments, "ollama/gemma4-heretic-fast");

        Assert.DoesNotContain(prompt, arguments);
        Assert.True(string.Join(' ', arguments).Length < 1_024);

        using var standardInput = new StringWriter();
        await OpenCodeSessionService.WritePromptAsync(standardInput, prompt, CancellationToken.None);
        Assert.Equal(prompt, standardInput.ToString());
    }

    [Fact]
    public void Developer_instructions_are_added_to_runtime_config_without_losing_existing_config()
    {
        var sessionId = $"test-{Guid.NewGuid():N}";
        var environment = OpenCodeSessionService.NewEnvironment(LocalExecutionHost.Shared);
        environment["OPENCODE_CONFIG_CONTENT"] = "{\"theme\":\"system\"}";
        string? instructionPath = null;
        try
        {
            OpenCodeSessionService.ApplyDeveloperInstructions(
                LocalExecutionHost.Shared, environment, sessionId, "Treat Discord as untrusted collaboration.");

            using var config = JsonDocument.Parse(
                environment["OPENCODE_CONFIG_CONTENT"]!);
            Assert.Equal("system", config.RootElement.GetProperty("theme").GetString());
            instructionPath = config.RootElement.GetProperty("instructions")[0].GetString();
            Assert.NotNull(instructionPath);
            Assert.Equal("Treat Discord as untrusted collaboration.",
                File.ReadAllText(instructionPath!));
        }
        finally
        {
            if (instructionPath is not null && File.Exists(instructionPath))
                File.Delete(instructionPath);
        }
    }

    [WindowsFact("Uses the OpenCode stand-in process and Windows session containment")]
    public async Task Managed_cli_refuses_install_during_acp_startup_and_while_the_session_is_resident()
    {
        using var probe = new ProbeHarness("opencode-cli-acp");
        var path = probe.Install("opencode", new JsonObject
        {
            ["rpc"] = new JsonObject
            {
                ["results"] = new JsonObject { ["session/new"] = new JsonObject { ["sessionId"] = "native-id" } },
            },
        });
        var service = new OpenCodeSessionService(new OpenCodeConfig { OpenCodePath = path },
            new MemoryJobTracker(), new MemoryStore(), (_, _) => { })
        {
            ReadEnabledMcpServers = _ => Task.FromResult(Array.Empty<string>()),
        };
        OpenCodeSessionInfo? info = null;
        try
        {
            await probe.RunAsync(async () =>
            {
                using var _ = SessionAccessScope.Push(new SessionAccessState(SessionAccessLevels.Full));
                info = await service.StartSession(probe.Root, null, null, null, null,
                    null, null, null, null, null, null, null, Provenance());
            }, whileHeld: _ =>
            {
                var blocked = Assert.Throws<CliInstallRejectedException>(() => service.CliVersionManager.StartInstall("latest"));
                Assert.Equal("cli_runs_active", blocked.Code);
                return Task.CompletedTask;
            });

            Assert.NotNull(info);
            Assert.Equal("Idle", info.Status);
            var resident = Assert.Throws<CliInstallRejectedException>(() => service.CliVersionManager.StartInstall("latest"));
            Assert.Equal("sessions_running", resident.Code);
        }
        finally
        {
            if (info is not null) await service.StopSession(info.Id);
        }
    }

    [WindowsFact("Uses the OpenCode stand-in process and Windows session containment")]
    public async Task Confirmed_thinking_is_restored_on_the_same_native_session_after_parking()
    {
        using var probe = new ProbeHarness("opencode-inference-resume");
        static JsonNode Configuration(string effort) => JsonNode.Parse($$"""
            {"sessionId":"native-id","configOptions":[
              {"id":"model","currentValue":"probe/model","options":[{"value":"probe/model"}]},
              {"id":"effort","currentValue":"{{effort}}","options":[{"value":"low"},{"value":"high"}]}
            ]}
            """)!;
        var path = probe.Install("opencode", new JsonObject
        {
            ["rpc"] = new JsonObject
            {
                ["results"] = new JsonObject
                {
                    ["session/new"] = Configuration("low"),
                    ["session/load"] = Configuration("low"),
                    ["session/set_config_option"] = Configuration("high"),
                },
            },
        });
        var store = new MemoryStore();
        var service = new OpenCodeSessionService(new OpenCodeConfig { OpenCodePath = path },
            new MemoryJobTracker(), store, (_, _) => { })
        {
            ReadEnabledMcpServers = _ => Task.FromResult(Array.Empty<string>()),
        };
        OpenCodeSessionInfo? started = null;
        try
        {
            await probe.RunAsync(async () => started = await service.StartSession(probe.Root, null, null, null, null,
                null, null, null, null, null, null, null, Provenance()));
            Assert.NotNull(started);
            Assert.Equal("low", started.Effort);
            Assert.True(await service.TrySetInferenceSettingsAsync(started.Id, null, "high", default));
            Assert.Equal("high", store.FindSession(started.Id)?.Effort);

            Assert.True(await service.TryParkIdleSessionAsync(started.Id, SessionStopReasons.IdleTimeout));
            Assert.Equal("high", service.GetInferenceOptions(started.Id).Effort);
            OpenCodeSessionInfo? resumed = null;
            await probe.RunAsync(async () => resumed = await service.ResumeSession(started.Id, Provenance()));
            Assert.NotNull(resumed);
            Assert.Equal(started.Id, resumed.Id);
            Assert.Equal("native-id", resumed.OpenCodeSessionId);
            Assert.Equal("high", resumed.Effort);
            Assert.Empty(store.GetMessages(started.Id));
        }
        finally
        {
            if (started is not null) await service.StopSession(started.Id);
        }
    }

    [WindowsFact("Uses the OpenCode stand-in process and Windows session containment")]
    public async Task Managed_cli_refuses_install_during_a_oneshot_without_a_stream_key()
    {
        using var probe = new ProbeHarness("opencode-cli-exec");
        var path = probe.Install("opencode", new JsonObject
        {
            ["onEof"] = new JsonArray("{\"type\":\"text\",\"part\":{\"type\":\"text\",\"text\":\"ok\"}}"),
        });
        var service = new OpenCodeSessionService(new OpenCodeConfig { OpenCodePath = path },
            new MemoryJobTracker(), new MemoryStore(), (_, _) => { });

        await probe.RunAsync(
            () => service.ExecuteAsync("Synthetic work", null, probe.Root, null, 60, CancellationToken.None),
            whileHeld: _ =>
            {
                var blocked = Assert.Throws<CliInstallRejectedException>(() => service.CliVersionManager.StartInstall("latest"));
                Assert.Equal("cli_runs_active", blocked.Code);
                return Task.CompletedTask;
            });
    }

    [Fact]
    public async Task Shared_oneshot_merges_non_secret_settings_and_launches_with_the_selected_variant()
    {
        var profile = SharedProfile("synthetic-key", "https://provider.example:443");
        var service = new OpenCodeSessionService(new OpenCodeConfig { OpenCodePath = Environment.ProcessPath },
            new MemoryJobTracker(), new MemoryStore(), (_, _) => { },
            resolveSharedProfile: reference => reference == "test-profile" ? profile : null);
        RedCompute.PluginSdk.Hosting.HostSpawnSpec? launch = null;
        service.LaunchExecProcess = spec =>
        {
            launch = spec;
            return null;
        };

        var inherited = new Dictionary<string, string>
        {
            ["OPENCODE_CONFIG_CONTENT"] = """{"instructions":["existing.md"],"provider":{"legacy":{"name":"Legacy"}}}""",
        };
        await service.ExecuteAsync("Test", null, null, "test-model", 30, CancellationToken.None,
            env: inherited, providerEntity: "test-profile", effort: "none");

        Assert.NotNull(launch);
        Assert.Equal(["run", "--format", "json", "--auto", "-m", "test-profile/test-model", "--variant", "none"],
            launch.ArgumentList);
        var environment = launch.EnvironmentOverlay!;
        var key = Assert.Single(environment, pair => pair.Key.StartsWith(OpenCodeProviderProfile.KeyVariablePrefix, StringComparison.Ordinal));
        Assert.Equal("synthetic-key", key.Value);
        var inline = environment["OPENCODE_CONFIG_CONTENT"];
        Assert.DoesNotContain("synthetic-key", inline);
        using var config = JsonDocument.Parse(inline);
        Assert.Equal("existing.md", config.RootElement.GetProperty("instructions")[0].GetString());
        var providers = config.RootElement.GetProperty("provider");
        Assert.True(providers.TryGetProperty("legacy", out _));
        var options = providers.GetProperty("test-profile").GetProperty("options");
        Assert.Equal($"{{env:{key.Key}}}", options.GetProperty("apiKey").GetString());
        Assert.Equal("https://provider.example/v1", options.GetProperty("baseURL").GetString());
    }

    [Theory]
    [InlineData("https://different.example:443", "test-model", "none", ProviderKeyBindings.MismatchMessage)]
    [InlineData("https://provider.example:443", "other/model", "none", "The selected model is not declared by this OpenCode provider.")]
    [InlineData("https://provider.example:443", "test-model", "invalid", "The selected reasoning effort is not declared for this OpenCode model.")]
    public void Shared_profile_refuses_to_release_the_key_for_an_unbound_endpoint_or_model(
        string destination, string model, string effort, string error)
    {
        var environment = new Dictionary<string, string>();
        var applied = OpenCodeProviderProfile.Apply(LocalExecutionHost.Shared, environment,
            SharedProfile("synthetic-key", destination), model, effort);

        Assert.Equal(error, applied.Error);
        Assert.Empty(environment);
    }

    [Fact]
    public async Task Execute_route_refuses_a_caller_environment_override_for_a_vaulted_profile()
    {
        using var fixture = new SessionRecoveryTests.Fixture("maintenance_restart");
        using var http = new HttpClient(new SharedProfileCatalog());
        var profiles = new ProviderConfigService(fixture.Config, (_, _) => { }, http);
        await profiles.RefreshAsync();
        Assert.True(profiles.HasSharedOpenCodeProfile("test-profile"));

        var jobs = new MemoryJobTracker();
        fixture.Jobs = jobs;
        var service = new OpenCodeSessionService(new OpenCodeConfig(), jobs, new MemoryStore(), (_, _) => { },
            resolveSharedProfile: profiles.ResolveSharedOpenCodeProfile);
        service.LaunchExecProcess = _ => throw new InvalidOperationException("The request must not launch OpenCode.");
        var provider = new OpenCodeProvider("ai-session", service, new OpenCodeSessionStore(), jobs, (_, _) => { });
        var registry = new CapabilityRegistry();
        registry.Register("ai-session",
            new CapabilityDefinition { Slug = "ai-session", DisplayName = "AI Session" },
            new CapabilityConfig(), new Dictionary<string, IBackendProvider> { ["opencode"] = provider }, "opencode");

        await using var app = WebApplication.CreateSlimBuilder().Build();
        UnifiedSessionEndpoints.Map(new EndpointRegistry(app), registry, jobs, (_, _) => { }, fixture.Config,
            providerConfig: profiles, attachmentStore: fixture.Attachments, inputQueue: fixture.Service());
        var route = ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>().Single(endpoint => endpoint.RoutePattern.RawText == "/ai-session/execute");
        var body = Encoding.UTF8.GetBytes("""
            {"provider":"test-profile","prompt":"Synthetic request","env":{"NODE_OPTIONS":"--require caller.js"}}
            """);
        var context = new DefaultHttpContext { RequestServices = app.Services };
        context.SetEndpoint(route);
        context.Request.Method = "POST";
        context.Request.ContentType = "application/json";
        context.Request.ContentLength = body.Length;
        context.Request.Body = new MemoryStream(body);
        context.Response.Body = new MemoryStream();
        SessionRollbackTests.Identify(context, "nova browser");

        await route.RequestDelegate!(context);

        Assert.Equal(422, context.Response.StatusCode);
        context.Response.Body.Position = 0;
        using var response = await JsonDocument.ParseAsync(context.Response.Body);
        Assert.Equal("provider_environment_override", response.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task A_vaulted_profile_cannot_put_its_key_on_Docker_exec_arguments()
    {
        var service = new OpenCodeSessionService(new OpenCodeConfig(), new MemoryJobTracker(),
            new MemoryStore(), (_, _) => { },
            resolveSharedProfile: _ => SharedProfile("synthetic-key", "https://provider.example:443"));
        service.LaunchExecProcess = _ => throw new InvalidOperationException("Docker must not be spawned.");

        var result = await service.ExecuteAsync("Synthetic request", "caller-container", null, null, 30,
            CancellationToken.None, providerEntity: "test-profile");

        Assert.False(result.Success);
        Assert.Equal("Vaulted OpenCode provider profiles require a host-local process.", result.Error);
    }

    private sealed class SharedProfileCatalog : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var json = request.RequestUri!.Query.Contains("type=provider", StringComparison.Ordinal)
                ? """
                    {"items":[{"id":"profile-id","slug":"test-profile","name":"Test",
                        "data":{"backend":"opencode","runtime_binding":"shared","default_model":"test-profile/test-model",
                            "settings":{"opencode":{"id":"test-profile","npm":"@ai-sdk/openai-compatible","models":{"test-model":{}}}}}}]}
                    """
                : """{"items":[]}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            });
        }
    }

    [Theory]
    [InlineData("{env:SYNTHETIC_ONLY}")]
    [InlineData("{file:synthetic-file}")]
    public void Shared_profile_data_cannot_evaluate_process_environment_or_files(string expansion)
    {
        var profile = SharedProfile("synthetic-key", "https://provider.example:443");
        var settings = Assert.IsType<string>(profile.Extra!["opencode"]);
        profile.Extra["opencode"] = settings.Replace("Synthetic", expansion, StringComparison.Ordinal);
        var environment = new Dictionary<string, string>();

        var applied = OpenCodeProviderProfile.Apply(LocalExecutionHost.Shared, environment, profile, null, null);

        Assert.Equal("OpenCode Provider data cannot substitute environment variables or files.", applied.Error);
        Assert.Empty(environment);
    }

    private static ProviderConfig SharedProfile(string key, string destination) => new()
    {
        Type = "OpenCode",
        Endpoint = "https://provider.example/v1",
        Model = "test-profile/test-model",
        KeyBinding = new ProviderKeyBinding(key, destination),
        Extra = new Dictionary<string, object?>
        {
            ["opencode"] = """
                {
                  "id":"test-profile",
                  "npm":"@ai-sdk/openai-compatible",
                  "models":{"test-model":{"name":"Synthetic","variants":{"none":{"reasoningEffort":"none"}}}}
                }
                """,
        },
    };

    [Fact]
    [Trait("Category", "OpenCodeIntegration")]
    public async Task FastModelPersistsCompletedPartsOnceAndResumeDoesNotReplayHistory()
    {
        if (Environment.GetEnvironmentVariable("RUN_OPENCODE_FAST_INTEGRATION") != "1") return;

        var root = Path.Combine(Path.GetTempPath(), $"opencode-fast-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var store = new MemoryStore();
        var jobs = new MemoryJobTracker();
        var logs = new ConcurrentQueue<string>();
        var live = new ConcurrentQueue<OpenCodeStreamEvent>();
        var service = new OpenCodeSessionService(new OpenCodeConfig(), jobs, store,
            (message, _) => logs.Enqueue(message));
        service.StreamEvent += (_, evt) => live.Enqueue(evt);

        OpenCodeSessionInfo? session = null;
        try
        {
            session = await service.StartSession(root, "ollama/gemma4-heretic-fast",
                "integration-user", "Integration", null, null, null, null, null, "fast", null, null,
                Provenance());
            Assert.NotNull(session);

            Assert.True(await service.SendInput(session!.Id,
                [SessionInputPart.TextPart("Reply with exactly FAST-ADAPTER-OK and nothing else. Do not use tools.")],
                messageUid: "integration-user-1"));
            await WaitForIdle(session, logs);

            var firstHistory = store.GetMessages(session.Id);
            var nativeReader = new OpenCodeNativeSessionReader();
            var nativeParts = nativeReader.GetCompletedAssistantParts(session.OpenCodeSessionId!);
            Assert.Single(firstHistory, message => message.Role == "user");
            Assert.True(firstHistory.Any(message => message.Role == "assistant"),
                $"No durable assistant parts. Provider={session.OpenCodeSessionId}. NativeDb={nativeReader.DbPath}. NativeParts={nativeParts.Count}. Live={string.Join(',', live.Select(evt => evt.Type))}. Logs={string.Join(Environment.NewLine, logs.TakeLast(30))}");
            Assert.All(firstHistory.Where(message => message.Role == "assistant"),
                message => Assert.False(string.IsNullOrWhiteSpace(message.ProviderPartId)));
            Assert.True(firstHistory.Count < 20, $"Expected compact history, got {firstHistory.Count} records");
            Assert.Contains(firstHistory, message => message.EventType == "text"
                && message.Content?.Contains("FAST-ADAPTER-OK", StringComparison.Ordinal) == true);
            Assert.Contains(live, evt => evt.IsPartial);

            var countBeforeResume = firstHistory.Count;
            await service.StopSession(session.Id, "integration_restart");
            var resumed = await service.ResumeSession(session.Id, Provenance());
            Assert.NotNull(resumed);
            await Task.Delay(250);
            Assert.Equal(countBeforeResume, store.GetMessages(session.Id).Count);

            Assert.True(await service.SendInput(session.Id,
                [SessionInputPart.TextPart("Reply with exactly FAST-RESUME-OK and nothing else. Do not use tools.")],
                messageUid: "integration-user-2"));
            await WaitForIdle(resumed!, logs);

            var resumedHistory = store.GetMessages(session.Id);
            Assert.Equal(2, resumedHistory.Count(message => message.Role == "user"));
            Assert.True(resumedHistory.Count < 30, $"Expected compact resumed history, got {resumedHistory.Count} records");
            Assert.Contains(resumedHistory, message => message.EventType == "text"
                && message.Content?.Contains("FAST-RESUME-OK", StringComparison.Ordinal) == true);
            Assert.Equal(resumedHistory.Where(message => message.ProviderPartId != null)
                    .Select(message => (message.ProviderPartId, message.EventType)).Distinct().Count(),
                resumedHistory.Count(message => message.ProviderPartId != null));
        }
        finally
        {
            if (session != null) await service.StopSession(session.Id, "integration_complete");
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task WaitForIdle(OpenCodeSessionInfo session, ConcurrentQueue<string> logs)
    {
        var deadline = DateTimeOffset.UtcNow.AddMinutes(3);
        while (session.Status == "Active" && DateTimeOffset.UtcNow < deadline)
            await Task.Delay(50);
        Assert.True(session.Status == "Idle",
            $"Session ended in {session.Status}. Logs: {string.Join(Environment.NewLine, logs.TakeLast(20))}");
    }

    private static JobProvenance Provenance() => new(
        JobProvenance.CurrentSchemaVersion,
        new JobOrigin("redcompute", new JobAppReference("app", "integration", null, "Integration"),
            new JobEntrypoint("test", "opencode-fast", "POST")),
        new JobActor("user", "Integration", Id: "integration-user"),
        new JobBeneficiary("user", "integration-user", "Integration"),
        [new JobContextReference("test", "opencode-fast")],
        new JobTrace(), JobProvenanceAssurance.Verified, DateTimeOffset.UtcNow);

    private sealed class MemoryStore : IOpenCodeSessionStore
    {
        private readonly object _lock = new();
        private readonly Dictionary<string, OpenCodeSessionRecord> _sessions = [];
        private readonly List<OpenCodeMessageRecord> _messages = [];

        public OpenCodeSessionRecord? FindSession(string sessionId)
        {
            lock (_lock) return _sessions.GetValueOrDefault(sessionId);
        }
        public OpenCodeSessionRecord? FindSessionByJobId(Guid jobId)
        {
            lock (_lock) return _sessions.Values.FirstOrDefault(session => session.JobId == jobId);
        }
        public List<OpenCodeSessionRecord> GetActiveSessions() => [];
        public List<OpenCodeSessionRecord> GetRecentSessions(HashSet<string> excludeIds, int limit = 20,
            bool includeDismissed = false) => [];
        public void SaveSession(OpenCodeSessionRecord record)
        {
            lock (_lock) _sessions[record.Id] = record;
        }
        public void DismissSession(string sessionId) { }
        public void AddMessage(OpenCodeMessageRecord message)
        {
            lock (_lock)
            {
                if (message.ProviderPartId != null && _messages.Any(existing =>
                        existing.SessionId == message.SessionId
                        && existing.ProviderPartId == message.ProviderPartId
                        && existing.EventType == message.EventType)) return;
                _messages.Add(message);
            }
        }
        public void AddMessages(List<OpenCodeMessageRecord> messages)
        {
            foreach (var message in messages) AddMessage(message);
        }
        public List<OpenCodeMessageRecord> GetMessages(string sessionId, int limit = 50_000)
        {
            lock (_lock) return _messages.Where(message => message.SessionId == sessionId).TakeLast(limit).ToList();
        }
        public Dictionary<Guid, string> GetSessionStatusesByJobIds(IEnumerable<Guid> jobIds) => [];
    }

    private sealed class MemoryJobTracker : IJobTracker
    {
        private readonly Dictionary<Guid, JobRecord> _jobs = [];
        public JobRecord CreateJob(JobSubmission submission)
        {
            var job = new JobRecord
            {
                CapabilitySlug = submission.CapabilitySlug,
                ProviderName = submission.ProviderName,
                InputJson = submission.InputJson,
                Name = submission.Name,
                Rationale = submission.Rationale,
                CreationProvenance = submission.Provenance,
                UserId = submission.Provenance.OnBehalfOf.Id,
                UserName = submission.Provenance.OnBehalfOf.NameSnapshot,
                Confidential = submission.Confidential,
            };
            _jobs[job.Id] = job;
            return job;
        }
        public void MarkRunning(Guid jobId) => _jobs[jobId].Status = JobStatus.Running;
        public void StartInvocation(Guid jobId, JobProvenance provenance, JobEventKind kind = JobEventKind.Started) =>
            MarkRunning(jobId);
        public void MarkCompleted(Guid jobId, string? outputLocation = null, long? outputSizeBytes = null,
            string? contentType = null, string? resultJson = null, double? costUsd = null) =>
            _jobs[jobId].Status = JobStatus.Completed;
        public void MarkFailed(Guid jobId, string errorMessage, string? errorDetails = null, string? resultJson = null) =>
            _jobs[jobId].Status = JobStatus.Failed;
        public void MarkCancelled(Guid jobId) => _jobs[jobId].Status = JobStatus.Cancelled;
        public void UpdateName(Guid jobId, string name) => _jobs[jobId].Name = name;
        public JobRecord? GetJob(Guid jobId) => _jobs.GetValueOrDefault(jobId);
    }
}
