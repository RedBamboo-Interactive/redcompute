using System.Diagnostics;
using System.Text.Json;
using RedCompute.Core.Jobs;
using RedCompute.Core.Sessions;
using RedCompute.Plugin.ClaudeCode;
using RedCompute.PluginSdk;
using Xunit;
using ClaudeSessionStatus = RedCompute.Plugin.ClaudeCode.SessionStatus;
using static RedCompute.App.Tests.ClaudeBackgroundWorkTests;

namespace RedCompute.App.Tests;

// A safeguards refusal ends the turn with api_error_status: null. TryGetInt32 throws on a JSON null,
// and that exception used to escape the stdout reader: no Idle, no error event, no durable record,
// and interrupts unanswered because their acknowledgement arrives through the dead reader. Recorded
// on 48542bb1 on 2026-11-04.
public sealed class ClaudeRefusalResultTests : IDisposable
{
    private const string RefusalText =
        "API Error: Opus 5.5's safeguards flagged this message (https://www.anthropic.com/legal/aup). "
        + "This sometimes happens with safe, normal conversations. Claude Code can't respond to this "
        + "message with Opus 5.5.\n\nTry rephrasing the request in a new session or change your model.\n\n"
        + "Details: `[reasoning_extraction]`";

    private static readonly string RefusalSystem = JsonSerializer.Serialize(new
    {
        type = "system",
        subtype = "model_refusal_no_fallback",
        original_model = "claude-opus-5-5",
        request_id = "req_test",
        api_refusal_category = "reasoning_extraction",
        content = "",
    });

    private static readonly string RefusalResult = JsonSerializer.Serialize(new
    {
        type = "result",
        subtype = "success",
        is_error = true,
        terminal_reason = "api_error",
        api_error_status = (int?)null,
        result = RefusalText,
        total_cost_usd = 0.0,
    });

    private readonly string _root = Path.Combine(
        Environment.GetEnvironmentVariable("REDLEAF_SCRATCH_DIR") ?? Path.GetTempPath(),
        "redcompute-refusal", Guid.NewGuid().ToString("N"));
    private readonly List<Process> _processes = [];

    public ClaudeRefusalResultTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task Refusal_ends_the_turn_and_leaves_the_reason_in_the_transcript()
    {
        var store = new RecordingStore();
        var (service, id) = await StartAsync(store);
        var streamed = new List<ClaudeStreamEvent>();
        service.StreamEvent += (_, evt) => streamed.Add(evt);

        Assert.True(service.ProcessOutputLine(id, RefusalSystem));
        Assert.True(service.ProcessOutputLine(id, RefusalResult));

        var info = service.GetSession(id).Info!;
        Assert.Equal(ClaudeSessionStatus.Idle, info.Status);
        Assert.Equal(SessionTurnOutcomes.Failed, info.LastTurnOutcome);

        var live = Assert.Single(streamed, e => e.Type == "error");
        Assert.Equal(RefusalText, live.Content);
        var durable = Assert.Single(store.Messages, m => m.EventType == "error");
        Assert.Equal(RefusalText, durable.Content);
        Assert.Equal(id, durable.SessionId);
    }

    [Fact]
    public async Task A_refused_session_takes_the_next_message()
    {
        var (service, id) = await StartAsync(new RecordingStore());
        Assert.True(service.ProcessOutputLine(id, RefusalResult));
        Assert.Equal(ClaudeSessionStatus.Idle, service.GetSession(id).Info!.Status);

        Assert.True(await service.SendInput(id, [SessionInputPart.TextPart("Test")], messageUid: "user-2"));
        Assert.Equal(ClaudeSessionStatus.Active, service.GetSession(id).Info!.Status);
    }

    [Fact]
    public async Task A_result_that_cannot_be_processed_still_ends_the_turn()
    {
        var store = new RecordingStore();
        var (service, id) = await StartAsync(store);

        Assert.True(service.ProcessUnreadableOutputLine(id, RefusalResult, new InvalidOperationException("boom")));

        Assert.Equal(ClaudeSessionStatus.Idle, service.GetSession(id).Info!.Status);
        Assert.Equal(RefusalText, Assert.Single(store.Messages, m => m.EventType == "error").Content);
    }

    [Fact]
    public async Task A_result_with_no_readable_message_still_says_the_turn_ended()
    {
        var store = new RecordingStore();
        var (service, id) = await StartAsync(store);

        Assert.True(service.ProcessUnreadableOutputLine(id, """{"type":"result","subtype":"success"}""",
            new InvalidOperationException("boom")));

        Assert.Equal(ClaudeSessionStatus.Idle, service.GetSession(id).Info!.Status);
        Assert.Contains("could not read", Assert.Single(store.Messages, m => m.EventType == "error").Content);
    }

    [Fact]
    public async Task An_unreadable_event_that_is_not_a_result_leaves_the_turn_running()
    {
        var store = new RecordingStore();
        var (service, id) = await StartAsync(store);

        Assert.True(service.ProcessUnreadableOutputLine(id, """{"type":"assistant"}""",
            new InvalidOperationException("boom")));
        Assert.True(service.ProcessUnreadableOutputLine(id, "not json", new InvalidOperationException("boom")));

        Assert.Equal(ClaudeSessionStatus.Active, service.GetSession(id).Info!.Status);
        Assert.DoesNotContain(store.Messages, m => m.EventType == "error");
    }

    [Theory]
    [InlineData("""{"type":"result","subtype":"error","api_error_status":null}""", "Claude request failed")]
    [InlineData("""{"type":"result","subtype":"error","api_error_status":500}""", "Claude API request failed with HTTP 500")]
    public void Terminal_error_reads_a_null_or_numeric_status(string json, string expected)
    {
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(expected, ClaudeSessionService.ReadTerminalResultError(doc.RootElement));
    }

    [Fact]
    public async Task An_authentication_result_still_classifies_as_one()
    {
        var (service, id) = await StartAsync(new RecordingStore());

        Assert.True(service.ProcessOutputLine(id, JsonSerializer.Serialize(new
        {
            type = "result",
            subtype = "success",
            is_error = true,
            terminal_reason = "api_error",
            api_error_status = 401,
            result = "Request rejected.",
        })));

        Assert.Equal("authentication_failed", service.GetSession(id).Info!.StopReason);
    }

    private async Task<(ClaudeSessionService Service, string Id)> StartAsync(RecordingStore store)
    {
        var claudePath = Path.Combine(_root, "claude.exe");
        if (!File.Exists(claudePath)) File.WriteAllText(claudePath, "");
        var service = new ClaudeSessionService(new ClaudeConfig { ClaudePath = claudePath }, new MemoryJobTracker(),
            store, new NullQualityModes(), () => null, (_, _) => { }, action => action());
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
            lock (_processes) _processes.Add(process);
            return process;
        };
        var info = service.StartSession(_root, "haiku", "owner", null, null, "high", null, null, null,
            JobProvenance.DirectRedCompute("POST", "/ai-session/sessions", new JobBeneficiary("user", "owner"), "req", "corr"));
        Assert.True(await service.SendInput(info!.Id, [SessionInputPart.TextPart("go")], messageUid: "user-1"));
        Assert.Equal(ClaudeSessionStatus.Active, service.GetSession(info.Id).Info!.Status);
        return (service, info.Id);
    }

    public void Dispose()
    {
        lock (_processes)
        {
            foreach (var process in _processes)
            {
                try { if (!process.HasExited) process.Kill(true); } catch { }
                process.Dispose();
            }
        }
        try { Directory.Delete(_root, true); } catch { }
    }
}
