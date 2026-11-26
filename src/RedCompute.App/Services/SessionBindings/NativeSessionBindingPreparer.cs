using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RedCompute.Core.Jobs;
using RedCompute.Core.Sessions;
using RedCompute.PluginSdk;

namespace RedCompute.App.Services.SessionBindings;

internal sealed record BindingHandoffArtifact(long RecordId, string Kind, string Name);

/// <summary>Prepares a physical provider behind the logical cutover. The caller supplies authorized policy and history.</summary>
internal static class NativeSessionBindingPreparer
{
    internal static bool Supports(ISessionProvider provider) => provider.ProviderId is "claude-code" or "codex" or "opencode"
        && provider is ISessionInferenceSettings and IIdleSessionParkingProvider
        && provider.Capabilities.HasFlag(SessionCapabilities.PersistentSessions | SessionCapabilities.Resume);

    internal static string? ChooseAccess(ISessionProvider provider, UnifiedSessionInfo source)
    {
        var sourceLevel = SessionAccessLevels.IsKnown(source.AccessLevel) ? source.AccessLevel! : SessionAccessLevels.Ask;
        var cap = SessionAccessLevels.IsKnown(source.MaxAccessLevel) ? source.MaxAccessLevel! : SessionAccessLevels.Full;
        var rank = SessionAccessLevels.Rank(SessionAccessLevels.Lower(sourceLevel, cap));
        return SessionAccessLevels.All.Take(rank + 1).Reverse().FirstOrDefault(provider.SupportedAccessLevels.Contains);
    }

    internal static string RuntimeId(BindingPreparation preparation)
    {
        if (preparation.PreviousDestination is { } previous) return previous.Native.RuntimeSessionId;
        return NewRuntimeId(preparation.LogicalId, preparation.Move.Id);
    }

    internal static string NewRuntimeId(string logicalId, string operationId)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(logicalId + ":" + operationId));
        return "binding-" + Convert.ToHexStringLower(hash.AsSpan(0, 16));
    }

    internal static async Task<BindingPreparationResult> PrepareAsync(BindingPreparation preparation,
        ISessionProvider provider, UnifiedSessionInfo source, string? providerEntity, JobProvenance provenance,
        string? developerInstructions, IReadOnlyList<BindingHandoffArtifact> authorizedArtifacts, CancellationToken ct)
    {
        var target = preparation.Move.Destination;
        if (!Supports(provider) || provider.ProviderId != target.ProviderId
            || SessionExecutionProfile.IsConversationOnly(source.ExecutionProfile))
            return new(Refusal: "binding_provider_unavailable");
        var accessLevel = ChooseAccess(provider, source);
        if (accessLevel is null)
            return new(Refusal: "binding_access_unsupported");

        var runtimeId = RuntimeId(preparation);
        var existing = provider.GetSessionInfo(runtimeId);
        if (existing is not null && (existing.UserId != source.UserId || existing.ProviderEntity != providerEntity
            || existing.ProjectPath != source.ProjectPath))
            return new(Refusal: "binding_identity_mismatch");
        if (preparation.PreviousDestination is not null && existing is null)
            return new(Refusal: "binding_previous_thread_missing");

        using var handoff = SessionHandoffScope.Push(new(runtimeId, preparation.Move.Id, HandoffJson(preparation, authorizedArtifacts)));
        var allowed = new SessionAccessState(accessLevel, source.MaxAccessLevel, source.Unattended,
            source.AccessLevelBeforePlan, source.AccessLevelSource);
        using var access = SessionAccessScope.Push(allowed);
        using var placement = SessionPlacementScope.Push(source.Placement?.ToJsonString());
        using var delivery = SessionDeliveryScope.Push(source.DeliveredAsOneMessage);

        UnifiedSessionInfo? prepared;
        if (existing is null)
        {
            ct.ThrowIfCancellationRequested();
            prepared = await provider.StartSessionAsync(source.ProjectPath, target.Model,
                source.UserId, source.UserName, source.UserAvatarUrl, target.Effort, null, null, null, null,
                providerEntity, source.RepositoryId, provenance, null, source.Confidential, developerInstructions);
        }
        else if (existing.Status == SessionStatus.Idle && preparation.PreviousDestination is null)
        {
            prepared = existing; // Lost acknowledgement after a fresh preparation persisted its deterministic ID.
        }
        else
        {
            if (existing.Status == SessionStatus.Idle
                && !await ((IIdleSessionParkingProvider)provider).TryParkIdleSessionAsync(runtimeId, SessionStopReasons.IdleTimeout, ct))
                return new(Refusal: "binding_previous_thread_busy");
            if (existing.Status != SessionStatus.Idle && (existing.Status != SessionStatus.Stopped
                || !SessionStopReasons.IsAutoResumable(existing.StopReason)))
                return new(Refusal: "binding_previous_thread_unavailable");
            if (provider.GetAccessState(runtimeId) != allowed && !provider.SetAccessState(runtimeId, allowed))
                return new(Refusal: "binding_access_unavailable");
            ct.ThrowIfCancellationRequested();
            prepared = await provider.ResumeSessionAsync(runtimeId, provenance);
        }

        if (prepared?.Status != SessionStatus.Idle || prepared.Id != runtimeId
            || prepared.ProviderSessionId is not { Length: > 0 } nativeId)
            return new(Refusal: "binding_prepare_refused");
        if (preparation.PreviousDestination is { } earlier && earlier.Native.NativeThreadId != nativeId)
            return new(Refusal: "binding_previous_thread_changed");
        if (!await ConfirmSettingsAsync((ISessionInferenceSettings)provider, prepared, target, ct))
            return new(Refusal: "binding_settings_unavailable");

        return new(new NativeSessionBinding(runtimeId, nativeId, target));
    }

    private static async Task<bool> ConfirmSettingsAsync(ISessionInferenceSettings settings, UnifiedSessionInfo prepared,
        BindingTarget target, CancellationToken ct)
    {
        var options = await settings.GetInferenceOptionsAsync(prepared.Id, ct);
        var canonical = settings.BindingModelId(target.Model);
        var model = options.Models.FirstOrDefault(candidate => candidate.Id == canonical);
        if (model is null || target.Effort is { } effort && !model.SupportedEfforts.Contains(effort, StringComparer.Ordinal))
            return false;
        if (options.Model == canonical && (target.Effort is null || options.Effort == target.Effort)) return true;
        if (!await settings.TrySetInferenceSettingsAsync(prepared.Id, canonical, target.Effort ?? model.DefaultEffort, ct))
            return false;
        var confirmed = await settings.GetInferenceOptionsAsync(prepared.Id, ct);
        return confirmed.Model == canonical && (target.Effort is null || confirmed.Effort == target.Effort);
    }

    internal static string HandoffJson(BindingPreparation request, IReadOnlyList<BindingHandoffArtifact> artifacts)
        => JsonSerializer.Serialize(new
        {
            throughRecordId = request.Move.HistoryThrough,
            turns = request.History.Select(turn => new { turn.Role, turn.MessageUid, turn.Content }),
            artifacts = artifacts.Where(artifact => artifact.RecordId > 0 && artifact.RecordId <= request.Move.HistoryThrough)
                .Take(12).Select(artifact => new
                {
                    artifact.RecordId,
                    kind = artifact.Kind.Length <= 32 ? artifact.Kind : artifact.Kind[..32],
                    name = artifact.Name.Length <= 120 ? artifact.Name : artifact.Name[..120],
                }),
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
}
