using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using RedCompute.Core.Sessions;
using RedCompute.Plugin.Codex;
using RedCompute.PluginSdk;
using Xunit;

namespace RedCompute.Plugin.Codex.Tests;

// Codex app-server approvals under access levels (codex-cli 0.163.0 schema): at ask each request
// parks as a permission card a person answers; unattended it is declined at once; at full it is
// accepted for the session as before; a conversation-only session declines whatever its level.
public sealed class CodexApprovalTests : IDisposable
{
    private readonly List<Process> _processes = [];
    private readonly string _root = Path.Combine(
        Environment.GetEnvironmentVariable("REDLEAF_SCRATCH_DIR") ?? Path.GetTempPath(),
        "redcompute-codex-approvals", Guid.NewGuid().ToString("N"));

    public CodexApprovalTests() => Directory.CreateDirectory(_root);

    private static readonly object Command = new
    {
        threadId = "thread-1", turnId = "turn-1", itemId = "item-cmd", command = "npm test", cwd = "C:\\repo",
        reason = "Run the tests",
    };

    [Fact]
    public async Task Ask_parks_an_approval_as_a_permission_and_a_person_answers_it()
    {
        var h = new Harness(this, SessionAccessLevels.Ask);

        await h.RequestAsync(7, "item/commandExecution/requestApproval", Command);

        var parked = Assert.Single(h.Service.GetPendingQuestions("session-1"));
        Assert.Equal(PendingSessionQuestion.PermissionKind, parked.Kind);
        Assert.Equal("approval-7", parked.RequestId);
        Assert.Equal("commandExecution", parked.ToolName);
        Assert.Equal("item-cmd", parked.ToolUseId);
        Assert.Equal("npm test", parked.Permission!.Summary);
        Assert.Equal("C:\\repo", parked.Permission.Cwd);
        Assert.Equal(TimeSpan.FromMinutes(30), parked.ExpiresAt - parked.AskedAt);
        Assert.Equal("Waiting", h.Info.Status);
        Assert.Contains(h.Events, e => e is { Type: "question", Kind: PendingSessionQuestion.PermissionKind, RequestId: "approval-7" });
        Assert.DoesNotContain(h.Store.Messages, m => m.EventType is "question" or "question_resolved");
        Assert.Empty(await h.ResponsesAsync(TimeSpan.FromMilliseconds(300)));

        Assert.Equal(QuestionAnswerResult.Allowed,
            h.Service.SubmitApprovalDecision("session-1", "approval-7", SessionPermissionDecisions.AllowSession));

        var response = Assert.Single(await h.ResponsesAsync());
        Assert.Equal(7, response["id"]!.GetValue<int>());
        Assert.Equal("acceptForSession", response["result"]!["decision"]!.GetValue<string>());
        Assert.Empty(h.Service.GetPendingQuestions("session-1"));
        Assert.Contains(h.Events, e => e is { Type: "question_resolved", RequestId: "approval-7", Content: "allow_session", Decision: "allow_session" });
        Assert.Equal("Active", h.Info.Status);
    }

    [Theory]
    [InlineData(SessionPermissionDecisions.AllowOnce, "accept", QuestionAnswerResult.Allowed)]
    [InlineData(SessionPermissionDecisions.Deny, "decline", QuestionAnswerResult.Denied)]
    public async Task Each_decision_reaches_the_app_server_in_its_own_words(string decision, string expected, QuestionAnswerResult result)
    {
        var h = new Harness(this, SessionAccessLevels.Ask);
        await h.RequestAsync(3, "item/fileChange/requestApproval", new { threadId = "thread-1", turnId = "turn-1", itemId = "item-patch", grantRoot = "C:\\repo" });

        Assert.Equal(result, h.Service.SubmitApprovalDecision("session-1", "approval-3", decision));

        Assert.Equal(expected, Assert.Single(await h.ResponsesAsync())["result"]!["decision"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_legacy_session_with_no_level_fails_closed_to_ask()
    {
        var h = new Harness(this, level: null);

        await h.RequestAsync(4, "item/commandExecution/requestApproval", Command);

        Assert.Single(h.Service.GetPendingQuestions("session-1"));
    }

    [Fact]
    public async Task Unattended_declines_at_once_with_a_notice_and_never_parks()
    {
        var h = new Harness(this, SessionAccessLevels.Ask, unattended: true);

        await h.RequestAsync(5, "item/commandExecution/requestApproval", Command);

        Assert.Empty(h.Service.GetPendingQuestions("session-1"));
        Assert.Equal("decline", Assert.Single(await h.ResponsesAsync())["result"]!["decision"]!.GetValue<string>());
        var notice = Assert.Single(h.Events, e => e.Type == "permission_denied");
        Assert.Equal("approval-5", notice.RequestId);
        Assert.Contains("unattended", notice.Content);
        Assert.DoesNotContain(h.Events, e => e.Type == "question");
        Assert.NotEqual("Waiting", h.Info.Status);
    }

    [Fact]
    public async Task Full_keeps_todays_behaviour()
    {
        var h = new Harness(this, SessionAccessLevels.Full);

        await h.RequestAsync(6, "item/commandExecution/requestApproval", Command);
        await h.RequestAsync(8, "item/permissions/requestApproval", new { permissions = new { network = new { enabled = true } } });

        Assert.Empty(h.Service.GetPendingQuestions("session-1"));
        var responses = await h.ResponsesAsync(count: 2);
        Assert.Equal("acceptForSession", responses.Single(r => r["id"]!.GetValue<int>() == 6)["result"]!["decision"]!.GetValue<string>());
        Assert.Equal("session", responses.Single(r => r["id"]!.GetValue<int>() == 8)["result"]!["scope"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(SessionAccessLevels.Ask)]
    [InlineData(SessionAccessLevels.Full)]
    public async Task Conversation_only_declines_whatever_the_level(string level)
    {
        var h = new Harness(this, level, profile: SessionExecutionProfile.ConversationOnly);

        await h.RequestAsync(9, "item/commandExecution/requestApproval", Command);

        Assert.Empty(h.Service.GetPendingQuestions("session-1"));
        Assert.Equal("decline", Assert.Single(await h.ResponsesAsync())["result"]!["decision"]!.GetValue<string>());
    }

    [Fact]
    public async Task An_unanswered_approval_is_declined_when_it_expires()
    {
        var h = new Harness(this, SessionAccessLevels.Ask);
        h.Service.ApprovalTimeout = TimeSpan.FromMilliseconds(100);

        await h.RequestAsync(10, "item/commandExecution/requestApproval", Command);

        Assert.Equal("decline", Assert.Single(await h.ResponsesAsync())["result"]!["decision"]!.GetValue<string>());
        Assert.Empty(h.Service.GetPendingQuestions("session-1"));
        Assert.Contains(h.Events, e => e is { Type: "question_resolved", RequestId: "approval-10", Content: "timeout", Decision: "deny" });
    }

    [Fact]
    public async Task Approval_answers_are_routed_only_to_approvals()
    {
        var h = new Harness(this, SessionAccessLevels.Ask);
        await h.RequestAsync(11, "item/commandExecution/requestApproval", Command);

        Assert.Null(h.Service.SubmitApprovalDecision("session-1", "item-1", SessionPermissionDecisions.AllowOnce));
        Assert.Null(h.Service.SubmitApprovalDecision("another-session", "approval-11", SessionPermissionDecisions.AllowOnce));
        Assert.Equal(QuestionAnswerResult.InvalidDecision, h.Service.SubmitApprovalDecision("session-1", "approval-11", "maybe"));
        Assert.Single(h.Service.GetPendingQuestions("session-1"));
    }

    [Fact]
    public async Task A_live_change_to_full_applies_to_the_next_approval()
    {
        var h = new Harness(this, SessionAccessLevels.Ask);

        Assert.True(h.Service.SetAccessState("session-1", new SessionAccessState(SessionAccessLevels.Full)));
        await h.RequestAsync(12, "item/commandExecution/requestApproval", Command);

        Assert.Empty(h.Service.GetPendingQuestions("session-1"));
        Assert.Equal("acceptForSession", Assert.Single(await h.ResponsesAsync())["result"]!["decision"]!.GetValue<string>());
        Assert.Equal(SessionAccessLevels.Full, h.Service.GetAccessState("session-1")!.AccessLevel);
    }

    [Theory]
    [InlineData("item/commandExecution/requestApproval", SessionPermissionDecisions.AllowOnce, """{"decision":"accept"}""")]
    [InlineData("item/commandExecution/requestApproval", SessionPermissionDecisions.AllowSession, """{"decision":"acceptForSession"}""")]
    [InlineData("item/commandExecution/requestApproval", SessionPermissionDecisions.Deny, """{"decision":"decline"}""")]
    [InlineData("item/fileChange/requestApproval", SessionPermissionDecisions.AllowSession, """{"decision":"acceptForSession"}""")]
    [InlineData("applyPatchApproval", SessionPermissionDecisions.AllowOnce, """{"decision":"approved"}""")]
    [InlineData("execCommandApproval", SessionPermissionDecisions.AllowSession, """{"decision":"approved_for_session"}""")]
    [InlineData("execCommandApproval", SessionPermissionDecisions.Deny, """{"decision":"denied"}""")]
    [InlineData("item/permissions/requestApproval", SessionPermissionDecisions.AllowOnce, """{"permissions":{"network":{"enabled":true}},"scope":"turn"}""")]
    [InlineData("item/permissions/requestApproval", SessionPermissionDecisions.AllowSession, """{"permissions":{"network":{"enabled":true}},"scope":"session"}""")]
    [InlineData("item/permissions/requestApproval", SessionPermissionDecisions.Deny, """{"permissions":{},"scope":"turn"}""")]
    public void Decisions_map_to_each_requests_schema(string method, string decision, string expected)
    {
        using var request = JsonDocument.Parse("""{"permissions":{"network":{"enabled":true}}}""");

        var response = JsonSerializer.Serialize(CodexInteractiveService.ApprovalResponse(method, request.RootElement, decision));

        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(expected), JsonNode.Parse(response)), response);
    }

    public void Dispose()
    {
        foreach (var process in _processes)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            process.Dispose();
        }
        try { Directory.Delete(_root, true); } catch { }
    }

    // The production service and session, with a real connection over a stand-in app-server that
    // appends every line written to its stdin to a file, so responses are read back as sent.
    private sealed class Harness
    {
        private readonly string _capture;
        public CodexInteractiveService Service { get; }
        public RecordingStore Store { get; } = new();
        public List<CodexStreamEvent> Events { get; } = [];
        public CodexSessionInfo Info { get; }

        public Harness(CodexApprovalTests owner, string? level, bool unattended = false, string profile = SessionExecutionProfile.Default)
        {
            _capture = Path.Combine(owner._root, Guid.NewGuid().ToString("N") + ".jsonl");
            File.WriteAllText(_capture, "");
            Service = new CodexInteractiveService(new CodexConfig(), Store, null!, null!, null!, null!, null!, () => null, (_, _) => { });
            Service.StreamEvent += (_, e) => { lock (Events) Events.Add(e); };
            var sessionType = typeof(CodexInteractiveService).GetNestedType("ManagedSession", BindingFlags.NonPublic)!;
            var session = Activator.CreateInstance(sessionType, nonPublic: true)!;
            Info = new CodexSessionInfo
            {
                Id = "session-1", ProjectName = "Test", ProjectPath = ".", ThreadId = "thread-1", Status = "Active",
                AccessLevel = level, Unattended = unattended, ExecutionProfile = profile,
            };
            sessionType.GetProperty("Info")!.SetValue(session, Info);
            var start = TestProcesses.AppendStdinTo(_capture, "CODEX_CAPTURE");
            start.CreateNoWindow = true;
            start.UseShellExecute = false;
            start.RedirectStandardInput = true;
            start.RedirectStandardOutput = true;
            start.RedirectStandardError = true;
            var process = Process.Start(start)!;
            owner._processes.Add(process);
            var connection = (CodexAppServerConnection)Activator.CreateInstance(typeof(CodexAppServerConnection),
                BindingFlags.NonPublic | BindingFlags.Instance, null, [process, (Action<string, Guid?>)((_, _) => { })], null)!;
            sessionType.GetProperty("Connection")!.SetValue(session, connection);
            sessionType.GetProperty("ActiveTurnId")!.SetValue(session, "turn-1");
            var sessions = typeof(CodexInteractiveService).GetField("_sessions", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(Service)!;
            sessions.GetType().GetMethod("TryAdd")!.Invoke(sessions, ["session-1", session]);
        }

        public Task RequestAsync(int id, string method, object @params)
        {
            var rpcId = JsonSerializer.SerializeToElement(id);
            var payload = JsonSerializer.SerializeToElement(@params);
            return (Task)typeof(CodexInteractiveService).GetMethod("OnServerRequestAsync", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(Service, ["session-1", rpcId, method, payload])!;
        }

        /// <summary>The JSON-RPC responses written so far, waiting until <paramref name="count"/> arrive (or the wait ends).</summary>
        public async Task<List<JsonNode>> ResponsesAsync(TimeSpan? wait = null, int count = 1)
        {
            var deadline = DateTime.UtcNow + (wait ?? TimeSpan.FromSeconds(15));
            List<JsonNode> lines;
            do
            {
                lines = Read();
                if (wait is null && lines.Count >= count) break;
                await Task.Delay(50);
            } while (DateTime.UtcNow < deadline);
            return lines;
        }

        private List<JsonNode> Read()
        {
            try
            {
                using var stream = new FileStream(_capture, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                return reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(line => JsonNode.Parse(line)!).Where(node => node["result"] is not null || node["error"] is not null).ToList();
            }
            catch (IOException) { return []; }
        }
    }

    private sealed class RecordingStore : ICodexSessionStore
    {
        public List<CodexMessageRecord> Messages { get; } = [];
        public List<CodexSessionRecord> GetActiveSessions() => [];
        public List<CodexSessionRecord> GetSessionsWithStopReason(string status, string stopReason) => [];
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
}
