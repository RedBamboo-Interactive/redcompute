using System.Diagnostics;
using RedCompute.Core.Jobs;
using RedCompute.Plugin.ClaudeCode;
using RedCompute.PluginSdk;
using RedCompute.PluginSdk.Hosting;
using Xunit;

namespace RedCompute.App.Tests;

// A session whose output reaches its reader as one message (Discord) must not be asked for progress
// lines: Claude Code 2.1.323 adds a "say what you are doing" reminder to long silent turns, and the
// answer to it becomes the opening line of the public reply. The option is explicit, never inferred
// from unattended, persisted with the session, and reaches the CLI as CLAUDE_CODE_SILENT_TURN_REMINDER=0
// (the CLI's boolean parser reads 0, false, no and off as false) on every launch.
public sealed class ClaudeDeliveredAsOneMessageTests : IDisposable
{
    private const string Variable = "CLAUDE_CODE_SILENT_TURN_REMINDER";

    private readonly string _root = Path.Combine(
        Environment.GetEnvironmentVariable("REDLEAF_SCRATCH_DIR") ?? Path.GetTempPath(),
        "redcompute-claude-delivery", Guid.NewGuid().ToString("N"));
    private readonly List<Process> _processes = [];
    private readonly List<ProcessStartInfo> _launches = [];
    private readonly ClaudeBackgroundWorkTests.RecordingStore _store = new();
    private readonly ClaudeSessionService _service;

    public ClaudeDeliveredAsOneMessageTests()
    {
        Directory.CreateDirectory(_root);
        var claudePath = Path.Combine(_root, "claude.exe");
        File.WriteAllText(claudePath, "");
        _service = new ClaudeSessionService(new ClaudeConfig { ClaudePath = claudePath },
            new ClaudeBackgroundWorkTests.MemoryJobTracker(), _store, new ClaudeBackgroundWorkTests.NullQualityModes(),
            () => null, (_, _) => { }, action => action());
        _service.LaunchSessionProcess = HostSeams.From(startInfo =>
        {
            lock (_launches) _launches.Add(startInfo);
            var sink = TestProcesses.AppendStdinTo(Path.Combine(_root, "stdin.jsonl"), "CLAUDE_DELIVERY_STDIN");
            sink.CreateNoWindow = true;
            sink.UseShellExecute = false;
            sink.RedirectStandardInput = true;
            sink.RedirectStandardOutput = true;
            sink.RedirectStandardError = true;
            var process = Process.Start(sink)!;
            lock (_processes) _processes.Add(process);
            return process;
        });
    }

    // ---- what reaches the CLI ----

    [Fact]
    public void The_overlay_carries_the_variable_only_when_the_session_is_delivered_as_one_message()
    {
        var on = ClaudeSessionService.LaunchEnvironment(null, "s1", deliveredAsOneMessage: true);
        Assert.NotNull(on);
        Assert.Equal("0", on![Variable]);

        var off = ClaudeSessionService.LaunchEnvironment(null, "s1", deliveredAsOneMessage: false);
        Assert.True(off is null || !off.ContainsKey(Variable));
    }

    [Fact]
    public void The_variable_joins_the_scratch_environment_without_replacing_it()
    {
        var environment = ClaudeSessionService.LaunchEnvironment(_root, "s1", deliveredAsOneMessage: true)!;

        Assert.Equal("0", environment[Variable]);
        Assert.Equal(_root, environment["REDLEAF_SCRATCH_DIR"]);
        Assert.Equal(_root, environment["TEMP"]);
    }

    [Fact]
    public void Start_in_a_delivery_scope_launches_the_CLI_with_the_reminder_off_and_persists_the_option()
    {
        string id;
        using (SessionDeliveryScope.Push(true))
            id = Start();

        Assert.Equal("0", _launches[^1].Environment[Variable]);
        Assert.True(_store.FindSession(id)!.DeliveredAsOneMessage);
        Assert.True(_service.GetSession(id).Info!.DeliveredAsOneMessage);
    }

    [Fact]
    public void Start_without_the_option_leaves_the_variable_to_the_CLI_default()
    {
        var inherited = Environment.GetEnvironmentVariable(Variable);
        var id = Start();

        Assert.Equal(inherited, _launches[^1].Environment.TryGetValue(Variable, out var value) ? value : null);
        Assert.False(_store.FindSession(id)!.DeliveredAsOneMessage);
    }

    [Fact]
    public void Unattended_alone_does_not_turn_the_reminder_off()
    {
        var inherited = Environment.GetEnvironmentVariable(Variable);
        using var access = SessionAccessScope.Push(new RedCompute.Core.Sessions.SessionAccessState(
            RedCompute.Core.Sessions.SessionAccessLevels.Auto, null, Unattended: true));
        var id = Start();

        Assert.Equal(inherited, _launches[^1].Environment.TryGetValue(Variable, out var value) ? value : null);
        Assert.False(_store.FindSession(id)!.DeliveredAsOneMessage);
    }

    // ---- resume and restart ----

    [Fact]
    public void Resume_keeps_the_option_and_launches_with_the_reminder_off()
    {
        _store.SaveSession(Record("stopped", deliveredAsOneMessage: true));

        Assert.True(_service.ResumeSession("stopped", Provenance()) is not null, _service.LastStartError);

        Assert.Equal("0", Assert.Single(_launches).Environment[Variable]);
        Assert.True(_store.FindSession("stopped")!.DeliveredAsOneMessage);
    }

    [Fact]
    public void Resume_of_a_session_without_the_option_does_not_set_the_variable()
    {
        var inherited = Environment.GetEnvironmentVariable(Variable);
        _store.SaveSession(Record("plain", deliveredAsOneMessage: false));

        Assert.True(_service.ResumeSession("plain", Provenance()) is not null, _service.LastStartError);

        Assert.Equal(inherited, Assert.Single(_launches).Environment.TryGetValue(Variable, out var value) ? value : null);
    }

    [Fact]
    public void Setting_the_option_on_a_stopped_session_takes_effect_at_its_next_resume()
    {
        // A binding created before the option existed: its row says nothing, the bridge sets it before the next message.
        _store.SaveSession(Record("legacy", deliveredAsOneMessage: false));

        Assert.True(_service.SetDeliveredAsOneMessage("legacy", true));
        Assert.True(_store.FindSession("legacy")!.DeliveredAsOneMessage);

        Assert.True(_service.ResumeSession("legacy", Provenance()) is not null, _service.LastStartError);
        Assert.Equal("0", Assert.Single(_launches).Environment[Variable]);
    }

    [Fact]
    public void Setting_the_option_on_a_live_session_persists_it_for_the_next_launch()
    {
        var id = Start();
        Assert.False(_store.FindSession(id)!.DeliveredAsOneMessage);

        Assert.True(_service.SetDeliveredAsOneMessage(id, true));

        Assert.True(_store.FindSession(id)!.DeliveredAsOneMessage);
        Assert.True(_service.GetSession(id).Info!.DeliveredAsOneMessage);
    }

    [Fact]
    public void The_option_can_be_cleared_again()
    {
        _store.SaveSession(Record("toggled", deliveredAsOneMessage: true));

        Assert.True(_service.SetDeliveredAsOneMessage("toggled", false));

        Assert.False(_store.FindSession("toggled")!.DeliveredAsOneMessage);
    }

    [Fact]
    public void An_unknown_session_is_reported_as_not_known()
        => Assert.False(_service.SetDeliveredAsOneMessage("missing", true));

    private string Start()
    {
        var info = _service.StartSession(_root, "haiku", "owner", null, null, "high", null, null, null, Provenance());
        Assert.NotNull(info);
        return info!.Id;
    }

    private static JobProvenance Provenance()
        => JobProvenance.DirectRedCompute("POST", "/ai-session/sessions", new JobBeneficiary("user", "owner"), "request-1", null);

    private ClaudeSessionRecord Record(string id, bool deliveredAsOneMessage) => new()
    {
        Id = id, ProjectName = "p", ProjectPath = _root, Status = "Stopped", ClaudeSessionId = "claude-conversation",
        MessageCount = 2, Model = "haiku", AccessLevel = RedCompute.Core.Sessions.SessionAccessLevels.Ask,
        DeliveredAsOneMessage = deliveredAsOneMessage,
    };

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
