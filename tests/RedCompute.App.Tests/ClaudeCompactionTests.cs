using System.Diagnostics;
using System.Text.Json;
using RedCompute.Core.Jobs;
using RedCompute.Core.Sessions;
using RedCompute.Plugin.ClaudeCode;
using RedCompute.PluginSdk;
using Xunit;

namespace RedCompute.App.Tests;

// Claude Code reports context compaction on `system` lines: status "compacting" (repeated while it
// works), then status null with compact_result "success" or "failed" (+ compact_error), then a
// compact_boundary carrying the token counts. Status null and "requesting" also arrive outside
// compaction. Like Codex's contextCompaction item, one compaction must surface as exactly one
// ContextCompaction tool_use/tool_result pair inside the running turn, never as a terminal status.
public sealed class ClaudeCompactionTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Environment.GetEnvironmentVariable("REDLEAF_SCRATCH_DIR") ?? Path.GetTempPath(),
        "redcompute-claude-compaction", Guid.NewGuid().ToString("N"));
    private readonly List<Process> _processes = [];

    public ClaudeCompactionTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task Auto_compaction_is_one_pair_in_the_running_turn_settled_by_its_boundary()
    {
        var (service, store, live, id) = await StartTurn();
        Feed(service, id,
            Status("requesting"),
            Status("compacting"),
            Status("compacting"),
            Status(null, compactResult: "success"));

        // Open immediately; the keepalive repeat and the success status add nothing.
        var open = Assert.Single(live, e => e.ToolName == ClaudeSessionService.ContextCompactionToolName);
        Assert.Equal("tool_use", open.Type);
        Assert.NotNull(open.MessageId);
        Assert.NotNull(open.MessageUid);
        Assert.DoesNotContain(live, e => e.Type is "tool_result" or "status");

        Feed(service, id, Boundary("auto", 269294, 17181), TextDelta("Back to work."),
            Result());

        var result = Assert.Single(live, e => e.Type == "tool_result");
        Assert.Equal(open.MessageId, result.MessageId);
        Assert.Equal(open.MessageUid, result.MessageUid);
        Assert.Equal("Context compacted (auto): 269,294 -> 17,181 tokens", result.Content);
        Assert.Equal(open.MessageUid, Assert.Single(live, e => e.Type == "text").MessageUid);
        Assert.Equal("idle", live[^1].Content);

        var records = store.Messages.Where(m => m.MessageId == open.MessageId).ToList();
        Assert.Equal(["tool_use", "tool_result"], records.Select(r => r.EventType));
        Assert.Equal(ClaudeSessionService.ContextCompactionToolName, records[0].ToolName);
        Assert.All(records, r => Assert.Equal(open.MessageUid, r.MessageUid));
    }

    [Fact]
    public async Task Failed_compaction_settles_with_the_cli_error()
    {
        var (service, _, live, id) = await StartTurn();
        Feed(service, id, Status("compacting"), Status(null, compactResult: "failed", compactError: "prompt too long"));

        var open = Assert.Single(live, e => e.Type == "tool_use");
        var result = Assert.Single(live, e => e.Type == "tool_result");
        Assert.Equal(open.MessageId, result.MessageId);
        Assert.Equal("Context compaction failed: prompt too long", result.Content);
    }

    [Fact]
    public async Task Boundary_without_a_compacting_status_is_still_one_settled_pair()
    {
        var (service, _, live, id) = await StartTurn();
        Feed(service, id, Boundary("manual", 50000, null));

        Assert.Equal(["tool_use", "tool_result"], live.Select(e => e.Type));
        Assert.Equal(live[0].MessageId, live[1].MessageId);
        Assert.Equal(live[0].MessageUid, live[1].MessageUid);
        Assert.Equal("Context compacted (manual): 50,000 tokens before", live[1].Content);
    }

    [Fact]
    public async Task Status_outside_compaction_emits_nothing()
    {
        var (service, store, live, id) = await StartTurn();
        Feed(service, id, Status("requesting"), Status(null), Status(null, permissionMode: "plan"));

        Assert.Empty(live);
        Assert.DoesNotContain(store.Messages, m => m.Role == "assistant");
    }

    [Fact]
    public async Task Turn_ending_mid_compaction_settles_it_before_idle()
    {
        var (service, _, live, id) = await StartTurn();
        Feed(service, id, Status("compacting"), Result());

        Assert.Equal(["tool_use", "tool_result", "status"], live.Select(e => e.Type));
        Assert.Equal("Context compaction ended with the turn", live[1].Content);
        Assert.Equal(live[0].MessageUid, live[1].MessageUid);
    }

    [Fact]
    public async Task Killed_session_mid_compaction_persists_the_settlement()
    {
        var (service, store, _, id) = await StartTurn();
        Feed(service, id, Status("compacting"));
        await service.ForceKill(id);

        var records = store.Messages.Where(m => m.ToolName == ClaudeSessionService.ContextCompactionToolName
            || m.EventType == "tool_result").ToList();
        Assert.Equal(["tool_use", "tool_result"], records.Select(r => r.EventType));
        Assert.Equal(records[0].MessageId, records[1].MessageId);
    }

    [Fact]
    public async Task A_second_compaction_in_the_same_turn_is_a_new_pair()
    {
        var (service, _, live, id) = await StartTurn();
        Feed(service, id,
            Status("compacting"), Status(null, compactResult: "success"), Boundary("auto", 200000, 20000),
            Status("compacting"), Status(null, compactResult: "success"), Boundary("auto", 210000, 21000));

        var uses = live.Where(e => e.Type == "tool_use").ToList();
        var results = live.Where(e => e.Type == "tool_result").ToList();
        Assert.Equal(2, uses.Count);
        Assert.NotEqual(uses[0].MessageId, uses[1].MessageId);
        Assert.Equal(uses.Select(u => u.MessageId), results.Select(r => r.MessageId));
        Assert.Single(live.Select(e => e.MessageUid).Distinct());
    }

    // --- harness ---

    private async Task<(ClaudeSessionService Service, ClaudeBackgroundWorkTests.RecordingStore Store,
        List<ClaudeStreamEvent> Live, string Id)> StartTurn()
    {
        var claudePath = Path.Combine(_root, "claude.exe");
        if (!File.Exists(claudePath)) File.WriteAllText(claudePath, "");
        var store = new ClaudeBackgroundWorkTests.RecordingStore();
        var service = new ClaudeSessionService(new ClaudeConfig { ClaudePath = claudePath },
            new ClaudeBackgroundWorkTests.MemoryJobTracker(), store, new ClaudeBackgroundWorkTests.NullQualityModes(),
            () => null, (_, _) => { }, action => action());
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
        var info = service.StartSession(_root, "opus", "owner", null, null, "high", null, null, null,
            JobProvenance.DirectRedCompute("POST", "/ai-session/sessions", new JobBeneficiary("user", "owner"), "request-1", null));
        Assert.NotNull(info);
        Assert.True(await service.SendInput(info!.Id, [SessionInputPart.TextPart("work")], messageUid: "user-1"));
        Feed(service, info.Id, Json(new { type = "system", subtype = "init", session_id = "c1", model = "opus" }));
        return (service, store, live, info.Id);
    }

    private static void Feed(ClaudeSessionService service, string id, params string[] lines)
    {
        foreach (var line in lines)
            Assert.True(service.ProcessOutputLine(id, line));
    }

    private static string Json(object value) => JsonSerializer.Serialize(value);

    private static string Status(string? status, string? compactResult = null, string? compactError = null,
        string? permissionMode = null)
    {
        var line = new Dictionary<string, object?>
        {
            ["type"] = "system", ["subtype"] = "status", ["status"] = status,
            ["session_id"] = "c1", ["uuid"] = Guid.NewGuid().ToString(),
        };
        if (permissionMode is not null) line["permissionMode"] = permissionMode;
        if (compactResult is not null) line["compact_result"] = compactResult;
        if (compactError is not null) line["compact_error"] = compactError;
        return Json(line);
    }

    private static string Boundary(string trigger, long preTokens, long? postTokens)
    {
        var metadata = new Dictionary<string, object> { ["trigger"] = trigger, ["pre_tokens"] = preTokens };
        if (postTokens is { } post) metadata["post_tokens"] = post;
        return Json(new
        {
            type = "system", subtype = "compact_boundary", session_id = "c1",
            uuid = Guid.NewGuid().ToString(), compact_metadata = metadata,
        });
    }

    private static string TextDelta(string text) => Json(new
    {
        type = "stream_event", session_id = "c1",
        @event = new { type = "content_block_delta", index = 0, delta = new { type = "text_delta", text } },
    });

    private static string Result() =>
        """{"type":"result","subtype":"success","is_error":false,"result":"Done.","total_cost_usd":0.01}""";

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
