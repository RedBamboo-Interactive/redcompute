using RedCompute.Core.Sessions;
using RedCompute.PluginSdk;

namespace RedCompute.App.Services;

internal static class SessionInferenceSettings
{
    internal sealed record Choice(SessionInferenceOptions Options, string? Error = null,
        string? Message = null, bool Retryable = false);

    internal static Choice Resolve(SessionInferenceOptions options, QueuedInferenceChange change)
    {
        if (options.Models.Count == 0)
            return new(options, "inference_options_unavailable", "Inference options are currently unavailable", true);

        var selected = options.Models.FirstOrDefault(model => model.Id == (change.Model ?? options.Model));
        if (selected is null)
            return new(options, "unsupported_model", "Choose a model available to this session's provider");
        if (change.Effort is not null && !selected.SupportedEfforts.Contains(change.Effort, StringComparer.Ordinal))
            return new(options, "unsupported_effort", "Choose a reasoning effort supported by the selected model");

        var compatibleEffort = options.Effort is { } effort && selected.SupportedEfforts.Contains(effort, StringComparer.Ordinal);
        return new(options with
        {
            Model = selected.Id,
            Effort = change.Effort ?? (selected.Id == options.Model || compatibleEffort ? options.Effort : selected.DefaultEffort),
        });
    }

    internal static Choice Apply(ISessionInferenceSettings settings, UnifiedSessionInfo session,
        SessionInferenceOptions current, QueuedInferenceChange change)
    {
        var choice = Resolve(current, change);
        if (choice.Error is not null || (current.Model == choice.Options.Model && current.Effort == choice.Options.Effort))
            return choice;

        var model = change.Model is null ? session.Model : choice.Options.Model;
        return settings.TrySetInferenceSettings(session.Id, model, choice.Options.Effort)
            ? choice : new(current, "inference_change_unavailable", "This session could not change its inference settings", true);
    }
}

