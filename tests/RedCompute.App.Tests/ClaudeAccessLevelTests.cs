using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
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

    [Fact]
    public void A_refused_reconcile_never_raises_the_level_to_a_higher_reported_mode()
    {
        var id = Start(new SessionAccessState(Ask));
        _cliModeError = _ => "no";
        _written.Clear();

        for (var turn = 0; turn < 3; turn++)
            Assert.True(_service.ProcessOutputLine(id,
                """{"type":"system","subtype":"init","session_id":"claude-1","permissionMode":"bypassPermissions","tools":[]}"""));

        Assert.Single(Written("set_permission_mode"));
        Assert.Equal(Ask, _service.GetAccessState(id)!.AccessLevel);
        Assert.Equal(Ask, _store.FindSession(id)!.AccessLevel);
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
        Assert.Equal(request["request"]!["description"]!.GetValue<string>(), parked.Permission!.Summary);
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
        Assert.Contains("unattended", verdict["message"]!.GetValue<string>());
        var notice = Assert.Single(Live(), e => e.Type == "permission_denied");
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
        Assert.Contains("No one approved 'Bash'", verdict["message"]!.GetValue<string>());
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
        Assert.Equal(new SessionAccessState(Auto), _service.GetAccessState(id));
        Assert.Equal(Auto, _store.FindSession(id)!.AccessLevel);
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
    [InlineData(Plan)]
    [InlineData(Ask)]
    [InlineData(null)]
    public void Oneshot_with_an_explicit_tools_grant_keeps_todays_flags(string? level)
    {
        var args = new List<string>();
        ClaudeSessionService.AddAgentArgs(args, null, null, 10, tools: ["Read"], allowedTools: ["Read"], addDirs: null, accessLevel: level);

        Assert.Contains("--dangerously-skip-permissions", args);
        Assert.DoesNotContain("--permission-mode", args);
        Assert.Equal("Read", args[args.IndexOf("--tools") + 1]);
    }

    [Fact]
    public void Oneshot_ask_merges_caller_preapprovals_with_the_file_tools()
    {
        var args = new List<string>();
        ClaudeSessionService.AddAgentArgs(args, null, null, 10, tools: null, allowedTools: ["Bash(git status)", "Read"], addDirs: null, accessLevel: Ask);

        Assert.Single(args, a => a == "--allowed-tools");
        Assert.Equal("Read,Glob,Grep,Edit,Write,NotebookEdit,Bash(git status)", args[args.IndexOf("--allowed-tools") + 1]);
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
