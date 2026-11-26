using RedCompute.Core.Jobs;
using RedCompute.Core.Sessions;
using RedCompute.PluginSdk;

namespace RedCompute.App.Services.SessionBindings;

/// <summary>One authorized application of a provider change to the existing input queue.</summary>
internal sealed class LocalSessionBindingRuntime(SessionBindingCommands commands, RedLeafSessionReader reader,
    UnifiedSessionInfo logicalSession, SessionInputQueueItem item, string bearer, JobProvenance provenance, JobProvenance? creation)
    : ISessionBindingRuntime
{
    private BindingHandoffArtifact[] _artifacts = [];

    public Task<bool> CanMoveAsync(string logicalId, SessionBinding source, BindingTarget destination, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var oldProvider = commands.GetNative(source.Native.Target);
        var target = commands.GetNative(destination);
        var current = oldProvider?.GetSessionInfo(source.Native.RuntimeSessionId);
        return Task.FromResult(target is not null && current is not null && NativeSessionBindingPreparer.Supports(target)
            && logicalId == logicalSession.Id && source.Native.Target.NodeId == destination.NodeId
            && current.UserId == logicalSession.UserId && current.UserId == item.OwnerUserId
            && current.Confidential == logicalSession.Confidential
            && NativeSessionBindingPreparer.ChooseAccess(target, current) is not null
            && (current.Status == SessionStatus.Idle || current.Status == SessionStatus.Stopped
                && SessionStopReasons.IsAutoResumable(current.StopReason))
            && oldProvider!.GetPendingQuestions(current.Id).Count == 0
            && SessionWorkStates.RunningBackgroundTasks(oldProvider.GetBackgroundTasks(current.Id)) == 0);
    }

    public async Task<long> ReadHistoryBoundaryAsync(string logicalId, CancellationToken ct)
        => (await reader.ReadBindingHistoryAsync(logicalSession, bearer, 1, null, null, ct)).FirstOrDefault()?.RecordId ?? 0;

    public async Task<IReadOnlyList<BindingHistoryText>> ReadHistoryAsync(string logicalId, long after, long through, CancellationToken ct)
    {
        if (after == through) return [];
        var rows = await reader.ReadBindingHistoryAsync(logicalSession, bearer, 120, after, through, ct);
        _artifacts = rows.SelectMany(row => row.Artifacts ?? []).Take(12).ToArray();
        return rows;
    }

    public async Task<BindingPreparationResult> PrepareAsync(BindingPreparation preparation, CancellationToken ct)
    {
        var source = commands.GetNative(preparation.Source.Native.Target)?.GetSessionInfo(preparation.Source.Native.RuntimeSessionId);
        var destination = commands.GetNative(preparation.Move.Destination);
        if (source is null || destination is null) return new(Refusal: "binding_destination_unavailable");

        var profile = preparation.PreviousDestination is { } previous
            ? destination.GetSessionInfo(previous.Native.RuntimeSessionId)?.ProviderEntity : null;
        // Only the recovered token is copied into the runtime, never the originating API's bearer.
        using var execution = SessionScratch.PushExecutionToken(bearer);
        return await NativeSessionBindingPreparer.PrepareAsync(preparation, destination, source, profile,
            preparation.PreviousDestination is null ? creation ?? provenance : provenance,
            source.NativeDeveloperInstructions, _artifacts, ct);
    }

    public async Task<bool> IsReadyAsync(string logicalId, SessionBindingMove move, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (move.Prepared is not { } binding || commands.GetNative(binding.Target) is not { } provider
            || provider.GetSessionInfo(binding.RuntimeSessionId) is not { } next
            || !MatchesPrepared(next, binding)) return false;
        if (next.Status == SessionStatus.Stopped && SessionStopReasons.IsAutoResumable(next.StopReason))
        {
            using var execution = SessionScratch.PushExecutionToken(bearer);
            next = await provider.ResumeSessionAsync(binding.RuntimeSessionId, provenance);
        }
        return next is { Status: SessionStatus.Idle } && MatchesPrepared(next, binding);
    }

    private bool MatchesPrepared(UnifiedSessionInfo next, NativeSessionBinding binding)
        => next.ProviderSessionId == binding.NativeThreadId && next.UserId == logicalSession.UserId
            && next.Confidential == logicalSession.Confidential && next.RepositoryId == logicalSession.RepositoryId
            && next.ProjectPath == logicalSession.ProjectPath;

    public async Task DiscardAsync(string logicalId, string moveId, CancellationToken ct)
    {
        var destination = item.BindingChange!.Destination;
        if (commands.GetNative(destination) is not IIdleSessionParkingProvider parking) return;
        var snapshot = commands.Binding(logicalId);
        var previous = snapshot?.Threads.LastOrDefault(thread =>
            thread.Native.Target.ProviderInstanceId == destination.ProviderInstanceId);
        var id = previous?.Native.RuntimeSessionId ?? NativeSessionBindingPreparer.NewRuntimeId(logicalId, moveId);
        if (snapshot?.Active.Native.RuntimeSessionId == id) return;
        if (commands.GetNative(destination)?.GetSessionInfo(id) is { Status: SessionStatus.Idle } prepared)
            await parking.TryParkIdleSessionAsync(prepared.Id, SessionStopReasons.IdleTimeout, ct);
    }
}
