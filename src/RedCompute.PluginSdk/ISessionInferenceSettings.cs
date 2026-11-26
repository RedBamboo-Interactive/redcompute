namespace RedCompute.PluginSdk;

/// <summary>Session-local inference settings used when the owning provider starts its next turn.</summary>
public interface ISessionInferenceSettings
{
    Task<SessionInferenceOptions> GetInferenceOptionsAsync(string sessionId, CancellationToken ct);

    /// <summary>Canonical id the provider reports for a supported launch alias.</summary>
    string BindingModelId(string requested) => requested;

    /// <summary>Persists a selection already validated against the owning provider's catalog.</summary>
    Task<bool> TrySetInferenceSettingsAsync(string sessionId, string? model, string? effort, CancellationToken ct);

    /// <summary>Whether applying settings while the provider is working can affect the current turn.</summary>
    bool RequiresIdleForInferenceChanges => false;

    /// <summary>Whether a recoverable parked session must resume before confirming the choice.</summary>
    bool RequiresResidentForInferenceChanges => false;
}

public sealed record SessionInferenceOptions(string? Model, string? Effort, IReadOnlyList<SessionInferenceModel> Models);

public sealed record SessionInferenceModel(string Id, string Name, string? DefaultEffort, IReadOnlyList<string> SupportedEfforts);
