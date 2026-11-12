using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using RedCompute.App.Services;
using RedCompute.Core.Jobs;
using RedCompute.Core.Sessions;
using RedCompute.Plugin.ClaudeCode;
using SessionStatus = RedCompute.Core.Sessions.SessionStatus;
using RedCompute.PluginSdk;
using Xunit;

namespace RedCompute.App.Tests;

// Claude Code under access levels: every launch passes the persisted level's permission mode, the
// CLI's own report never changes the level, and a can_use_tool ask (captured from CLI 2.1.318 in
// acceptEdits, Fixtures/claude-permissions) parks as a permission a person answers, or is denied
// at once when nobody is watching.
public sealed class ClaudeAccessLevelTests : IDisposable
{
    private const string Plan = SessionAccessLevels.Plan, Ask = SessionAccessLevels.Ask,
        Auto = SessionAccessLevels.Auto, Full = SessionAccessLevels.Full;

    private readonly string _root = Path.Combine(
        Environment.GetEnvironmentVariable("REDLEAF_SCRATCH_DIR") ?? Path.GetTempPath(),
        "redcompute-claude-access", Guid.NewGuid().ToString("N"));
    private readonly List<Process> _processes = [];
    private readonly List<ProcessStartInfo> _launches = [];
    private readonly ConcurrentQueue<string> _written = new();
    private readonly List<ClaudeStreamEvent> _live = [];
    private readonly ClaudeBackgroundWorkTests.RecordingStore _store = new();
    private readonly ClaudeSessionService _service;
    // How the fake CLI answers set_permission_mode: a null error is success. Switched off, it never answers.
    private Func<string, string?> _cliModeError = _ => null;
    private bool _cliAnswersModes = true;
    private const string BypassNotLaunched =
        "Cannot set permission mode to bypassPermissions because the session was not launched with --dangerously-skip-permissions";

    public ClaudeAccessLevelTests()
    {
        Directory.CreateDirectory(_root);
        var claudePath = Path.Combine(_root, "claude.exe");
        File.WriteAllText(claudePath, "");
        _service = new ClaudeSessionService(new ClaudeConfig { ClaudePath = claudePath },
            new ClaudeBackgroundWorkTests.MemoryJobTracker(), _store, new ClaudeBackgroundWorkTests.NullQualityModes(),
            () => null, (_, _) => { }, action => action());
        _service.LaunchSessionProcess = startInfo =>
        {
            lock (_launches) _launches.Add(startInfo);
            // A sink that never echoes: an echo would hand our own set_permission_mode back as an
            // inbound request, and RedCompute's refusal of it would answer the pending switch.
            var sink = TestProcesses.AppendStdinTo(Path.Combine(_root, "stdin.jsonl"), "CLAUDE_ACCESS_STDIN");
            sink.CreateNoWindow = true;
            sink.UseShellExecute = false;
            sink.RedirectStandardInput = true;
            sink.RedirectStandardOutput = true;
            sink.RedirectStandardError = true;
            var process = Process.Start(sink)!;
            lock (_processes) _processes.Add(process);
            return process;
        };
        _service.ControlLineWritten = (id, json) =>
        {
            _written.Enqueue(json);
            AnswerPermissionMode(id, json);
        };
        _service.StreamEvent += (_, e) => { lock (_live) _live.Add(e); };
    }

    // ---- launch arguments ----

    [Theory]
    [InlineData(Plan, "plan")]
    [InlineData(Ask, "acceptEdits")]
    [InlineData(Auto, "auto")]
    [InlineData(Full, "bypassPermissions")]
    public void Start_launches_at_the_resolved_level_and_persists_it(string level, string mode)
    {
        var id = Start(new SessionAccessState(level, Full, Unattended: false));

        Assert.Equal(mode, PermissionModeArg(_launches[^1]));
        Assert.Contains(AllowBypass, _launches[^1].ArgumentList);
        Assert.Equal(level, _service.GetAccessState(id)!.AccessLevel);
        Assert.Equal(level, _store.FindSession(id)!.AccessLevel);
        Assert.Equal(Full, _store.FindSession(id)!.MaxAccessLevel);
    }

    [Fact]
    public void Start_without_a_resolved_level_fails_closed_to_ask()
    {
        var id = Start(null);

        Assert.Equal("acceptEdits", PermissionModeArg(_launches[^1]));
        Assert.Equal(Ask, _service.GetAccessState(id)!.AccessLevel);
    }

    [Theory]
    [InlineData(Plan, "plan")]
    [InlineData(Ask, "acceptEdits")]
    [InlineData(Auto, "auto")]
    [InlineData(Full, "bypassPermissions")]
    [InlineData(null, "acceptEdits")]   // a legacy row: never its old hardcoded bypass
    public void Resume_launches_at_the_persisted_level(string? level, string mode)
    {
        _store.SaveSession(Record("stopped", level));

        Assert.True(_service.ResumeSession("stopped", Provenance()) is not null, _service.LastStartError);

        var launch = Assert.Single(_launches);
        Assert.Equal(mode, PermissionModeArg(launch));
        Assert.Contains(AllowBypass, launch.ArgumentList);
        Assert.Equal("claude-conversation", ArgAfter(launch, "--resume"));
    }

    // 2026-11-11 (80570766): the 9 Nov worker that came back at ask was the access-level rollout
    // (rows with no level, then bbaf384), not the restart. This pins the restart itself: a session
    // a planned restart cut mid-turn is recovered without touching its level, and resumes at it.
    [Theory]
    [InlineData(Full, SessionAccessLevelSources.Explicit, "bypassPermissions")]
    [InlineData(Full, SessionAccessLevelSources.Parent, "bypassPermissions")]
    [InlineData(Full, SessionAccessLevelSources.Policy, "bypassPermissions")]
    [InlineData(Auto, SessionAccessLevelSources.Parent, "auto")]
    [InlineData(Ask, SessionAccessLevelSources.Explicit, "acceptEdits")]
    public void A_session_a_planned_restart_cut_mid_turn_keeps_its_level_and_resumes_at_it(string level, string source, string mode)
    {
        var record = Record("worker", level);
        record.Status = "Active";
        record.AccessLevelSource = source;
        record.MaxAccessLevel = Full;
        _store.SaveSession(record);
        var checkpoint = Path.Combine(_root, "planned-restart.json");
        PlannedRestartCheckpoint.Write(new("run", DateTimeOffset.UtcNow, [new("claude-code", "worker", null) { ForcedStop = true }]), checkpoint);
        try
        {
            Assert.NotNull(PlannedRestartCheckpoint.LoadForStartup(checkpoint));
            _service.RecoverSessions();
        }
        finally { PlannedRestartCheckpoint.Consume(checkpoint); }

        var recovered = _store.FindSession("worker")!;
        Assert.Equal("Stopped", recovered.Status);
        Assert.Equal(SessionStopReasons.MaintenanceRestart, recovered.StopReason);
        Assert.Equal((level, source, Full), (recovered.AccessLevel, recovered.AccessLevelSource, recovered.MaxAccessLevel));

        Assert.True(_service.ResumeSession("worker", Provenance()) is not null, _service.LastStartError);
        Assert.Equal(mode, PermissionModeArg(Assert.Single(_launches)));
        Assert.Equal(level, _service.GetAccessState("worker")!.AccessLevel);
    }

    [Theory]
    [InlineData(Plan, "plan")]
    [InlineData(Ask, "acceptEdits")]
    [InlineData(Auto, "auto")]
    [InlineData(Full, "bypassPermissions")]
    [InlineData(null, "acceptEdits")]
    public void Rollback_fork_probe_launches_at_the_persisted_level(string? level, string mode)
    {
        var record = Record("rewound", level);
        var target = new ClaudeForkTarget("rollback-1", "uuid-kept", "uuid-dropped", ["uuid-dropped"]);

        var startInfo = (ProcessStartInfo)typeof(ClaudeSessionService)
            .GetMethod("ForkProbeStartInfo", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(_service, [Path.Combine(_root, "claude.exe"), record, target])!;

        Assert.Equal(mode, PermissionModeArg(startInfo));
        Assert.Contains(AllowBypass, startInfo.ArgumentList);
        Assert.Equal("uuid-kept", ArgAfter(startInfo, "--resume-session-at"));
    }

    [Theory]
    [InlineData(Auto, "auto")]
    [InlineData(Plan, "plan")]
    [InlineData(Ask, "acceptEdits")]
    [InlineData(Full, "bypassPermissions")]
    public async Task Forced_interrupt_recovery_relaunches_at_the_level(string level, string mode)
    {
        var id = Start(new SessionAccessState(level));
        Assert.True(_service.ProcessOutputLine(id,
            $$"""{"type":"system","subtype":"init","session_id":"claude-1","permissionMode":"{{mode}}","tools":[]}"""));

        var sessions = (System.Collections.IDictionary)typeof(ClaudeSessionService)
            .GetField("_sessions", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(_service)!;
        var managed = sessions[id]!;
        await (Task)typeof(ClaudeSessionService)
            .GetMethod("KillAndResumeAfterForcedInterrupt", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(_service, [id, managed])!;

        Assert.Equal(2, _launches.Count);
        Assert.Equal(mode, PermissionModeArg(_launches[1]));
        Assert.Contains(AllowBypass, _launches[1].ArgumentList);
        Assert.Equal(level, _service.GetAccessState(id)!.AccessLevel);
    }

    [Fact]
    public void The_init_report_never_changes_the_level_and_a_mismatch_is_set_again()
    {
        var id = Start(new SessionAccessState(Ask));
        _written.Clear();

        Assert.True(_service.ProcessOutputLine(id,
            """{"type":"system","subtype":"init","session_id":"claude-1","permissionMode":"bypassPermissions","tools":[]}"""));

        Assert.Equal(Ask, _service.GetAccessState(id)!.AccessLevel);
        Assert.Equal(Ask, _store.FindSession(id)!.AccessLevel);
        var reset = Assert.Single(Written("set_permission_mode"));
        Assert.Equal("acceptEdits", reset["request"]!["mode"]!.GetValue<string>());
        Assert.Equal("acceptEdits", _service.GetSession(id).Info!.PermissionMode);

        _written.Clear();
        Assert.True(_service.ProcessOutputLine(id,
            """{"type":"system","subtype":"init","session_id":"claude-1","permissionMode":"acceptEdits","tools":[]}"""));
        Assert.Empty(Written("set_permission_mode"));
    }

    [Fact]
    public void A_live_change_switches_with_set_permission_mode_and_persists()
    {
        var id = Start(new SessionAccessState(Ask));
        _written.Clear();

        Assert.True(_service.SetAccessState(id, new SessionAccessState(Auto)));
        Assert.True(_service.SetAccessState(id, new SessionAccessState(Auto, Unattended: true)));

        var change = Assert.Single(Written("set_permission_mode"));
        Assert.Equal("auto", change["request"]!["mode"]!.GetValue<string>());
        Assert.Equal(Auto, _store.FindSession(id)!.AccessLevel);
        Assert.True(_store.FindSession(id)!.Unattended);
    }

    // The 2026-11-09 incident: raised ask -> full while live, the CLI refused, RedCompute recorded
    // full anyway, then retried at every turn's init while every tool call kept parking.
    [Fact]
    public async Task A_refused_live_raise_keeps_the_level_and_returns_the_cli_reason()
    {
        var id = Start(new SessionAccessState(Ask));
        _cliModeError = mode => mode == "bypassPermissions" ? BypassNotLaunched : null;

        var result = await _service.ApplyAccessStateAsync(id, new SessionAccessState(Full));

        Assert.False(result.Applied);
        Assert.Equal($"Claude Code said: \"{BypassNotLaunched}\"", result.Reason);
        Assert.Equal(Ask, _service.GetAccessState(id)!.AccessLevel);
        Assert.Equal(Ask, _store.FindSession(id)!.AccessLevel);
        Assert.Equal("acceptEdits", _service.GetSession(id).Info!.PermissionMode);
    }

    [Fact]
    public async Task A_live_raise_is_recorded_only_once_the_cli_confirms_it()
    {
        var id = Start(new SessionAccessState(Ask));
        _cliAnswersModes = false;
        _written.Clear();

        var pending = _service.ApplyAccessStateAsync(id, new SessionAccessState(Full));
        var request = Assert.Single(Written("set_permission_mode"));
        Assert.Equal("bypassPermissions", request["request"]!["mode"]!.GetValue<string>());
        Assert.False(pending.IsCompleted);
        Assert.Equal(Ask, _store.FindSession(id)!.AccessLevel);

        Assert.True(_service.ProcessOutputLine(id, ModeResponse(request["request_id"]!.GetValue<string>(), "bypassPermissions", null)));
        var result = await pending.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(result.Applied);
        Assert.Equal(Full, _service.GetAccessState(id)!.AccessLevel);
        Assert.Equal(Full, _store.FindSession(id)!.AccessLevel);
        Assert.Equal("bypassPermissions", _service.GetSession(id).Info!.PermissionMode);
    }

    [Fact]
    public async Task An_unanswered_switch_counts_as_refused_and_a_late_success_is_put_back()
    {
        var id = Start(new SessionAccessState(Ask));
        _service.PermissionModeConfirmTimeout = TimeSpan.FromMilliseconds(100);
        _cliAnswersModes = false;
        _written.Clear();

        var result = await _service.ApplyAccessStateAsync(id, new SessionAccessState(Full));

        Assert.False(result.Applied);
        Assert.Contains("did not confirm", result.Reason);
        Assert.Equal(Ask, _store.FindSession(id)!.AccessLevel);

        _cliAnswersModes = true;
        var late = Assert.Single(Written("set_permission_mode"))["request_id"]!.GetValue<string>();
        Assert.True(_service.ProcessOutputLine(id, ModeResponse(late, "bypassPermissions", null)));
        await WaitUntil(() => Written("set_permission_mode").Count == 2);
        Assert.Equal("acceptEdits", Written("set_permission_mode")[1]["request"]!["mode"]!.GetValue<string>());
        Assert.Equal(Ask, _service.GetAccessState(id)!.AccessLevel);
    }

    [Fact]
    public void A_refused_reconcile_tries_once_then_records_the_level_the_cli_really_runs_at()
    {
        // Persisted full while the CLI runs acceptEdits: the incident's state after its refused raise.
        var id = Start(new SessionAccessState(Full));
        _cliModeError = mode => mode == "bypassPermissions" ? BypassNotLaunched : null;
        _written.Clear();

        for (var turn = 0; turn < 3; turn++)
            Assert.True(_service.ProcessOutputLine(id,
                """{"type":"system","subtype":"init","session_id":"claude-1","permissionMode":"acceptEdits","tools":[]}"""));

        var attempt = Assert.Single(Written("set_permission_mode"));
        Assert.Equal("bypassPermissions", attempt["request"]!["mode"]!.GetValue<string>());
        Assert.Equal(Ask, _service.GetAccessState(id)!.AccessLevel);
        Assert.Equal(Ask, _store.FindSession(id)!.AccessLevel);
        Assert.Equal("acceptEdits", _service.GetSession(id).Info!.PermissionMode);
    }

    // Fail closed: a CLI running above the level the person set, that will not switch down, is
    // stopped; the recorded level stands for its next launch. A mode that is not a level counts as above.
    [Theory]
    [InlineData(Ask, "bypassPermissions", "acceptEdits")]
    [InlineData(Plan, "auto", "plan")]
    [InlineData(Ask, "someFutureMode", "acceptEdits")]
    public async Task A_cli_above_its_level_that_will_not_switch_down_is_stopped_at_the_recorded_level(
        string level, string reported, string expected)
    {
        var id = Start(new SessionAccessState(level));
        _cliModeError = _ => "no";
        _written.Clear();

        Assert.True(_service.ProcessOutputLine(id,
            $$"""{"type":"system","subtype":"init","session_id":"claude-1","permissionMode":"{{reported}}","tools":[]}"""));
        await WaitUntil(() => _service.GetSession(id).Info?.Status.ToString() == "Stopped");

        var attempt = Assert.Single(Written("set_permission_mode"));
        Assert.Equal(expected, attempt["request"]!["mode"]!.GetValue<string>());
        var record = _store.FindSession(id)!;
        Assert.Equal(level, record.AccessLevel);
        Assert.Equal(SessionStopReasons.AboveAccessLevel, record.StopReason);
        Assert.Equal(level, _service.GetAccessState(id)!.AccessLevel);
        var notice = Assert.Single(Live(), e => e.Type == "error");
        Assert.Contains("Stopped this session", notice.Content);
        Assert.Contains($"'{reported}'", notice.Content);
        Assert.Contains("Claude Code said: \"no\"", notice.Content);
        Assert.False(_service.ProcessOutputLine(id,
            $$"""{"type":"system","subtype":"init","session_id":"claude-1","permissionMode":"{{reported}}","tools":[]}"""));
    }

    [Fact]
    public async Task A_cli_that_switches_down_when_asked_keeps_running()
    {
        var id = Start(new SessionAccessState(Ask));
        _written.Clear();

        Assert.True(_service.ProcessOutputLine(id,
            """{"type":"system","subtype":"init","session_id":"claude-1","permissionMode":"bypassPermissions","tools":[]}"""));
        await Task.Delay(100);

        Assert.Single(Written("set_permission_mode"));
        Assert.NotEqual("Stopped", _service.GetSession(id).Info!.Status.ToString());
        Assert.Equal("acceptEdits", _service.GetSession(id).Info!.PermissionMode);
        Assert.DoesNotContain(Live(), e => e.Type == "error");
    }

    [Fact]
    public void A_refused_plan_exit_stays_in_plan()
    {
        var id = Start(new SessionAccessState(Plan, AccessLevelBeforePlan: Full));
        _cliModeError = mode => mode == "bypassPermissions" ? BypassNotLaunched : null;
        Assert.True(_service.ProcessOutputLine(id, CanUseTool("exit", "ExitPlanMode", new { plan = "1. do it" })));

        Assert.Equal(QuestionAnswerResult.Allowed, _service.SubmitQuestionAnswer(id,
            new SessionQuestionAnswer { RequestId = "exit", Decision = SessionPermissionDecisions.AllowOnce }));

        Assert.Single(Written("set_permission_mode"));
        Assert.Equal(new SessionAccessState(Plan, AccessLevelBeforePlan: Full), _service.GetAccessState(id));
        Assert.Equal(Plan, _store.FindSession(id)!.AccessLevel);
    }

    [Fact]
    public async Task Leaving_plan_stays_within_the_cap_and_is_never_remembered_for_the_session()
    {
        var id = Start(new SessionAccessState(Plan, Auto, AccessLevelBeforePlan: Full));
        Assert.True(_service.ProcessOutputLine(id, CanUseTool("exit", "ExitPlanMode", new { plan = "1. do it" })));

        Assert.Equal(QuestionAnswerResult.Allowed, _service.SubmitQuestionAnswer(id,
            new SessionQuestionAnswer { RequestId = "exit", Decision = SessionPermissionDecisions.AllowSession }));
        await Task.Delay(100);
        Assert.Equal(Auto, _service.GetAccessState(id)!.AccessLevel);

        // Back in plan (anyone may lower): leaving again needs a person again.
        Assert.True(_service.SetAccessState(id, _service.GetAccessState(id)! with { AccessLevel = Plan, AccessLevelBeforePlan = Auto }));
        Assert.True(_service.ProcessOutputLine(id, CanUseTool("exit-2", "ExitPlanMode", new { plan = "2. more" })));
        Assert.Equal("exit-2", Assert.Single(_service.GetPendingQuestions(id)).RequestId);
    }

    [Fact]
    public void A_stopped_session_change_is_persisted_for_its_next_launch()
    {
        _store.SaveSession(Record("stopped", Full));

        Assert.True(_service.SetAccessState("stopped", new SessionAccessState(Ask)));

        Assert.Equal(Ask, _store.FindSession("stopped")!.AccessLevel);
        Assert.Empty(_written);
    }

    // ---- can_use_tool: park, answer, control_response ----

    [Fact]
    public void Captured_ask_parks_and_allow_session_answers_exactly_as_the_cli_accepted()
    {
        var lines = Fixture("allow-session-then-repeat-bash");
        var request = JsonNode.Parse(lines[1])!;
        var requestId = request["request_id"]!.GetValue<string>();
        var id = Start(new SessionAccessState(Ask));
        Assert.True(_service.ProcessOutputLine(id, lines[0]));
        var records = _store.Messages.Count;

        Assert.True(_service.ProcessOutputLine(id, lines[1]));

        var parked = Assert.Single(_service.GetPendingQuestions(id));
        Assert.Equal(PendingSessionQuestion.PermissionKind, parked.Kind);
        Assert.Equal(requestId, parked.RequestId);
        Assert.Equal("Bash", parked.ToolName);
        Assert.Equal("toolu_01UTW9gEtEYsKqgjsvvJ3ffn", parked.ToolUseId);
        Assert.Equal(TimeSpan.FromMinutes(30), parked.ExpiresAt - parked.AskedAt);
        // The card shows the exact command; the model's own description travels apart.
        Assert.Equal(request["request"]!["input"]!["command"]!.GetValue<string>(), parked.Permission!.Summary);
        Assert.Equal(request["request"]!["description"]!.GetValue<string>(), parked.Permission.Description);
        Assert.Equal("node -e \"console.log(6*7)\"", parked.Permission.Input.GetProperty("command").GetString());
        Assert.Equal(SessionPermissionDecisions.All, parked.Permission.Options);
        Assert.Equal(records, _store.Messages.Count); // control state, never a transcript record
        Assert.Contains(Live(), e => e is { Type: "question", Kind: PendingSessionQuestion.PermissionKind } && e.RequestId == requestId);
        Assert.Equal(SessionWorkStates.AwaitingAnswer, SessionTurnWorkState(id));

        _written.Clear();
        var result = _service.SubmitQuestionAnswer(id, new SessionQuestionAnswer
            { RequestId = requestId, Decision = SessionPermissionDecisions.AllowSession });

        Assert.Equal(QuestionAnswerResult.Allowed, result);
        var response = Assert.Single(_written.Select(json => JsonNode.Parse(json)!), n => n["type"]!.GetValue<string>() == "control_response");
        Assert.Equal("success", response["response"]!["subtype"]!.GetValue<string>());
        Assert.Equal(requestId, response["response"]!["request_id"]!.GetValue<string>());
        // Byte for byte what the capture sent CLI 2.1.318, after which the repeated command ran
        // without a second ask and nothing was written to the project's settings.
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(Fixture("allow-session-response")[0]), response["response"]!["response"]),
            response.ToJsonString());
        Assert.Empty(_service.GetPendingQuestions(id));
        Assert.Contains(Live(), e => e is { Type: "question_resolved" } && e.RequestId == requestId && e.Content == "allow_session" && e.Decision == "allow_session" && e.Kind == "permission");

        // The rest of the capture: the second, identical command arrives with no ask at all.
        foreach (var line in lines.Skip(2)) Assert.True(_service.ProcessOutputLine(id, line));
        Assert.Empty(_service.GetPendingQuestions(id));
    }

    [Fact]
    public void Deny_answers_with_the_denial_the_model_reads()
    {
        var lines = Fixture("can-use-tool-acceptedits-bash");
        var id = Start(new SessionAccessState(Ask));
        Assert.True(_service.ProcessOutputLine(id, lines[1]));
        var requestId = Assert.Single(_service.GetPendingQuestions(id)).RequestId;
        _written.Clear();

        Assert.Equal(QuestionAnswerResult.Denied, _service.SubmitQuestionAnswer(id,
            new SessionQuestionAnswer { RequestId = requestId, Decision = SessionPermissionDecisions.Deny }));

        var verdict = Response(requestId);
        Assert.Equal("deny", verdict["behavior"]!.GetValue<string>());
        Assert.Contains("denied 'Bash'", verdict["message"]!.GetValue<string>());
        Assert.Contains(Live(), e => e is { Type: "question_resolved" } && e.RequestId == requestId && e.Content == "deny" && e.Decision == "deny" && e.Kind == "permission");
    }

    [Fact]
    public void Allow_once_runs_the_call_and_remembers_nothing()
    {
        var lines = Fixture("can-use-tool-acceptedits-bash");
        var id = Start(new SessionAccessState(Ask));
        Assert.True(_service.ProcessOutputLine(id, lines[1]));
        var requestId = Assert.Single(_service.GetPendingQuestions(id)).RequestId;

        Assert.Equal(QuestionAnswerResult.Allowed, _service.SubmitQuestionAnswer(id,
            new SessionQuestionAnswer { RequestId = requestId, Decision = SessionPermissionDecisions.AllowOnce }));

        var verdict = Response(requestId);
        Assert.Equal("allow", verdict["behavior"]!.GetValue<string>());
        Assert.Null(verdict["updatedPermissions"]);
        Assert.Contains(Live(), e => e is { Type: "question_resolved" } && e.RequestId == requestId && e.Content == "allow_once" && e.Decision == "allow_once" && e.Kind == "permission");

        // Asked again, it parks again.
        Assert.True(_service.ProcessOutputLine(id, lines[1].Replace(requestId, "second-ask")));
        Assert.Equal("second-ask", Assert.Single(_service.GetPendingQuestions(id)).RequestId);
    }

    [Fact]
    public void An_unattended_session_denies_at_once_with_a_visible_notice_and_never_parks()
    {
        var lines = Fixture("can-use-tool-acceptedits-bash");
        var requestId = JsonNode.Parse(lines[1])!["request_id"]!.GetValue<string>();
        var id = Start(new SessionAccessState(Ask, Unattended: true));
        _written.Clear();

        Assert.True(_service.ProcessOutputLine(id, lines[1]));

        Assert.Empty(_service.GetPendingQuestions(id));
        var verdict = Response(requestId);
        Assert.Equal("deny", verdict["behavior"]!.GetValue<string>());
        // The model is told why and where a person changes it.
        Assert.Equal("Denied: this session runs at Ask with nobody to approve 'Bash'. The installation owner can change this in "
            + "RedLeaf Settings > Agent permissions, or raise this session in its Info panel. Continue without it.",
            verdict["message"]!.GetValue<string>());
        var notice = Assert.Single(Live(), e => e.Type == "permission_denied");
        Assert.Equal(verdict["message"]!.GetValue<string>(), notice.Content);
        Assert.Equal(requestId, notice.RequestId);
        Assert.Equal("Bash", notice.ToolName);
        Assert.DoesNotContain(Live(), e => e.Type == "question");
    }

    [Fact]
    public async Task An_unanswered_permission_is_denied_when_it_expires()
    {
        var lines = Fixture("can-use-tool-acceptedits-bash");
        var requestId = JsonNode.Parse(lines[1])!["request_id"]!.GetValue<string>();
        _service.QuestionAnswerTimeout = TimeSpan.FromMilliseconds(100);
        var id = Start(new SessionAccessState(Ask));
        Assert.True(_service.ProcessOutputLine(id, lines[1]));

        await WaitUntil(() => _service.GetPendingQuestions(id).Count == 0);

        var verdict = Response(requestId);
        Assert.Equal("deny", verdict["behavior"]!.GetValue<string>());
        var message = verdict["message"]!.GetValue<string>();
        Assert.StartsWith("Denied: this session runs at Ask and nobody approved 'Bash' within 1 minute.", message);
        Assert.Contains("RedLeaf Settings > Agent permissions", message);
        Assert.Contains("Info panel", message);
        Assert.Contains(Live(), e => e is { Type: "question_resolved", Content: "timeout", Decision: "deny" } && e.RequestId == requestId);
    }

    [Fact]
    public void Allow_session_without_a_cli_suggestion_is_remembered_by_redcompute_for_that_exact_call()
    {
        var id = Start(new SessionAccessState(Ask));
        Assert.True(_service.ProcessOutputLine(id, CanUseTool("r1", "Bash", new { command = "npm test" })));

        Assert.Equal(QuestionAnswerResult.Allowed, _service.SubmitQuestionAnswer(id,
            new SessionQuestionAnswer { RequestId = "r1", Decision = SessionPermissionDecisions.AllowSession }));
        Assert.Null(Response("r1")["updatedPermissions"]);

        Assert.True(_service.ProcessOutputLine(id, CanUseTool("r2", "Bash", new { command = "npm test" })));
        Assert.Empty(_service.GetPendingQuestions(id));
        Assert.Equal("allow", Response("r2")["behavior"]!.GetValue<string>());

        Assert.True(_service.ProcessOutputLine(id, CanUseTool("r3", "Bash", new { command = "rm -rf build" })));
        Assert.Equal("r3", Assert.Single(_service.GetPendingQuestions(id)).RequestId);
    }

    [Fact]
    public void A_permission_takes_only_a_decision_and_a_question_never_takes_one()
    {
        var id = Start(new SessionAccessState(Ask));
        Assert.True(_service.ProcessOutputLine(id, CanUseTool("perm", "Bash", new { command = "make" })));

        Assert.Equal(QuestionAnswerResult.InvalidDecision, _service.SubmitQuestionAnswer(id,
            new SessionQuestionAnswer { RequestId = "perm", PositionalAnswers = ["yes"] }));
        Assert.Single(_service.GetPendingQuestions(id)); // still parked for a real answer
        Assert.Equal(QuestionAnswerResult.RequestNotFound, _service.SubmitQuestionAnswer(id,
            new SessionQuestionAnswer { RequestId = "nothing", Decision = SessionPermissionDecisions.AllowOnce }));
    }

    [Fact]
    public void Approving_exit_plan_mode_returns_to_the_level_plan_was_entered_from()
    {
        var id = Start(new SessionAccessState(Plan, AccessLevelBeforePlan: Auto));
        Assert.True(_service.ProcessOutputLine(id, CanUseTool("exit", "ExitPlanMode", new { plan = "1. do it" })));
        Assert.Equal("ExitPlanMode", Assert.Single(_service.GetPendingQuestions(id)).ToolName);

        Assert.Equal(QuestionAnswerResult.Allowed, _service.SubmitQuestionAnswer(id,
            new SessionQuestionAnswer { RequestId = "exit", Decision = SessionPermissionDecisions.AllowOnce }));

        var setMode = Assert.Single(Response("exit")["updatedPermissions"]!.AsArray());
        Assert.Equal("setMode", setMode!["type"]!.GetValue<string>());
        Assert.Equal("auto", setMode["mode"]!.GetValue<string>());
        Assert.Equal("session", setMode["destination"]!.GetValue<string>());
        Assert.Equal(new SessionAccessState(Auto, AccessLevelSource: SessionAccessLevelSources.Explicit), _service.GetAccessState(id));
        Assert.Equal(Auto, _store.FindSession(id)!.AccessLevel);
        Assert.Equal(SessionAccessLevelSources.Explicit, _store.FindSession(id)!.AccessLevelSource);
    }

    [Fact]
    public void Ask_user_question_still_parks_as_a_question()
    {
        var id = Start(new SessionAccessState(Full));
        Assert.True(_service.ProcessOutputLine(id, CanUseTool("q", "AskUserQuestion", new { questions = Array.Empty<object>() })));

        var parked = Assert.Single(_service.GetPendingQuestions(id));
        Assert.Equal(PendingSessionQuestion.QuestionKind, parked.Kind);
        Assert.Null(parked.Permission);
    }

    // ---- following the policy ----

    // The 2026-11-09 incident: sessions that predated the policy resolved to ask at the 14:35
    // relaunch and stayed there after the owner set defaultLevel full at 15:30, so a delegated
    // worker parked Bash asks nobody answered. A policy-sourced session now follows the default live.
    [Fact]
    public async Task A_legacy_ask_session_follows_a_later_full_default_live_and_its_parked_approval_stays()
    {
        var id = Start(new SessionAccessState(Ask));   // resolved from policy, persisted without a source
        Assert.True(_service.ProcessOutputLine(id, CanUseTool("parked", "Bash", new { command = "dotnet test" })));
        _written.Clear();

        var changed = await Access(Full, id).PolicyChangedAsync();

        Assert.Equal([id], changed.Followed);
        Assert.Empty(changed.Demoted);
        var change = Assert.Single(Written("set_permission_mode"));
        Assert.Equal("bypassPermissions", change["request"]!["mode"]!.GetValue<string>());
        Assert.Equal(Full, _service.GetAccessState(id)!.AccessLevel);
        Assert.Equal(Full, _store.FindSession(id)!.AccessLevel);
        Assert.Equal(SessionAccessLevelSources.Policy, _service.GetAccessState(id)!.Source);
        // The next tool call runs in bypassPermissions; the ask already parked still waits for its answer.
        Assert.Equal("bypassPermissions", _service.GetSession(id).Info!.PermissionMode);
        Assert.Equal("parked", Assert.Single(_service.GetPendingQuestions(id)).RequestId);
        Assert.DoesNotContain(_written, json => json.Contains("\"parked\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_explicit_session_keeps_its_level_when_the_default_changes()
    {
        var id = Start(new SessionAccessState(Ask, AccessLevelSource: SessionAccessLevelSources.Explicit));
        _written.Clear();

        var changed = await Access(Full, id).PolicyChangedAsync();

        Assert.Empty(changed.Followed);
        Assert.Empty(Written("set_permission_mode"));
        Assert.Equal(Ask, _service.GetAccessState(id)!.AccessLevel);
    }

    // Nova's Discord sessions: unattended, capped at auto. A full default lifts them to auto only,
    // and an ask is still denied at once rather than parked.
    [Fact]
    public async Task A_capped_unattended_session_follows_only_up_to_its_cap_and_still_never_parks()
    {
        var id = Start(new SessionAccessState(Ask, Auto, Unattended: true));
        _written.Clear();

        await Access(Full, id).PolicyChangedAsync();

        Assert.Equal("auto", Assert.Single(Written("set_permission_mode"))["request"]!["mode"]!.GetValue<string>());
        Assert.Equal(new SessionAccessState(Auto, Auto, Unattended: true), _service.GetAccessState(id));
        Assert.True(_service.ProcessOutputLine(id, CanUseTool("denied", "Bash", new { command = "rm -rf build" })));
        Assert.Empty(_service.GetPendingQuestions(id));
    }

    [Fact]
    public async Task A_stopped_policy_session_relaunches_at_the_current_default_and_an_explicit_one_keeps_its_level()
    {
        _store.SaveSession(Record("followed", Ask));
        var chosen = Record("chosen", Ask);
        chosen.AccessLevelSource = SessionAccessLevelSources.Explicit;
        _store.SaveSession(chosen);
        var access = Access(Full, "followed", "chosen");

        await access.EnsureLaunchStateAsync(_provider!, "followed");
        await access.EnsureLaunchStateAsync(_provider!, "chosen");
        Assert.True(_service.ResumeSession("followed", Provenance()) is not null, _service.LastStartError);

        Assert.Equal("bypassPermissions", PermissionModeArg(Assert.Single(_launches)));
        Assert.Equal(Full, _store.FindSession("followed")!.AccessLevel);
        Assert.Null(_store.FindSession("followed")!.AccessLevelSource);
        Assert.Equal(Ask, _store.FindSession("chosen")!.AccessLevel);
    }

    private ISessionProvider? _provider;

    /// <summary>The access service over this Claude service, with RedLeaf's policy at <paramref name="defaultLevel"/> and Full enabled.</summary>
    private SessionAccessService Access(string defaultLevel, params string[] sessions)
    {
        var proxy = DispatchProxy.Create<ISessionProvider, ClaudeProviderAdapter>();
        var adapter = (ClaudeProviderAdapter)(object)proxy;
        adapter.Service = _service;
        adapter.Sessions = sessions;
        _provider = proxy;
        var policy = new StaticSessionAccessPolicySource(new SessionAccessPolicy
        {
            DefaultLevel = defaultLevel,
            FullAccess = new FullAccessAcknowledgement(true, 1),
        });
        return new SessionAccessService(policy, () => [proxy], _ => null, (_, _) => { });
    }

    /// <summary>The slice of ClaudeCodeProvider the access service uses, straight onto the service under test.</summary>
    public class ClaudeProviderAdapter : DispatchProxy
    {
        internal ClaudeSessionService Service = null!;
        internal string[] Sessions = [];

        protected override object? Invoke(MethodInfo? method, object?[]? args)
            => SessionProviderProxy.Route(method, args, InvokeProvider);

        private object? InvokeProvider(MethodInfo? method, object?[]? args) => method!.Name switch
        {
            "get_ProviderId" => "claude-code",
            "get_ProviderDisplayName" => "Claude Code",
            "get_SupportedAccessLevels" => ClaudeSessionService.SupportedAccessLevels,
            nameof(ISessionProvider.GetSessions) => Sessions.Select(Unified).ToList(),
            nameof(ISessionProvider.GetSession) => ((UnifiedSessionInfo?)Unified((string)args![0]!), new List<UnifiedMessageRecord>()),
            nameof(ISessionProvider.GetAccessState) => Service.GetAccessState((string)args![0]!),
            nameof(ISessionProvider.ApplyAccessStateAsync) => Service.ApplyAccessStateAsync(
                (string)args![0]!, (SessionAccessState)args[1]!, (CancellationToken)args[2]!),
            nameof(ISessionProvider.StopSessionAsync) => Service.StopSession((string)args![0]!),
            _ => throw new NotSupportedException(method.Name),
        };

        private static UnifiedSessionInfo Unified(string id) => new()
        {
            Id = id, Provider = "claude-code", ProjectName = "p", ProjectPath = "p", Status = SessionStatus.Idle,
        };
    }

    // ---- one-shots ----

    [Theory]
    [InlineData(Full, new[] { "--dangerously-skip-permissions" }, null)]
    [InlineData(Auto, new[] { "--permission-mode", "auto" }, null)]
    [InlineData(Plan, new[] { "--permission-mode", "plan" }, null)]
    [InlineData(Ask, new[] { "--permission-mode", "dontAsk" }, "Read,Glob,Grep,Edit,Write,NotebookEdit")]
    [InlineData(null, new[] { "--permission-mode", "dontAsk" }, "Read,Glob,Grep,Edit,Write,NotebookEdit")]
    public void Oneshot_without_a_tools_grant_runs_at_the_mapped_level(string? level, string[] expected, string? allowed)
    {
        var args = new List<string>();
        ClaudeSessionService.AddAgentArgs(args, null, null, 10, tools: null, allowedTools: null, addDirs: null, accessLevel: level);

        Assert.True(ContainsSequence(args, expected), string.Join(' ', args));
        Assert.Equal(level == Full, args.Contains("--dangerously-skip-permissions"));
        // A one-shot never changes level mid-run: it launches at its level and needs no opt-in.
        Assert.DoesNotContain(AllowBypass, args);
        Assert.Equal(allowed, args.Contains("--allowed-tools") ? args[args.IndexOf("--allowed-tools") + 1] : null);
    }

    [Theory]
    [InlineData(Plan, "plan")]
    [InlineData(Ask, "dontAsk")]
    [InlineData(null, "dontAsk")]
    [InlineData(Auto, "auto")]
    public void Oneshot_with_an_explicit_tools_list_still_runs_at_its_level(string? level, string mode)
    {
        // A tools list narrows which tools exist. It used to skip every permission at any level,
        // so any caller could run Bash unprompted by naming it.
        var args = new List<string>();
        ClaudeSessionService.AddAgentArgs(args, null, null, 10, tools: ["Read", "Bash"], allowedTools: null, addDirs: null, accessLevel: level);

        Assert.DoesNotContain("--dangerously-skip-permissions", args);
        Assert.Equal(mode, args[args.IndexOf("--permission-mode") + 1]);
        Assert.Equal("Read,Bash", args[args.IndexOf("--tools") + 1]);
        if (mode == "dontAsk")
            Assert.Equal("Read", args[args.IndexOf("--allowed-tools") + 1]);
        else
            Assert.DoesNotContain("--allowed-tools", args);
    }

    [Fact]
    public void Oneshot_with_a_tools_list_at_full_skips_permissions()
    {
        var args = new List<string>();
        ClaudeSessionService.AddAgentArgs(args, null, null, 10, tools: ["Read"], allowedTools: ["Read"], addDirs: null, accessLevel: Full);

        Assert.Contains("--dangerously-skip-permissions", args);
        Assert.DoesNotContain("--permission-mode", args);
        Assert.Equal("Read", args[args.IndexOf("--tools") + 1]);
    }

    [Theory]
    [InlineData(Ask, "Permissions: this session runs at Ask.")]
    [InlineData(null, "Permissions: this session runs at Ask.")]
    [InlineData(Auto, "Permissions: this session runs at Auto.")]
    [InlineData(Plan, null)]
    [InlineData(Full, null)]
    public void Oneshot_that_can_be_denied_is_told_why_and_where_to_change_it(string? level, string? opening)
    {
        var args = new List<string>();
        ClaudeSessionService.AddAgentArgs(args, null, null, 10, tools: null, allowedTools: null, addDirs: null, accessLevel: level);

        if (opening is null)
        {
            Assert.DoesNotContain("--append-system-prompt", args);
            return;
        }
        Assert.Single(args, a => a == "--append-system-prompt");
        Assert.DoesNotContain("--system-prompt", args);
        var note = args[args.IndexOf("--append-system-prompt") + 1];
        Assert.StartsWith(opening + " Nobody watches it, so anything that needs approval is denied at once.", note);
        Assert.Contains("RedLeaf Settings > Agent permissions", note);
    }

    [Fact]
    public void Oneshot_with_a_tools_list_below_full_is_told_why_too()
    {
        var args = new List<string>();
        ClaudeSessionService.AddAgentArgs(args, null, null, 10, tools: ["Read"], allowedTools: null, addDirs: null, accessLevel: Ask);

        Assert.Contains("--append-system-prompt", args);
    }

    [Theory]
    [InlineData(Ask, "Read,Glob,Grep,Edit,Write,NotebookEdit")]
    [InlineData(Auto, "Read")]
    [InlineData(Plan, null)]
    [InlineData(Full, "Bash(git status),Read,Bash,WebFetch")]
    public void Oneshot_caller_preapprovals_reach_past_file_tools_only_at_full(string level, string? allowed)
    {
        // nova-session and automation recovery pre-approve Bash and the web tools: below Full
        // that would skip the person (ask) or the classifier (auto) for exactly what they guard.
        var args = new List<string>();
        ClaudeSessionService.AddAgentArgs(args, null, null, 10, tools: null,
            allowedTools: ["Bash(git status)", "Read", "Bash", "WebFetch"], addDirs: null, accessLevel: level);

        Assert.Equal(allowed, args.Contains("--allowed-tools") ? args[args.IndexOf("--allowed-tools") + 1] : null);
    }

    [Fact]
    public void Only_allow_rules_from_the_cli_are_kept_for_the_session()
    {
        var suggestions = JsonDocument.Parse("""
            [
              {"type":"addRules","behavior":"allow","rules":[{"toolName":"Bash","ruleContent":"npm test"}],"destination":"localSettings"},
              {"type":"setMode","mode":"bypassPermissions","destination":"session"},
              {"type":"addDirectories","directories":["C:\\"],"destination":"session"},
              {"type":"addRules","behavior":"deny","rules":[{"toolName":"Bash"}],"destination":"session"},
              "not an object"
            ]
            """).RootElement;

        var kept = ClaudeSessionService.SessionScopedSuggestions(suggestions).Cast<Dictionary<string, object?>>().ToList();

        var rule = Assert.Single(kept);
        Assert.Equal("addRules", ((JsonElement)rule["type"]!).GetString());
        Assert.Equal("session", rule["destination"]);
    }

    [Fact]
    public void A_remembered_approval_without_a_cli_rule_matches_exactly_that_call()
    {
        static JsonElement Input(string json) => JsonDocument.Parse(json).RootElement.Clone();
        var fetch = ClaudeSessionService.SessionAllowRule.For("WebFetch", Input("""{"url":"https://a.test","prompt":"x"}"""));
        var bash = ClaudeSessionService.SessionAllowRule.For("Bash", Input("""{"command":"npm test","description":"tests"}"""));

        Assert.True(fetch.Matches("WebFetch", Input("""{"url":"https://a.test","prompt":"x"}""")));
        Assert.False(fetch.Matches("WebFetch", Input("""{"url":"https://evil.test","prompt":"x"}""")));
        Assert.True(bash.Matches("Bash", Input("""{"command":"npm test","description":"other words"}""")));
        Assert.False(bash.Matches("Bash", Input("""{"command":"npm test; rm -rf ~"}""")));
    }

    // ---- helpers ----

    private const string AllowBypass = "--allow-dangerously-skip-permissions";

    /// <summary>The fake CLI's answer to set_permission_mode, in the shape CLI 2.1.318 sends it.</summary>
    private void AnswerPermissionMode(string sessionId, string json)
    {
        if (!_cliAnswersModes) return;
        var node = JsonNode.Parse(json)!;
        if (node["type"]?.GetValue<string>() != "control_request"
            || node["request"]?["subtype"]?.GetValue<string>() != "set_permission_mode") return;
        var mode = node["request"]!["mode"]!.GetValue<string>();
        _service.ProcessOutputLine(sessionId, ModeResponse(node["request_id"]!.GetValue<string>(), mode, _cliModeError(mode)));
    }

    private static string ModeResponse(string requestId, string mode, string? error) => error is null
        ? JsonSerializer.Serialize(new
        {
            type = "control_response",
            response = new { subtype = "success", request_id = requestId, response = new { mode } },
        })
        : JsonSerializer.Serialize(new
        {
            type = "control_response",
            response = new { subtype = "error", request_id = requestId, error, error_code = "bypass_not_launched" },
        });

    private string Start(SessionAccessState? access)
    {
        using var scope = SessionAccessScope.Push(access);
        var info = _service.StartSession(_root, "haiku", "owner", null, null, "high", null, null, null, Provenance());
        Assert.NotNull(info);
        return info!.Id;
    }

    private static JobProvenance Provenance()
        => JobProvenance.DirectRedCompute("POST", "/ai-session/sessions", new JobBeneficiary("user", "owner"), "request-1", null);

    private ClaudeSessionRecord Record(string id, string? level) => new()
    {
        Id = id, ProjectName = "p", ProjectPath = _root, Status = "Stopped", ClaudeSessionId = "claude-conversation",
        MessageCount = 2, Model = "haiku", AccessLevel = level,
    };

    private string SessionTurnWorkState(string id)
        => SessionWorkStates.Derive(Enum.Parse<SessionStatus>(_service.GetSession(id).Info!.Status.ToString()), _service.GetPendingQuestions(id).Count, []);

    private static string CanUseTool(string requestId, string tool, object input) => JsonSerializer.Serialize(new
    {
        type = "control_request",
        request_id = requestId,
        request = new { subtype = "can_use_tool", tool_name = tool, input, tool_use_id = "toolu_" + requestId },
    });

    private static string[] Fixture(string name)
        => File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Fixtures", "claude-permissions", name + ".jsonl"))
            .Where(line => line.Length > 0).ToArray();

    private List<JsonNode> Written(string subtype)
        => _written.Select(json => JsonNode.Parse(json)!)
            .Where(n => n["type"]?.GetValue<string>() == "control_request" && n["request"]?["subtype"]?.GetValue<string>() == subtype)
            .ToList();

    private JsonNode Response(string requestId)
        => _written.Select(json => JsonNode.Parse(json)!)
            .Single(n => n["type"]?.GetValue<string>() == "control_response"
                && n["response"]!["request_id"]!.GetValue<string>() == requestId)["response"]!["response"]!;

    private List<ClaudeStreamEvent> Live() { lock (_live) return [.. _live]; }

    private static string? PermissionModeArg(ProcessStartInfo startInfo) => ArgAfter(startInfo, "--permission-mode");

    private static string? ArgAfter(ProcessStartInfo startInfo, string flag)
    {
        var args = startInfo.ArgumentList.ToList();
        Assert.Single(args, a => a == flag);
        return args[args.IndexOf(flag) + 1];
    }

    private static bool ContainsSequence(List<string> args, string[] sequence)
        => Enumerable.Range(0, Math.Max(0, args.Count - sequence.Length + 1))
            .Any(i => args.Skip(i).Take(sequence.Length).SequenceEqual(sequence));

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
        lock (_processes)
            foreach (var process in _processes)
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
                process.Dispose();
            }
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { }
    }
}
