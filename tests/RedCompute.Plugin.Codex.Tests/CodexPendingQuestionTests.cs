using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using RedCompute.Core.Sessions;
using RedCompute.Plugin.Codex;
using Xunit;

namespace RedCompute.Plugin.Codex.Tests;

// requestUserInput is Codex's AskUserQuestion. A parked one is readable control state until the
// session stops waiting for it: answered, declined, its turn ended, or the app-server gone.
public sealed class CodexPendingQuestionTests : IDisposable
{
    private readonly List<Process> _processes = [];

    [Fact]
    public void Parked_question_is_readable_with_its_original_input_and_no_deadline()
    {
        var h = new Harness(this);
        var before = DateTimeOffset.UtcNow;

        h.Park("item-1");

        var question = Assert.Single(h.Service.GetPendingQuestions("session-1"));
        Assert.Equal("item-1", question.RequestId);
        Assert.Equal("requestUserInput", question.ToolName);
        Assert.Equal("item-1", question.ToolUseId);
        var asked = Assert.Single(question.Questions.EnumerateArray());
        Assert.Equal("Which database?", asked.GetProperty("question").GetString());
        Assert.Equal(["Postgres", "SQLite"], asked.GetProperty("options").EnumerateArray()
            .Select(o => o.GetProperty("label").GetString()));
        Assert.Equal("turn-1", question.Input.GetProperty("turnId").GetString());
        Assert.Equal(question.Questions.GetRawText(), question.Input.GetProperty("questions").GetRawText());
        Assert.InRange(question.AskedAt, before, DateTimeOffset.UtcNow);
        Assert.Null(question.ExpiresAt);
        Assert.Empty(h.Service.GetPendingQuestions("another-session"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Answer_or_decline_clears_the_question(bool decline)
    {
        var h = new Harness(this);
        h.Park("item-1");

        Assert.True(h.Service.SubmitQuestionAnswer("session-1", new SessionQuestionAnswer
        {
            RequestId = "item-1",
            Decline = decline,
            PositionalAnswers = decline ? null : ["Postgres"],
        }));

        Assert.Empty(h.Service.GetPendingQuestions("session-1"));
        Assert.Contains(h.Events, e => e is { Type: "question_resolved", RequestId: "item-1" });
    }

    [Fact]
    public void Ended_turn_clears_the_question_without_a_transcript_record()
    {
        var h = new Harness(this);
        h.Park("item-1");
        var records = h.Store.Messages.Count;

        // An interrupt (cancel) ends the turn the question belonged to.
        h.Notify("turn/completed", new { turn = new { status = "interrupted", items = Array.Empty<object>() } });

        Assert.Empty(h.Service.GetPendingQuestions("session-1"));
        Assert.DoesNotContain(h.Store.Messages.Skip(records), m => m.EventType is "question" or "question_resolved");
    }

    [Fact]
    public void Session_end_clears_the_question()
    {
        var h = new Harness(this);
        h.Park("item-1");

        typeof(CodexInteractiveService).GetMethod("OnExited", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(h.Service, ["session-1", 0]);

        Assert.Empty(h.Service.GetPendingQuestions("session-1"));
    }

    public void Dispose()
    {
        foreach (var process in _processes)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            process.Dispose();
        }
    }

    // The production service and per-session state, with a real connection over a stand-in
    // process so answers are written exactly as they would be to the app-server.
    private sealed class Harness
    {
        private readonly object _session;
        public CodexInteractiveService Service { get; }
        public RecordingStore Store { get; } = new();
        public List<CodexStreamEvent> Events { get; } = [];

        public Harness(CodexPendingQuestionTests owner)
        {
            Service = new CodexInteractiveService(new CodexConfig(), Store, null!, null!, null!, null!, null!, () => null, (_, _) => { });
            Service.StreamEvent += (_, e) => Events.Add(e);
            var sessionType = typeof(CodexInteractiveService).GetNestedType("ManagedSession", BindingFlags.NonPublic)!;
            _session = Activator.CreateInstance(sessionType, nonPublic: true)!;
            sessionType.GetProperty("Info")!.SetValue(_session, new CodexSessionInfo
            {
                Id = "session-1", ProjectName = "Test", ProjectPath = ".", ThreadId = "thread-1", Status = "Active",
            });
            var process = Process.Start(new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory, "more.com"),
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            })!;
            owner._processes.Add(process);
            var connection = (CodexAppServerConnection)Activator.CreateInstance(typeof(CodexAppServerConnection),
                BindingFlags.NonPublic | BindingFlags.Instance, null, [process, (Action<string, Guid?>)((_, _) => { })], null)!;
            sessionType.GetProperty("Connection")!.SetValue(_session, connection);
            sessionType.GetProperty("ActiveTurnId")!.SetValue(_session, "turn-1");
            var sessions = typeof(CodexInteractiveService).GetField("_sessions", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(Service)!;
            sessions.GetType().GetMethod("TryAdd")!.Invoke(sessions, ["session-1", _session]);
        }

        public void Park(string itemId)
        {
            using var request = JsonDocument.Parse(JsonSerializer.Serialize(new
            {
                threadId = "thread-1",
                turnId = "turn-1",
                itemId,
                questions = new[]
                {
                    new
                    {
                        id = "q-db",
                        header = "Database",
                        question = "Which database?",
                        options = new[]
                        {
                            new { label = "Postgres", description = "Shared server" },
                            new { label = "SQLite", description = "Local file" },
                        },
                    },
                },
            }));
            using var id = JsonDocument.Parse("7");
            typeof(CodexInteractiveService).GetMethod("HandleUserInputRequest", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(Service, [_session, id.RootElement, request.RootElement]);
        }

        public void Notify(string method, object payload)
        {
            using var json = JsonDocument.Parse(JsonSerializer.Serialize(payload));
            typeof(CodexInteractiveService).GetMethod("OnNotification", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(Service, ["session-1", method, json.RootElement]);
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
