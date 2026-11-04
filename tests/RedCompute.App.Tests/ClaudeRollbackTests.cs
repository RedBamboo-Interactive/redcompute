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

// The provider half of a rollback with the CLI faked. The chain shapes come from real sessions of
// 2.1.318 in scratch: a fork truncates only in memory until an entry lands on the fork point, a
// re-sent uuid hangs the CLI, and a refused turn's companion and synthetic reply follow its prompt.
public sealed class ClaudeRollbackTests : IDisposable
{
    private const string Fork = "11111111-1111-4111-8111-11111111aaaa";
    private const string Claude = "c1a0de00-0000-4000-8000-000000000001";

    private readonly string _root = Path.Combine(
        Environment.GetEnvironmentVariable("REDLEAF_SCRATCH_DIR") ?? Path.GetTempPath(),
        "redcompute-claude-rollback", Guid.NewGuid().ToString("N"));
    private readonly List<Process> _processes = [];
    private readonly List<ProcessStartInfo> _launches = [];
    private readonly List<ProcessStartInfo> _probes = [];
    private ClaudeForkProbeResult _probe = new(ClaudeForkProbeStatus.Accepted);

    public ClaudeRollbackTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task One_turn_rollback_forks_with_the_validated_flag_on_the_next_launch()
    {
        var store = new ForkStore();
        var chain = TwoTurns();
        var service = Seed(store, chain, ("u1", "P1"), ("u2", "P2"));

        var result = await service.RollbackAsync(Request("u2", kept: ["u1"], dropped: ["u2"]));

        Assert.Equal(SessionRollbackStatus.Applied, result.Status);
        Assert.Equal(Fork, Target(store)!.ForkAtUuid);
        Assert.Equal("P2", Target(store)!.DropsTurnUuid);
        Assert.Equal(1, store.FindSession("s")!.MessageCount);
        Assert.Equal(["u1"], store.GetMessages("s").Where(m => m.Role == "user").Select(m => m.MessageUid));
        Assert.Contains(store.AllMessages, m => m.MessageUid == "u2" && m.RolledBack); // Kept on disk.

        var info = service.ResumeSession("s", Provenance());

        Assert.NotNull(info);
        var args = Assert.Single(_launches).ArgumentList.ToList();
        Assert.Equal(Claude, args[args.IndexOf("--resume") + 1]);
        Assert.Equal(Fork, args[args.IndexOf("--resume-session-at") + 1]);
        Assert.Equal("P2", args[args.IndexOf("--resume-drops-turn") + 1]);
    }

    [Fact]
    public async Task Several_turns_pass_only_the_fork_point_because_the_validated_flag_refuses_a_range()
    {
        var store = new ForkStore();
        var chain = TwoTurns();
        chain.Prompt("P3", "third");
        chain.Reply();
        var service = Seed(store, chain, ("u1", "P1"), ("u2", "P2"), ("u3", "P3"));

        var result = await service.RollbackAsync(Request("u2", kept: ["u1"], dropped: ["u2", "u3"]));

        Assert.Equal(SessionRollbackStatus.Applied, result.Status);
        Assert.Null(Target(store)!.DropsTurnUuid);
        Assert.Equal(["P2", "P3"], Target(store)!.DroppedPromptUuids);
        service.ResumeSession("s", Provenance());
        var args = Assert.Single(_launches).ArgumentList.ToList();
        Assert.Equal(Fork, args[args.IndexOf("--resume-session-at") + 1]);
        Assert.DoesNotContain("--resume-drops-turn", args);
    }

    [Fact]
    public async Task Rollback_of_a_refused_turn_forks_before_its_companion_and_synthetic_reply()
    {
        var store = new ForkStore();
        var chain = new ChainFile(this);
        chain.Prompt("P1", "kept");
        var fork = chain.Reply(Fork);
        chain.RefusedTurn("P2", "the refused prompt");
        var service = Seed(store, chain, ("u1", "P1"), ("u2", "P2"));

        var loaded = ClaudeChain.Load(chain.Path);
        Assert.Equal(["P1", "P2"], loaded.Live.Where(entry => entry.IsPrompt).Select(entry => entry.Uuid));

        var result = await service.RollbackAsync(Request("u2", kept: ["u1"], dropped: ["u2"]));

        Assert.Equal(SessionRollbackStatus.Applied, result.Status);
        Assert.Equal(fork, Target(store)!.ForkAtUuid);
        Assert.Equal("P2", Target(store)!.DropsTurnUuid);
    }

    [Fact]
    public async Task Fork_target_survives_a_restart_until_the_chain_on_disk_shows_it_landed()
    {
        var store = new ForkStore();
        var chain = TwoTurns();
        var service = Seed(store, chain, ("u1", "P1"), ("u2", "P2"));
        Assert.Equal(SessionRollbackStatus.Applied,
            (await service.RollbackAsync(Request("u2", kept: ["u1"], dropped: ["u2"]))).Status);

        // A restart: the plugin comes back with only its store.
        var restarted = Service(store);
        Assert.NotNull(restarted.ResumeSession("s", Provenance()));
        Assert.Contains("--resume-session-at", _launches[^1].ArgumentList);

        // The process died before the next message. Only bookkeeping reached the file, and a plain
        // resume would bring everything back, so the target must still be applied.
        await restarted.TryParkIdleSessionAsync("s", SessionStopReasons.IdleTimeout);
        chain.Bookkeeping();
        var again = Service(store);
        Assert.NotNull(again.ResumeSession("s", Provenance()));
        Assert.Contains("--resume-session-at", _launches[^1].ArgumentList);
        Assert.NotNull(Target(store));

        // The next message lands on the fork point.
        await again.TryParkIdleSessionAsync("s", SessionStopReasons.IdleTimeout);
        chain.PromptOn(Fork, "P4", "next");
        chain.Reply();
        var after = Service(store);
        Assert.NotNull(after.ResumeSession("s", Provenance()));
        Assert.DoesNotContain("--resume-session-at", _launches[^1].ArgumentList);
        Assert.Null(Target(store));
    }

    [Fact]
    public async Task A_rejected_fork_at_rollback_time_changes_nothing()
    {
        var store = new ForkStore();
        var service = Seed(store, TwoTurns(), ("u1", "P1"), ("u2", "P2"));
        _probe = new(ClaudeForkProbeStatus.Rejected, "Resume rejected by --resume-drops-turn: range holds a prompt");

        var result = await service.RollbackAsync(Request("u2", kept: ["u1"], dropped: ["u2"]));

        Assert.Equal(SessionRollbackStatus.Refused, result.Status);
        Assert.Equal(SessionRollbackRefusals.ProviderRejected, result.Code);
        Assert.Contains("Resume rejected", result.Message);
        Assert.Null(Target(store));
        Assert.Equal(2, store.FindSession("s")!.MessageCount);
        Assert.DoesNotContain(store.AllMessages, m => m.RolledBack);
    }

    [Fact]
    public async Task A_fork_rejected_at_launch_clears_resumes_plainly_reports_and_never_retries()
    {
        var store = new ForkStore();
        var service = Seed(store, TwoTurns(), ("u1", "P1"), ("u2", "P2"));
        Assert.Equal(SessionRollbackStatus.Applied,
            (await service.RollbackAsync(Request("u2", kept: ["u1"], dropped: ["u2"]))).Status);
        _probe = new(ClaudeForkProbeStatus.Rejected, "Resume rejected by --resume-drops-turn: the file moved");
        var probesBefore = _probes.Count;
        var streamed = new List<ClaudeStreamEvent>();
        var restarted = Service(store);
        restarted.StreamEvent += (_, evt) => streamed.Add(evt);

        var info = restarted.ResumeSession("s", Provenance());

        Assert.NotNull(info);
        var launch = Assert.Single(_launches);
        Assert.DoesNotContain("--resume-session-at", launch.ArgumentList);
        Assert.Contains("--resume", launch.ArgumentList);
        Assert.Null(Target(store));
        var error = Assert.Single(store.AllMessages, m => m.EventType == "error");
        Assert.Contains("Resume rejected", error.Content);
        Assert.Contains(streamed, evt => evt.Type == "error" && evt.Content!.Contains("could not be applied"));

        // No retry on any later launch.
        await restarted.TryParkIdleSessionAsync("s", SessionStopReasons.IdleTimeout);
        Assert.NotNull(Service(store).ResumeSession("s", Provenance()));
        Assert.Equal(probesBefore + 1, _probes.Count);
        Assert.DoesNotContain("--resume-session-at", _launches[^1].ArgumentList);
    }

    [Fact]
    public async Task An_unreachable_cli_at_launch_keeps_the_target_rather_than_bringing_the_turns_back()
    {
        var store = new ForkStore();
        var service = Seed(store, TwoTurns(), ("u1", "P1"), ("u2", "P2"));
        await service.RollbackAsync(Request("u2", kept: ["u1"], dropped: ["u2"]));
        _probe = new(ClaudeForkProbeStatus.Unavailable, "The CLI did not answer in time");

        var restarted = Service(store);
        var info = restarted.ResumeSession("s", Provenance());

        Assert.Null(info);
        Assert.Empty(_launches);
        Assert.Contains("did not answer", restarted.LastStartError);
        Assert.NotNull(Target(store));
    }

    [Fact]
    public async Task An_unavailable_cli_at_rollback_time_changes_nothing()
    {
        var store = new ForkStore();
        var service = Seed(store, TwoTurns(), ("u1", "P1"), ("u2", "P2"));
        _probe = new(ClaudeForkProbeStatus.Unavailable, "The CLI did not answer in time");

        var result = await service.RollbackAsync(Request("u2", kept: ["u1"], dropped: ["u2"]));

        Assert.Equal(SessionRollbackStatus.Unavailable, result.Status);
        Assert.Null(Target(store));
        Assert.DoesNotContain(store.AllMessages, m => m.RolledBack);
    }

    [Fact]
    public async Task A_crash_between_the_target_and_the_mark_is_finished_by_the_repeat()
    {
        var store = new ForkStore();
        var service = Seed(store, TwoTurns(), ("u1", "P1"), ("u2", "P2"));
        store.CrashOnMark = true;
        await Assert.ThrowsAsync<IOException>(() => service.RollbackAsync(Request("u2", kept: ["u1"], dropped: ["u2"])));
        Assert.NotNull(Target(store));
        Assert.DoesNotContain(store.AllMessages, m => m.RolledBack);
        var probes = _probes.Count;
        store.CrashOnMark = false;

        var repeat = await Service(store).RollbackAsync(Request("u2", kept: ["u1"], dropped: ["u2"]));

        Assert.Equal(SessionRollbackStatus.Applied, repeat.Status);
        Assert.Equal(probes, _probes.Count);
        Assert.Contains(store.AllMessages, m => m.MessageUid == "u2" && m.RolledBack);
        Assert.Contains(store.AllMessages, m => m.MessageUid == "u1" && !m.RolledBack);
    }

    [Fact]
    public async Task Repeating_the_same_rollback_id_after_a_crash_does_not_probe_again()
    {
        var store = new ForkStore();
        var service = Seed(store, TwoTurns(), ("u1", "P1"), ("u2", "P2"));
        await service.RollbackAsync(Request("u2", kept: ["u1"], dropped: ["u2"]));
        var probes = _probes.Count;

        var repeat = await Service(store).RollbackAsync(Request("u2", kept: ["u1"], dropped: ["u2"]));

        Assert.Equal(SessionRollbackStatus.Applied, repeat.Status);
        Assert.Equal(probes, _probes.Count);
    }

    [Fact]
    public async Task Dropping_the_first_message_starts_a_fresh_conversation_and_keeps_the_old_file()
    {
        var store = new ForkStore();
        var chain = TwoTurns();
        var service = Seed(store, chain, ("u1", "P1"), ("u2", "P2"));
        var probes = _probes.Count;

        var result = await service.RollbackAsync(Request("u1", kept: [], dropped: ["u1", "u2"]));

        Assert.Equal(SessionRollbackStatus.Applied, result.Status);
        Assert.Equal(probes, _probes.Count); // There is no fork to prove.
        Assert.Null(store.FindSession("s")!.ClaudeSessionId);
        Assert.True(File.Exists(chain.Path));

        Assert.NotNull(Service(store).ResumeSession("s", Provenance()));
        var launch = Assert.Single(_launches);
        Assert.DoesNotContain("--resume", launch.ArgumentList);
        Assert.DoesNotContain("--resume-session-at", launch.ArgumentList);
        Assert.Null(Target(store));
    }

    [Fact]
    public async Task A_target_before_a_compaction_boundary_is_refused_from_the_chain_itself()
    {
        var store = new ForkStore();
        var chain = new ChainFile(this);
        chain.Prompt("P1", "absorbed");
        chain.Reply();
        chain.CompactBoundary();
        chain.Prompt("P2", "second");
        chain.Reply();
        var service = Seed(store, chain, ("u1", "P1"), ("u2", "P2"));

        var result = await service.RollbackAsync(Request("u1", kept: [], dropped: ["u1", "u2"]));

        Assert.Equal(SessionRollbackStatus.Refused, result.Status);
        Assert.Equal(SessionRollbackRefusals.TargetBeforeCompaction, result.Code);
        Assert.Null(Target(store));
    }

    [Fact]
    public async Task A_missing_provider_file_is_refused()
    {
        var store = new ForkStore();
        var chain = TwoTurns();
        var service = Seed(store, chain, ("u1", "P1"), ("u2", "P2"));
        File.Delete(chain.Path);

        var result = await service.RollbackAsync(Request("u2", kept: ["u1"], dropped: ["u2"]));

        Assert.Equal(SessionRollbackRefusals.ProviderHistoryMissing, result.Code);
    }

    [Fact]
    public void Older_turns_without_a_recorded_uuid_map_by_text_from_the_newest_and_refuse_on_any_disagreement()
    {
        var chain = TwoTurns();
        var loaded = ClaudeChain.Load(chain.Path);

        var (plan, refusal) = loaded.ResolveRollback(
            new SessionRollbackRequest("s", "r", "u2", [new("u1", "first")], [new("u2", "second")]),
            new Dictionary<string, string>());
        Assert.Null(refusal);
        Assert.Equal("P2", plan!.DropsTurnUuid);

        var (none, disagreement) = loaded.ResolveRollback(
            new SessionRollbackRequest("s", "r", "u1", [], [new("u1", "first"), new("u2", "not what the provider has")]),
            new Dictionary<string, string>());
        Assert.Null(none);
        Assert.Equal(SessionRollbackRefusals.TargetNotMapped, disagreement!.Code);
    }

    [Fact]
    public void An_envelope_quoting_the_previous_turn_cannot_pair_a_walk_that_is_out_of_step()
    {
        // The chain holds one prompt the transcript does not. Each envelope quotes the turn before it,
        // as real ones do, so the newest prompt holds the text of the turn being rewound to.
        var chain = new ChainFile(this);
        chain.Prompt("P1", "first");
        chain.Reply();
        chain.Prompt("P2", Framed("first", "second"));
        chain.Reply();
        chain.Prompt("P3", Framed("second", "not in the transcript"));
        var loaded = ClaudeChain.Load(chain.Path);

        var (plan, refusal) = loaded.ResolveRollback(
            new SessionRollbackRequest("s", "r", "u2", [new("u1", "first")], [new("u2", "second")]),
            new Dictionary<string, string>());

        Assert.Null(plan);
        Assert.Equal(SessionRollbackRefusals.TargetNotMapped, refusal!.Code);
    }

    [Theory]
    [InlineData("ok", "ok", true)]
    [InlineData("<context>a</context>\n\nok", "ok", true)]
    [InlineData("<context>a\n b</context>\n\nthe  whole\n message", "the whole message", true)]
    [InlineData("<context>ok</context>\n\nsomething else", "ok", false)]
    [InlineData("thanks, ok", "ok", false)]
    [InlineData("<context>a</context>\n\nthanks, ok", "ok", false)]
    [InlineData("ok then", "ok", false)]
    [InlineData("anything", "   ", false)]
    [InlineData(null, "ok", false)]
    public void A_prompt_pairs_only_when_the_text_closes_it(string? prompt, string canonical, bool pairs) =>
        Assert.Equal(pairs, ClaudeChain.TextMatches(prompt, canonical));

    /// <summary>
    /// The shape that started this: a retried refused turn twice over, each retry hanging off the
    /// refused reply, and a hook summary after the last good reply. Structure of 48542bb1, no text of it.
    /// </summary>
    [Fact]
    public void Three_refused_turns_rewind_to_the_entry_after_the_last_good_reply()
    {
        var chain = new ChainFile(this);
        chain.Prompt("P0", Framed("older", "kept"));
        var goodReply = chain.Reply();
        var fork = chain.HookSummary();
        chain.RefusedTurn("P1", Framed("kept", "refused one"));
        chain.RefusedTurn("P2", Framed("refused one", "two"));
        chain.RefusedTurn("P3", Framed("two", "3"));
        var loaded = ClaudeChain.Load(chain.Path);
        Assert.Equal(["P0", "P1", "P2", "P3"], loaded.Live.Where(entry => entry.IsPrompt).Select(entry => entry.Uuid));
        var turns = new SessionRollbackUserTurn[] { new("u0", "kept"), new("u1", "refused one"), new("u2", "two"), new("u3", "3") };
        Assert.Equal(goodReply, loaded.Live.Single(entry => entry.Uuid == fork).ParentUuid);

        var (plan, refusal) = loaded.ResolveRollback(
            new SessionRollbackRequest("s", "r", "u1", turns[..1].ToList(), turns[1..].ToList()),
            new Dictionary<string, string>());

        Assert.Null(refusal);
        Assert.Equal(fork, plan!.ForkAtUuid);
        Assert.Null(plan.DropsTurnUuid);
        Assert.Equal(["P1", "P2", "P3"], plan.DroppedPromptUuids);

        var (newest, _) = loaded.ResolveRollback(
            new SessionRollbackRequest("s", "r", "u3", turns[..3].ToList(), turns[3..].ToList()),
            new Dictionary<string, string>());
        Assert.Equal("P3", newest!.DropsTurnUuid);
    }

    private static string Framed(string previous, string text) =>
        $"<context>\nThe previous message was: {previous}\n</context>\n\n{text}";

    [Fact]
    public void A_prompt_the_person_still_sees_as_kept_can_never_fall_in_the_dropped_range()
    {
        var chain = TwoTurns();
        var loaded = ClaudeChain.Load(chain.Path);

        var (plan, refusal) = loaded.ResolveRollback(
            new SessionRollbackRequest("s", "r", "u1", [new("u2", "second")], [new("u1", "first")]),
            new Dictionary<string, string> { ["u1"] = "P1", ["u2"] = "P2" });

        Assert.Null(plan);
        Assert.Equal(SessionRollbackRefusals.TargetNotMapped, refusal!.Code);
    }

    [Fact]
    public void The_chain_is_the_parent_walk_so_a_forked_off_branch_is_not_live()
    {
        var chain = TwoTurns();
        chain.PromptOn(Fork, "P3", "after the fork");
        chain.Reply();

        var loaded = ClaudeChain.Load(chain.Path);

        Assert.Equal(["P1", "P3"], loaded.Live.Where(entry => entry.IsPrompt).Select(entry => entry.Uuid));
        Assert.True(loaded.HasConsumed(new ClaudeForkTarget("r", Fork, "P2", ["P2"])));
        Assert.False(ClaudeChain.Load(TwoTurns().Path).HasConsumed(new ClaudeForkTarget("r", Fork, "P2", ["P2"])));
    }

    [Fact]
    public async Task Prompt_uuids_are_random_per_write_because_a_resent_uuid_hangs_the_cli()
    {
        var store = new RecordingStore();
        var claudePath = Path.Combine(_root, "claude.exe");
        File.WriteAllText(claudePath, "");
        var service = new ClaudeSessionService(new ClaudeConfig { ClaudePath = claudePath }, new MemoryJobTracker(),
            store, new NullQualityModes(), () => null, (_, _) => { }, action => action());
        service.LaunchSessionProcess = Launch;
        var info = service.StartSession(_root, "haiku", "owner", null, null, "high", null, null, null, Provenance());

        Assert.True(await service.SendInput(info!.Id, [SessionInputPart.TextPart("go")], messageUid: "same-uid"));
        service.ProcessOutputLine(info.Id, JsonSerializer.Serialize(new
        {
            type = "result", subtype = "success", is_error = false, result = "ok", total_cost_usd = 0.0,
        }));
        Assert.True(await service.SendInput(info.Id, [SessionInputPart.TextPart("go")], messageUid: "same-uid"));

        var uuids = store.Messages.Where(m => m.Role == "user").Select(m => m.PromptUuid).ToList();
        Assert.Equal(2, uuids.Count);
        Assert.All(uuids, uuid => Assert.True(Guid.TryParse(uuid, out _)));
        Assert.NotEqual(uuids[0], uuids[1]);
    }

    [Theory]
    [InlineData("""{"type":"control_response","response":{"subtype":"success","request_id":"rollback-probe"}}""", "Accepted")]
    [InlineData("""{"type":"result","subtype":"error_during_execution","is_error":true,"errors":["Resume rejected by --resume-drops-turn: nope"]}""", "Rejected")]
    [InlineData("""{"type":"result","subtype":"error_during_execution","is_error":true,"errors":["No message found with message.uuid of: x"]}""", "Rejected")]
    [InlineData("""{"type":"system","subtype":"init"}""", null)]
    [InlineData("not json", null)]
    [InlineData("", null)]
    public void The_boot_probe_reads_the_cli_answer(string line, string? status)
    {
        var result = ClaudeSessionService.ClassifyForkProbeLine(line);

        Assert.Equal(status, result?.Status.ToString());
        if (status == "Rejected") Assert.False(string.IsNullOrWhiteSpace(result!.Message));
    }

    [Fact]
    public void The_probe_request_is_not_a_prompt_so_no_model_is_called()
    {
        using var request = JsonDocument.Parse(ClaudeSessionService.ForkProbeRequest);

        Assert.Equal("control_request", request.RootElement.GetProperty("type").GetString());
        Assert.False(request.RootElement.TryGetProperty("message", out _));
    }

    private ChainFile TwoTurns()
    {
        var chain = new ChainFile(this);
        chain.Prompt("P1", "first");
        chain.Reply(Fork);
        chain.Prompt("P2", "second");
        chain.Reply();
        return chain;
    }

    private static SessionRollbackRequest Request(string target, string[] kept, string[] dropped) => new(
        "s", "rollback-1", target,
        kept.Select(uid => new SessionRollbackUserTurn(uid, TextOf(uid))).ToList(),
        dropped.Select(uid => new SessionRollbackUserTurn(uid, TextOf(uid))).ToList());

    private static string TextOf(string uid) => uid switch
    {
        "u1" => "first",
        "u2" => "second",
        "u3" => "third",
        _ => uid,
    };

    private static ClaudeForkTarget? Target(ForkStore store) => ClaudeForkTarget.Parse(store.GetForkTarget("s"));

    private ClaudeSessionService Seed(ForkStore store, ChainFile chain, params (string Uid, string Prompt)[] turns)
    {
        store.SaveSession(new ClaudeSessionRecord
        {
            Id = "s", ClaudeSessionId = Claude, ProjectName = "Test", ProjectPath = _root,
            Model = "haiku", Effort = "high", UserId = "owner", Status = "Stopped",
            StopReason = SessionStopReasons.IdleTimeout, StartedAt = DateTimeOffset.UtcNow,
            MessageCount = turns.Length,
        });
        foreach (var (uid, prompt) in turns)
        {
            store.AddMessage(new ClaudeMessageRecord
            {
                SessionId = "s", Role = "user", EventType = "text", Content = TextOf(uid),
                MessageUid = uid, PromptUuid = prompt, Timestamp = DateTimeOffset.UtcNow,
            });
            store.AddMessage(new ClaudeMessageRecord
            {
                SessionId = "s", Role = "assistant", EventType = "text", Content = "reply",
                MessageUid = "a" + uid, Timestamp = DateTimeOffset.UtcNow,
            });
        }
        return Service(store);
    }

    private ClaudeSessionService Service(IClaudeSessionStore store)
    {
        var claudePath = Path.Combine(_root, "claude.exe");
        if (!File.Exists(claudePath)) File.WriteAllText(claudePath, "");
        var service = new ClaudeSessionService(new ClaudeConfig { ClaudePath = claudePath }, new MemoryJobTracker(),
            store, new NullQualityModes(), () => null, (_, _) => { }, action => action());
        service.LaunchSessionProcess = Launch;
        service.ProjectsRootOverride = Path.Combine(_root, "projects");
        service.ProbeFork = (startInfo, _) =>
        {
            lock (_probes) _probes.Add(startInfo);
            return Task.FromResult(_probe);
        };
        return service;
    }

    private Process Launch(ProcessStartInfo requested)
    {
        lock (_launches) _launches.Add(requested);
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
    }

    private static JobProvenance Provenance() =>
        JobProvenance.DirectRedCompute("POST", "/ai-session/sessions", new JobBeneficiary("user", "owner"), "req", "corr");

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

    /// <summary>A transcript with the entry shapes of a real session file.</summary>
    private sealed class ChainFile
    {
        public string Path { get; }
        private string? _parent;

        public ChainFile(ClaudeRollbackTests owner)
        {
            var folder = System.IO.Path.Combine(owner._root, "projects", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            Path = System.IO.Path.Combine(folder, $"{Claude}.jsonl");
            File.WriteAllText(Path, "");
        }

        private void Write(object entry) =>
            File.AppendAllText(Path, JsonSerializer.Serialize(entry) + "\n");

        public void Prompt(string uuid, string text) => PromptOn(_parent, uuid, text);

        public void PromptOn(string? parent, string uuid, string text)
        {
            Write(new { type = "user", uuid, parentUuid = parent, isSidechain = false, message = new { role = "user", content = text } });
            _parent = uuid;
        }

        public string Reply(string? uuid = null)
        {
            uuid ??= Guid.NewGuid().ToString();
            Write(new
            {
                type = "assistant", uuid, parentUuid = _parent, isSidechain = false,
                message = new { role = "assistant", content = new[] { new { type = "text", text = "reply" } } },
            });
            _parent = uuid;
            return uuid;
        }

        public string HookSummary()
        {
            var uuid = Guid.NewGuid().ToString();
            Write(new { type = "system", subtype = "stop_hook_summary", uuid, parentUuid = _parent, hookCount = 1 });
            _parent = uuid;
            return uuid;
        }

        public void RefusedTurn(string prompt, string text)
        {
            Prompt(prompt, text);
            var refused = Guid.NewGuid().ToString();
            var info = Guid.NewGuid().ToString();
            Write(new { type = "system", subtype = "informational", uuid = info, parentUuid = _parent, content = "fixture" });
            var companion = Guid.NewGuid().ToString();
            Write(new
            {
                type = "user", uuid = companion, parentUuid = info, isMeta = true, turnCompanion = true,
                refusedMessageId = refused, message = new { role = "user", content = "fixture companion" },
            });
            var system = Guid.NewGuid().ToString();
            Write(new
            {
                type = "system", subtype = "model_refusal_no_fallback", uuid = system, parentUuid = companion,
                refusedUserMessageUuid = prompt, content = "",
            });
            var synthetic = Guid.NewGuid().ToString();
            Write(new
            {
                type = "assistant", uuid = synthetic, parentUuid = system, isApiErrorMessage = true, error = "invalid_request",
                refusedMessageId = refused,
                message = new
                {
                    role = "assistant", model = "<synthetic>", stop_reason = "refusal",
                    content = new[] { new { type = "text", text = "API Error: fixture refusal text" } },
                },
            });
            _parent = synthetic;
        }

        public void CompactBoundary()
        {
            var boundary = Guid.NewGuid().ToString();
            Write(new { type = "system", subtype = "compact_boundary", uuid = boundary, parentUuid = (string?)null, content = "Conversation compacted" });
            var summary = Guid.NewGuid().ToString();
            Write(new
            {
                type = "user", uuid = summary, parentUuid = boundary, isCompactSummary = true,
                message = new { role = "user", content = "summary of everything before" },
            });
            _parent = summary;
        }

        /// <summary>What a fork launched with no input leaves behind: no chain entries at all.</summary>
        public void Bookkeeping() =>
            File.AppendAllText(Path, JsonSerializer.Serialize(new { type = "mode", mode = "normal", sessionId = Claude }) + "\n");
    }

    internal sealed class ForkStore : IClaudeSessionStore
    {
        private readonly Dictionary<string, ClaudeSessionRecord> _sessions = [];
        private readonly List<ClaudeMessageRecord> _messages = [];
        private readonly Dictionary<string, string?> _fork = [];

        public List<ClaudeMessageRecord> AllMessages { get { lock (_messages) return [.. _messages]; } }

        public string? GetForkTarget(string sessionId) { lock (_fork) return _fork.GetValueOrDefault(sessionId); }
        public void SetForkTarget(string sessionId, string? targetJson) { lock (_fork) _fork[sessionId] = targetJson; }

        public bool CrashOnMark { get; set; }

        public void MarkMessagesRolledBack(string sessionId, string messageUid)
        {
            if (CrashOnMark) throw new IOException("The process died here");
            lock (_messages)
            {
                var from = _messages.FindIndex(m => m.SessionId == sessionId && m.Role == "user" && m.MessageUid == messageUid);
                if (from < 0) return;
                for (var i = from; i < _messages.Count; i++)
                    if (_messages[i].SessionId == sessionId) _messages[i].RolledBack = true;
            }
        }

        public ClaudeSessionRecord? FindSession(string sessionId) { lock (_sessions) return _sessions.GetValueOrDefault(sessionId); }
        public ClaudeSessionRecord? FindSessionByJobId(Guid jobId) { lock (_sessions) return _sessions.Values.FirstOrDefault(s => s.JobId == jobId); }
        public List<ClaudeSessionRecord> GetActiveSessions() => [];
        public List<ClaudeSessionRecord> GetSessionsWithBackgroundTasks() => [];
        public List<ClaudeSessionRecord> GetRecentSessions(HashSet<string> excludeIds, int limit = 20, bool includeDismissed = false) => [];
        public void SaveSession(ClaudeSessionRecord record) { lock (_sessions) _sessions[record.Id] = record; }
        public void DismissSession(string sessionId) { }
        public void AddMessage(ClaudeMessageRecord message) { lock (_messages) _messages.Add(message); }
        public void AddMessages(List<ClaudeMessageRecord> messages) { lock (_messages) _messages.AddRange(messages); }
        public List<ClaudeMessageRecord> GetMessages(string sessionId, int limit = 50_000) =>
            AllMessages.Where(m => m.SessionId == sessionId && !m.RolledBack).TakeLast(limit).ToList();
        public DateTimeOffset GetLastMessageTimestamp(string sessionId) => default;
        public Dictionary<Guid, string> GetSessionStatusesByJobIds(IEnumerable<Guid> jobIds) => [];
    }
}
