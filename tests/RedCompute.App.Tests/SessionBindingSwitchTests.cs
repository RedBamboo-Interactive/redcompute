using RedCompute.App.Services.SessionBindings;
using Xunit;

namespace RedCompute.App.Tests;

public sealed class SessionBindingSwitchTests : IDisposable
{
    private const string Logical = "same-session";
    private static readonly BindingTarget Codex = new("codex", "default-codex", "local", "codex-a", "low");
    private static readonly BindingTarget Claude = new("claude-code", "default-claude", "local", "claude-b", "high");
    private static readonly NativeSessionBinding Original = new("runtime-codex", "native-codex", Codex);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "binding-journal-" + Guid.NewGuid().ToString("N"));
    private SessionBindingJournal Open() => new(Path.Combine(_root, "bindings.db"));

    [Fact]
    public async Task Changes_harness_and_returns_to_the_original_native_thread_with_a_single_history()
    {
        var journal = Open();
        var runtime = new Runtime();
        journal.OpenOrAttach(Logical, Original);

        runtime.Deliver(journal, "before");
        runtime.History.Add(new(2, "tool", "tool_result", null, "private tool payload"));
        var toClaude = await new SessionBindingSwitch(journal, runtime)
            .ApplyAsync(Logical, Original, "to-claude", 0, Claude, default);
        Assert.Equal(BindingMoveState.Committed, toClaude.State);
        Assert.Equal(1, toClaude.CommittedGeneration);
        Assert.Equal(["before"], runtime.LatestPreparation!.History.Select(item => item.Content));
        runtime.Deliver(Open(), "during");

        var back = await new SessionBindingSwitch(Open(), runtime)
            .ApplyAsync(Logical, Original, "back-to-codex", 1, Codex, default);
        Assert.Equal(2, back.CommittedGeneration);
        Assert.Equal(Original, back.Prepared);
        Assert.Equal(["during"], runtime.LatestPreparation!.History.Select(item => item.Content));

        runtime.Deliver(Open(), "after");
        Assert.Equal([("before", "native-codex"), ("during", "native-claude-code"), ("after", "native-codex")], runtime.Deliveries);
        Assert.Equal([0, 1, 2], Open().Read(Logical)!.Threads.Select(thread => thread.Generation));
        Assert.Equal([Logical], runtime.LogicalIds.Distinct());
        Assert.Equal(["before", "during", "after"], runtime.History.Where(row => row.Role == "user").Select(row => row.Content));
    }

    [Fact]
    public async Task Preparation_refusal_keeps_the_old_thread_and_does_not_consume_a_generation()
    {
        var runtime = new Runtime { RefusePreparation = true };
        var service = new SessionBindingSwitch(Open(), runtime);
        var rejected = await service.ApplyAsync(Logical, Original, "unready-claude", 0, Claude, default);
        Assert.Equal(BindingMoveState.Rejected, rejected.State);
        Assert.Equal("fake_prepare_refused", rejected.Error);
        Assert.Equal(Original, Open().Read(Logical)!.Active.Native);
        var replay = await service.ApplyAsync(Logical, Original, "unready-claude", 0, Claude, default);
        Assert.Equal(rejected, replay);
        Assert.Single(runtime.Preparations);
        Assert.Contains("unready-claude", runtime.Discarded);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Restart_at_either_side_of_native_prepare_reuses_the_operation_and_commits_once(bool savedPrepare)
    {
        var runtime = new Runtime { LosePrepareResponse = !savedPrepare, LoseReadinessResponse = savedPrepare };
        runtime.History.Add(new(1, "user", "text", "before", "before"));
        var first = new SessionBindingSwitch(Open(), runtime);
        await Assert.ThrowsAsync<IOException>(() => first.ApplyAsync(Logical, Original, "retry-move", 0, Claude, default));

        var interrupted = Open().Read(Logical)!;
        Assert.Equal(Original, interrupted.Active.Native);
        Assert.Equal(savedPrepare ? BindingMoveState.Prepared : BindingMoveState.Pending, Assert.Single(interrupted.Moves).State);

        runtime.History.Add(new(2, "user", "text", "later", "later"));
        var restarted = new SessionBindingSwitch(Open(), runtime);
        var move = await restarted.ApplyAsync(Logical, Original, "retry-move", 0, Claude, default);
        Assert.Equal(BindingMoveState.Committed, move.State);
        Assert.Equal(1, move.CommittedGeneration);
        var replay = await restarted.ApplyAsync(Logical, Original, "retry-move", 0, Claude, default);
        Assert.Equal(move, replay);
        Assert.Equal(savedPrepare ? 1 : 2, runtime.Preparations.Count);
        Assert.Equal("before", Assert.Single(runtime.LatestPreparation!.History).Content);
        Assert.Single(runtime.Created);
        Assert.Equal(2, Open().Read(Logical)!.Threads.Count);

        var conflict = await Assert.ThrowsAsync<SessionBindingConflictException>(() =>
            restarted.ApplyAsync(Logical, Original, "retry-move", 0, Claude with { Effort = "low" }, default));
        Assert.Equal("idempotency_conflict", conflict.Code);
        var stale = await Assert.ThrowsAsync<SessionBindingConflictException>(() =>
            restarted.ApplyAsync(Logical, Original, "stale-client", 0, Codex, default));
        Assert.Equal("binding_changed", stale.Code);
    }

    [Fact]
    public async Task A_destination_that_loses_authority_during_prepare_cannot_be_promoted()
    {
        var runtime = new Runtime { RevokeAfterPrepare = true };
        var result = await new SessionBindingSwitch(Open(), runtime)
            .ApplyAsync(Logical, Original, "revoked-move", 0, Claude, default);
        Assert.Equal(BindingMoveState.Rejected, result.State);
        Assert.Equal(0, Open().Read(Logical)!.Active.Generation);
        Assert.Contains("revoked-move", runtime.Discarded);
    }

    [Fact]
    public void A_stale_journal_writer_cannot_replace_the_current_binding()
    {
        var journal = Open();
        var original = journal.OpenOrAttach(Logical, Original);
        var secondWriter = Open();
        var stale = secondWriter.Read(Logical)!;
        var move = new SessionBindingMove("winner", 0, Claude, 0, BindingMoveState.Pending);
        Assert.True(journal.TrySave(original, original with { Revision = 1, Moves = [move] }));
        Assert.False(secondWriter.TrySave(stale, stale with { Revision = 1, Moves = [] }));
        Assert.Equal(move, Assert.Single(secondWriter.Read(Logical)!.Moves));
    }

    [Fact]
    public void Handoff_is_bounded_and_excludes_unapproved_roles_and_future_records()
    {
        var history = Enumerable.Range(1, 40)
            .Select(id => new BindingHistoryText(id, "assistant", "text", $"uid-{id}", $"turn-{id}"))
            .Append(new(41, "system", "text", null, "developer context"))
            .Append(new(42, "assistant", "tool_call", null, "private call"))
            .Append(new(43, "user", "text", "future", "future"))
            .ToArray();
        var context = SessionBindingSwitch.BoundedHandoff(history, 5, 42);
        Assert.Equal(24, context.Count);
        Assert.Equal("uid-17", context[0].MessageUid);
        Assert.Equal("uid-40", context[^1].MessageUid);

        var longText = new string('x', 15_000);
        var bounded = SessionBindingSwitch.BoundedHandoff([new(1, "user", "text", "long", longText)], 0, 1);
        Assert.Equal(12_000, Assert.Single(bounded).Content.Length);
    }

    public void Dispose() => ScratchDirectory.Delete(_root);

    private sealed class Runtime : ISessionBindingRuntime
    {
        public readonly List<BindingHistoryText> History = [];
        public readonly List<(string, string)> Deliveries = [];
        public readonly List<string> LogicalIds = [];
        public readonly List<string> Preparations = [];
        public readonly Dictionary<string, NativeSessionBinding> Created = [];
        public readonly HashSet<string> Discarded = [];
        public BindingPreparation? LatestPreparation;
        public bool RefusePreparation, LosePrepareResponse, LoseReadinessResponse, RevokeAfterPrepare;

        public void Deliver(SessionBindingJournal journal, string content)
        {
            var head = journal.Read(Logical)!;
            Deliveries.Add((content, head.Active.Native.NativeThreadId));
            LogicalIds.Add(head.LogicalId);
            History.Add(new(History.Count + 1, "user", "text", content, content));
        }

        public Task<bool> CanMoveAsync(string logicalId, SessionBinding source, BindingTarget destination, CancellationToken ct)
            => Task.FromResult(!RevokeAfterPrepare || Preparations.Count == 0);

        public Task<long> ReadHistoryBoundaryAsync(string logicalId, CancellationToken ct)
            => Task.FromResult(History.LastOrDefault()?.RecordId ?? 0);

        public Task<IReadOnlyList<BindingHistoryText>> ReadHistoryAsync(string logicalId, long after, long through, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<BindingHistoryText>>(History.ToArray());

        public Task<BindingPreparationResult> PrepareAsync(BindingPreparation preparation, CancellationToken ct)
        {
            LatestPreparation = preparation;
            var move = preparation.Move;
            Preparations.Add(move.Id);
            if (RefusePreparation) return Task.FromResult(new BindingPreparationResult(Refusal: "fake_prepare_refused"));
            if (!Created.TryGetValue(move.Id, out var binding))
            {
                binding = preparation.PreviousDestination?.Native is { } returning
                    ? returning with { Target = move.Destination }
                    : new("runtime-" + move.Destination.ProviderId, "native-" + move.Destination.ProviderId, move.Destination);
                Created.Add(move.Id, binding);
            }
            if (LosePrepareResponse)
            {
                LosePrepareResponse = false;
                throw new IOException("simulated interruption after the native thread started");
            }
            return Task.FromResult(new BindingPreparationResult(binding));
        }

        public Task<bool> IsReadyAsync(string logicalId, SessionBindingMove move, CancellationToken ct)
        {
            if (LoseReadinessResponse)
            {
                LoseReadinessResponse = false;
                throw new IOException("simulated interruption after the prepare receipt was saved");
            }
            return Task.FromResult(Created.ContainsKey(move.Id));
        }

        public Task DiscardAsync(string logicalId, string moveId, CancellationToken ct)
        {
            Discarded.Add(moveId);
            Created.Remove(moveId);
            return Task.CompletedTask;
        }
    }
}
