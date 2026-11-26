using System.Text.Json;
using RedCompute.Core.Sessions;
using RedCompute.PluginSdk;

namespace RedCompute.Plugin.ClaudeCode;

public partial class ClaudeSessionService
{
    private volatile ClaudeInferenceModel[]? _inferenceModels;
    internal sealed record ClaudeInferenceModel(string Alias, SessionInferenceModel Option, bool SupportsAuto);

    internal async Task<SessionInferenceOptions> GetInferenceOptionsAsync(string sessionId, CancellationToken ct)
    {
        var info = GetSessionInfo(sessionId);
        if (info is null) return new(null, null, []);
        var models = _sessions.TryGetValue(sessionId, out var session)
            ? await ReadInferenceModelsAsync(session, ct)
            : _inferenceModels ?? [];
        return InferenceOptions(LaunchModel(info.Model, _config.Model), info.Effort, models, info.Access.AccessLevel);
    }

    private async Task<ClaudeInferenceModel[]> ReadInferenceModelsAsync(ManagedSession session, CancellationToken ct)
    {
        await session.InferenceCatalogGate.WaitAsync(ct);
        try
        {
            if (session.InferenceModels is { } cached) return cached;
            var initialized = await SendInferenceControlAsync(session, new { subtype = "initialize" }, ct);
            if (initialized.Error is not null) return [];
            var models = ParseInferenceModels(initialized.Payload);
            if (models.Length == 0) return [];

            // Older CLIs can report a catalogue and still reject the flag-settings control.
            // An empty patch checks the running protocol without changing any session flags.
            var probe = await SendInferenceControlAsync(session, new
            {
                subtype = "apply_flag_settings",
                settings = new Dictionary<string, string?>(),
            }, ct);
            if (probe.Error is not null) return [];

            session.InferenceModels = models;
            _inferenceModels = models;
            return models;
        }
        finally { session.InferenceCatalogGate.Release(); }
    }

    internal string BindingModelId(string requested)
        => _inferenceModels?.FirstOrDefault(candidate => candidate.Alias == requested)?.Option.Id ?? requested;

    internal Task<bool> TrySetInferenceSettingsAsync(string sessionId, string? model, string? effort, CancellationToken ct)
        => _sessions.TryGetValue(sessionId, out var session)
            ? TrySetLiveInferenceSettingsAsync(session, model, effort, ct)
            : Task.FromResult(TrySetStoppedInferenceSettings(sessionId, model, effort));

    private async Task<bool> TrySetLiveInferenceSettingsAsync(ManagedSession session, string? model, string? effort, CancellationToken ct)
    {
        await session.AccessGate.WaitAsync(ct);
        try
        {
            var info = session.Info;
            if (info.Status != SessionStatus.Idle || session.UserInputUnconsumed
                || SessionWorkStates.RunningBackgroundTasks(info.BackgroundTasks) > 0)
                return false;

            var changedModel = model != info.Model;
            if (changedModel && info.Access.AccessLevel == SessionAccessLevels.Auto
                && session.InferenceModels?.Any(candidate => candidate.Option.Id == model && candidate.SupportsAuto) != true)
                return false;

            var settings = InferenceFlagSettings(info.Model, model, effort);
            // If a previous apply lost its acknowledgement, always name the model this time too.
            if (session.InferenceStateUnknown)
                settings["model"] = LaunchModel(model, _config.Model);
            var response = await SendInferenceControlAsync(session, new { subtype = "apply_flag_settings", settings }, ct);
            if (response.Error is not null)
            {
                session.InferenceStateUnknown |= response.Uncertain;
                return false;
            }

            var previous = (info.Model, info.Effort, info.QualityTier, info.ContextTokens, info.ContextWindow);
            info.Model = model;
            info.Effort = effort;
            if (changedModel)
            {
                info.QualityTier = null;
                info.ContextTokens = null;
                info.ContextWindow = null;
            }
            try { PersistSessionRecord(info); }
            catch
            {
                (info.Model, info.Effort, info.QualityTier, info.ContextTokens, info.ContextWindow) = previous;
                session.InferenceStateUnknown = true;
                throw;
            }
            session.InferenceStateUnknown = false;
            SessionUpdated?.Invoke(info);
            return true;
        }
        finally { session.AccessGate.Release(); }
    }

    private bool TrySetStoppedInferenceSettings(string sessionId, string? model, string? effort)
    {
        var record = _sessionStore.FindSession(sessionId);
        if (record is null || _restarting.ContainsKey(sessionId)) return false;
        if (record.Model != model)
        {
            record.Model = model;
            record.QualityTier = null;
            record.ContextWindow = null;
        }
        record.Effort = effort;
        _sessionStore.SaveSession(record);
        return true;
    }

    private async Task<bool> RestoreInferenceBeforeInputAsync(ManagedSession session)
    {
        await session.AccessGate.WaitAsync();
        try
        {
            if (!session.InferenceStateUnknown) return true;
            var info = session.Info;
            if (info.Status != SessionStatus.Idle || session.UserInputUnconsumed) return false;
            var settings = new Dictionary<string, string?>
            {
                ["model"] = LaunchModel(info.Model, _config.Model),
                ["effortLevel"] = info.Effort,
            };
            var response = await SendInferenceControlAsync(session,
                new { subtype = "apply_flag_settings", settings }, CancellationToken.None);
            if (response.Error is not null) return false;
            session.InferenceStateUnknown = false;
            return true;
        }
        finally { session.AccessGate.Release(); }
    }

    private async Task<ControlResponse> SendInferenceControlAsync(ManagedSession session, object request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var requestId = $"inference-{Guid.NewGuid():N}";
        var response = new TaskCompletionSource<ControlResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.PendingControlRequests[requestId] = response;
        try
        {
            await WriteControlLineAsync(session, new { type = "control_request", request_id = requestId, request });
            return await response.Task.WaitAsync(PermissionModeConfirmTimeout);
        }
        catch (TimeoutException)
        {
            return new("Claude Code did not confirm the inference settings change", Uncertain: true);
        }
        catch (Exception ex)
        {
            _log($"[Claude] Inference control failed for {session.Info.Id}: {ex.Message}", null);
            return new("Claude Code could not be reached", Uncertain: true);
        }
        finally { session.PendingControlRequests.TryRemove(requestId, out _); }
    }

    internal static Dictionary<string, string?> InferenceFlagSettings(string? previousModel, string? model, string? effort)
    {
        var settings = new Dictionary<string, string?> { ["effortLevel"] = effort };
        if (model != previousModel) settings["model"] = model;
        return settings;
    }

    internal static ClaudeInferenceModel[] ParseInferenceModels(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object || !payload.TryGetProperty("models", out var models)
            || models.ValueKind != JsonValueKind.Array) return [];

        var result = new List<ClaudeInferenceModel>();
        foreach (var entry in models.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object) continue;
            var alias = InferString(entry, "value");
            var resolved = InferString(entry, "resolvedModel");
            if (string.IsNullOrWhiteSpace(alias) || string.IsNullOrWhiteSpace(resolved)) continue;
            var efforts = entry.TryGetProperty("supportedEffortLevels", out var levels) && levels.ValueKind == JsonValueKind.Array
                ? levels.EnumerateArray().Where(level => level.ValueKind == JsonValueKind.String)
                    .Select(level => level.GetString()!).Where(level => !string.IsNullOrWhiteSpace(level)).Distinct().ToArray()
                : [];
            var supportsAuto = entry.TryGetProperty("supportsAutoMode", out var auto) && auto.ValueKind == JsonValueKind.True;
            result.Add(new(alias, new(resolved, InferString(entry, "displayName") ?? resolved, null, efforts), supportsAuto));
        }
        return result.ToArray();
    }

    internal static SessionInferenceOptions InferenceOptions(string? model, string? effort,
        IReadOnlyList<ClaudeInferenceModel> catalog, string? accessLevel)
    {
        var selected = catalog.FirstOrDefault(candidate => candidate.Alias == (model ?? "default"))?.Option.Id ?? model;
        var choices = catalog.Where(candidate => accessLevel != SessionAccessLevels.Auto || candidate.SupportsAuto)
            .GroupBy(candidate => candidate.Option.Id, StringComparer.Ordinal)
            .Select(group => (group.FirstOrDefault(candidate => candidate.Alias != "default") ?? group.First()).Option)
            .ToArray();
        return new(selected, effort, choices);
    }

    private static string? InferString(JsonElement value, string name)
        => value.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.String ? field.GetString() : null;
}
