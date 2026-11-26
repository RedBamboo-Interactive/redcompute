using System.Text.Json;
using RedCompute.Core.Configuration;
using RedCompute.PluginSdk;

namespace RedCompute.Plugin.OpenCode;

public partial class OpenCodeSessionService
{
    private const string DefaultEffort = "default";

    internal SessionInferenceOptions GetInferenceOptions(string sessionId)
    {
        var info = GetSessionInfo(sessionId);
        if (info is null) return new(null, null, []);
        var models = info.InferenceModels ?? [];
        if (info.ProviderEntity is { } reference)
        {
            var allowed = DeclaredInferenceModels(_resolveSharedProfile?.Invoke(reference));
            _sessions.TryGetValue(sessionId, out var session);
            var residentScope = session?.InferenceModelScope;
            models = models.Where(model => allowed.Contains(model.Id)
                && (residentScope is null || residentScope.Contains(model.Id))).ToArray();
        }
        return new(info.Model, info.Effort, models);
    }

    internal async Task<bool> TrySetInferenceSettingsAsync(
        string sessionId, string? model, string? effort, CancellationToken ct)
    {
        if (!_sessions.TryGetValue(sessionId, out var session)) return false;
        await session.AdmissionLock.WaitAsync(ct);
        try
        {
            var info = session.Info;
            var target = model ?? info.Model;
            if (info.Status != "Idle" || session.Cts.IsCancellationRequested || target is null
                || !GetInferenceOptions(sessionId).Models.Any(choice => choice.Id == target))
                return false;

            var confirmed = await SelectInferenceOptionsAsync(session, target, effort);
            if (confirmed is null) return false;

            var previous = (info.Model, info.Effort, info.InferenceModels, info.InferenceSettingsPinned, info.QualityTier, info.ContextWindow);
            AcceptInferenceOptions(info, confirmed);
            info.InferenceSettingsPinned = true;
            if (previous.Model != info.Model)
            {
                info.QualityTier = null;
                info.ContextWindow = null;
            }
            try { PersistSessionRecord(info); }
            catch
            {
                (info.Model, info.Effort, info.InferenceModels, info.InferenceSettingsPinned, info.QualityTier, info.ContextWindow) = previous;
                session.InferenceStateUnknown = true;
                throw;
            }
            session.InferenceStateUnknown = false;
            SessionUpdated?.Invoke(info);
            return true;
        }
        finally { session.AdmissionLock.Release(); }
    }

    // Called while SendInput holds AdmissionLock. A timed-out control may have applied on
    // the native side, so no prompt can go through until the durable selection is restored.
    private async Task<bool> RestoreInferenceBeforeInputAsync(ManagedSession session)
    {
        if (!session.InferenceStateUnknown) return true;
        if (session.Info.Model is not { } model) return false;
        var confirmed = await SelectInferenceOptionsAsync(session, model, session.Info.Effort);
        if (confirmed is null || confirmed.Effort != session.Info.Effort) return false;
        session.InferenceStateUnknown = false;
        return true;
    }

    private async Task<SessionInferenceOptions?> SelectInferenceOptionsAsync(
        ManagedSession session, string model, string? effort)
    {
        if (session.AcpSessionId is null) return null;
        var info = session.Info;
        var models = info.InferenceModels ?? [];
        try
        {
            SessionInferenceOptions? selection;
            if (session.InferenceStateUnknown || info.Model != model)
            {
                var value = effort is not null and not DefaultEffort ? $"{model}/{effort}" : model;
                selection = ParseInferenceOptions(await SetConfigAsync(session, "model", value), models);
                if (effort == DefaultEffort && selection?.Model == model && selection.Effort != DefaultEffort)
                    selection = ParseInferenceOptions(await SetConfigAsync(session, "effort", DefaultEffort), models);
            }
            else
            {
                selection = ParseInferenceOptions(await SetConfigAsync(session, "effort", effort ?? DefaultEffort), models);
            }

            if (selection?.Model == model && (effort is null || selection.Effort == effort))
                return selection;
        }
        catch (Exception ex)
        {
            _log($"[OpenCode] Inference control failed for {info.Id}: {ex.GetType().Name}", null);
        }
        session.InferenceStateUnknown = true;
        return null;
    }

    private Task<JsonElement> SetConfigAsync(ManagedSession session, string configId, object value)
        => SendRequest(session, "session/set_config_option", new { sessionId = session.AcpSessionId, configId, value });

    private static void AcceptInferenceOptions(OpenCodeSessionInfo info, SessionInferenceOptions options)
    {
        info.Model = options.Model;
        info.Effort = options.Effort;
        info.InferenceModels = options.Models.ToArray();
    }

    internal static SessionInferenceOptions? ParseInferenceOptions(
        JsonElement result, IReadOnlyList<SessionInferenceModel>? before = null)
    {
        if (!TryReadArray(result, "configOptions", out var options)) return null;
        var config = options.EnumerateArray().Where(value => value.ValueKind == JsonValueKind.Object).ToArray();
        var modelOption = config.FirstOrDefault(option => ConfigString(option, "id") == "model");
        var effortOption = config.FirstOrDefault(option => ConfigString(option, "id") == "effort");
        var selected = ConfigString(modelOption, "currentValue");
        if (selected is null || !TryReadArray(modelOption, "options", out var modelEntries)) return null;

        var efforts = TryReadArray(effortOption, "options", out var effortEntries)
            ? ReadSelectValues(effortEntries).Select(option => option.Value).Distinct().ToArray()
            : [];
        var effort = ConfigString(effortOption, "currentValue");
        if (effort is not null && !efforts.Contains(effort, StringComparer.Ordinal)) return null;
        var previous = (before ?? []).ToDictionary(option => option.Id, StringComparer.Ordinal);
        var models = ReadSelectValues(modelEntries).DistinctBy(option => option.Value).Select(option =>
            new SessionInferenceModel(option.Value, option.Name, null,
                option.Value == selected ? efforts : previous.GetValueOrDefault(option.Value)?.SupportedEfforts ?? []))
            .ToArray();
        return models.Any(model => model.Id == selected) ? new(selected, effort, models) : null;
    }

    private static IEnumerable<(string Value, string Name)> ReadSelectValues(JsonElement options)
    {
        foreach (var option in options.EnumerateArray())
        {
            var value = ConfigString(option, "value");
            if (!string.IsNullOrWhiteSpace(value))
                yield return (value, ConfigString(option, "name") ?? value);
            else if (TryReadArray(option, "options", out var group))
                foreach (var member in group.EnumerateArray())
                    if (ConfigString(member, "value") is { Length: > 0 } memberValue)
                        yield return (memberValue, ConfigString(member, "name") ?? memberValue);
        }
    }

    private static bool TryReadArray(JsonElement value, string property, out JsonElement array)
    {
        array = default;
        return value.ValueKind == JsonValueKind.Object && value.TryGetProperty(property, out array)
            && array.ValueKind == JsonValueKind.Array;
    }

    private static string? ConfigString(JsonElement value, string property)
        => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(property, out var text)
            && text.ValueKind == JsonValueKind.String ? text.GetString() : null;

    private static SessionInferenceModel[]? ReadInferenceModels(string? json)
    {
        if (json is null) return null;
        try { return JsonSerializer.Deserialize<SessionInferenceModel[]>(json); }
        catch (JsonException) { return null; }
    }

    private static HashSet<string> DeclaredInferenceModels(ProviderConfig? profile)
    {
        if (profile?.Extra?.GetValueOrDefault("opencode") is not string settings) return [];
        try
        {
            using var document = JsonDocument.Parse(settings);
            var root = document.RootElement;
            return ConfigString(root, "id") is { } id
                && root.TryGetProperty("models", out var models) && models.ValueKind == JsonValueKind.Object
                ? models.EnumerateObject().Select(model => $"{id}/{model.Name}").ToHashSet(StringComparer.Ordinal)
                : [];
        }
        catch (JsonException) { return []; }
    }
}
