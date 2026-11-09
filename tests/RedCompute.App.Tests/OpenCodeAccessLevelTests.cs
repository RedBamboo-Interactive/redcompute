using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using RedCompute.Core.Sessions;
using RedCompute.Plugin.OpenCode;
using Xunit;

namespace RedCompute.App.Tests;

// OpenCode's ACP session/request_permission under access levels: at ask it parks as a permission
// card offering what its option kinds allow; an unattended session rejects at once and never
// parks (the 2026-09-24 detached-session deadlock); full selects allow_once as before.
public sealed class OpenCodeAccessLevelTests : IDisposable
{
    private readonly List<Process> _processes = [];
    private readonly string _root = Path.Combine(
        Environment.GetEnvironmentVariable("REDLEAF_SCRATCH_DIR") ?? Path.GetTempPath(),
        "redcompute-opencode-access", Guid.NewGuid().ToString("N"));

    public OpenCodeAccessLevelTests() => Directory.CreateDirectory(_root);

    private static readonly object[] AllKinds =
    [
        new { optionId = "once", name = "Allow once", kind = "allow_once" },
        new { optionId = "always", name = "Always allow", kind = "allow_always" },
        new { optionId = "reject", name = "Reject", kind = "reject_once" },
    ];

    [Fact]
    public async Task Ask_parks_the_request_as_a_permission_and_a_person_answers_it()
    {
        var h = new Harness(this, SessionAccessLevels.Ask);

        await h.RequestAsync(21, AllKinds);

        var parked = Assert.Single(h.Service.GetPendingQuestions("session-1"));
        Assert.Equal(PendingSessionQuestion.PermissionKind, parked.Kind);
        Assert.Equal("permission-21", parked.RequestId);
        Assert.Equal("execute", parked.ToolName);
        Assert.Equal("call-1", parked.ToolUseId);
        Assert.Equal("Run npm test", parked.Permission!.Summary);
        Assert.Equal("npm test", parked.Permission.Input.GetProperty("command").GetString());
        Assert.Equal(SessionPermissionDecisions.All, parked.Permission.Options);
        Assert.Equal(TimeSpan.FromMinutes(30), parked.ExpiresAt - parked.AskedAt);
        Assert.Contains(h.Events, e => e is { Type: "question", Kind: PendingSessionQuestion.PermissionKind, RequestId: "permission-21" });
        Assert.Empty(await h.ResponsesAsync(TimeSpan.FromMilliseconds(300)));

        Assert.Equal(QuestionAnswerResult.Allowed, h.Service.SubmitQuestionAnswer("session-1",
            new SessionQuestionAnswer { RequestId = "permission-21", Decision = SessionPermissionDecisions.AllowSession }));

        var response = Assert.Single(await h.ResponsesAsync());
        Assert.Equal(21, response["id"]!.GetValue<int>());
        Assert.Equal("selected", response["result"]!["outcome"]!["outcome"]!.GetValue<string>());
        Assert.Equal("always", response["result"]!["outcome"]!["optionId"]!.GetValue<string>());
        Assert.Empty(h.Service.GetPendingQuestions("session-1"));
        Assert.Contains(h.Events, e => e is { Type: "question_resolved", RequestId: "permission-21", Content: "allow_session", Decision: "allow_session" });
    }

    [Theory]
    [InlineData(SessionPermissionDecisions.AllowOnce, "once")]
    [InlineData(SessionPermissionDecisions.Deny, "reject")]
    public async Task Each_decision_selects_its_option(string decision, string optionId)
    {
        var h = new Harness(this, SessionAccessLevels.Ask);
        await h.RequestAsync(22, AllKinds);

        h.Service.SubmitQuestionAnswer("session-1", new SessionQuestionAnswer { RequestId = "permission-22", Decision = decision });

        Assert.Equal(optionId, Assert.Single(await h.ResponsesAsync())["result"]!["outcome"]!["optionId"]!.GetValue<string>());
    }

    [Fact]
    public async Task Only_the_offered_decisions_are_accepted()
    {
        var h = new Harness(this, SessionAccessLevels.Ask);
        await h.RequestAsync(23, [new { optionId = "once", name = "Allow once", kind = "allow_once" }, new { optionId = "no", name = "Reject", kind = "reject_always" }]);

        var parked = Assert.Single(h.Service.GetPendingQuestions("session-1"));
        Assert.Equal([SessionPermissionDecisions.AllowOnce, SessionPermissionDecisions.Deny], parked.Permission!.Options);
        Assert.Equal(QuestionAnswerResult.InvalidDecision, h.Service.SubmitQuestionAnswer("session-1",
            new SessionQuestionAnswer { RequestId = "permission-23", Decision = SessionPermissionDecisions.AllowSession }));
        Assert.Equal(QuestionAnswerResult.InvalidDecision, h.Service.SubmitQuestionAnswer("session-1",
            new SessionQuestionAnswer { RequestId = "permission-23", PositionalAnswers = ["yes"] }));
        Assert.Single(h.Service.GetPendingQuestions("session-1"));

        Assert.Equal(QuestionAnswerResult.Denied, h.Service.SubmitQuestionAnswer("session-1",
            new SessionQuestionAnswer { RequestId = "permission-23", Decision = SessionPermissionDecisions.Deny }));
        Assert.Equal("no", Assert.Single(await h.ResponsesAsync())["result"]!["outcome"]!["optionId"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(SessionAccessLevels.Ask)]
    [InlineData(null)]   // a legacy row fails closed to ask, and unattended still never parks
    public async Task Unattended_rejects_at_once_and_never_parks(string? level)
    {
        var h = new Harness(this, level, unattended: true);

        await h.RequestAsync(24, AllKinds);

        Assert.Empty(h.Service.GetPendingQuestions("session-1"));
        Assert.Equal("reject", Assert.Single(await h.ResponsesAsync())["result"]!["outcome"]!["optionId"]!.GetValue<string>());
        var notice = Assert.Single(h.Events, e => e.Type == "permission_denied");
        Assert.Equal("permission-24", notice.RequestId);
        Assert.Contains("unattended", notice.Content);
        Assert.DoesNotContain(h.Events, e => e.Type == "question");
    }

    [Fact]
    public async Task Full_keeps_todays_allow_once()
    {
        var h = new Harness(this, SessionAccessLevels.Full);

        await h.RequestAsync(25, AllKinds);

        Assert.Empty(h.Service.GetPendingQuestions("session-1"));
        Assert.Equal("once", Assert.Single(await h.ResponsesAsync())["result"]!["outcome"]!["optionId"]!.GetValue<string>());
    }

    [Fact]
    public async Task An_unanswered_permission_is_rejected_when_it_expires()
    {
        var h = new Harness(this, SessionAccessLevels.Ask);
        h.Service.PermissionTimeout = TimeSpan.FromMilliseconds(100);

        await h.RequestAsync(26, AllKinds);

        Assert.Equal("reject", Assert.Single(await h.ResponsesAsync())["result"]!["outcome"]!["optionId"]!.GetValue<string>());
        Assert.Empty(h.Service.GetPendingQuestions("session-1"));
        Assert.Contains(h.Events, e => e is { Type: "question_resolved", RequestId: "permission-26", Content: "timeout", Decision: "deny" });
    }

    [Theory]
    [InlineData(SessionPermissionDecisions.AllowOnce, """{"outcome":{"outcome":"selected","optionId":"once"}}""")]
    [InlineData(SessionPermissionDecisions.AllowSession, """{"outcome":{"outcome":"selected","optionId":"always"}}""")]
    [InlineData(SessionPermissionDecisions.Deny, """{"outcome":{"outcome":"selected","optionId":"reject"}}""")]
    public void Decisions_map_to_acp_outcomes(string decision, string expected)
    {
        var options = JsonSerializer.SerializeToElement(AllKinds);

        var outcome = JsonSerializer.Serialize(OpenCodeSessionService.PermissionOutcome(options, decision));

        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(expected), JsonNode.Parse(outcome)), outcome);
    }

    [Fact]
    public void A_decision_with_no_matching_option_cancels()
    {
        var options = JsonSerializer.SerializeToElement(new[] { new { optionId = "once", kind = "allow_once" } });

        var outcome = JsonSerializer.Serialize(OpenCodeSessionService.PermissionOutcome(options, SessionPermissionDecisions.Deny));

        Assert.Equal("""{"outcome":{"outcome":"cancelled"}}""", outcome);
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

    // The production service and session over a stand-in OpenCode whose stdin is appended to a
    // file, so the ACP responses are read back exactly as written.
    private sealed class Harness
    {
        private readonly string _capture;
        private readonly OpenCodeSessionService.ManagedSession _session;
        public OpenCodeSessionService Service { get; }
        public List<OpenCodeStreamEvent> Events { get; } = [];

        public Harness(OpenCodeAccessLevelTests owner, string? level, bool unattended = false)
        {
            _capture = Path.Combine(owner._root, Guid.NewGuid().ToString("N") + ".jsonl");
            File.WriteAllText(_capture, "");
            Service = new OpenCodeSessionService(new OpenCodeConfig(), null!, new Store(), (_, _) => { });
            Service.StreamEvent += (_, e) => { lock (Events) Events.Add(e); };
            var start = TestProcesses.AppendStdinTo(_capture, "OPENCODE_CAPTURE");
            start.CreateNoWindow = true;
            start.UseShellExecute = false;
            start.RedirectStandardInput = true;
            start.RedirectStandardOutput = true;
            start.RedirectStandardError = true;
            var process = Process.Start(start)!;
            owner._processes.Add(process);
            _session = new OpenCodeSessionService.ManagedSession(new OpenCodeSessionInfo
            {
                Id = "session-1", Status = "Active", ProjectName = "fixture", ProjectPath = owner._root,
                AccessLevel = level, Unattended = unattended,
            }, process, new CancellationTokenSource()) { AcpSessionId = "native-session", CurrentAssistantUid = "turn-1" };
            var sessions = typeof(OpenCodeSessionService).GetField("_sessions", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(Service)!;
            sessions.GetType().GetMethod("TryAdd")!.Invoke(sessions, ["session-1", _session]);
        }

        public Task RequestAsync(int id, object[] options)
        {
            var root = JsonSerializer.SerializeToElement(new
            {
                jsonrpc = "2.0", id, method = "session/request_permission",
                @params = new
                {
                    sessionId = "native-session",
                    toolCall = new { toolCallId = "call-1", title = "Run npm test", kind = "execute", rawInput = new { command = "npm test" } },
                    options,
                },
            });
            return (Task)typeof(OpenCodeSessionService).GetMethod("HandleAgentRequest", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(Service, [_session, "session/request_permission", root])!;
        }

        public async Task<List<JsonNode>> ResponsesAsync(TimeSpan? wait = null)
        {
            var deadline = DateTime.UtcNow + (wait ?? TimeSpan.FromSeconds(15));
            List<JsonNode> lines;
            do
            {
                lines = Read();
                if (wait is null && lines.Count > 0) break;
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
                    .Select(line => JsonNode.Parse(line)!).ToList();
            }
            catch (IOException) { return []; }
        }
    }

    private sealed class Store : IOpenCodeSessionStore
    {
        public void AddMessages(List<OpenCodeMessageRecord> messages) { }
        public void AddMessage(OpenCodeMessageRecord m) { }
        public List<OpenCodeMessageRecord> GetMessages(string sessionId, int limit = 50_000) => [];
        public OpenCodeSessionRecord? FindSession(string sessionId) => null;
        public OpenCodeSessionRecord? FindSessionByJobId(Guid jobId) => null;
        public List<OpenCodeSessionRecord> GetActiveSessions() => [];
        public List<OpenCodeSessionRecord> GetRecentSessions(HashSet<string> excludeIds, int limit = 20, bool includeDismissed = false) => [];
        public void SaveSession(OpenCodeSessionRecord record) { }
        public void DismissSession(string sessionId) { }
        public Dictionary<Guid, string> GetSessionStatusesByJobIds(IEnumerable<Guid> jobIds) => [];
    }
}
