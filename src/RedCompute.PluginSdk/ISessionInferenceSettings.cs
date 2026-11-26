namespace RedCompute.PluginSdk;

/// <summary>Session-local inference settings used when the owning provider starts its next turn.</summary>
public interface ISessionInferenceSettings
{
    Task<SessionInferenceOptions> GetInferenceOptionsAsync(string sessionId, CancellationToken ct);

    /// <summary>Persists a selection already validated against the owning provider's catalog.</summary>
    Task<bool> TrySetInferenceSettingsAsync(string sessionId, string? model, string? effort, CancellationToken ct);

    /// <summary>Whether applying settings while the provider is working can affect the current turn.</summary>
    bool RequiresIdleForInferenceChanges => false;
}

public sealed record SessionInferenceOptions(string? Model, string? Effort, IReadOnlyList<SessionInferenceModel> Models);

public sealed record SessionInferenceModel(string Id, string Name, string? DefaultEffort, IReadOnlyList<string> SupportedEfforts);
