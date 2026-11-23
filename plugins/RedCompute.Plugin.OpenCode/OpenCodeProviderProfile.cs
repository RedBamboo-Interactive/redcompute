using System.Text.Json;
using System.Text.Json.Nodes;
using RedCompute.Core.Configuration;
using RedCompute.PluginSdk;
using RedCompute.PluginSdk.Hosting;

namespace RedCompute.Plugin.OpenCode;

/// <summary>Applies one selected shared Provider entity to an OpenCode process launch.</summary>
internal static class OpenCodeProviderProfile
{
    internal const string KeyVariablePrefix = "REDLEAF_OPENCODE_PROVIDER_KEY_";

    internal static (string? Model, string? Error) Apply(
        IExecutionHost host, IDictionary<string, string> environment, ProviderConfig provider,
        string? model, string? effort)
    {
        if (provider.Extra?.GetValueOrDefault("opencode") is not string settings)
            return (null, "OpenCode provider settings are missing.");

        JsonObject? profile;
        try { profile = JsonNode.Parse(settings) as JsonObject; }
        catch (JsonException) { profile = null; }
        if (profile is null
            || ReadString(profile["id"]) is not { Length: > 0 } id || id.Contains('/')
            || ReadString(profile["npm"]) is not { Length: > 0 } npm
            || profile["models"] is not JsonObject models)
            return (null, "OpenCode provider settings must define an id, npm adapter and models.");

        if (ContainsRuntimeExpansion(profile.ToJsonString()) || ContainsRuntimeExpansion(provider.DisplayName))
            return (null, "OpenCode Provider data cannot substitute environment variables or files.");

        // These are the bundled adapters for the supported OpenAI wire formats. A per-model
        // provider override could load a different adapter or choose a different API endpoint.
        if (npm is not ("@ai-sdk/openai-compatible" or "@ai-sdk/openai")
            || models.Any(entry => entry.Value is not JsonObject definition || definition.ContainsKey("provider")))
            return (null, "OpenCode provider models must use one bundled OpenAI adapter and the provider endpoint.");

        if (!Uri.TryCreate(provider.Endpoint, UriKind.Absolute, out var endpoint)
            || (endpoint.Scheme != Uri.UriSchemeHttps
                && (endpoint.Scheme != Uri.UriSchemeHttp || !endpoint.IsLoopback))
            || endpoint.UserInfo.Length > 0 || endpoint.Query.Length > 0 || endpoint.Fragment.Length > 0)
            return (null, "OpenCode provider endpoint must be HTTPS or a local HTTP endpoint.");

        var key = ProviderKeyBindings.Release(provider, endpoint);
        if (string.IsNullOrEmpty(key))
            return (null, provider.KeyBinding is null
                ? "The OpenCode provider requires a vaulted API key."
                : ProviderKeyBindings.MismatchMessage);

        var requested = string.IsNullOrWhiteSpace(model) ? provider.Model : model;
        if (string.IsNullOrWhiteSpace(requested))
            return (null, "OpenCode provider default model is missing.");
        var prefix = id + "/";
        var modelId = requested.StartsWith(prefix, StringComparison.Ordinal)
            ? requested[prefix.Length..]
            : requested;
        if (models[modelId] is not JsonObject selected)
            return (null, "The selected model is not declared by this OpenCode provider.");

        if (!string.IsNullOrWhiteSpace(effort)
            && (selected["variants"] is not JsonObject variants || variants[effort] is not JsonObject))
            return (null, "The selected reasoning effort is not declared for this OpenCode model.");

        // Config is allowed to contain other providers. A random private variable avoids sharing
        // a credential name between these processes, and the serialized config contains a reference.
        var keyVariable = KeyVariablePrefix + Guid.NewGuid().ToString("N");
        var qualifiedModel = prefix + modelId;
        var config = OpenCodeSessionService.ReadInlineConfig(host, environment);
        if (config["provider"] is not JsonObject providers)
        {
            providers = new JsonObject();
            config["provider"] = providers;
        }

        providers[id] = new JsonObject
        {
            ["name"] = provider.DisplayName ?? id,
            ["npm"] = npm,
            ["models"] = models.DeepClone(),
            ["options"] = new JsonObject
            {
                ["baseURL"] = endpoint.AbsoluteUri.TrimEnd('/'),
                ["apiKey"] = $"{{env:{keyVariable}}}",
            },
        };

        if (config["enabled_providers"] is JsonArray enabled
            && !enabled.Any(value => ReadString(value) == id))
            enabled.Add(id);

        config["model"] = qualifiedModel;
        environment["OPENCODE_CONFIG_CONTENT"] = config.ToJsonString();
        environment[keyVariable] = key;
        return (qualifiedModel, null);
    }

    private static bool ContainsRuntimeExpansion(string? value)
        => value?.Contains("{env:", StringComparison.Ordinal) == true
            || value?.Contains("{file:", StringComparison.Ordinal) == true;

    private static string? ReadString(JsonNode? node)
        => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
}
