using System.Security.Claims;
using RedCompute.App.Services.Nodes;
using RedCompute.Core.Jobs;
using RedCompute.Core.Sessions;
using RedCompute.PluginSdk;
using RedCompute.PluginSdk.Hosting.Local;

namespace RedCompute.App.Services.SessionBindings;

internal sealed class NodeSessionBindingRuntime(ControllerRemoteSessions controller, ClaimsPrincipal caller,
    SessionBindingCommands commands, RedLeafSessionReader reader, UnifiedSessionInfo logicalSession,
    SessionInputQueueStore queue, SessionInputQueueItem item, string bearer, JobProvenance provenance, JobProvenance? creation)
    : ISessionBindingRuntime
{
    private readonly LocalSessionBindingRuntime _local = new(commands, reader, logicalSession, item, bearer, provenance, creation);

    public async Task<bool> CanMoveAsync(string logicalId, SessionBinding source, BindingTarget destination, CancellationToken ct)
    {
        if (logicalId != logicalSession.Id || logicalSession.UserId != item.OwnerUserId || logicalSession.Confidential
            || SessionExecutionProfile.IsConversationOnly(logicalSession.ExecutionProfile)
            || source.Native.Target.ProviderId != destination.ProviderId)
            return false;
        if (source.Native.Remote is { } remote)
        {
            if (item.BindingChange?.ReplacesHeldBinding == true)
            {
                if (await queue.HasUnsettledControllerRemoteDispatchAsync(logicalId, ct)) return false;
            }
            else if (!await controller.IsMoveSourceIdleAsync(caller, remote, item.Id, ct))
                return false;
        }
        else if (!LocalSourceIdle(commands, source.Native, item.OwnerUserId)
            || destination.Runtime is { } remoteDestination
                && await LocalRevisionAsync(source.Native, ct) != remoteDestination.Revision)
            return false;

        if (destination.Runtime is not { } registration)
            return LocalDestination(logicalId, destination) is { } existing
                && LocalSourceIdle(commands, existing.Native, item.OwnerUserId)
                && await LocalRevisionAsync(existing.Native, ct) == source.Native.Remote?.Placement.Revision;
        var placement = new RemoteSessionPlacement(destination.NodeId, registration.RuntimeId, registration.Workspace,
            registration.Revision, destination.ProviderId, destination.ProviderInstanceId, destination.Model, destination.Effort);
        var cap = logicalSession.MaxAccessLevel ?? SessionAccessLevels.Ask;
        var preview = await controller.ValidateMoveAsync(caller, logicalId, checked(source.Generation + 1), item.Id, placement, cap, ct);
        return preview.AccessLevel == registration.AccessLevel && preview.MaxAccessLevel == registration.MaxAccessLevel;
    }

    public Task<long> ReadHistoryBoundaryAsync(string logicalId, CancellationToken ct)
        => _local.ReadHistoryBoundaryAsync(logicalId, ct);

    public Task<IReadOnlyList<BindingHistoryText>> ReadHistoryAsync(string logicalId, long after, long through, CancellationToken ct)
        => _local.ReadHistoryAsync(logicalId, after, through, ct);

    public async Task<BindingPreparationResult> PrepareAsync(BindingPreparation preparation, CancellationToken ct)
    {
        if (preparation.Move.Destination.Runtime is not null)
        {
            try
            {
                if (preparation.PreviousDestination is { Native.Remote: { } previousRemote, RetirementMoveId: { } retirement } retired)
                {
                    if (!await controller.RetireRedirectedSourceAsync(caller, previousRemote, retirement, ct))
                        return new(Refusal: "binding_previous_thread_unavailable");
                    await queue.ConfirmNodeRetirementAsync(logicalSession.Id, retired, ct);
                }
                return new(await controller.PrepareMoveAsync(caller, preparation, AuthorizedArtifacts(preparation), ct));
            }
            catch (ControllerRemoteSessionException ex) when (ex.Status is >= 400 and < 500)
            {
                return new(Refusal: ex.Code);
            }
        }

        if (preparation.PreviousDestination is not { } previous
            || commands.GetNative(previous.Native.Target) is not { } provider
            || provider.GetSessionInfo(previous.Native.RuntimeSessionId) is not { } prior
            || prior.UserId != item.OwnerUserId || prior.Confidential != logicalSession.Confidential)
            return new(Refusal: "binding_previous_thread_missing");
        using var execution = SessionScratch.PushExecutionToken(bearer);
        return await NativeSessionBindingPreparer.PrepareAsync(preparation, provider, prior, prior.ProviderEntity,
            provenance, prior.NativeDeveloperInstructions, AuthorizedArtifacts(preparation), ct);
    }

    public Task<bool> IsReadyAsync(string logicalId, SessionBindingMove move, CancellationToken ct)
        => move.Prepared?.Remote is not null ? Task.FromResult(true) : _local.IsReadyAsync(logicalId, move, ct);

    public async Task<bool> SealSourceAsync(string logicalId, SessionBinding source, SessionBindingMove move, CancellationToken ct)
    {
        if (source.Native.Remote is { } remote)
        {
            // The controller is the only issuer. A held generation with no unsettled send can
            // be retired locally even when the peer cannot acknowledge a seal.
            if (item.BindingChange?.ReplacesHeldBinding == true)
                return !await queue.HasUnsettledControllerRemoteDispatchAsync(logicalId, ct);
            return await controller.SealMoveSourceAsync(caller, remote, move, ct);
        }
        if (!LocalSourceIdle(commands, source.Native, item.OwnerUserId))
            return false;
        var provider = commands.GetNative(source.Native.Target);
        var current = provider?.GetSessionInfo(source.Native.RuntimeSessionId);
        return current?.Status == SessionStatus.Stopped
            || provider is IIdleSessionParkingProvider parking
                && await parking.TryParkIdleSessionAsync(source.Native.RuntimeSessionId, SessionStopReasons.IdleTimeout, ct);
    }

    public async Task DiscardAsync(string logicalId, string moveId, CancellationToken ct)
    {
        var change = item.BindingChange!;
        if (change.Destination.Runtime is null)
        {
            await _local.DiscardAsync(logicalId, moveId, ct);
            return;
        }

        var move = new SessionBindingMove(moveId, change.ExpectedGeneration, change.Destination, 0, BindingMoveState.Rejected);
        await controller.DiscardMoveAsync(caller, logicalId, move, ct);
    }

    internal static bool LocalSourceIdle(SessionBindingCommands commands, NativeSessionBinding binding, string owner)
        => commands.GetNative(binding.Target) is { } provider && NativeSessionBindingPreparer.Supports(provider)
            && provider.GetSessionInfo(binding.RuntimeSessionId) is { } info
            && info.ProviderSessionId == binding.NativeThreadId && info.UserId == owner
            && (info.Status == SessionStatus.Idle || info.Status == SessionStatus.Stopped
                && SessionStopReasons.IsAutoResumable(info.StopReason))
            && provider.GetPendingQuestions(info.Id).Count == 0
            && SessionWorkStates.RunningBackgroundTasks(provider.GetBackgroundTasks(info.Id)) == 0;

    private Task<string?> LocalRevisionAsync(NativeSessionBinding binding, CancellationToken ct)
        => HostGit.ReadCleanHeadAsync(LocalExecutionHost.Shared,
            commands.GetNative(binding.Target)?.GetSessionInfo(binding.RuntimeSessionId)?.ProjectPath, ct);

    private SessionBinding? LocalDestination(string logicalId, BindingTarget destination)
        => commands.Binding(logicalId)?.Threads.LastOrDefault(thread => thread.Native.Remote is null
            && thread.Native.Target.NodeId == destination.NodeId && thread.Native.Target.ProviderId == destination.ProviderId
            && thread.Native.Target.ProviderInstanceId == destination.ProviderInstanceId);

    private BindingHandoffArtifact[] AuthorizedArtifacts(BindingPreparation preparation)
        => _local.Artifacts.Where(artifact => artifact.RecordId <= preparation.Move.HistoryThrough).Select(artifact => artifact with
        {
            Kind = artifact.Kind.Length <= 32 ? artifact.Kind : artifact.Kind[..32],
            Name = artifact.Name.Length <= 120 ? artifact.Name : artifact.Name[..120],
        }).Take(12).ToArray();
}
