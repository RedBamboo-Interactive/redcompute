using RedCompute.Core.Jobs;

namespace RedCompute.PluginSdk;

public sealed record VoiceHistoryMessage(string Role, string Content);

public sealed record VoiceSessionCreateRequest(
    string SessionId,
    string OfferSdp,
    string Model,
    string Voice,
    string Instructions,
    JobProvenance Provenance,
    IReadOnlyList<VoiceHistoryMessage>? InitialHistory = null);

public sealed record VoiceSessionCreateResult(
    string ProviderSessionId,
    string AnswerSdp);

public sealed record VoiceProviderEvent(
    string Type,
    string? EventId = null,
    string? Delta = null,
    long? StartMs = null,
    long? EndMs = null,
    string? DelegationId = null,
    long? OffsetMs = null,
    string? ErrorCode = null,
    string? ErrorMessage = null);

public sealed record VoiceDelegationUpdate(
    string EventId,
    string Kind,
    string Content);

/// <summary>
/// Provider-neutral low-latency voice transport. Implementations own only their
/// external protocol and credentials. Leaf lifecycle, transcript projection and
/// delegated work stay in RedCompute/Nova.
/// </summary>
public interface IVoiceSessionProvider
{
    string VoiceProviderId { get; }
    string DefaultModel { get; }
    string DefaultVoice { get; }
    Task<VoiceSessionCreateResult> CreateVoiceSessionAsync(
        VoiceSessionCreateRequest request,
        Func<VoiceProviderEvent, Task> onEvent,
        CancellationToken ct);
    Task SendDelegationUpdateAsync(
        string providerSessionId,
        string delegationId,
        VoiceDelegationUpdate update,
        CancellationToken ct);
    Task CloseVoiceSessionAsync(string providerSessionId, CancellationToken ct);
}
