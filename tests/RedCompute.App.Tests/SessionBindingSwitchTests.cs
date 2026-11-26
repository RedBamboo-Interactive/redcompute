using System.Reflection;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using RedCompute.App.Services;
using RedCompute.App.Services.SessionBindings;
using RedCompute.Core.Capabilities;
using RedCompute.Core.Configuration;
using RedCompute.Core.Providers;
using RedCompute.Core.Sessions;
using RedCompute.PluginSdk;
using Xunit;

namespace RedCompute.App.Tests;

public sealed class SessionBindingSwitchTests
{
    private const string Logical = "session";
    private static readonly BindingTarget Codex = new("codex", "default-codex", "local", "codex-a", "low");
    private static readonly BindingTarget Claude = new("claude-code", "default-claude", "local", "claude-b", "high");
    private static readonly NativeSessionBinding Original = new("runtime-codex", "native-codex", Codex);

    [Fact]
    public async Task Changes_harness_and_returns_to_the_original_native_thread_in_fifo_order()
    {
        using var f = new SessionRecoveryTests.Fixture("maintenance_restart");
        var runtime = new Runtime();
        await f.Store.EnqueueAsync(f.Submission("before"));
        var toClaude = await AdmitAsync(f, "to-claude", 0, Claude);
        await f.Store.EnqueueAsync(f.Submission("during"));
        var service = new SessionBindingSwitch(f.Store, runtime);
        Assert.Null(await service.ApplyAsync(toClaude, Original, default));
        Assert.Empty(runtime.Preparations);

        var before = await ClaimOneAsync(f.Store);
        runtime.Deliver(await ActiveAsync(f.Store), before);
        await f.Store.MarkDeliveredAsync([before], before.MessageUid);
        Assert.Null(await service.ApplyAsync(toClaude, Original, default)); // the earlier turn is still open
        CloseTurn(f.Store);

        runtime.History.Add(new(2, "tool", "tool_result", null, "private tool payload"));
        var first = await service.ApplyAsync(toClaude, Original, default);
        Assert.Equal(BindingMoveState.Committed, first?.State);
        Assert.Equal(1, first?.CommittedGeneration);
        Assert.Equal(["before"], runtime.LatestPreparation!.History.Select(item => item.Content));
        await DeliverAsync(Reopen(f), runtime);

        var back = await AdmitAsync(f, "back-to-codex", 1, Codex);
        var returned = await new SessionBindingSwitch(Reopen(f), runtime).ApplyAsync(back, Original, default);
        Assert.Equal(2, returned?.CommittedGeneration);
        Assert.Equal(Original, returned?.Prepared);
        Assert.Equal(["during"], runtime.LatestPreparation!.History.Select(item => item.Content));

        await f.Store.EnqueueAsync(f.Submission("after"));
        await DeliverAsync(Reopen(f), runtime);
        Assert.Equal([("before", "native-codex"), ("during", "native-claude-code"), ("after", "native-codex")], runtime.Deliveries);
        Assert.Equal([0, 1, 2], (await Reopen(f).ReadBindingSnapshotAsync(Logical))!.Threads.Select(thread => thread.Generation));
        Assert.Equal([Logical], runtime.LogicalIds.Distinct());
        Assert.Equal(["before", "during", "after"], runtime.History.Where(row => row.Role == "user").Select(row => row.Content));
    }

    [Fact]
    public async Task Commands_and_events_follow_the_committed_binding_after_reopen()
    {
        using var f = new SessionRecoveryTests.Fixture("maintenance_restart");
        var runtime = new Runtime();
        var item = await AdmitAsync(f, "command-route", 0, Claude);
        var move = await new SessionBindingSwitch(f.Store, runtime).ApplyAsync(item, Original, default);
        var destination = Assert.IsType<NativeSessionBinding>(move!.Prepared);

        var registry = new CapabilityRegistry();
        var old = FakeProvider(Codex, Original.RuntimeSessionId);
        var next = FakeProvider(Claude, destination.RuntimeSessionId);
        registry.Register("ai-session", new CapabilityDefinition { Slug = "ai-session", DisplayName = "AI Session" },
            new CapabilityConfig(), new() { [Codex.ProviderInstanceId] = old, [Claude.ProviderInstanceId] = next }, null);
        var commands = new SessionBindingCommands(registry, new SessionBindingTranscriptRoutes(Reopen(f)));

        var (provider, info) = commands.Find(Logical);
        Assert.Equal(Logical, info?.Id);
        Assert.Equal("claude-code", info?.Provider);
        Assert.Equal("codex", info?.TranscriptProvider);
        var stored = old.GetSessionInfo(Original.RuntimeSessionId)!.AsLogicalSession(Logical, "codex");
        var listed = stored.WithActiveBinding(info!);
        Assert.Equal("claude-code", listed.Provider);
        Assert.Equal("codex", listed.TranscriptProvider);
        Assert.Equal("codex", stored.Provider);
        Assert.Equal((null, null), commands.Find(destination.RuntimeSessionId));
        Assert.Null(commands.ProjectEvent(old.GetSessionInfo(Original.RuntimeSessionId)!));
        Assert.Equal(Logical, commands.ProjectEvent(next.GetSessionInfo(destination.RuntimeSessionId)!)?.Id);

        await provider!.StopSessionAsync(Logical);
        Assert.Empty(((CommandProxy)(object)old).Stopped);
        Assert.Equal(destination.RuntimeSessionId, Assert.Single(((CommandProxy)(object)next).Stopped));
    }

    private static ICommandProvider FakeProvider(BindingTarget target, string runtimeId)
    {
        var provider = DispatchProxy.Create<ICommandProvider, CommandProxy>();
        var proxy = (CommandProxy)(object)provider;
        proxy.Info = new() { Id = runtimeId, Provider = target.ProviderId, ProjectName = "test", ProjectPath = "test" };
        return provider;
    }

    public interface ICommandProvider : IBackendProvider, ISessionProvider;

    public class CommandProxy : DispatchProxy
    {
        public UnifiedSessionInfo Info { get; set; } = null!;
        public List<string> Stopped { get; } = [];

        protected override object? Invoke(MethodInfo? method, object?[]? args) => method?.Name switch
        {
            "get_ProviderId" => Info.Provider,
            nameof(ISessionProvider.GetSessionInfo) => (string)args![0]! == Info.Id ? Info : null,
            nameof(ISessionProvider.StopSessionAsync) => Stop((string)args![0]!),
            _ => throw new InvalidOperationException($"Unexpected call to {method?.Name}"),
        };

        private Task Stop(string id)
        {
            Stopped.Add(id);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Preparation_refusal_keeps_the_old_thread_and_writes_the_failed_queue_receipt()
    {
        using var f = new SessionRecoveryTests.Fixture("maintenance_restart");
        var runtime = new Runtime { RefusePreparation = true };
        var admitted = await AdmitAsync(f, "unready-claude", 0, Claude);
        var service = new SessionBindingSwitch(f.Store, runtime);
        var rejected = await service.ApplyAsync(admitted, Original, default);
        Assert.Equal(BindingMoveState.Rejected, rejected?.State);
        Assert.Equal("fake_prepare_refused", rejected?.Error);
        Assert.Equal(Original, await ActiveAsync(Reopen(f)));
        var commands = new SessionBindingCommands(new(), new SessionBindingTranscriptRoutes(Reopen(f)));
        Assert.False(commands.IsBound(Logical));
        var replay = await service.ApplyAsync(admitted, Original, default);
        Assert.Equal(rejected, replay);
        Assert.Single(runtime.Preparations);
        Assert.Contains(admitted.Id, runtime.Discarded);
        var item = await ReceiptAsync(f.Store, admitted);
        Assert.Equal(SessionInputQueueState.Failed, item.State);
        Assert.False(item.ErrorRetryable);
        Assert.Null(item.TurnId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Restart_at_either_side_of_native_prepare_reuses_the_operation_and_commits_once(bool savedPrepare)
    {
        using var f = new SessionRecoveryTests.Fixture("maintenance_restart");
        var runtime = new Runtime { LosePrepareResponse = !savedPrepare, LoseReadinessResponse = savedPrepare };
        runtime.History.Add(new(1, "user", "text", "before", "before"));
        var admitted = await AdmitAsync(f, "retry-move", 0, Claude);
        var first = new SessionBindingSwitch(f.Store, runtime);
        await Assert.ThrowsAsync<IOException>(() => first.ApplyAsync(admitted, Original, default));

        var reopened = Reopen(f);
        Assert.Equal(Original, await ActiveAsync(reopened));
        var interrupted = await ReceiptAsync(reopened, admitted);
        Assert.Equal(SessionInputQueueState.Pending, interrupted.State);
        Assert.Equal(savedPrepare ? BindingMoveState.Prepared : BindingMoveState.Pending, interrupted.BindingMove?.State);

        runtime.History.Add(new(2, "user", "text", "later", "later"));
        var restarted = new SessionBindingSwitch(reopened, runtime);
        var move = await restarted.ApplyAsync(admitted, Original, default);
        Assert.Equal(BindingMoveState.Committed, move?.State);
        Assert.Equal(1, move?.CommittedGeneration);
        var replay = await restarted.ApplyAsync(admitted, Original, default);
        Assert.Equal(move, replay);
        Assert.Equal(savedPrepare ? 1 : 2, runtime.Preparations.Count);
        Assert.Equal("before", Assert.Single(runtime.LatestPreparation!.History).Content);
        Assert.Single(runtime.Created);
        Assert.Equal(2, (await Reopen(f).ReadBindingSnapshotAsync(Logical))!.Threads.Count);
        var receipt = await ReceiptAsync(Reopen(f), admitted);
        Assert.Equal(SessionInputQueueState.Delivered, receipt.State);
        Assert.Null(receipt.TurnId);
        Assert.Null(receipt.DeliveredMessageUid);
        Assert.Equal(admitted.Id, (await AdmitAsync(f, "retry-move", 0, Claude)).Id);

        var conflict = await Assert.ThrowsAsync<SessionInputQueueStoreException>(() =>
            AdmitAsync(f, "retry-move", 0, Claude with { Effort = "low" }));
        Assert.Equal("idempotency_conflict", conflict.Code);
        var stale = await AdmitAsync(f, "stale-client", 0, Codex);
        Assert.Equal("binding_changed", (await restarted.ApplyAsync(stale, Original, default))?.Error);
    }

    [Fact]
    public async Task Receipt_failure_rolls_back_promotion_and_the_same_prepared_operation_can_settle()
    {
        using var f = new SessionRecoveryTests.Fixture("maintenance_restart");
        var runtime = new Runtime();
        var item = await AdmitAsync(f, "atomic-move", 0, Claude);
        await using var db = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = f.Attachments.DatabasePath, Pooling = false }.ToString());
        await db.OpenAsync();
        await using var command = db.CreateCommand();
        command.CommandText = """
            CREATE TRIGGER FailBindingReceipt BEFORE UPDATE OF State ON SessionQueuedInputs
            WHEN NEW.BindingChangeJson IS NOT NULL AND NEW.State = 'Delivered'
            BEGIN SELECT RAISE(ABORT, 'simulated receipt write failure'); END;
            """;
        await command.ExecuteNonQueryAsync();

        var service = new SessionBindingSwitch(f.Store, runtime);
        await Assert.ThrowsAsync<SqliteException>(() => service.ApplyAsync(item, Original, default));
        var reopened = Reopen(f);
        Assert.Equal(Original, await ActiveAsync(reopened));
        var interrupted = await ReceiptAsync(reopened, item);
        Assert.Equal(SessionInputQueueState.Pending, interrupted.State);
        Assert.Equal(BindingMoveState.Prepared, interrupted.BindingMove?.State);
        Assert.Null(interrupted.MetadataJson);

        command.CommandText = "DROP TRIGGER FailBindingReceipt";
        await command.ExecuteNonQueryAsync();
        var completed = await new SessionBindingSwitch(reopened, runtime).ApplyAsync(item, Original, default);
        var receipt = await ReceiptAsync(Reopen(f), item);
        var snapshot = (await Reopen(f).ReadBindingSnapshotAsync(Logical))!;
        Assert.Equal(SessionInputQueueState.Delivered, receipt.State);
        Assert.Equal(snapshot.Active.Generation, completed?.CommittedGeneration);
        Assert.Equal(snapshot.Active.Native, completed?.Prepared);
        using var metadata = JsonDocument.Parse(receipt.MetadataJson!);
        Assert.Equal(1, metadata.RootElement.GetProperty("applied").GetProperty("generation").GetInt64());
        Assert.Single(runtime.Preparations);
    }

    [Fact]
    public async Task A_lost_source_seal_reply_retains_the_old_binding_and_blocks_later_input_until_exact_replay()
    {
        using var f = new SessionRecoveryTests.Fixture("maintenance_restart");
        var runtime = new Runtime { LoseSealResponse = true };
        var target = Codex with { NodeId = "remote" };
        var operation = await AdmitAsync(f, "move-node", 0, target, changesNode: true);
        var later = (await f.Store.EnqueueAsync(f.Submission("after-move"))).Item;
        var transaction = new SessionBindingSwitch(f.Store, runtime);
        await Assert.ThrowsAsync<IOException>(() => transaction.ApplyAsync(operation, Original, default));

        var reopened = Reopen(f);
        Assert.Equal(Original, await ActiveAsync(reopened));
        var held = await ReceiptAsync(reopened, operation);
        Assert.Equal(BindingMoveState.SealPending, held.BindingMove?.State);
        var cancel = await Assert.ThrowsAsync<SessionInputQueueStoreException>(() =>
            reopened.CancelAsync(Logical, operation.Id, operation.OwnerUserId));
        Assert.Equal("binding_seal_pending", cancel.Code);
        Assert.Equal(operation.Id, (await reopened.GetHeadAsync(Logical))!.Id);
        Assert.Empty(await reopened.ClaimBatchAsync(Logical, "test", TimeSpan.FromMinutes(1)));

        // A failed candidate readiness check after seal intent cannot silently restore old dispatch.
        runtime.RevokeAfterPrepare = true;
        var resumed = new SessionBindingSwitch(reopened, runtime);
        var moved = await resumed.ApplyAsync(operation, Original, default);
        Assert.Equal(BindingMoveState.Committed, moved?.State);
        Assert.Equal(1, moved?.CommittedGeneration);
        Assert.Equal(target, (await ActiveAsync(Reopen(f))).Target);
        Assert.Equal([operation.Id, operation.Id], runtime.Seals);
        Assert.Single(runtime.Preparations);
        Assert.Equal(later.Id, (await reopened.GetHeadAsync(Logical))!.Id);
        Assert.Equal(moved, await resumed.ApplyAsync(operation, Original, default));
        Assert.Equal(2, runtime.Seals.Count);
    }

    [Fact]
    public async Task A_binding_control_blocks_later_steering_and_interrupt_delivery()
    {
        using var f = new SessionRecoveryTests.Fixture("maintenance_restart");
        var item = await AdmitAsync(f, "control", 0, Claude);
        await f.Store.EnqueueAsync(f.Submission("interrupt") with { DeliveryPolicy = SessionInputDeliveryPolicy.InterruptCurrent });
        await f.Store.EnqueueAsync(f.Submission("steer") with { DeliveryPolicy = SessionInputDeliveryPolicy.SteerCurrent });
        Assert.Equal(item.Id, (await f.Store.GetHeadAsync(Logical))?.Id);
        Assert.Null(await f.Store.GetSteerHeadAsync(Logical));
        Assert.Empty(await f.Store.ClaimBatchAsync(Logical, "test", TimeSpan.FromMinutes(1)));
        Assert.Empty(await f.Store.ClaimSteerBatchAsync(Logical, "test", TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public async Task A_destination_that_loses_authority_during_prepare_cannot_be_promoted()
    {
        using var f = new SessionRecoveryTests.Fixture("maintenance_restart");
        var runtime = new Runtime { RevokeAfterPrepare = true };
        var move = await AdmitAsync(f, "revoked", 0, Claude);
        var result = await new SessionBindingSwitch(f.Store, runtime).ApplyAsync(move, Original, default);
        Assert.Equal(BindingMoveState.Rejected, result?.State);
        Assert.Equal(0, (await Reopen(f).ReadBindingSnapshotAsync(Logical))!.Active.Generation);
        Assert.Contains(move.Id, runtime.Discarded);
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

    private static SessionInputQueueStore Reopen(SessionRecoveryTests.Fixture f) => new(f.Config, f.Attachments);

    private static async Task<SessionInputQueueItem> AdmitAsync(SessionRecoveryTests.Fixture f, string key,
        long generation, BindingTarget destination, bool changesNode = false)
        => (await f.Store.EnqueueAsync(f.Submission(key) with
        {
            Input = [],
            IdempotencyKey = key,
            BindingChange = new(generation, destination, changesNode),
        })).Item;

    private static async Task<SessionInputQueueItem> ReceiptAsync(SessionInputQueueStore queue, SessionInputQueueItem item)
        => Assert.IsType<SessionInputQueueItem>(await queue.GetAsync(item.SessionId, item.Id, item.OwnerUserId));

    private static async Task<NativeSessionBinding> ActiveAsync(SessionInputQueueStore queue)
        => (await queue.ReadBindingSnapshotAsync(Logical))?.Active.Native ?? Original;

    private static async Task<SessionInputQueueItem> ClaimOneAsync(SessionInputQueueStore queue)
        => Assert.Single(await queue.ClaimBatchAsync(Logical, "test", TimeSpan.FromMinutes(1)));

    private static void CloseTurn(SessionInputQueueStore queue)
    {
        queue.MarkTurnActive(Logical);
        queue.CloseTurn(Logical, new("Idle", null, null, null, 0));
    }

    private static async Task DeliverAsync(SessionInputQueueStore queue, Runtime runtime)
    {
        var item = await ClaimOneAsync(queue);
        runtime.Deliver(await ActiveAsync(queue), item);
        await queue.MarkDeliveredAsync([item], item.MessageUid);
        CloseTurn(queue);
    }

    private sealed class Runtime : ISessionBindingRuntime
    {
        public readonly List<BindingHistoryText> History = [];
        public readonly List<(string, string)> Deliveries = [];
        public readonly List<string> LogicalIds = [];
        public readonly List<string> Preparations = [];
        public readonly Dictionary<string, NativeSessionBinding> Created = [];
        public readonly HashSet<string> Discarded = [];
        public BindingPreparation? LatestPreparation;
        public bool RefusePreparation, LosePrepareResponse, LoseReadinessResponse, RevokeAfterPrepare, LoseSealResponse;
        public readonly List<string> Seals = [];

        public void Deliver(NativeSessionBinding binding, SessionInputQueueItem item)
        {
            Deliveries.Add((item.DisplayContent, binding.NativeThreadId));
            LogicalIds.Add(item.SessionId);
            History.Add(new(History.Count + 1, "user", "text", item.MessageUid, item.DisplayContent));
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

        public Task<bool> SealSourceAsync(string logicalId, SessionBinding source, SessionBindingMove move, CancellationToken ct)
        {
            Seals.Add(move.Id);
            if (LoseSealResponse)
            {
                LoseSealResponse = false;
                throw new IOException("simulated lost response after sealing the source");
            }
            return Task.FromResult(true);
        }

        public Task DiscardAsync(string logicalId, string moveId, CancellationToken ct)
        {
            Discarded.Add(moveId);
            Created.Remove(moveId);
            return Task.CompletedTask;
        }
    }
}
