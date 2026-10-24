using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using RedCompute.Core.Jobs;
using RedCompute.Core.Sessions;
using RedCompute.Plugin.ClaudeCode;
using RedCompute.PluginSdk;
using Xunit;
using ClaudeSessionStatus = RedCompute.Plugin.ClaudeCode.SessionStatus;

namespace RedCompute.App.Tests;

// Claude Code 2.1.306 runs subagents and shells in the background by default. Their stdout lines
// carry `parent_tool_use_id` and keep arriving after the launching turn's result, and the CLI
// reports each task's lifecycle on `system` lines. These tests replay the exact streams recorded
// by the 050e1e14 protocol spikes (Fixtures/claude-background; run4 is the process-tree kill and
// resume) through the same ProcessOutputLine path the stdout reader uses.
//
// Invariants (Background Work design): background work never makes the session Active; a child
// keeps the launching turn's messageUid and carries its parent; RedCompute owns the task state;
// every task ends in a durable terminal record, `lost` when its process died.
public sealed class ClaudeBackgroundWorkTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Environment.GetEnvironmentVariable("REDLEAF_SCRATCH_DIR") ?? Path.GetTempPath(),
        "redcompute-claude-background", Guid.NewGuid().ToString("N"));
    private readonly List<Process> _processes = [];

    public ClaudeBackgroundWorkTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task Run1_children_keep_the_launching_turn_and_the_session_stays_idle_while_they_work()
    {
        const string agentTool = "toolu_01CMyoYLZK4R2QAMntGXHb6p";
        const string bashTool = "toolu_01V7wNKZgv3miUEbvggPv87q";
        const string agentTask = "aa8e56cd9d0843eed";
        const string bashTask = "b5xx2332q";
        var h = Start();
        Assert.True(await h.Service.SendInput(h.Id, [SessionInputPart.TextPart("launch")], messageUid: "user-1"));

        var sawFirstResult = false;
        var wakeStarted = false;
        foreach (var entry in Load("run1"))
        {
            if (entry.Line is not { } line) continue;
            h.Feed(line);
            var type = line.GetProperty("type").GetString();
            var subtype = line.TryGetProperty("subtype", out var s) ? s.GetString() : null;
            if (type == "result") sawFirstResult = true;
            if (type == "system" && subtype == "init" && sawFirstResult) wakeStarted = true;

            // Between the launching turn's result and the CLI's own wake turn, the subagent keeps
            // streaming tool calls: the main loop is idle and must stay so.
            if (sawFirstResult && !wakeStarted)
                Assert.Equal(ClaudeSessionStatus.Idle, h.Info.Status);
            // The CLI's snapshot is authoritative for the running set.
            if (subtype == "background_tasks_changed")
                Assert.Equal(SnapshotIds(line), h.Service.GetBackgroundTasks(h.Id).Select(t => t.TaskId).ToList());
        }

        var records = h.Store.Messages;
        var launchUid = Single(records, r => r is { EventType: "tool_use", MessageId: agentTool }).MessageUid;
        Assert.NotNull(launchUid);
        Assert.Equal(launchUid, Single(records, r => r is { EventType: "tool_use", MessageId: bashTool }).MessageUid);

        // Every subagent record carries its parent and the launching turn's uid.
        var children = records.Where(r => r.ParentToolUseId is not null && r.EventType != "task").ToList();
        Assert.Equal(4, children.Count);
        Assert.All(children, child =>
        {
            Assert.Equal(agentTool, child.ParentToolUseId);
            Assert.Equal(launchUid, child.MessageUid);
        });

        // No phantom turn: the only assistant turns are the launching turn and two wake turns.
        var turns = records.Where(r => r.Role == "assistant" && r.MessageUid is not null)
            .Select(r => r.MessageUid).Distinct().ToList();
        Assert.Equal(3, turns.Count);

        // Two durable records per task: start and terminal.
        var tasks = TaskRecords(records);
        Assert.Equal([(agentTask, "running"), (bashTask, "running"), (agentTask, "completed"), (bashTask, "completed")],
            tasks.Select(t => (t.TaskId, t.Status)).ToList());
        Assert.All(tasks, t => Assert.Equal(launchUid, t.Record.MessageUid));
        Assert.All(tasks.Where(t => t.TaskId == agentTask), t => Assert.Equal(agentTool, t.Record.ParentToolUseId));
        Assert.All(tasks.Where(t => t.TaskId == bashTask), t => Assert.Equal(bashTool, t.Record.ParentToolUseId));
        var agentDone = tasks.Single(t => t is { TaskId: agentTask, Status: "completed" }).Input;
        Assert.Equal("agent", agentDone["kind"]!.GetValue<string>());
        Assert.Equal("sub done", agentDone["summary"]!.GetValue<string>());
        Assert.Equal(2, agentDone["usage"]!["toolUses"]!.GetValue<int>());
        Assert.Equal("shell", tasks.First(t => t.TaskId == bashTask).Input["kind"]!.GetValue<string>());

        // Each wake turn names the task that woke it, live and durably.
        var idles = h.Live.Where(e => e is { Type: "status", Content: "idle" }).ToList();
        Assert.Equal(3, idles.Count);
        Assert.Null(idles[0].TurnOrigin);
        Assert.Equal(agentTask, idles[1].TurnOrigin?.TaskId);
        Assert.Equal(bashTask, idles[2].TurnOrigin?.TaskId);
        Assert.All(idles.Skip(1), idle => Assert.Equal(TurnOrigin.BackgroundTask, idle.TurnOrigin!.Kind));
        var wakeFirstEvents = h.Live.Where(e => e.TurnOrigin is not null && e.Type != "status").ToList();
        Assert.Equal([agentTask, bashTask], wakeFirstEvents.Select(e => e.TurnOrigin!.TaskId).ToList());
        var wakeTexts = records.Where(r => r is { EventType: "text", Content: "noted" }).ToList();
        Assert.Equal(2, wakeTexts.Count);
        Assert.Equal([agentTask, bashTask], wakeTexts.Select(r => OriginTask(r)).ToList());
        Assert.DoesNotContain(records, r => r.TurnOriginJson is not null
            && r.EventType != "status" && r.Content != "noted");

        Assert.Empty(h.Service.GetBackgroundTasks(h.Id));
        Assert.Null(h.Store.FindSession(h.Id)!.BackgroundTasksJson);
        Assert.Equal("Idle", h.Store.FindSession(h.Id)!.Status);
    }

    [Fact]
    public async Task Run1_running_tasks_are_persisted_and_published_without_making_the_session_active()
    {
        var h = Start();
        h.Service.BackgroundProgressInterval = TimeSpan.FromHours(1);
        var published = new List<(ClaudeSessionStatus Status, int Tasks)>();
        h.Service.SessionUpdated += info => { lock (published) published.Add((info.Status, info.BackgroundTasks.Count)); };
        Assert.True(await h.Service.SendInput(h.Id, [SessionInputPart.TextPart("launch")], messageUid: "user-1"));

        var entries = Load("run1").Where(e => e.Line is not null).ToList();
        var firstResult = entries.FindIndex(e => e.Line!.Value.GetProperty("type").GetString() == "result");
        foreach (var entry in entries.Take(firstResult + 1)) h.Feed(entry.Line!.Value);

        // The launching turn is over; both tasks run on, the row lists them for recovery.
        Assert.Equal(ClaudeSessionStatus.Idle, h.Info.Status);
        Assert.Equal(2, h.Info.BackgroundTasks.Count);
        Assert.Equal(["aa8e56cd9d0843eed", "b5xx2332q"],
            ClaudeSessionService.ReadBackgroundTasks(h.Store.FindSession(h.Id)!.BackgroundTasksJson).Select(t => t.TaskId));
        var progress = h.Info.BackgroundTasks[0];
        Assert.Equal("Running Echo beta", progress.LastActivity);
        Assert.Equal("Bash", progress.LastToolName);
        Assert.Contains((ClaudeSessionStatus.Idle, 2), published);
    }

    [Fact]
    public async Task Run2_interrupt_stops_the_running_agent_and_stop_task_stays_idempotent()
    {
        const string agentTool = "toolu_01Pzngk2nZMNSDgUuD5nousv";
        const string agentTask = "a75c17ed13163f1f0";
        const string foregroundTool = "toolu_012V9gb4rWwqxuFoUZP9wweY";
        var h = Start(capture: true);
        var sends = 0;
        var stopResults = new List<BackgroundTaskStopResult>();
        foreach (var entry in Load("run2"))
        {
            if (entry.Sent is { } sent)
            {
                var kind = sent.GetProperty("type").GetString();
                var request = kind == "control_request" ? sent.GetProperty("request").GetProperty("subtype").GetString() : null;
                if (kind == "user")
                    Assert.True(await h.Service.SendInput(h.Id, [SessionInputPart.TextPart($"prompt {++sends}")], messageUid: $"user-{sends}"));
                else if (request == "interrupt")
                    Assert.Equal(ClaudeSessionService.InterruptResult.Interrupted, h.Service.InterruptSession(h.Id));
                else if (request == "stop_task")
                {
                    // The task is already dead: the CLI still answers success.
                    var first = h.Service.TryStopBackgroundTaskAsync(h.Id, agentTask);
                    h.Feed(ControlResponse(await h.StopRequestId(1)));
                    stopResults.Add(await first.WaitAsync(TimeSpan.FromSeconds(5)));
                    var second = h.Service.TryStopBackgroundTaskAsync(h.Id, agentTask);
                    h.Feed(ControlResponse(await h.StopRequestId(2)));
                    stopResults.Add(await second.WaitAsync(TimeSpan.FromSeconds(5)));
                }
                continue;
            }
            if (entry.Line is not { } line || line.GetProperty("type").GetString() == "control_response") continue;
            h.Feed(line);
        }

        var records = h.Store.Messages;
        var launchUid = Single(records, r => r is { EventType: "tool_use", MessageId: agentTool }).MessageUid;
        var secondTurnUid = Single(records, r => r is { EventType: "tool_use", MessageId: foregroundTool }).MessageUid;
        Assert.NotEqual(launchUid, secondTurnUid);
        // The subagent's calls interleave with the second user turn yet keep the first turn's
        // uid; the second turn's own tool calls are untouched.
        var children = records.Where(r => r.ParentToolUseId == agentTool && r.EventType != "task").ToList();
        Assert.Equal(2, children.Count);
        Assert.All(children, child => Assert.Equal(launchUid, child.MessageUid));
        Assert.Equal(secondTurnUid, Single(records, r => r is { EventType: "tool_result", MessageId: foregroundTool }).MessageUid);

        Assert.Equal([(agentTask, "running"), (agentTask, "stopped")],
            TaskRecords(records).Select(t => (t.TaskId, t.Status)).ToList());
        Assert.Equal([BackgroundTaskStopStatus.Accepted, BackgroundTaskStopStatus.Accepted], stopResults.Select(r => r.Status));
        Assert.Equal(2, h.CapturedStopTasks().Count);
        Assert.Empty(h.Service.GetBackgroundTasks(h.Id));
        Assert.Equal(ClaudeSessionStatus.Idle, h.Info.Status);
    }

    [Fact]
    public async Task Run3_stop_task_on_a_live_agent_and_a_nested_background_shell_attach_by_id()
    {
        const string firstAgentTool = "toolu_01Y2BRdGHrtUG4z355BNzGjV";
        const string secondAgentTool = "toolu_01AMtz2v4z6fC5jmZn5Q1Y2A";
        const string nestedBashTool = "toolu_01DoeANTn3bVet8tDNZDaFde";
        const string firstTask = "ad7d310d7112b6ed3";
        const string secondTask = "afa1a7c801d9a0171";
        const string nestedTask = "b5pk8rhx0";
        var h = Start(capture: true);
        Task<BackgroundTaskStopResult>? stop = null;
        string? stopRequestId = null;
        foreach (var entry in Load("run3"))
        {
            if (entry.Sent is { } sent)
            {
                if (sent.GetProperty("type").GetString() == "user")
                    Assert.True(await h.Service.SendInput(h.Id, [SessionInputPart.TextPart("launch two")], messageUid: "user-1"));
                else
                {
                    stop = h.Service.TryStopBackgroundTaskAsync(h.Id, firstTask);
                    stopRequestId = await h.StopRequestId(1);
                }
                continue;
            }
            if (entry.Line is not { } line) continue;
            if (line.GetProperty("type").GetString() == "control_response")
            {
                h.Feed(ControlResponse(stopRequestId!));
                continue;
            }
            h.Feed(line);
        }

        Assert.Equal(BackgroundTaskStopStatus.Accepted, (await stop!.WaitAsync(TimeSpan.FromSeconds(5))).Status);
        var records = h.Store.Messages;
        var launchUid = Single(records, r => r is { EventType: "tool_use", MessageId: firstAgentTool }).MessageUid;
        Assert.Equal(launchUid, Single(records, r => r is { EventType: "tool_use", MessageId: secondAgentTool }).MessageUid);

        // The nested shell was launched by the second subagent's own Bash call: the task attaches
        // to that child square and still resolves to the turn that launched the subagent.
        var nestedCall = Single(records, r => r is { EventType: "tool_use", MessageId: nestedBashTool });
        Assert.Equal(secondAgentTool, nestedCall.ParentToolUseId);
        Assert.Equal(launchUid, nestedCall.MessageUid);
        var tasks = TaskRecords(records);
        Assert.All(tasks.Where(t => t.TaskId == nestedTask), t =>
        {
            Assert.Equal(nestedBashTool, t.Record.ParentToolUseId);
            Assert.Equal(launchUid, t.Record.MessageUid);
            Assert.Equal(secondTask, t.Input["parentTaskId"]!.GetValue<string>());
        });
        Assert.Equal([
            (firstTask, "running"), (secondTask, "running"), (firstTask, "stopped"),
            (nestedTask, "running"), (secondTask, "completed"), (nestedTask, "stopped"),
        ], tasks.Select(t => (t.TaskId, t.Status)).ToList());

        // Wake turns name their task by the run id on result.origin; the nested shell wakes none.
        var origins = h.Live.Where(e => e is { Type: "status", Content: "idle" }).Select(e => e.TurnOrigin?.TaskId).ToList();
        Assert.Equal([null, firstTask, secondTask], origins);
        Assert.All(records.Where(r => r.ParentToolUseId is not null && r.EventType != "task"),
            r => Assert.Equal(launchUid, r.MessageUid));
        Assert.Empty(h.Service.GetBackgroundTasks(h.Id));
    }

    [Fact]
    public async Task Run4_process_tree_kill_records_lost_tasks_and_resume_ignores_the_replayed_notifications()
    {
        const string agentTask = "a359f59250a180b51";
        const string bashTask = "bjwurjao6";
        const string foregroundNested = "bo8c8qu7s";
        var h = Start(capture: true);
        var entries = Load("run4");
        var killed = false;
        foreach (var entry in entries.Where(e => e.Proc == "A" || e.Note is not null))
        {
            if (entry.Note is not null)
            {
                killed = true;
                break;
            }
            if (entry.Sent is not null)
                Assert.True(await h.Service.SendInput(h.Id, [SessionInputPart.TextPart("launch")], messageUid: "user-1"));
            else if (entry.Line is { } line)
                h.Feed(line);
        }
        Assert.True(killed);
        Assert.Equal(2, h.Info.BackgroundTasks.Count);
        Assert.DoesNotContain(h.Info.BackgroundTasks, t => t.TaskId == foregroundNested);

        h.KillProcess();
        await WaitUntil(() => h.Store.FindSession(h.Id)!.Status == "Stopped");
        var lost = TaskRecords(h.Store.Messages).Where(t => t.Status == "lost").ToList();
        Assert.Equal([agentTask, bashTask], lost.Select(t => t.TaskId).ToList());
        Assert.All(lost, t => Assert.NotNull(t.Record.ParentToolUseId));
        Assert.Contains(h.Live, e => e.Type == "task" && e.ToolInput!.ToString()!.Contains("\"lost\""));
        // The 8cd57bb idle-exit classification is unchanged.
        Assert.Equal(SessionStopReasons.OrphanedOnRestart, h.Store.FindSession(h.Id)!.StopReason);
        Assert.Null(h.Store.FindSession(h.Id)!.BackgroundTasksJson);

        // Resume: the new CLI replays "stopped" notifications for the dead tasks and runs one
        // empty wake turn per task before taking the user's message.
        var resumed = h.Service.ResumeSession(h.Id, Provenance());
        Assert.NotNull(resumed);
        var statuses = new List<ClaudeSessionStatus>();
        h.Service.SessionUpdated += info => { if (info.Id == h.Id) lock (statuses) statuses.Add(info.Status); };
        var liveBefore = h.Live.Count;
        foreach (var entry in entries.Where(e => e.Proc == "B"))
        {
            if (entry.Sent is not null)
                Assert.True(await h.Service.SendInput(h.Id, [SessionInputPart.TextPart("still running?")], messageUid: "user-2"));
            else if (entry.Line is { } line)
            {
                h.Feed(line);
                if (line.GetProperty("type").GetString() == "result"
                    && line.TryGetProperty("origin", out _))
                    Assert.Equal(ClaudeSessionStatus.Active, h.Info.Status);
            }
        }

        var tasks = TaskRecords(h.Store.Messages);
        Assert.Equal(2, tasks.Count(t => t.Status == "lost"));
        Assert.DoesNotContain(tasks, t => t.Status == "stopped");
        Assert.DoesNotContain(tasks, t => t.TaskId == foregroundNested);
        // No Idle until the user's own turn ends; exactly one idle status for it.
        var resumedLive = h.Live.Skip(liveBefore).ToList();
        Assert.Single(resumedLive, e => e is { Type: "status", Content: "idle" });
        Assert.Equal(ClaudeSessionStatus.Idle, statuses[^1]);
        Assert.DoesNotContain(ClaudeSessionStatus.Idle, statuses.Take(statuses.Count - 1));
        Assert.DoesNotContain(resumedLive, e => e.TurnOrigin is not null);
    }

    // Run6 (Claude Code 2.1.309, recorded 2026-10-24): a Bash with a 15 s timeout runs 40 s. The CLI
    // announces it as foreground, then at the timeout lists it in a snapshot and patches
    // `is_backgrounded: true`; the turn ends while it runs and a wake turn reports it. Before the
    // promotion the tracker dropped it, so the session looked parkable with work still running.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Run6_a_foreground_shell_moved_to_the_background_is_tracked_until_it_finishes(bool patchBeforeSnapshot)
    {
        const string bashTool = "toolu_019nF9XiBrcabdVmH79M2pqR";
        const string bashTask = "b275w01pt";
        var h = Start();
        Assert.True(await h.Service.SendInput(h.Id, [SessionInputPart.TextPart("launch")], messageUid: "user-1"));

        var lines = Load("run6-promoted-shell").Select(e => e.Line!.Value).ToList();
        if (patchBeforeSnapshot)
        {
            var snapshot = lines.FindIndex(IsSubtype("background_tasks_changed"));
            var patch = lines.FindIndex(IsSubtype("task_updated"));
            Assert.Equal(snapshot + 1, patch);
            (lines[snapshot], lines[patch]) = (lines[patch], lines[snapshot]);
        }

        var results = 0;
        foreach (var line in lines)
        {
            h.Feed(line);
            if (line.GetProperty("type").GetString() != "result" || ++results != 1) continue;

            // The launching turn is over and the shell runs on: Idle, but not parkable.
            Assert.Equal(ClaudeSessionStatus.Idle, h.Info.Status);
            var running = Assert.Single(h.Service.GetBackgroundTasks(h.Id));
            Assert.Equal(bashTask, running.TaskId);
            Assert.Equal(BackgroundTaskKinds.Shell, running.Kind);
            Assert.Equal(bashTool, running.ParentToolUseId);
            Assert.False(await h.Service.TryParkIdleSessionAsync(h.Id, SessionStopReasons.IdleTimeout));
            Assert.Equal(ClaudeSessionStatus.Idle, h.Info.Status);
        }
        Assert.Equal(2, results);

        var records = h.Store.Messages;
        var launchUid = Single(records, r => r is { EventType: "tool_use", MessageId: bashTool }).MessageUid;
        Assert.NotNull(launchUid);
        var tasks = TaskRecords(records);
        Assert.Equal([(bashTask, "running"), (bashTask, "completed")], tasks.Select(t => (t.TaskId, t.Status)).ToList());
        Assert.All(tasks, t =>
        {
            Assert.Equal(launchUid, t.Record.MessageUid);
            Assert.Equal(bashTool, t.Record.ParentToolUseId);
        });

        // The CLI's wake turn names the shell that woke it.
        var idles = h.Live.Where(e => e is { Type: "status", Content: "idle" }).ToList();
        Assert.Equal(2, idles.Count);
        Assert.Null(idles[0].TurnOrigin);
        Assert.Equal(bashTask, idles[1].TurnOrigin?.TaskId);
        Assert.Empty(h.Service.GetBackgroundTasks(h.Id));
        Assert.Null(h.Store.FindSession(h.Id)!.BackgroundTasksJson);
    }

    [Fact]
    public async Task A_shell_that_finishes_in_the_foreground_is_never_background_work()
    {
        var h = Start();
        Assert.True(await h.Service.SendInput(h.Id, [SessionInputPart.TextPart("launch")], messageUid: "user-1"));
        h.Feed("""{"type":"system","subtype":"task_started","task_id":"bfg1","run_id":"r1","tool_use_id":"toolu_fg","description":"git status","is_backgrounded":false,"task_type":"local_bash"}""");
        Assert.Empty(h.Service.GetBackgroundTasks(h.Id));
        h.Feed("""{"type":"system","subtype":"task_updated","task_id":"bfg1","run_id":"r1","patch":{"status":"completed","end_time":1792859790196}}""");
        h.Feed("""{"type":"system","subtype":"task_notification","task_id":"bfg1","run_id":"r1","tool_use_id":"toolu_fg","status":"completed","summary":"git status"}""");

        Assert.Empty(h.Service.GetBackgroundTasks(h.Id));
        Assert.Empty(TaskRecords(h.Store.Messages));
        Assert.DoesNotContain(h.Live, e => e.Type == "task");
    }

    private static Predicate<JsonElement> IsSubtype(string subtype) => line =>
        line.GetProperty("type").GetString() == "system"
        && line.TryGetProperty("subtype", out var s) && s.GetString() == subtype;

    [Fact]
    public void Startup_recovery_records_tasks_left_running_as_lost_once_the_mirror_exists()
    {
        var store = new RecordingStore();
        store.SaveSession(new ClaudeSessionRecord
        {
            Id = "s1", ProjectName = "Nova", ProjectPath = _root, Status = "Idle", StopReason = "completed",
            StartedAt = DateTimeOffset.UtcNow,
            BackgroundTasksJson = JsonSerializer.Serialize(new[]
            {
                new BackgroundTaskInfo
                {
                    TaskId = "task-1", Kind = BackgroundTaskKinds.Agent, Status = BackgroundTaskStatuses.Running,
                    Description = "Research", AgentType = "Explore", StartedAt = DateTimeOffset.UtcNow.AddMinutes(-3),
                    ParentToolUseId = "toolu_agent", MessageUid = "turn-1",
                },
            }),
        });
        var deferred = new List<Action>();

        _ = new ClaudeSessionService(new ClaudeConfig { ClaudePath = ClaudePath() }, new MemoryJobTracker(), store,
            new NullQualityModes(), () => null, (_, _) => { }, deferred.Add);

        // Nothing durable is written before the suite mirror can carry it.
        Assert.Empty(store.Messages);
        Assert.Equal("Stopped", store.FindSession("s1")!.Status);
        Assert.NotNull(store.FindSession("s1")!.BackgroundTasksJson);

        foreach (var action in deferred) action();

        var record = Assert.Single(store.Messages);
        Assert.Equal(("system", "task"), (record.Role, record.EventType));
        Assert.Equal("toolu_agent", record.ParentToolUseId);
        Assert.Equal("turn-1", record.MessageUid);
        var input = JsonNode.Parse(record.ToolInput!)!;
        Assert.Equal("lost", input["status"]!.GetValue<string>());
        Assert.Equal("task-1", input["taskId"]!.GetValue<string>());
        Assert.NotNull(input["endedAt"]);
        Assert.Null(store.FindSession("s1")!.BackgroundTasksJson);
    }

    [Fact]
    public async Task Stop_requests_for_unknown_sessions_or_tasks_are_side_effect_free()
    {
        var h = Start(capture: true);
        Assert.Equal(BackgroundTaskStopStatus.SessionNotFound,
            (await h.Service.TryStopBackgroundTaskAsync("missing", "task")).Status);
        Assert.Equal(BackgroundTaskStopStatus.TaskNotFound,
            (await h.Service.TryStopBackgroundTaskAsync(h.Id, "never-started")).Status);
        Assert.Empty(h.CapturedStopTasks());
    }

    [Fact]
    public void Provider_exposes_the_capability_and_carries_parent_and_origin_on_unified_events()
    {
        var store = new RecordingStore();
        var service = CreateService(store);
        var provider = new ClaudeCodeProvider("ai-session", service, store, new MemoryJobTracker(), (_, _) => { });
        Assert.True(provider.Capabilities.HasFlag(SessionCapabilities.SupportsBackgroundTaskStop));
        var events = new List<UnifiedStreamEvent>();
        ((ISessionProvider)provider).SessionStreamEvent += (_, e) => events.Add(e);

        service.EmitStreamEvent("s", new ClaudeStreamEvent
        {
            Type = "tool_use", MessageId = "toolu_child", ParentToolUseId = "toolu_agent", MessageUid = "turn-1",
            TurnOrigin = new TurnOrigin { Kind = TurnOrigin.BackgroundTask, TaskId = "task-1" },
        });

        var unified = Assert.Single(events);
        Assert.Equal("toolu_agent", unified.ParentToolUseId);
        Assert.Equal("task-1", unified.TurnOrigin?.TaskId);
    }

    // --- turn outcome: how each turn ended, so an interrupted turn is never reported completed ---

    [Fact]
    public async Task Successful_result_reports_a_completed_turn()
    {
        var h = Start();
        Assert.True(await h.Service.SendInput(h.Id, [SessionInputPart.TextPart("work")], messageUid: "user-1"));
        var before = DateTimeOffset.UtcNow;
        h.Feed("""{"type":"result","subtype":"success","is_error":false,"result":"Done.","total_cost_usd":0.01}""");

        Assert.Equal(ClaudeSessionStatus.Idle, h.Info.Status);
        Assert.Equal(SessionTurnOutcomes.Completed, h.Info.LastTurnOutcome);
        Assert.True(h.Info.LastTurnEndedAt >= before);
    }

    [Fact]
    public async Task Interrupt_that_aborts_the_turn_reports_it_interrupted()
    {
        var h = Start();
        Assert.True(await h.Service.SendInput(h.Id, [SessionInputPart.TextPart("work")], messageUid: "user-1"));
        Assert.Equal(ClaudeSessionService.InterruptResult.Interrupted, h.Service.InterruptSession(h.Id));
        h.Feed("""{"type":"result","subtype":"error_during_execution","is_error":true,"error":"Request was aborted.","total_cost_usd":0.01}""");

        Assert.Equal(ClaudeSessionStatus.Idle, h.Info.Status);
        Assert.Equal(SessionTurnOutcomes.Interrupted, h.Info.LastTurnOutcome);
        Assert.Contains(h.Live, e => e.Type == "status" && e.Content == "interrupted");
    }

    [Fact]
    public async Task Interrupt_raced_by_a_finishing_turn_reports_it_completed()
    {
        var h = Start();
        Assert.True(await h.Service.SendInput(h.Id, [SessionInputPart.TextPart("work")], messageUid: "user-1"));
        h.Service.InterruptSession(h.Id);
        h.Feed("""{"type":"result","subtype":"success","is_error":false,"result":"Done.","total_cost_usd":0.01}""");

        Assert.Equal(SessionTurnOutcomes.Completed, h.Info.LastTurnOutcome);
    }

    [Fact]
    public async Task Error_result_without_an_interrupt_reports_a_failed_turn()
    {
        var h = Start();
        Assert.True(await h.Service.SendInput(h.Id, [SessionInputPart.TextPart("work")], messageUid: "user-1"));
        h.Feed("""{"type":"result","subtype":"error","error":"provider unavailable"}""");

        Assert.Equal(SessionTurnOutcomes.Failed, h.Info.LastTurnOutcome);
    }

    // --- harness ---

    private sealed record Entry(string? Proc, JsonElement? Line, JsonElement? Sent, string? Note);

    private static List<Entry> Load(string name)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "claude-background", name + ".jsonl");
        return File.ReadAllLines(path).Where(line => !string.IsNullOrWhiteSpace(line)).Select(line =>
        {
            var root = JsonDocument.Parse(line).RootElement;
            return new Entry(
                root.TryGetProperty("proc", out var proc) ? proc.GetString() : null,
                root.TryGetProperty("line", out var l) ? l : null,
                root.TryGetProperty("sent", out var sent) ? sent : null,
                root.TryGetProperty("note", out var note) ? note.GetString() : null);
        }).ToList();
    }

    private static List<string> SnapshotIds(JsonElement snapshot) =>
        snapshot.GetProperty("tasks").EnumerateArray().Select(t => t.GetProperty("task_id").GetString()!).ToList();

    private static string ControlResponse(string requestId) => JsonSerializer.Serialize(new
    {
        type = "control_response",
        response = new { subtype = "success", request_id = requestId, response = new { } },
    });

    private sealed record TaskRecord(ClaudeMessageRecord Record, JsonNode Input)
    {
        public string TaskId => Input["taskId"]!.GetValue<string>();
        public string Status => Input["status"]!.GetValue<string>();
    }

    private static List<TaskRecord> TaskRecords(IEnumerable<ClaudeMessageRecord> records) =>
        records.Where(r => r.EventType == "task")
            .Select(r =>
            {
                Assert.Equal("system", r.Role);
                return new TaskRecord(r, JsonNode.Parse(r.ToolInput!)!);
            })
            .ToList();

    private static string? OriginTask(ClaudeMessageRecord record) =>
        record.TurnOriginJson is null ? null : JsonNode.Parse(record.TurnOriginJson)!["TaskId"]?.GetValue<string>();

    private static ClaudeMessageRecord Single(IEnumerable<ClaudeMessageRecord> records, Func<ClaudeMessageRecord, bool> match)
        => Assert.Single(records, r => match(r));

    private static JobProvenance Provenance() =>
        JobProvenance.DirectRedCompute("POST", "/ai-session/sessions", new JobBeneficiary("user", "owner"), "request-1", null);

    private sealed class Harness(ClaudeSessionService service, RecordingStore store, List<ClaudeStreamEvent> live,
        string id, string? capturePath, Func<Process> currentProcess)
    {
        public ClaudeSessionService Service => service;
        public RecordingStore Store => store;
        public List<ClaudeStreamEvent> Live { get { lock (live) return [.. live]; } }
        public string Id => id;
        public ClaudeSessionInfo Info => service.GetSession(id).Info!;

        public void Feed(JsonElement line) => Feed(line.GetRawText());
        public void Feed(string line) => Assert.True(service.ProcessOutputLine(id, line));
        public void KillProcess() => currentProcess().Kill(entireProcessTree: true);

        public List<JsonElement> CapturedStopTasks()
        {
            if (capturePath is null || !File.Exists(capturePath)) return [];
            using var stream = new FileStream(capturePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return new StreamReader(stream).ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => JsonDocument.Parse(line).RootElement)
                .Where(root => root.TryGetProperty("request", out var request)
                    && request.TryGetProperty("subtype", out var subtype) && subtype.GetString() == "stop_task")
                .ToList();
        }

        public async Task<string> StopRequestId(int ordinal)
        {
            await WaitUntil(() => CapturedStopTasks().Count >= ordinal);
            return CapturedStopTasks()[ordinal - 1].GetProperty("request_id").GetString()!;
        }
    }

    private Harness Start(bool capture = false)
    {
        var store = new RecordingStore();
        var service = CreateService(store);
        var capturePath = capture ? Path.Combine(_root, $"stdin-{Guid.NewGuid():N}.jsonl") : null;
        Process? current = null;
        service.LaunchSessionProcess = _ =>
        {
            ProcessStartInfo start;
            if (capturePath is null)
            {
                start = new ProcessStartInfo { FileName = Path.Combine(Environment.SystemDirectory, "more.com") };
            }
            else
            {
                start = new ProcessStartInfo { FileName = "powershell.exe" };
                start.Environment["STDIN_CAPTURE_PATH"] = capturePath;
                start.ArgumentList.Add("-NoProfile");
                start.ArgumentList.Add("-Command");
                start.ArgumentList.Add("while ($null -ne ($line = [Console]::In.ReadLine())) { [IO.File]::AppendAllText($env:STDIN_CAPTURE_PATH, $line + [Environment]::NewLine) }");
            }
            start.CreateNoWindow = true;
            start.UseShellExecute = false;
            start.RedirectStandardInput = true;
            start.RedirectStandardOutput = true;
            start.RedirectStandardError = true;
            current = Process.Start(start)!;
            _processes.Add(current);
            return current;
        };
        var live = new List<ClaudeStreamEvent>();
        service.StreamEvent += (_, e) => { lock (live) live.Add(e); };
        var info = service.StartSession(_root, "haiku", "owner", null, null, "high", null, null, null, Provenance());
        Assert.NotNull(info);
        return new Harness(service, store, live, info!.Id, capturePath, () => current!);
    }

    private string ClaudePath()
    {
        var claudePath = Path.Combine(_root, "claude.exe");
        if (!File.Exists(claudePath)) File.WriteAllText(claudePath, "");
        return claudePath;
    }

    private ClaudeSessionService CreateService(RecordingStore store) =>
        new(new ClaudeConfig { ClaudePath = ClaudePath() }, new MemoryJobTracker(), store, new NullQualityModes(),
            () => null, (_, _) => { }, action => action());

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Condition was not reached");
            await Task.Delay(25);
        }
    }

    public void Dispose()
    {
        foreach (var process in _processes)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            process.Dispose();
        }
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { }
    }

    internal sealed class RecordingStore : IClaudeSessionStore
    {
        private readonly Dictionary<string, ClaudeSessionRecord> _sessions = [];
        private readonly List<ClaudeMessageRecord> _messages = [];

        public List<ClaudeMessageRecord> Messages { get { lock (_messages) return [.. _messages]; } }

        public ClaudeSessionRecord? FindSession(string sessionId) { lock (_sessions) return _sessions.GetValueOrDefault(sessionId); }
        public ClaudeSessionRecord? FindSessionByJobId(Guid jobId) { lock (_sessions) return _sessions.Values.FirstOrDefault(s => s.JobId == jobId); }
        public List<ClaudeSessionRecord> GetActiveSessions() { lock (_sessions) return _sessions.Values.Where(s => s.Status is "Active" or "Idle" or "Starting").ToList(); }
        public List<ClaudeSessionRecord> GetSessionsWithBackgroundTasks() { lock (_sessions) return _sessions.Values.Where(s => s.BackgroundTasksJson is not null).ToList(); }
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

    internal sealed class MemoryJobTracker : IJobTracker
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

    internal sealed class NullQualityModes : IProviderQualityModeResolver
    {
        public bool TryResolveRequested(string qualityTier, string? preferredProvider, out ProviderQualityMode? resolved)
        {
            resolved = null;
            return false;
        }
    }
}
