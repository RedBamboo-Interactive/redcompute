using System.Diagnostics;
using System.Text.Json;
using RedCompute.Core.Jobs;
using RedCompute.Core.Sessions;
using RedCompute.Plugin.ClaudeCode;
using Xunit;

namespace RedCompute.App.Tests;

// A parked AskUserQuestion is control state: readable while the CLI waits on it (incident
// eb0d256c663d, where the requestId was only in the log), never a transcript record, and gone the
// moment the session stops waiting, whichever way that happens.
public sealed class ClaudePendingQuestionTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Environment.GetEnvironmentVariable("REDLEAF_SCRATCH_DIR") ?? Path.GetTempPath(),
        "redcompute-claude-questions", Guid.NewGuid().ToString("N"));
    private readonly List<Process> _processes = [];

    public ClaudePendingQuestionTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void Parked_question_is_readable_with_its_options_and_deadline_and_writes_no_transcript_record()
    {
        var (service, store, live, id) = Start();
        var recordsBefore = store.Messages.Count;
        var before = DateTimeOffset.UtcNow;

        Park(service, id, "req-1");

        var question = Assert.Single(service.GetPendingQuestions(id));
        Assert.Equal("req-1", question.RequestId);
        Assert.Equal("AskUserQuestion", question.ToolName);
        Assert.Equal("toolu_ask", question.ToolUseId);
        var asked = Assert.Single(question.Questions.EnumerateArray());
        Assert.Equal("Which database?", asked.GetProperty("question").GetString());
        Assert.Equal(["Postgres", "SQLite"], asked.GetProperty("options").EnumerateArray()
            .Select(o => o.GetProperty("label").GetString()));
        Assert.Equal(question.Questions.GetRawText(), question.Input.GetProperty("questions").GetRawText());
        Assert.InRange(question.AskedAt, before, DateTimeOffset.UtcNow);
        Assert.Equal(question.AskedAt + TimeSpan.FromMinutes(30), question.ExpiresAt);
        Assert.Equal(recordsBefore, store.Messages.Count);
        Assert.Contains(live(), e => e.Type == "question" && e.RequestId == "req-1");
        Assert.Empty(service.GetPendingQuestions("another-session"));
    }

    [Theory]
    [InlineData(false, "answered")]
    [InlineData(true, "declined")]
    public void Answer_or_decline_clears_the_question(bool decline, string outcome)
    {
        var (service, _, live, id) = Start();
        Park(service, id, "req-1");

        var result = service.SubmitQuestionAnswer(id, new SessionQuestionAnswer
        {
            RequestId = "req-1",
            Decline = decline,
            PositionalAnswers = decline ? null : ["Postgres"],
        });

        Assert.Equal(decline ? QuestionAnswerResult.Declined : QuestionAnswerResult.Answered, result);
        Assert.Empty(service.GetPendingQuestions(id));
        Assert.Contains(live(), e => e is { Type: "question_resolved", RequestId: "req-1" } && e.Content == outcome);
    }

    [Fact]
    public void Cli_cancel_clears_the_question()
    {
        var (service, _, live, id) = Start();
        Park(service, id, "req-1");

        Assert.True(service.ProcessOutputLine(id, """{"type":"control_cancel_request","request_id":"req-1"}"""));

        Assert.Empty(service.GetPendingQuestions(id));
        Assert.Contains(live(), e => e is { Type: "question_resolved", RequestId: "req-1", Content: "cancelled" });
    }

    [Fact]
    public async Task Expiry_clears_the_question()
    {
        var (service, _, live, id) = Start(timeout: TimeSpan.FromMilliseconds(100));
        Park(service, id, "req-1");
        Assert.Equal(TimeSpan.FromMilliseconds(100), service.GetPendingQuestions(id)[0].ExpiresAt - service.GetPendingQuestions(id)[0].AskedAt);

        await WaitUntil(() => service.GetPendingQuestions(id).Count == 0);

        Assert.Contains(live(), e => e is { Type: "question_resolved", RequestId: "req-1", Content: "timeout" });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Session_end_clears_the_question(bool forceKill)
    {
        var (service, _, live, id) = Start();
        Park(service, id, "req-1");

        if (forceKill) await service.ForceKill(id);
        else await service.StopSession(id);

        Assert.Empty(service.GetPendingQuestions(id));
        Assert.Contains(live(), e => e is { Type: "question_resolved", RequestId: "req-1", Content: "session_ended" });
    }

    private static void Park(ClaudeSessionService service, string id, string requestId)
    {
        var line = JsonSerializer.Serialize(new
        {
            type = "control_request",
            request_id = requestId,
            request = new
            {
                subtype = "can_use_tool",
                tool_name = "AskUserQuestion",
                tool_use_id = "toolu_ask",
                input = new
                {
                    questions = new[]
                    {
                        new
                        {
                            question = "Which database?",
                            header = "Database",
                            multiSelect = false,
                            options = new[]
                            {
                                new { label = "Postgres", description = "Shared server" },
                                new { label = "SQLite", description = "Local file" },
                            },
                        },
                    },
                },
            },
        });
        Assert.True(service.ProcessOutputLine(id, line));
    }

    private (ClaudeSessionService Service, ClaudeBackgroundWorkTests.RecordingStore Store,
        Func<List<ClaudeStreamEvent>> Live, string Id) Start(TimeSpan? timeout = null)
    {
        var store = new ClaudeBackgroundWorkTests.RecordingStore();
        var claudePath = Path.Combine(_root, "claude.exe");
        if (!File.Exists(claudePath)) File.WriteAllText(claudePath, "");
        var service = new ClaudeSessionService(new ClaudeConfig { ClaudePath = claudePath },
            new ClaudeBackgroundWorkTests.MemoryJobTracker(), store, new ClaudeBackgroundWorkTests.NullQualityModes(),
            () => null, (_, _) => { }, action => action());
        if (timeout is { } t) service.QuestionAnswerTimeout = t;
        service.LaunchSessionProcess = _ =>
        {
            var process = Process.Start(new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory, "more.com"),
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            })!;
            _processes.Add(process);
            return process;
        };
        var live = new List<ClaudeStreamEvent>();
        service.StreamEvent += (_, e) => { lock (live) live.Add(e); };
        var info = service.StartSession(_root, "haiku", "owner", null, null, "high", null, null, null,
            JobProvenance.DirectRedCompute("POST", "/ai-session/sessions", new JobBeneficiary("user", "owner"), "request-1", null));
        Assert.NotNull(info);
        return (service, store, () => { lock (live) return [.. live]; }, info!.Id);
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

    public void Dispose()
    {
        foreach (var process in _processes)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            process.Dispose();
        }
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { }
    }
}
