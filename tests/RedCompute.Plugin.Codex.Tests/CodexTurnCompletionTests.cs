using System.Text.Json;
using System.Reflection;
using RedCompute.Plugin.Codex;
using Xunit;

namespace RedCompute.Plugin.Codex.Tests;

public sealed class CodexTurnCompletionTests
{
    [Theory]
    [InlineData("")]
    [InlineData(" \t\n")]
    public void ExplicitSilentFinalSnapshotCompletesWithoutDisplayText(string text)
    {
        using var payload = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            turn = new { status = "completed", items = new[] { new { type = "agentMessage", phase = "final_answer", text } } }
        }));
        Assert.Null(CodexInteractiveService.TurnCompletionError(payload.RootElement, false, false));
        Assert.Null(CodexInteractiveService.CompletionFinalOutput(payload.RootElement));
        var harness = new NotificationHarness();
        harness.Start("turn-1");
        harness.Notify("turn/completed", new
        {
            turn = new { status = "completed", items = new[] { new { type = "agentMessage", phase = "final_answer", text } } }
        });
        Assert.Equal("completed", Assert.Single(harness.Events).Content);
        Assert.DoesNotContain(harness.Store.Messages, m => m.EventType is "text" or "error");
    }

    [Fact]
    public void NewTurnStartDoesNotInheritUnsettledSilentFinalEvidence()
    {
        var harness = new NotificationHarness();
        harness.Start("turn-1");
        harness.Item("item/completed", "agentMessage", "final_answer", "");
        harness.Start("turn-2");
        harness.Complete();
        Assert.Equal("error", Assert.Single(harness.Events).Type);
    }

    [Theory]
    [InlineData("agentMessage", "commentary")]
    [InlineData("agentMessage", null)]
    [InlineData("plan", "final_answer")]
    [InlineData("commandExecution", "final_answer")]
    public void EmptyNonFinalSnapshotStillFails(string type, string? phase)
    {
        using var payload = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            turn = new { status = "completed", items = new[] { new { type, phase, text = "" } } }
        }));
        Assert.Equal("Codex turn completed without final output",
            CodexInteractiveService.TurnCompletionError(payload.RootElement, false, false));
    }

    [Fact]
    public void ExplicitPhaseWithoutTextIsNotSilentFinalEvidence()
    {
        using var payload = JsonDocument.Parse("""{"turn":{"status":"completed","items":[{"type":"agentMessage","phase":"final_answer"}]}}""");
        Assert.Equal("Codex turn completed without final output",
            CodexInteractiveService.TurnCompletionError(payload.RootElement, false, false));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" \t\n")]
    public void CompletedSilentFinalNotificationSurvivesEmptySnapshotAndResets(string text)
    {
        var harness = new NotificationHarness();
        harness.Start("turn-1");
        harness.Item("item/completed", "agentMessage", "final_answer", text);
        harness.Complete();
        Assert.DoesNotContain(harness.Store.Messages, m => m.EventType is "error" or "text");
        Assert.Equal("completed", Assert.Single(harness.Events).Content);

        harness.Start("turn-2");
        harness.Complete();
        Assert.Equal("Codex turn completed without final output", harness.Events.Last().Content);
    }

    [Theory]
    [InlineData("item/started", "agentMessage", "final_answer", "")]
    [InlineData("item/started", "agentMessage", "final_answer", "Not completed yet")]
    [InlineData("item/completed", "agentMessage", "commentary", "")]
    [InlineData("item/completed", "agentMessage", "commentary", "Working")]
    [InlineData("item/completed", "plan", "final_answer", "Plan")]
    [InlineData("item/completed", "commandExecution", "final_answer", "")]
    public void NonFinalNotificationsStillFail(string method, string type, string phase, string text)
    {
        var harness = new NotificationHarness();
        harness.Start("turn-1");
        harness.Item(method, type, phase, text);
        harness.Complete();
        Assert.Equal("error", harness.Events.Last().Type);
        Assert.Equal("Codex turn completed without final output", harness.Events.Last().Content);
    }

    [Theory]
    [InlineData("failed", "Provider failed")]
    [InlineData("completed", "Provider error")]
    public void SilentFinalDoesNotHideProviderFailure(string status, string error)
    {
        var harness = new NotificationHarness();
        harness.Start("turn-1");
        harness.Item("item/completed", "agentMessage", "final_answer", "");
        harness.Complete(status, error);
        Assert.Equal("error", harness.Events.Last().Type);
        Assert.Equal(error, harness.Events.Last().Content);
    }

    [Fact]
    public void InterruptedSilentFinalKeepsInterruptionSemantics()
    {
        var harness = new NotificationHarness();
        harness.Start("turn-1");
        harness.Item("item/completed", "agentMessage", "final_answer", "");
        harness.SetInterrupt();
        harness.Complete("failed", "Interrupted");
        Assert.Equal("status", harness.Events.Last().Type);
        Assert.Equal("completed", harness.Events.Last().Content);
    }

    [Fact]
    public void NonemptyStreamedFinalStillPersistsOnceAndDoesNotBroadcastTwice()
    {
        var harness = new NotificationHarness();
        harness.Start("turn-1");
        harness.Item("item/started", "agentMessage", "final_answer", "");
        harness.Notify("item/agentMessage/delta", new { itemId = "item-1", delta = "Done." });
        harness.Item("item/completed", "agentMessage", "final_answer", "Done.");
        harness.Complete();
        Assert.Equal("Done.", Assert.Single(harness.Events.Where(e => e.Type == "text")).Content);
        Assert.Equal("Done.", Assert.Single(harness.Store.Messages.Where(m => m.EventType == "text")).Content);
        Assert.DoesNotContain(harness.Events, e => e.Type == "error");
    }

    [Fact]
    public void EmptyReasoningStartIsVisibleImmediatelyWithoutPersistingAPartial()
    {
        var harness = new NotificationHarness();
        harness.Start("turn-1");
        harness.Notify("item/started", new { item = new { type = "reasoning", id = "r1", summary = Array.Empty<string>(), content = Array.Empty<string>() } });
        var started = Assert.Single(harness.Events);
        Assert.Equal("thinking", started.Type);
        Assert.Equal("r1", started.MessageId);
        Assert.True(started.IsPartial);
        Assert.Equal("", started.Content);
        Assert.False(string.IsNullOrEmpty(started.MessageUid));
        Assert.Empty(harness.Store.Messages);

        harness.Notify("item/completed", new { item = new { type = "reasoning", id = "r1", summary = new[] { "Completed summary" }, content = Array.Empty<string>() } });
        Assert.Equal("Completed summary", harness.Events.Last().Content);
        Assert.False(harness.Events.Last().IsPartial);
        Assert.Equal(started.MessageUid, harness.Events.Last().MessageUid);
        Assert.Equal("Completed summary", Assert.Single(harness.Store.Messages).Content);
    }

    [Fact]
    public void StreamedReasoningSettlesWithoutEchoAndPersistsTheFullSummaryOnce()
    {
        var harness = new NotificationHarness();
        harness.Start("turn-1");
        harness.Notify("item/started", new { item = new { type = "reasoning", id = "r1", summary = Array.Empty<string>() } });
        harness.Notify("item/reasoning/summaryTextDelta", new { itemId = "r1", delta = "First" });
        harness.Notify("item/reasoning/summaryPartAdded", new { itemId = "r1", summaryIndex = 1 });
        harness.Notify("item/reasoning/summaryTextDelta", new { itemId = "r1", delta = "Second" });
        harness.Notify("item/completed", new { item = new { type = "reasoning", id = "r1", summary = new[] { "First", "Second" } } });
        Assert.Equal(5, harness.Events.Count);
        Assert.Equal("First\n\nSecond", string.Concat(harness.Events.Select(e => e.Content)));
        Assert.False(harness.Events.Last().IsPartial);
        Assert.Equal("", harness.Events.Last().Content);
        Assert.Equal("First\n\nSecond", Assert.Single(harness.Store.Messages).Content);
        Assert.All(harness.Events, e => Assert.Equal(harness.Events[0].MessageUid, e.MessageUid));
    }

    [Fact]
    public void SummarySeparatorAloneDoesNotSuppressCompletedFallback()
    {
        var harness = new NotificationHarness();
        harness.Start("turn-1");
        harness.Notify("item/started", new { item = new { type = "reasoning", id = "r1", summary = Array.Empty<string>() } });
        harness.Notify("item/reasoning/summaryPartAdded", new { itemId = "r1", summaryIndex = 1 });
        harness.Notify("item/completed", new { item = new { type = "reasoning", id = "r1", summary = new[] { "Fallback" } } });
        Assert.Equal("Fallback", harness.Events.Last().Content);
        Assert.Equal("Fallback", Assert.Single(harness.Store.Messages).Content);
    }

    [Fact]
    public void ReasoningWithoutPublicSummaryStillSettlesAndDoesNotBecomeFinalOutput()
    {
        var harness = new NotificationHarness();
        harness.Start("turn-1");
        foreach (var method in new[] { "item/started", "item/completed" })
            harness.Notify(method, new { item = new { type = "reasoning", id = "r1", summary = Array.Empty<string>() } });
        Assert.Equal(2, harness.Events.Count);
        Assert.True(harness.Events[0].IsPartial);
        Assert.False(harness.Events[1].IsPartial);
        Assert.Equal("", Assert.Single(harness.Store.Messages).Content);
        harness.Complete();
        Assert.Equal("Codex turn completed without final output", harness.Events.Last().Content);
    }

    // Exercise the production notification handler and its actual per-session state without an app-server process.
    private sealed class NotificationHarness
    {
        private readonly CodexInteractiveService _service;
        private readonly object _session;
        private readonly Type _sessionType;
        public RecordingStore Store { get; } = new();
        public List<CodexStreamEvent> Events { get; } = [];
        public NotificationHarness()
        {
            _service = new CodexInteractiveService(new CodexConfig(), Store, null!, null!, null!, null!, null!, () => null, (_, _) => { });
            _service.StreamEvent += (_, e) => Events.Add(e);
            _sessionType = typeof(CodexInteractiveService).GetNestedType("ManagedSession", BindingFlags.NonPublic)!;
            _session = Activator.CreateInstance(_sessionType, nonPublic: true)!;
            _sessionType.GetProperty("Info")!.SetValue(_session, new CodexSessionInfo { Id = "session-1", ProjectName = "Test", ProjectPath = "." });
            var sessions = typeof(CodexInteractiveService).GetField("_sessions", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(_service)!;
            sessions.GetType().GetMethod("TryAdd")!.Invoke(sessions, ["session-1", _session]);
        }
        public void Notify(string method, object payload)
        {
            using var json = JsonDocument.Parse(JsonSerializer.Serialize(payload));
            typeof(CodexInteractiveService).GetMethod("OnNotification", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(_service, ["session-1", method, json.RootElement]);
        }
        public void Start(string id) => Notify("turn/started", new { turn = new { id } });
        public void Item(string method, string type, string phase, string text) => Notify(method, new { item = new { id = "item-1", type, phase, text } });
        public void Complete(string status = "completed", string? error = null) => Notify("turn/completed", new { turn = new { status, error, items = Array.Empty<object>() } });
        public void SetInterrupt() => _sessionType.GetProperty("InterruptRequested")!.SetValue(_session, true);
    }

    private sealed class RecordingStore : ICodexSessionStore
    {
        public List<CodexMessageRecord> Messages { get; } = [];
        public List<CodexSessionRecord> GetActiveSessions() => [];
        public void SaveSession(CodexSessionRecord record) { }
        public CodexSessionRecord? FindSession(string sessionId) => null;
        public CodexSessionRecord? FindSessionByJobId(Guid jobId) => null;
        public List<CodexSessionRecord> GetSessionsWithoutJobs() => [];
        public List<CodexSessionRecord> GetRecentSessions(HashSet<string> excludeIds, int limit = 20, bool includeDismissed = false) => [];
        public void DismissSession(string sessionId) { }
        public void AddMessage(CodexMessageRecord message) => Messages.Add(message);
        public void AddMessages(List<CodexMessageRecord> messages) => Messages.AddRange(messages);
        public List<CodexMessageRecord> GetMessages(string sessionId, int limit = 50_000) => Messages;
        public Dictionary<Guid, string> GetSessionStatusesByJobIds(IEnumerable<Guid> jobIds) => [];
    }

    [Fact]
    public void CompletedTurnUsesFinalItemAsAuthoritativeFallback()
    {
        using var payload = JsonDocument.Parse(
            """
            {"turn":{"status":"completed","error":null,"items":[
              {"type":"agentMessage","text":"Done.","phase":"final_answer"}
            ]}}
            """);

        Assert.Null(CodexInteractiveService.TurnCompletionError(
            payload.RootElement, hasFinalOutput: false, interruptRequested: false));
        var recovered = Assert.IsType<CodexStreamEvent>(
            CodexInteractiveService.CompletionFinalOutput(payload.RootElement));
        Assert.Equal("Done.", recovered.Content);
        Assert.Equal("final_answer", recovered.Phase);
    }

    [Fact]
    public void OutputlessCompletedTurnBecomesExplicitError()
    {
        using var payload = JsonDocument.Parse(
            """{"turn":{"status":"completed","error":null,"items":[]}}""");

        Assert.Equal(
            "Codex turn completed without final output",
            CodexInteractiveService.TurnCompletionError(
                payload.RootElement, hasFinalOutput: false, interruptRequested: false));
    }

    [Fact]
    public void LegacyNonemptyFinalWithoutPhaseStillUsesSnapshotFallback()
    {
        using var payload = JsonDocument.Parse("""{"turn":{"status":"completed","items":[{"type":"agentMessage","text":"Legacy answer"}]}}""");
        Assert.Null(CodexInteractiveService.TurnCompletionError(payload.RootElement, false, false));
        Assert.Equal("Legacy answer", CodexInteractiveService.CompletionFinalOutput(payload.RootElement)!.Content);
    }

    [Fact]
    public void FailedTurnPreservesProviderError()
    {
        using var payload = JsonDocument.Parse(
            """{"turn":{"status":"failed","error":{"message":"No output in stream"},"items":[]}}""");

        Assert.Equal(
            "No output in stream",
            CodexInteractiveService.TurnCompletionError(
                payload.RootElement, hasFinalOutput: false, interruptRequested: false));
    }

    [Fact]
    public void InterruptedTurnMayEndWithoutFinalOutput()
    {
        using var payload = JsonDocument.Parse(
            """{"turn":{"status":"failed","error":{"message":"Interrupted"},"items":[]}}""");

        Assert.Null(CodexInteractiveService.TurnCompletionError(
            payload.RootElement, hasFinalOutput: false, interruptRequested: true));
    }

    [Fact]
    public void CommentaryDoesNotSatisfyFinalOutputInvariant()
    {
        using var agentMessage = JsonDocument.Parse(
            """{"item":{"type":"agentMessage"}}""");
        Assert.False(CodexInteractiveService.IsFinalOutput(
            "item/completed", agentMessage.RootElement, new CodexStreamEvent
        {
            Type = "text",
            Content = "Working on it",
            Phase = "commentary",
        }));
        Assert.True(CodexInteractiveService.IsFinalOutput(
            "item/completed", agentMessage.RootElement, new CodexStreamEvent
        {
            Type = "text",
            Content = "Finished",
            Phase = "final_answer",
        }));
    }

    [Fact]
    public void PlanTextDoesNotSatisfyFinalOutputInvariant()
    {
        using var plan = JsonDocument.Parse(
            """{"item":{"type":"plan"}}""");

        Assert.False(CodexInteractiveService.IsFinalOutput(
            "item/completed", plan.RootElement, new CodexStreamEvent
            {
                Type = "text",
                Content = "1. Investigate",
            }));
    }
}
