using System.Diagnostics;
using System.Text.Json;
using RedCompute.Core.Jobs;
using RedCompute.Core.Sessions;
using RedCompute.Plugin.ClaudeCode;
using RedCompute.PluginSdk;
using Xunit;
using ClaudeSessionStatus = RedCompute.Plugin.ClaudeCode.SessionStatus;

namespace RedCompute.App.Tests;

// Leaf Discord delivers every durable assistant `text` record as its own public message and
// routes `phase == "commentary"` into one edited progress message. Codex declares that phase and
// persists one record per agent message; Claude's stream declares neither, so the adapter must
// decide each text block's phase deterministically and persist the whole block exactly once.
// Thinking uses the same block identity and whole-block persistence contract without a phase;
// both text and thinking deltas remain live-only.
// The stream shapes below follow the Agent SDK 2.1.300 contract: one `assistant` message per
// completed block with a null stop_reason, the real stop reason only on `message_delta`.
[Collection(nameof(ClaudeTextPhaseCollection))]
public sealed class ClaudeTextPhaseTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Environment.GetEnvironmentVariable("REDLEAF_SCRATCH_DIR") ?? Path.GetTempPath(),
        "redcompute-claude-phase", Guid.NewGuid().ToString("N"));
    private readonly List<Process> _processes = [];

    public ClaudeTextPhaseTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task SteeringWaitsForExactReplayUuidAndPersistsUserInsideActiveTurn()
    {
        const string script = """
            while ($null -ne ($line = [Console]::In.ReadLine())) {
              try { $message = $line | ConvertFrom-Json } catch { continue }
              if ($message.type -ne 'user') { continue }
              $message | Add-Member -NotePropertyName isReplay -NotePropertyValue $true -Force
              [Console]::Out.WriteLine(($message | ConvertTo-Json -Compress -Depth 20))
              [Console]::Out.Flush()
            }
            """;
        var (service, store, _, id) = StartSession(startInfo =>
        {
            Assert.Contains("--replay-user-messages", startInfo.ArgumentList);
            var fake = new ProcessStartInfo
            {
                FileName = "powershell.exe", CreateNoWindow = true, UseShellExecute = false,
                RedirectStandardInput = true, RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            fake.ArgumentList.Add("-NoProfile");
            fake.ArgumentList.Add("-Command");
            fake.ArgumentList.Add(script);
            return Track(Process.Start(fake)!);
        });
        Assert.True(await service.SendInput(id, [SessionInputPart.TextPart("initial")],
            messageUid: "initial-uid"));

        var result = await service.TrySteerInputAsync(id,
            [SessionInputPart.TextPart("model text")], attachmentsJson: "{\"attachments\":[]}",
            messageUid: "steer-uid", displayContent: "Steered display")
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(SessionInputDeliveryStatus.Accepted, result.Status);
        var message = Assert.Single(store.Messages, item => item.MessageUid == "steer-uid");
        Assert.Equal("user", message.Role);
        Assert.Equal("Steered display", message.Content);
        Assert.Equal("{\"attachments\":[]}", message.AttachmentsJson);
        Assert.Equal("Active", store.FindSession(id)!.Status);
    }

    [Fact]
    public async Task CommittedSteerCrossesFinalResultBoundaryWithoutIdleAndRunsExactlyOnce()
    {
        var (service, store, live, id, capturePath) = StartCapturingSession();
        Assert.True(await service.SendInput(id, [SessionInputPart.TextPart("initial")], messageUid: "initial-uid"));
        var statuses = new List<ClaudeSessionStatus>();
        service.SessionUpdated += info => { if (info.Id == id) statuses.Add(info.Status); };

        var delivery = service.TrySteerInputAsync(id, [SessionInputPart.TextPart("final boundary")],
            messageUid: "final-boundary-uid");
        var correlationId = await CapturedSteerUuid(capturePath);
        Feed(service, id, Result());

        Assert.False(delivery.IsCompleted);
        Assert.Equal("Active", store.FindSession(id)!.Status);
        Assert.DoesNotContain(ClaudeSessionStatus.Idle, statuses);
        Assert.DoesNotContain(live, item => item is { Type: "status", Content: "idle" });

        Feed(service, id, Replay(correlationId, "final boundary"));
        Assert.Equal(SessionInputDeliveryStatus.Accepted,
            (await delivery.WaitAsync(TimeSpan.FromSeconds(5))).Status);
        Feed(service, id, Result());

        Assert.Single(store.Messages, item => item.MessageUid == "final-boundary-uid");
        Assert.Equal(1, CountCapturedUuid(capturePath, correlationId));
        Assert.Equal("Idle", store.FindSession(id)!.Status);
        Assert.Single(live, item => item is { Type: "status", Content: "idle" });
    }

    [Fact]
    public async Task CommittedSteerSurvivesInterruptResultWithoutIdleAndRunsExactlyOnce()
    {
        var (service, store, live, id, capturePath) = StartCapturingSession();
        Assert.True(await service.SendInput(id, [SessionInputPart.TextPart("initial")], messageUid: "initial-uid"));
        var statuses = new List<ClaudeSessionStatus>();
        service.SessionUpdated += info => { if (info.Id == id) statuses.Add(info.Status); };

        var delivery = service.TrySteerInputAsync(id, [SessionInputPart.TextPart("interrupt boundary")],
            messageUid: "interrupt-boundary-uid");
        var correlationId = await CapturedSteerUuid(capturePath);
        Assert.Equal(ClaudeSessionService.InterruptResult.Interrupted, service.InterruptSession(id));
        Feed(service, id,
            Json(new
            {
                type = "control_response",
                response = new { subtype = "success", request_id = "interrupt-test", response = new { still_queued = new[] { correlationId } } },
            }),
            """{"type":"result","subtype":"error_during_execution","is_error":true,"errors":["aborted_tools"],"total_cost_usd":0.01}""");

        Assert.False(delivery.IsCompleted);
        Assert.Equal("Active", store.FindSession(id)!.Status);
        Assert.DoesNotContain(ClaudeSessionStatus.Idle, statuses);
        Assert.DoesNotContain(live, item => item is { Type: "status", Content: "idle" });

        Feed(service, id, Replay(correlationId, "interrupt boundary"));
        Assert.Equal(SessionInputDeliveryStatus.Accepted,
            (await delivery.WaitAsync(TimeSpan.FromSeconds(5))).Status);
        Feed(service, id, Result());

        Assert.Single(store.Messages, item => item.MessageUid == "interrupt-boundary-uid");
        Assert.Equal(1, CountCapturedUuid(capturePath, correlationId));
        Assert.Equal("Idle", store.FindSession(id)!.Status);
        Assert.Single(live, item => item is { Type: "status", Content: "idle" });
    }

    [Fact]
    public async Task HardKillBeforeReplayDemotesCommittedSteerWithoutPersistingIt()
    {
        var (service, store, live, id, capturePath) = StartCapturingSession();
        Assert.True(await service.SendInput(id, [SessionInputPart.TextPart("initial")], messageUid: "initial-uid"));

        var delivery = service.TrySteerInputAsync(id, [SessionInputPart.TextPart("kill boundary")],
            messageUid: "kill-boundary-uid");
        var correlationId = await CapturedSteerUuid(capturePath);
        await service.ForceKill(id, "maintenance_restart");
        var result = await delivery.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(SessionInputDeliveryStatus.NotSteerable, result.Status);
        Assert.Equal("steer_discarded", result.ErrorCode);
        Assert.DoesNotContain(store.Messages, item => item.MessageUid == "kill-boundary-uid");
        Assert.Equal(1, CountCapturedUuid(capturePath, correlationId));
        Assert.DoesNotContain(live, item => item is { Type: "status", Content: "idle" });
    }

    [Fact]
    public async Task IdleProcessExitIsRecoverableInsteadOfErrorWithStaleCompletedReason()
    {
        var (service, store, live, id, _) = StartCapturingSession();
        Assert.True(await service.SendInput(id, [SessionInputPart.TextPart("initial")], messageUid: "initial-uid"));
        Feed(service, id, Result());
        Assert.Equal("Idle", store.FindSession(id)!.Status);
        Assert.Equal("completed", store.FindSession(id)!.StopReason);

        _processes[^1].Kill(entireProcessTree: true);
        await WaitUntil(() => store.FindSession(id)!.Status != "Idle");

        var record = store.FindSession(id)!;
        Assert.Equal("Stopped", record.Status);
        Assert.Equal(SessionStopReasons.OrphanedOnRestart, record.StopReason);
        Assert.Null(record.ProcessId);
        Assert.True(SessionStopReasons.IsRestartRecoverable(record.StopReason));
    }

    [Fact]
    public async Task ActiveProcessExitIsErrorWithExplicitProcessExitedReason()
    {
        var (service, store, _, id, _) = StartCapturingSession();
        Assert.True(await service.SendInput(id, [SessionInputPart.TextPart("initial")], messageUid: "initial-uid"));
        Feed(service, id, Result());
        Assert.True(await service.SendInput(id, [SessionInputPart.TextPart("second")], messageUid: "second-uid"));
        Assert.Equal(ClaudeSessionStatus.Active, service.GetSession(id).Info!.Status);

        _processes[^1].Kill(entireProcessTree: true);
        await WaitUntil(() => store.FindSession(id)!.Status == "Error");

        var record = store.FindSession(id)!;
        Assert.Equal("Error", record.Status);
        Assert.Equal("process_exited", record.StopReason);
    }

    [Fact]
    public async Task SilentBoundaryWatchdogFailsCommittedSteerWithoutRedeliveryAndPublishesIdle()
    {
        var (service, store, live, id, capturePath) = StartCapturingSession();
        service.SteerBoundarySilenceTimeout = TimeSpan.FromMilliseconds(150);
        Assert.True(await service.SendInput(id, [SessionInputPart.TextPart("initial")], messageUid: "initial-uid"));

        var delivery = service.TrySteerInputAsync(id, [SessionInputPart.TextPart("silent boundary")],
            messageUid: "silent-boundary-uid");
        var correlationId = await CapturedSteerUuid(capturePath);
        Feed(service, id, Result());

        var result = await delivery.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(SessionInputDeliveryStatus.Unavailable, result.Status);
        Assert.Equal("steer_unconfirmed", result.ErrorCode);
        Assert.False(result.Retryable);
        Assert.Equal("Idle", store.FindSession(id)!.Status);
        Assert.Equal("steer_unconfirmed", store.FindSession(id)!.StopReason);
        Assert.Single(live, item => item is { Type: "status", Content: "idle" });
        Assert.DoesNotContain(store.Messages, item => item.MessageUid == "silent-boundary-uid");
        Assert.Equal(1, CountCapturedUuid(capturePath, correlationId));
    }

    [Fact]
    public async Task BoundaryWatchdogResetsWhileStdoutKeepsFlowing()
    {
        var (service, store, live, id, capturePath) = StartCapturingSession();
        // Six feeds 200 ms apart span 1.2 s, twice the 600 ms silence timeout, so a watchdog that
        // did not reset would fire; each gap leaves 400 ms of slack for a loaded machine.
        service.SteerBoundarySilenceTimeout = TimeSpan.FromMilliseconds(600);
        Assert.True(await service.SendInput(id, [SessionInputPart.TextPart("initial")], messageUid: "initial-uid"));

        var delivery = service.TrySteerInputAsync(id, [SessionInputPart.TextPart("active boundary")],
            messageUid: "active-boundary-uid");
        var correlationId = await CapturedSteerUuid(capturePath);
        Feed(service, id, Result());

        for (var i = 0; i < 6; i++)
        {
            await Task.Delay(200);
            Feed(service, id, $$"""{"type":"system","subtype":"heartbeat","sequence":{{i}}}""");
        }

        Assert.False(delivery.IsCompleted);
        Assert.Equal("Active", store.FindSession(id)!.Status);
        Assert.DoesNotContain(live, item => item is { Type: "status", Content: "idle" });

        Feed(service, id, Replay(correlationId, "active boundary"));
        var result = await delivery.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(SessionInputDeliveryStatus.Accepted, result.Status);
        Assert.Single(store.Messages, item => item.MessageUid == "active-boundary-uid");
        Assert.Equal(1, CountCapturedUuid(capturePath, correlationId));
    }

    [Fact]
    public void Text_before_a_tool_call_is_commentary_and_the_turn_ending_text_is_final()
    {
        var (service, store, live, id) = StartSession();

        Feed(service, id,
            MessageStart("msg_1"),
            BlockStart(0, "text"),
            TextDelta(0, "Let me "),
            TextDelta(0, "check the binding."),
            BlockStop(0),
            AssistantText("msg_1", "Let me check the binding."),
            BlockStart(1, "tool_use"),
            AssistantToolUse("msg_1", "toolu_1", "Bash", """{"command":"git status"}"""),
            BlockStop(1),
            MessageDelta("tool_use"),
            MessageStop(),
            ToolResult("toolu_1", "clean"),
            MessageStart("msg_2"),
            BlockStart(0, "text"),
            TextDelta(0, "The binding "),
            TextDelta(0, "is healthy."),
            BlockStop(0),
            AssistantText("msg_2", "The binding is healthy."),
            MessageDelta("end_turn"),
            MessageStop(),
            Result());

        Assert.Equal(
            [
                ("text", "Let me check the binding.", "commentary"),
                ("tool_use", null, null),
                ("tool_result", "clean", null),
                ("text", "The binding is healthy.", "final_answer"),
                ("status", "idle", null),
            ],
            store.Assistant(id).Select(m => (m.EventType, m.Content, m.Phase)).ToArray());

        // One logical turn: every record shares the turn uid the live events carried.
        var uid = Assert.Single(store.Assistant(id).Where(m => m.EventType != "status")
            .Select(m => m.MessageUid).Distinct());
        Assert.NotNull(uid);
        Assert.All(live.Where(e => e.Type == "text"), e => Assert.Equal(uid, e.MessageUid));
    }

    [Fact]
    public void Live_deltas_stream_unphased_and_share_their_block_identity_with_the_durable_record()
    {
        var (service, store, live, id) = StartSession();

        Feed(service, id,
            MessageStart("msg_1"),
            BlockStart(0, "text"),
            TextDelta(0, "Checking."),
            BlockStart(1, "tool_use"),
            AssistantToolUse("msg_1", "toolu_1", "Read", """{"file_path":"a"}"""),
            MessageDelta("tool_use"),
            ToolResult("toolu_1", "ok"),
            MessageStart("msg_2"),
            BlockStart(0, "text"),
            TextDelta(0, "Done."),
            MessageDelta("end_turn"),
            Result());

        var liveText = live.Where(e => e.Type == "text").ToList();
        Assert.Equal(["Checking.", "Done."], liveText.Select(e => e.Content));
        // Deltas are live-only: they never claim a phase they cannot know yet, and the settled
        // record is never re-broadcast (the client would append it onto the streamed copy).
        Assert.All(liveText, e => Assert.True(e.IsPartial));
        Assert.All(liveText, e => Assert.Null(e.Phase));

        var durable = store.Assistant(id).Where(m => m.EventType == "text").ToList();
        Assert.Equal(liveText.Select(e => e.MessageId), durable.Select(m => m.MessageId));
        Assert.Equal(["msg_1:0", "msg_2:0"], durable.Select(m => m.MessageId));
        Assert.Equal(["commentary", "final_answer"], durable.Select(m => m.Phase));
    }

    [Fact]
    public void Text_only_turn_is_one_final_record_persisted_before_idle_is_published()
    {
        var (service, store, _, id) = StartSession();
        ClaudeMessageRecord[]? atIdle = null;
        service.SessionUpdated += info =>
        {
            if (info.Id == id && info.Status == RedCompute.Plugin.ClaudeCode.SessionStatus.Idle)
                atIdle = store.Assistant(id).ToArray();
        };

        Feed(service, id,
            MessageStart("msg_1"),
            BlockStart(0, "text"),
            TextDelta(0, "<discord-"),
            TextDelta(0, "no-reply/>"),
            BlockStop(0),
            AssistantText("msg_1", "<discord-no-reply/>"),
            MessageDelta("end_turn"),
            MessageStop(),
            Result());

        var text = Assert.Single(store.Assistant(id), m => m.EventType == "text");
        // Exactly one record, so the bridge's exact-match no-reply suppression can see it whole.
        Assert.Equal("<discord-no-reply/>", text.Content);
        Assert.Equal("final_answer", text.Phase);
        // The settlement callback fires on Idle and immediately reads the transcript.
        Assert.NotNull(atIdle);
        Assert.Contains(atIdle!, m => m.EventType == "text" && m.Phase == "final_answer");
    }

    [Fact]
    public void Multiple_tool_rounds_yield_commentary_per_round_and_one_final()
    {
        var (service, store, _, id) = StartSession();

        var lines = new List<string>();
        for (var round = 1; round <= 3; round++)
        {
            lines.AddRange([
                MessageStart($"msg_{round}"),
                BlockStart(0, "thinking"),
                ThinkingDelta(0, $"plan {round}"),
                BlockStart(1, "text"),
                TextDelta(1, $"Step {round}."),
                BlockStart(2, "tool_use"),
                AssistantToolUse($"msg_{round}", $"toolu_{round}", "Grep", "{}"),
                MessageDelta("tool_use"),
                ToolResult($"toolu_{round}", $"result {round}"),
            ]);
        }
        lines.AddRange([
            MessageStart("msg_4"),
            BlockStart(0, "text"),
            TextDelta(0, "All three checks passed."),
            MessageDelta("end_turn"),
            Result(),
        ]);
        Feed(service, id, [.. lines]);

        var text = store.Assistant(id).Where(m => m.EventType == "text").ToList();
        Assert.Equal(["Step 1.", "Step 2.", "Step 3.", "All three checks passed."], text.Select(m => m.Content));
        Assert.Equal(["commentary", "commentary", "commentary", "final_answer"], text.Select(m => m.Phase));
        Assert.Equal(["msg_1:1", "msg_2:1", "msg_3:1", "msg_4:0"], text.Select(m => m.MessageId));

        // Commentary lands before the tool it introduces, as it streamed.
        var order = store.Assistant(id).Where(m => m.EventType is "text" or "tool_use")
            .Select(m => m.EventType).ToArray();
        Assert.Equal(["text", "tool_use", "text", "tool_use", "text", "tool_use", "text"], order);
    }

    [Fact]
    public void Stop_reason_tool_use_settles_commentary_without_a_tool_block_start()
    {
        var tracker = new ClaudeTextBlockTracker();
        Apply(tracker, MessageStart("msg_1"));
        tracker.Append(null, 0, "Looking.", "uid-1");

        var settled = Apply(tracker, MessageDelta("tool_use"));

        var only = Assert.Single(settled);
        Assert.Equal(("Looking.", "commentary", "msg_1:0", "uid-1"),
            (only.Content, only.Phase, only.MessageId, only.MessageUid));
        Assert.Empty(tracker.SettleTurn());
    }

    [Theory]
    [InlineData("end_turn")]
    [InlineData("max_tokens")]
    [InlineData("stop_sequence")]
    public void A_non_tool_stop_leaves_text_for_the_turn_end_to_settle_as_final(string stopReason)
    {
        var tracker = new ClaudeTextBlockTracker();
        Apply(tracker, MessageStart("msg_1"));
        tracker.Append(null, 0, "Answer.", "uid-1");

        Assert.Empty(Apply(tracker, MessageDelta(stopReason)));
        Assert.Equal("final_answer", Assert.Single(tracker.SettleTurn()).Phase);
    }

    [Fact]
    public void A_settled_final_is_never_relabelled_by_later_tool_activity()
    {
        var (service, store, _, id) = StartSession();

        Feed(service, id,
            MessageStart("msg_1"),
            BlockStart(0, "text"),
            TextDelta(0, "Final answer."),
            MessageDelta("end_turn"),
            Result(),
            // A late or next-turn tool start must not reach back into the settled block.
            MessageStart("msg_2"),
            BlockStart(0, "tool_use"),
            MessageDelta("tool_use"));

        var text = Assert.Single(store.Assistant(id), m => m.EventType == "text");
        Assert.Equal("final_answer", text.Phase);
    }

    [Fact]
    public void Subagent_text_is_commentary_even_when_unsettled_at_the_turn_end()
    {
        var tracker = new ClaudeTextBlockTracker();
        Apply(tracker, MessageStart("msg_main"));
        tracker.Append("toolu_task", 0, "Subagent thinking aloud.", "uid-1");
        tracker.Append(null, 0, "Main answer.", "uid-1");

        var settled = tracker.SettleTurn();

        Assert.Equal(
            [("Subagent thinking aloud.", "commentary"), ("Main answer.", "final_answer")],
            settled.Select(s => (s.Content, s.Phase)));
    }

    [Fact]
    public void Interrupted_turn_keeps_its_partial_text_as_the_final_record()
    {
        var (service, store, _, id) = StartSession();

        Feed(service, id,
            MessageStart("msg_1"),
            BlockStart(0, "text"),
            TextDelta(0, "I was halfway thr"),
            """{"type":"result","subtype":"error_during_execution","is_error":true,"error":"Request was aborted."}""");

        var records = store.Assistant(id).Select(m => (m.EventType, m.Content, m.Phase)).ToArray();
        Assert.Equal(("text", "I was halfway thr", "final_answer"), records[0]);
        Assert.Equal("error", records[1].EventType);
    }

    [Fact]
    public async Task Stopping_mid_block_persists_the_streamed_text_instead_of_losing_it()
    {
        var (service, store, _, id) = StartSession();

        Feed(service, id,
            MessageStart("msg_1"),
            BlockStart(0, "text"),
            TextDelta(0, "Partial reply"));
        await service.ForceKill(id, "maintenance_restart");

        var text = Assert.Single(store.Assistant(id), m => m.EventType == "text");
        Assert.Equal(("Partial reply", "final_answer"), (text.Content, text.Phase));
    }

    [Fact]
    public void Empty_and_whitespace_blocks_produce_no_record()
    {
        var (service, store, _, id) = StartSession();

        Feed(service, id,
            MessageStart("msg_1"),
            BlockStart(0, "text"),
            TextDelta(0, "\n\n"),
            BlockStart(1, "tool_use"),
            MessageDelta("tool_use"),
            Result());

        Assert.DoesNotContain(store.Assistant(id), m => m.EventType == "text");
    }

    [Fact]
    public async Task Real_stdout_reader_produces_the_same_records_as_the_replay()
    {
        var fixture = Path.Combine(_root, "turn.jsonl");
        await File.WriteAllLinesAsync(fixture, [
            MessageStart("msg_1"),
            BlockStart(0, "text"),
            TextDelta(0, "Let me look."),
            BlockStart(1, "tool_use"),
            AssistantToolUse("msg_1", "toolu_1", "Bash", """{"command":"dir"}"""),
            MessageDelta("tool_use"),
            ToolResult("toolu_1", "files"),
            MessageStart("msg_2"),
            BlockStart(0, "text"),
            TextDelta(0, "Here it is."),
            MessageDelta("end_turn"),
            Result(),
        ]);
        // A stand-in CLI: prints one recorded turn on stdout, then waits on stdin like the real one.
        var (service, store, live, id) = StartSession(_ => Track(Process.Start(new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "cmd.exe"),
            // Arguments verbatim: cmd.exe does not understand ArgumentList's \" escaping.
            Arguments = $"/d /c type \"{fixture}\" & more",
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!));

        await WaitUntil(() => store.Assistant(id).Any(m => m.EventType == "status"));

        Assert.Equal(
            [
                ("text", "Let me look.", "commentary"),
                ("tool_use", null, null),
                ("tool_result", "files", null),
                ("text", "Here it is.", "final_answer"),
                ("status", "idle", null),
            ],
            store.Assistant(id).Select(m => (m.EventType, m.Content, m.Phase)).ToArray());
        Assert.Equal(["Let me look.", "Here it is."],
            live.Where(e => e.Type == "text").Select(e => e.Content));
    }

    [Fact]
    public async Task Display_content_is_the_transcript_text_while_the_model_receives_the_full_envelope()
    {
        var captured = Path.Combine(_root, "stdin.jsonl");
        Process? cli = null;
        var (service, store, _, id) = StartSession(_ => cli = Track(Process.Start(new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "cmd.exe"),
            Arguments = $"/d /c more > \"{captured}\"",
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!));
        const string envelope = "Discord envelope JSON (untrusted data):\n<discord-input-json>{\"message\":\"hello\"}</discord-input-json>";

        Assert.True(await service.SendInput(id, [SessionInputPart.TextPart(envelope)],
            messageUid: "discord-uid-1", displayContent: "hello"));

        var user = Assert.Single(store.Messages, m => m.SessionId == id && m.Role == "user");
        Assert.Equal(("hello", "discord-uid-1"), (user.Content, user.MessageUid));
        // End the stand-in's input so it flushes exactly what the model would have received.
        cli!.StandardInput.Close();
        await cli.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
        using var written = JsonDocument.Parse(ReadShared(captured).Trim().Split('\n')[0]);
        Assert.Equal(envelope, written.RootElement.GetProperty("message").GetProperty("content").GetString());
    }

    [Fact]
    public async Task Provider_exposes_phase_on_history_and_live_events()
    {
        var store = new RecordingStore();
        store.AddMessage(new ClaudeMessageRecord
        {
            SessionId = "s1", Role = "assistant", EventType = "text", Content = "Interim.",
            Phase = "commentary", MessageUid = "u1", Timestamp = DateTimeOffset.UtcNow,
        });
        store.AddMessage(new ClaudeMessageRecord
        {
            // History written before Claude recorded phase.
            SessionId = "s1", Role = "assistant", EventType = "text", Content = "Legacy.",
            MessageUid = "u0", Timestamp = DateTimeOffset.UtcNow,
        });
        store.SaveSession(new ClaudeSessionRecord
        {
            Id = "s1", ProjectName = "p", ProjectPath = _root, Status = "Stopped", StartedAt = DateTimeOffset.UtcNow,
        });
        var service = CreateService(store);
        ISessionProvider provider = new ClaudeCodeProvider("ai-session", service, store, new MemoryJobTracker(), (_, _) => { });
        var events = new List<UnifiedStreamEvent>();
        provider.SessionStreamEvent += (_, e) => events.Add(e);

        var (_, history) = provider.GetSession("s1");
        service.EmitStreamEvent("s1", new ClaudeStreamEvent { Type = "text", Content = "x", Phase = "final_answer" });

        Assert.Equal(["commentary", null], history.Select(m => m.Phase));
        Assert.Equal("final_answer", Assert.Single(events).Phase);
    }

    [Fact]
    public void Thinking_deltas_are_live_only_and_one_whole_block_is_persisted_with_the_same_identity()
    {
        var (service, store, live, id) = StartSession();

        Feed(service, id,
            MessageStart("msg_thinking"),
            BlockStart(0, "thinking"),
            ThinkingDelta(0, "Inspect"),
            ThinkingDelta(0, " the "),
            ThinkingDelta(0, "ordering."),
            BlockStop(0));

        var partials = live.Where(e => e.Type == "thinking").ToList();
        Assert.Equal(["Inspect", " the ", "ordering."], partials.Select(e => e.Content));
        Assert.All(partials, e => Assert.True(e.IsPartial));
        Assert.All(partials, e => Assert.Equal("msg_thinking:0", e.MessageId));

        var durable = Assert.Single(store.Assistant(id), m => m.EventType == "thinking");
        Assert.Equal("Inspect the ordering.", durable.Content);
        Assert.Equal("msg_thinking:0", durable.MessageId);
        Assert.Equal(partials[0].MessageUid, durable.MessageUid);
        Assert.Null(durable.Phase);
        // content_block_stop persists silently; re-broadcasting the whole block would duplicate
        // the live partials, exactly as it would for text.
        Assert.Equal(3, partials.Count);
    }

    [Fact]
    public void Thinking_tools_results_and_final_text_keep_live_order_and_one_turn_uid()
    {
        var (service, store, _, id) = StartSession();

        Feed(service, id,
            MessageStart("msg_1"),
            BlockStart(0, "thinking"),
            ThinkingDelta(0, "Plan one."),
            BlockStop(0),
            BlockStart(1, "tool_use"),
            AssistantToolUse("msg_1", "toolu_1", "Read", "{}"),
            BlockStop(1),
            MessageDelta("tool_use"),
            ToolResult("toolu_1", "result"),
            MessageStart("msg_2"),
            BlockStart(0, "thinking"),
            ThinkingDelta(0, "Plan two."),
            BlockStop(0),
            BlockStart(1, "text"),
            TextDelta(1, "Done."),
            BlockStop(1),
            MessageDelta("end_turn"),
            Result());

        var records = store.Assistant(id).Where(m => m.EventType != "status").ToList();
        Assert.Equal(
            ["thinking", "tool_use", "tool_result", "thinking", "text"],
            records.Select(m => m.EventType));
        Assert.Equal(
            ["msg_1:0", "toolu_1", "toolu_1", "msg_2:0", "msg_2:1"],
            records.Select(m => m.MessageId));
        Assert.Single(records.Select(m => m.MessageUid).Distinct());
        Assert.All(records.Where(m => m.EventType == "thinking"), m => Assert.Null(m.Phase));
    }

    [Theory]
    [InlineData("force_kill")]
    [InlineData("stop")]
    [InlineData("interrupt")]
    public async Task Terminal_turn_paths_persist_partial_thinking_once(string ending)
    {
        var (service, store, _, id) = StartSession(ending == "stop"
            ? _ => Track(Process.Start(new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory, "cmd.exe"),
                Arguments = "/d /q /c set /p input=",
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            })!)
            : null);

        Feed(service, id,
            SystemInit(),
            MessageStart("msg_1"),
            BlockStart(0, "thinking"),
            ThinkingDelta(0, "Still reasoning"));

        if (ending == "force_kill")
        {
            await service.ForceKill(id, "maintenance_restart");
        }
        else if (ending == "stop")
        {
            await service.StopSession(id, "user_stopped");
        }
        else
        {
            Assert.Equal(ClaudeSessionService.InterruptResult.Interrupted, service.InterruptSession(id));
            Feed(service, id,
                """{"type":"result","subtype":"error_during_execution","is_error":true,"error":"Request was aborted."}""");
        }

        var thinking = Assert.Single(store.Assistant(id), m => m.EventType == "thinking");
        Assert.Equal(("Still reasoning", "msg_1:0"), (thinking.Content, thinking.MessageId));
    }

    [Fact]
    public async Task Idle_parking_updates_without_terminal_event_and_explicit_stop_reclassifies_it()
    {
        var (service, store, _, id) = StartSession();
        var updates = new List<ClaudeSessionInfo>();
        var ended = new List<(string Reason, string? StopReason)>();
        service.SessionUpdated += info => updates.Add(info);
        service.SessionEnded += (_, reason, stopReason) => ended.Add((reason, stopReason));
        Feed(service, id, SystemInit(), Result());
        updates.Clear();

        Assert.True(await service.TryParkIdleSessionAsync(id, SessionStopReasons.IdleTimeout));
        var parked = store.FindSession(id)!;
        Assert.Equal("Stopped", parked.Status);
        Assert.Equal(SessionStopReasons.IdleTimeout, parked.StopReason);
        Assert.Single(updates);
        Assert.Empty(ended);

        await service.StopSession(id);
        Assert.Equal(SessionStopReasons.UserStopped, store.FindSession(id)!.StopReason);
        Assert.Single(ended);
    }

    [Fact]
    public async Task Process_exit_mid_thinking_persists_the_partial_block_once()
    {
        Process? cli = null;
        var (service, store, _, id) = StartSession(_ => cli = Track(Process.Start(new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "more.com"),
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!));

        Feed(service, id,
            MessageStart("msg_1"),
            BlockStart(0, "thinking"),
            ThinkingDelta(0, "Interrupted by exit"));
        cli!.Kill(entireProcessTree: true);
        await cli.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
        await WaitUntil(() => store.Assistant(id).Any(m => m.EventType == "thinking"));

        var thinking = Assert.Single(store.Assistant(id), m => m.EventType == "thinking");
        Assert.Equal(("Interrupted by exit", "msg_1:0"), (thinking.Content, thinking.MessageId));
    }

    [Fact]
    public void Parent_and_subagent_thinking_with_the_same_index_never_interleave()
    {
        var (service, store, _, id) = StartSession();

        Feed(service, id,
            MessageStart("msg_parent"),
            MessageStart("msg_subagent", "toolu_task"),
            BlockStart(0, "thinking"),
            BlockStart(0, "thinking", "toolu_task"),
            ThinkingDelta(0, "parent-a", null),
            ThinkingDelta(0, "sub-a", "toolu_task"),
            ThinkingDelta(0, "parent-b", null),
            ThinkingDelta(0, "sub-b", "toolu_task"),
            BlockStop(0),
            BlockStop(0, "toolu_task"));

        var thinking = store.Assistant(id).Where(m => m.EventType == "thinking").ToList();
        Assert.Equal(["parent-aparent-b", "sub-asub-b"], thinking.Select(m => m.Content));
        Assert.Equal(["msg_parent:0", "msg_subagent:0"], thinking.Select(m => m.MessageId));
    }

    [Fact]
    public void Whitespace_only_thinking_is_not_persisted()
    {
        var (service, store, live, id) = StartSession();

        Feed(service, id,
            MessageStart("msg_1"),
            BlockStart(0, "thinking"),
            ThinkingDelta(0, " \n\t"),
            BlockStop(0),
            Result());

        Assert.Single(live, e => e.Type == "thinking");
        Assert.DoesNotContain(store.Assistant(id), m => m.EventType == "thinking");
    }

    // A background subagent keeps calling tools after the launching turn's result. Before its
    // parent was read, each such call minted a fresh turn uid: a phantom assistant turn.
    [Fact]
    public void Subagent_calls_after_the_result_keep_the_launching_turn_and_do_not_open_a_new_one()
    {
        var (service, store, live, id) = StartSession();

        Feed(service, id,
            MessageStart("msg_1"),
            BlockStart(0, "tool_use"),
            AssistantToolUse("msg_1", "toolu_agent", "Agent", """{"run_in_background":true}"""),
            ToolResult("toolu_agent", "Async agent launched"),
            MessageStart("msg_2"),
            BlockStart(0, "text"),
            TextDelta(0, "Launched."),
            Result(),
            AssistantToolUse("msg_sub", "toolu_child", "Bash", """{"command":"echo alpha"}""", parent: "toolu_agent"),
            ToolResult("toolu_child", "alpha", parent: "toolu_agent"));

        var launchUid = Assert.Single(store.Assistant(id), m => m.MessageId == "toolu_agent" && m.EventType == "tool_use").MessageUid;
        var children = store.Assistant(id).Where(m => m.ParentToolUseId == "toolu_agent").ToList();
        Assert.Equal(2, children.Count);
        Assert.All(children, child => Assert.Equal(launchUid, child.MessageUid));
        Assert.Equal(launchUid, Assert.Single(live, e => e.MessageId == "toolu_child" && e.Type == "tool_use").MessageUid);
        Assert.Single(store.Assistant(id).Where(m => m.EventType != "status").Select(m => m.MessageUid).Distinct());
        Assert.Equal(ClaudeSessionStatus.Idle, service.GetSession(id).Info!.Status);

        // The next main-loop event still opens a fresh turn: children never reused or reset it.
        Feed(service, id, SystemInit(), MessageStart("msg_3"), BlockStart(0, "text"), TextDelta(0, "next"));
        Assert.NotEqual(launchUid, live[^1].MessageUid);
    }

    // --- harness ---

    private (ClaudeSessionService Service, RecordingStore Store, List<ClaudeStreamEvent> Live, string Id) StartSession(
        Func<ProcessStartInfo, Process?>? launch = null)
    {
        var store = new RecordingStore();
        var service = CreateService(store);
        service.LaunchSessionProcess = launch ?? (_ => Track(Process.Start(new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "more.com"),
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!));
        var live = new List<ClaudeStreamEvent>();
        service.StreamEvent += (_, e) => { lock (live) live.Add(e); };
        var info = service.StartSession(_root, "opus", "owner", null, null, "high", null, null, null,
            JobProvenance.DirectRedCompute("POST", "/ai-session/sessions", new JobBeneficiary("user", "owner"), "request-1", null));
        Assert.NotNull(info);
        return (service, store, live, info!.Id);
    }

    private (ClaudeSessionService Service, RecordingStore Store, List<ClaudeStreamEvent> Live, string Id, string CapturePath)
        StartCapturingSession()
    {
        var capturePath = Path.Combine(_root, $"stdin-{Guid.NewGuid():N}.jsonl");
        var session = StartSession(_ =>
        {
            var fake = new ProcessStartInfo
            {
                FileName = "powershell.exe", CreateNoWindow = true, UseShellExecute = false,
                RedirectStandardInput = true, RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            fake.Environment["STEER_CAPTURE_PATH"] = capturePath;
            fake.ArgumentList.Add("-NoProfile");
            fake.ArgumentList.Add("-Command");
            fake.ArgumentList.Add("while ($null -ne ($line = [Console]::In.ReadLine())) { [IO.File]::AppendAllText($env:STEER_CAPTURE_PATH, $line + [Environment]::NewLine) }");
            return Track(Process.Start(fake)!);
        });
        return (session.Service, session.Store, session.Live, session.Id, capturePath);
    }

    private static async Task<string> CapturedSteerUuid(string path)
    {
        string? correlationId = null;
        await WaitUntil(() =>
        {
            if (!File.Exists(path)) return false;
            foreach (var line in CapturedLines(path))
            {
                using var document = JsonDocument.Parse(line);
                if (document.RootElement.TryGetProperty("uuid", out var uuid))
                    correlationId = uuid.GetString();
            }
            return correlationId is not null;
        });
        return correlationId!;
    }

    private static int CountCapturedUuid(string path, string correlationId) =>
        CapturedLines(path).Count(line => line.Contains(correlationId, StringComparison.Ordinal));

    /// <summary>
    /// Complete JSON lines the fake CLI has appended so far. The CLI keeps appending while the test
    /// reads, so the file is opened with write sharing (File.ReadAllLines refuses it with a sharing
    /// violation) and a line still being written is left for the next poll.
    /// </summary>
    private static IEnumerable<string> CapturedLines(string path)
    {
        var text = ReadShared(path);
        var complete = text.EndsWith('\n') ? text : text[..(text.LastIndexOf('\n') + 1)];
        foreach (var line in complete.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var isJson = true;
            try { using var _ = JsonDocument.Parse(line); }
            catch (JsonException) { isJson = false; }
            if (isJson) yield return line;
        }
    }

    private ClaudeSessionService CreateService(RecordingStore store)
    {
        var claudePath = Path.Combine(_root, "claude.exe");
        if (!File.Exists(claudePath)) File.WriteAllText(claudePath, "");
        return new ClaudeSessionService(new ClaudeConfig { ClaudePath = claudePath }, new MemoryJobTracker(),
            store, new NullQualityModes(), () => null, (_, _) => { });
    }

    private Process Track(Process process)
    {
        _processes.Add(process);
        return process;
    }

    private static void Feed(ClaudeSessionService service, string id, params string[] lines)
    {
        foreach (var line in lines)
            Assert.True(service.ProcessOutputLine(id, line));
    }

    private static IReadOnlyList<ClaudeTextBlockTracker.SettledText> Apply(ClaudeTextBlockTracker tracker, string line)
    {
        using var doc = JsonDocument.Parse(line);
        return ClaudeSessionService.ApplyTextLifecycle(tracker, doc.RootElement);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Condition was not reached");
            await Task.Delay(25);
        }
    }

    private static string ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return new StreamReader(stream).ReadToEnd();
    }

    private static string Json(object value) => JsonSerializer.Serialize(value);

    private static string Stream(object evt, string? parent = null) =>
        Json(new { type = "stream_event", @event = evt, parent_tool_use_id = parent, session_id = "c1" });

    private static string SystemInit() =>
        Json(new { type = "system", subtype = "init", session_id = "c1", model = "opus" });

    private static string MessageStart(string id, string? parent = null) =>
        Stream(new { type = "message_start", message = new { id, role = "assistant", content = Array.Empty<object>() } }, parent);

    private static string BlockStart(int index, string type, string? parent = null) =>
        Stream(new { type = "content_block_start", index, content_block = new { type } }, parent);

    private static string TextDelta(int index, string text) =>
        Stream(new { type = "content_block_delta", index, delta = new { type = "text_delta", text } });

    private static string ThinkingDelta(int index, string thinking, string? parent = null) =>
        Stream(new { type = "content_block_delta", index, delta = new { type = "thinking_delta", thinking } }, parent);

    private static string BlockStop(int index, string? parent = null) =>
        Stream(new { type = "content_block_stop", index }, parent);

    private static string MessageDelta(string stopReason) =>
        Stream(new { type = "message_delta", delta = new { stop_reason = stopReason } });

    private static string MessageStop() => Stream(new { type = "message_stop" });

    private static string AssistantText(string id, string text, string? parent = null) => Json(new
    {
        type = "assistant", parent_tool_use_id = parent,
        message = new { id, role = "assistant", stop_reason = (string?)null, content = new[] { new { type = "text", text } } },
    });

    private static string AssistantToolUse(string id, string toolId, string name, string inputJson, string? parent = null) => Json(new
    {
        type = "assistant", parent_tool_use_id = parent,
        message = new
        {
            id, role = "assistant", stop_reason = (string?)null,
            content = new object[] { new { type = "tool_use", id = toolId, name, input = JsonDocument.Parse(inputJson).RootElement } },
        },
    });

    private static string ToolResult(string toolId, string content, string? parent = null) => Json(new
    {
        type = "user", parent_tool_use_id = parent,
        message = new { role = "user", content = new[] { new { type = "tool_result", tool_use_id = toolId, content } } },
    });

    private static string Result() =>
        """{"type":"result","subtype":"success","is_error":false,"result":"done","total_cost_usd":0.01}""";

    private static string Replay(string correlationId, string content) => Json(new
    {
        type = "user", isReplay = true, uuid = correlationId,
        message = new { role = "user", content }, parent_tool_use_id = (string?)null,
    });

    public void Dispose()
    {
        foreach (var process in _processes)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            process.Dispose();
        }
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { }
    }

    private sealed class RecordingStore : IClaudeSessionStore
    {
        private readonly Dictionary<string, ClaudeSessionRecord> _sessions = [];
        private readonly List<ClaudeMessageRecord> _messages = [];

        public List<ClaudeMessageRecord> Messages { get { lock (_messages) return [.. _messages]; } }
        public IReadOnlyList<ClaudeMessageRecord> Assistant(string sessionId) =>
            Messages.Where(m => m.SessionId == sessionId && m.Role == "assistant").ToList();

        public ClaudeSessionRecord? FindSession(string sessionId) { lock (_sessions) return _sessions.GetValueOrDefault(sessionId); }
        public ClaudeSessionRecord? FindSessionByJobId(Guid jobId) { lock (_sessions) return _sessions.Values.FirstOrDefault(s => s.JobId == jobId); }
        public List<ClaudeSessionRecord> GetActiveSessions() { lock (_sessions) return _sessions.Values.Where(s => s.Status is "Active" or "Idle" or "Starting").ToList(); }
        public List<ClaudeSessionRecord> GetRecentSessions(HashSet<string> excludeIds, int limit = 20, bool includeDismissed = false)
        { lock (_sessions) return _sessions.Values.Where(s => !excludeIds.Contains(s.Id)).Take(limit).ToList(); }
        public void SaveSession(ClaudeSessionRecord record) { lock (_sessions) _sessions[record.Id] = record; }
        public void DismissSession(string sessionId) { }
        public void AddMessage(ClaudeMessageRecord message) { lock (_messages) _messages.Add(message); }
        public void AddMessages(List<ClaudeMessageRecord> messages) { lock (_messages) _messages.AddRange(messages); }
        public List<ClaudeMessageRecord> GetMessages(string sessionId, int limit = 50_000) =>
            Messages.Where(m => m.SessionId == sessionId).ToList();
        public DateTimeOffset GetLastMessageTimestamp(string sessionId) => default;
        public Dictionary<Guid, string> GetSessionStatusesByJobIds(IEnumerable<Guid> jobIds) => [];
    }

    private sealed class MemoryJobTracker : IJobTracker
    {
        private readonly Dictionary<Guid, JobRecord> _jobs = [];

        public JobRecord CreateJob(JobSubmission submission)
        {
            var job = new JobRecord { CapabilitySlug = submission.CapabilitySlug, ProviderName = submission.ProviderName };
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

    private sealed class NullQualityModes : IProviderQualityModeResolver
    {
        public bool TryResolveRequested(string qualityTier, string? preferredProvider, out ProviderQualityMode? resolved)
        {
            resolved = null;
            return false;
        }
    }
}

/// <summary>
/// ClaudeTextPhaseTests drive real fake-CLI processes and watchdog timers measured in milliseconds.
/// Run alongside CPU-heavy classes (image decoding, large HTTP bodies) those timers stretched past
/// their thresholds, so the class runs with parallelization off.
/// </summary>
[CollectionDefinition(nameof(ClaudeTextPhaseCollection), DisableParallelization = true)]
public sealed class ClaudeTextPhaseCollection;
