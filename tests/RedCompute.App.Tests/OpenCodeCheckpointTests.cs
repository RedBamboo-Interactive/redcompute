using System.Diagnostics;
using System.Text.Json;
using RedCompute.App.Services;
using RedCompute.Core.Sessions;
using RedCompute.Plugin.OpenCode;
using RedCompute.PluginSdk;
using Xunit;

namespace RedCompute.App.Tests;

public sealed class OpenCodeCheckpointTests
{
    [Fact]
    public void StatelessJsonPreservesNativePartAndMessageIdentity()
    {
        var events = OpenCodeSessionService.ParseStreamLine("""
            {"type":"text","sessionID":"ses_native","part":{"id":"prt_native","messageID":"msg_native","sessionID":"ses_native","type":"text","text":"READY"}}
            """);

        var evt = Assert.Single(events);
        Assert.Equal("text", evt.Type);
        Assert.Equal("READY", evt.Content);
        Assert.Equal("prt_native", evt.ProviderPartId);
        Assert.Equal("msg_native", evt.MessageId);
    }

    [Fact]
    public void StatelessCheckpointPublishesParentSessionBeforeMessages()
    {
        var trace = new List<string>();
        var previousSession = SuiteMirror.SessionUpserted;
        var previousCompleted = SuiteMirror.CompletedMessagesAdded;
        try
        {
            SuiteMirror.SessionUpserted = session =>
            {
                trace.Add($"session:{session.Id}");
                Assert.Equal("opencode", session.Provider);
                Assert.Equal(Guid.Parse("8625d4c7-77ee-4192-ae2d-2a9c9de68766"), session.JobId);
            };
            SuiteMirror.CompletedMessagesAdded = messages =>
                trace.Add($"messages:{messages.Count}");

            OpenCodeSessionStore.PublishCompletedMessagesWithParent(
                [new OpenCodeMessageRecord
                {
                    SessionId = "8625d4c7-77ee-4192-ae2d-2a9c9de68766",
                    Role = "assistant",
                    EventType = "text",
                    Content = "READY",
                    ProviderPartId = "prt_native",
                    MessageId = "msg_native",
                    MessageUid = "msg_native",
                    Timestamp = DateTimeOffset.UnixEpoch,
                }],
                sessionExists: false);

            Assert.Equal(
                ["session:8625d4c7-77ee-4192-ae2d-2a9c9de68766", "messages:1"],
                trace);
        }
        finally
        {
            SuiteMirror.SessionUpserted = previousSession;
            SuiteMirror.CompletedMessagesAdded = previousCompleted;
        }
    }

    [Fact]
    public void PersistentCheckpointDoesNotRepublishParentSession()
    {
        var sessionPublishes = 0;
        var completedPublishes = 0;
        var previousSession = SuiteMirror.SessionUpserted;
        var previousCompleted = SuiteMirror.CompletedMessagesAdded;
        try
        {
            SuiteMirror.SessionUpserted = _ => sessionPublishes++;
            SuiteMirror.CompletedMessagesAdded = _ => completedPublishes++;

            OpenCodeSessionStore.PublishCompletedMessagesWithParent(
                [new OpenCodeMessageRecord
                {
                    SessionId = "persistent-session",
                    Role = "assistant",
                    EventType = "text",
                    Content = "READY",
                    ProviderPartId = "prt_native",
                    MessageId = "msg_native",
                    MessageUid = "msg_native",
                    Timestamp = DateTimeOffset.UnixEpoch,
                }],
                sessionExists: true);

            Assert.Equal(0, sessionPublishes);
            Assert.Equal(1, completedPublishes);
        }
        finally
        {
            SuiteMirror.SessionUpserted = previousSession;
            SuiteMirror.CompletedMessagesAdded = previousCompleted;
        }
    }

    [Fact]
    public void CheckpointFailureBuffersFollowingChunksAndRecoveryKeepsTheirOrder()
    {
        var native = new NativeReader { Parts = FirstStep() };
        var store = new Store { FailNext = true };
        var service = new OpenCodeSessionService(new OpenCodeConfig(), null!, store, (_, _) => { }, native);
        using var process = new Process(); using var cts = new CancellationTokenSource();
        var session = Session(process, cts);
        var live = new List<OpenCodeStreamEvent>();
        service.StreamEvent += (_, e) => live.Add(e);
        service.HandleLiveChunk(session, "text", "step-", "native-2");
        Assert.True(session.PendingCheckpoint);
        Assert.Single(session.DeferredLiveChunks);
        Assert.DoesNotContain(live, e => e.MessageId == "native-2");
        service.HandleLiveChunk(session, "text", "two", "native-2");
        Assert.False(session.PendingCheckpoint);
        Assert.Empty(session.DeferredLiveChunks);
        Assert.Equal(new[] { "step-", "two" }, live.Where(e => e.MessageId == "native-2").Select(e => e.Content));
        Assert.Equal(3, store.Rows.Count);
    }

    [Fact]
    public void LightweightPayloadKeepsTheOriginalTransportIdentity()
    {
        var evt = new UnifiedStreamEvent { Type = "tool_result", MessageUid = "turn", MessageId = "call",
            Epoch = "epoch", Sequence = 42, Phase = "commentary", RequestId = "request",
            ToolName = "read", Content = "large output", ToolResult = "large output" };
        var lightweight = SessionTranscriptPipeline.WithPayload(evt, new TranscriptPayloadRef { RecordId = 99 });
        Assert.Equal((evt.Epoch, evt.Sequence, evt.MessageUid, evt.MessageId, evt.Phase, evt.RequestId),
            (lightweight.Epoch, lightweight.Sequence, lightweight.MessageUid, lightweight.MessageId, lightweight.Phase, lightweight.RequestId));
        Assert.Null(lightweight.Content); Assert.Null(lightweight.ToolResult);
        Assert.Equal(99, lightweight.PayloadRef!.RecordId);
    }

    [Fact]
    public async Task CompletedPrefixPrecedesNextLiveStepAndFinalNativePartsAreNotDuplicated()
    {
        var native = new NativeReader();
        var store = new Store();
        var service = new OpenCodeSessionService(new OpenCodeConfig(), null!, store, (_, _) => { }, native);
        using var process = new Process();
        using var cts = new CancellationTokenSource();
        var session = Session(process, cts);
        var trace = new List<string>();
        service.StreamEvent += (_, e) => trace.Add($"live:{e.Type}:{e.Content}");
        store.OnCheckpoint = rows => trace.Add($"checkpoint:{rows.Count}");

        service.HandleLiveChunk(session, "text", "step-one", "native-1");
        Update(service, session, """{"sessionUpdate":"tool_call","title":"read_file","toolCallId":"call-1","rawInput":{}}""");
        Update(service, session, """{"sessionUpdate":"tool_call_update","status":"completed","toolCallId":"call-1","content":[{"type":"content","content":{"type":"image","mimeType":"image/png","data":"aW1hZ2U="}}]}""");
        Assert.Empty(store.Rows);
        Assert.DoesNotContain(trace, entry => entry.StartsWith("live:tool_result:"));

        native.Parts = FirstStep();
        service.HandleLiveChunk(session, "text", "step-two", "native-2");
        Assert.Equal(new[] { "text", "tool_use", "tool_result" }, store.Rows.Select(r => r.EventType));
        Assert.True(trace.IndexOf("checkpoint:3") < trace.IndexOf("live:text:step-two"));
        Assert.Contains("aW1hZ2U=", store.Rows[^1].AttachmentsJson);
        Assert.False(session.HasCompletedTerminalMessage);

        native.Parts = [.. native.Parts, Text("final", "native-2", "step-two", terminal: true)];
        await service.ReconcileCompletedTurn(session);
        service.ReconcileAvailableParts(session);
        var count = trace.Count;
        service.HandleLiveChunk(session, "text", "step-two", "native-2");
        Update(service, session, """{"sessionUpdate":"tool_call_update","status":"completed","toolCallId":"call-1"}""");
        Assert.Equal(count, trace.Count);
        Assert.Equal(4, store.Rows.Count);
        Assert.All(store.Rows, r => Assert.Equal("turn-1", r.MessageUid));
    }

    [Fact]
    public async Task EndTurnWaitsForTerminalNativeMessageAfterIntermediateCheckpoint()
    {
        var native = new NativeReader { Parts = FirstStep() };
        var store = new Store();
        var service = new OpenCodeSessionService(new OpenCodeConfig(), null!, store, (_, _) => { }, native);
        using var process = new Process(); using var cts = new CancellationTokenSource();
        var session = Session(process, cts);
        service.ReconcileAvailableParts(session);
        var ending = service.ReconcileCompletedTurn(session);
        Assert.False(ending.IsCompleted);
        native.Parts = [.. native.Parts, Text("final", "native-2", "answer", terminal: true)];
        await ending.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(4, store.Rows.Count);
    }

    [Fact]
    public void FailedCheckpointRetriesNativeIdsWithoutRepeatingMissingLiveSuffix()
    {
        var native = new NativeReader { Parts = FirstStep() };
        var store = new Store { FailNext = true };
        var service = new OpenCodeSessionService(new OpenCodeConfig(), null!, store, (_, _) => { }, native);
        using var process = new Process(); using var cts = new CancellationTokenSource();
        var session = Session(process, cts);
        var live = new List<OpenCodeStreamEvent>();
        service.StreamEvent += (_, e) => live.Add(e);
        Assert.Throws<IOException>(() => service.ReconcileAvailableParts(session));
        Assert.Empty(session.KnownNativePartIds);
        service.ReconcileAvailableParts(session);
        Assert.Single(live, e => e.Type == "text");
        Assert.Single(live, e => e.Type == "tool_use");
        Assert.Equal(3, store.Rows.Count);
        Assert.Equal(2, session.KnownNativePartIds.Count);
    }

    [Fact]
    public async Task ReadersCannotObserveHalfWrittenPrefixAndOtherSessionsRemainReadable()
    {
        var gate = new TranscriptCheckpointCoordinator();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var rows = new List<string>();
        var writer = Task.Run(() => gate.Write("session-1", () =>
        {
            rows.Add("text"); entered.SetResult();
            Assert.True(release.Wait(TimeSpan.FromSeconds(5)));
            rows.Add("tool_use"); rows.Add("tool_result");
        }));
        await entered.Task;
        try
        {
            var read = gate.ReadAsync("session-1", () => Task.FromResult(rows.ToArray()));
            Assert.False(read.IsCompleted);
            Assert.Equal("other", await gate.ReadAsync("session-2", () => Task.FromResult("other")));
            release.Set(); await writer;
            Assert.Equal(new[] { "text", "tool_use", "tool_result" }, await read);
        }
        finally { release.Set(); }
    }

    [Fact]
    public async Task FailedPrefixBlocksSnapshotsAndRetriesWithSameStampsBeforeNewerParts()
    {
        var gate = new TranscriptCheckpointCoordinator(); var sequences = new Sequences();
        var persisted = new List<(string Type, long Sequence)>();
        var live = new List<UnifiedStreamEvent>(); var fail = true;
        var publisher = new CompletedTranscriptPublisher(gate, sequences, (message, stamp) =>
        {
            if (message.EventType == "tool_result" && fail) { fail = false; throw new IOException("interrupted append"); }
            persisted.Add((message.EventType, stamp.Sequence));
            return message.EventType == "tool_result" ? new TranscriptPayloadRef { RecordId = 12 } : null;
        }, (_, _, e) => live.Add(e));
        var batch = FirstStep().SelectMany(Records).ToArray();
        Assert.Throws<IOException>(() => publisher.Publish(batch));
        Assert.Empty(live);
        await Assert.ThrowsAsync<InvalidOperationException>(() => gate.ReadAsync("session-1", () => Task.FromResult(0)));
        publisher.Publish([Snapshot(Text("final", "native-2", "answer", true), "text")]);
        Assert.Equal(new[] { ("text", 1L), ("tool_use", 2L), ("tool_result", 3L), ("text", 4L) }, persisted);
        Assert.Single(live); Assert.Equal(3, live[0].Sequence); Assert.Equal("epoch", live[0].Epoch);
        Assert.Equal(12, live[0].PayloadRef!.RecordId); Assert.Null(live[0].ToolResult);
        Assert.Equal(4, await gate.ReadAsync("session-1", () => Task.FromResult(persisted.Count)));
    }

    [Fact]
    public void CheckpointResultIsPublishedOnlyAfterFullBatchAndDuplicateDeliveryIsHarmless()
    {
        var gate = new TranscriptCheckpointCoordinator(); var sequences = new Sequences();
        var persisted = new List<AiMessageSnapshot>(); var events = new List<UnifiedStreamEvent>();
        var publisher = new CompletedTranscriptPublisher(gate, sequences, (m, _) =>
        { persisted.Add(m); return m.EventType == "tool_result" ? new TranscriptPayloadRef { RecordId = 3 } : null; },
        (_, _, e) => { Assert.Equal(4, persisted.Count); events.Add(e); });
        var batch = FirstStep().SelectMany(Records).Append(Snapshot(Text("final", "native-2", "answer", true), "text")).ToArray();
        publisher.Publish(batch); publisher.Publish(batch);
        Assert.Equal(4, persisted.Count); Assert.Single(events); Assert.Equal(3, events[0].Sequence);
    }

    private static OpenCodeSessionService.ManagedSession Session(Process process, CancellationTokenSource cts) =>
        new(new OpenCodeSessionInfo { Id = "session-1", Status = "Active", ProjectName = "fixture", ProjectPath = Path.GetTempPath() }, process, cts)
        { AcpSessionId = "native-session", CurrentAssistantUid = "turn-1" };
    private static void Update(OpenCodeSessionService service, OpenCodeSessionService.ManagedSession session, string json)
    { using var doc = JsonDocument.Parse("{\"update\":" + json + "}"); service.HandleSessionUpdate(session, doc.RootElement); }
    private static OpenCodeNativePart Text(string id, string message, string text, bool terminal = false) =>
        new(id, message, "text", text, null, null, null, null, null, false, DateTimeOffset.UnixEpoch, terminal);
    private static OpenCodeNativePart[] FirstStep() => [Text("text-1", "native-1", "step-one"),
        new("tool-1", "native-1", "tool", null, "read_file", "call-1", "{}", "contents", null, false, DateTimeOffset.UnixEpoch)];
    private static IEnumerable<AiMessageSnapshot> Records(OpenCodeNativePart part) => part.Type == "tool"
        ? [Snapshot(part, "tool_use"), Snapshot(part, "tool_result")] : [Snapshot(part, "text")];
    private static AiMessageSnapshot Snapshot(OpenCodeNativePart p, string type) => new()
    { Provider = "opencode", SessionId = "session-1", Role = "assistant", EventType = type,
        MessageUid = "turn-1", ProviderPartId = p.Id, MessageId = p.CallId ?? p.MessageId,
        ToolName = p.ToolName, Content = p.Text, ToolResult = type == "tool_result" ? p.ToolResult : null };
    private sealed class Sequences : ITranscriptSequenceStore
    { private long _value; public TranscriptStamp Next(string provider, string sessionId) => new("epoch", ++_value); }
    private sealed class NativeReader : IOpenCodeNativeSessionReader
    {
        public IReadOnlyList<OpenCodeNativePart> Parts = [];
        public OpenCodeNativeMessageVisibility GetMessageVisibility(string sessionId, string messageId) => OpenCodeNativeMessageVisibility.Visible;
        public IReadOnlyList<OpenCodeNativePart> GetCompletedAssistantParts(string sessionId) => Parts;
    }
    private sealed class Store : IOpenCodeSessionStore
    {
        public List<OpenCodeMessageRecord> Rows { get; } = [];
        public Action<List<OpenCodeMessageRecord>>? OnCheckpoint;
        public bool FailNext;
        public void AddMessages(List<OpenCodeMessageRecord> messages)
        { foreach (var m in messages) AddMessage(m); if (FailNext) { FailNext = false; throw new IOException("mirror failed"); } OnCheckpoint?.Invoke(messages); }
        public void AddMessage(OpenCodeMessageRecord m)
        { if (!Rows.Any(r => r.ProviderPartId == m.ProviderPartId && r.EventType == m.EventType)) Rows.Add(m); }
        public List<OpenCodeMessageRecord> GetMessages(string sessionId, int limit = 50_000) => Rows;
        public OpenCodeSessionRecord? FindSession(string sessionId) => null;
        public OpenCodeSessionRecord? FindSessionByJobId(Guid jobId) => null;
        public List<OpenCodeSessionRecord> GetActiveSessions() => [];
        public List<OpenCodeSessionRecord> GetRecentSessions(HashSet<string> excludeIds, int limit = 20, bool includeDismissed = false) => [];
        public void SaveSession(OpenCodeSessionRecord record) { }
        public void DismissSession(string sessionId) { }
        public Dictionary<Guid, string> GetSessionStatusesByJobIds(IEnumerable<Guid> jobIds) => [];
    }
}
