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
    string? ErrorMessage = null,
    string? ClientEventId = null);

public sealed record VoiceDelegationUpdate(
    string EventId,
    string Kind,
    string Content);

/// <summary>
/// Unsolicited backend content for an open voice session, not tied to a delegation.
/// <see cref="VoiceBackendUpdateModes.Speak"/> asks the voice model to convey it aloud;
/// <see cref="VoiceBackendUpdateModes.Context"/> only adds it to the conversation.
/// </summary>
public sealed record VoiceBackendUpdate(
    string EventId,
    string UpdateId,
    string Mode,
    string Content);

public static class VoiceBackendUpdateModes
{
    public const string Speak = "speak";
    public const string Context = "context";

    public static bool IsKnown(string? mode) => mode is Speak or Context;
}

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

    /// <summary>
    /// Whether <see cref="SendBackendUpdateAsync"/> is implemented. Providers that
    /// predate unsolicited backend updates keep the default and report unsupported.
    /// </summary>
    bool SupportsBackendUpdates => false;

    /// <summary>
    /// Delivers one unsolicited backend update. The core decides timing: it never calls
    /// this for a spoken update while the user or the voice model is speaking.
    /// </summary>
    Task SendBackendUpdateAsync(string providerSessionId, VoiceBackendUpdate update,
        CancellationToken ct)
        => throw new NotSupportedException($"{VoiceProviderId} does not accept backend updates");
}
