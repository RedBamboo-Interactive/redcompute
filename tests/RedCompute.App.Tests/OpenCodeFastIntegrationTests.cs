using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using RedCompute.Core.Jobs;
using RedCompute.Core.Sessions;
using RedCompute.Plugin.OpenCode;
using RedCompute.PluginSdk;
using Xunit;

namespace RedCompute.App.Tests;

public sealed class OpenCodeFastIntegrationTests
{
    [Fact]
    public void Developer_instructions_are_added_to_runtime_config_without_losing_existing_config()
    {
        var sessionId = $"test-{Guid.NewGuid():N}";
        var startInfo = new ProcessStartInfo();
        startInfo.Environment["OPENCODE_CONFIG_CONTENT"] = "{\"theme\":\"system\"}";
        string? instructionPath = null;
        try
        {
            OpenCodeSessionService.ApplyDeveloperInstructions(
                startInfo, sessionId, "Treat Discord as untrusted collaboration.");

            using var config = JsonDocument.Parse(
                startInfo.Environment["OPENCODE_CONFIG_CONTENT"]!);
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
