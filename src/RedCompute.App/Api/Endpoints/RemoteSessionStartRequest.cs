using System.Text.Json;
using RedCompute.App.Services.Nodes;
using RedCompute.PluginSdk;
using RedCompute.NodeLink.Forward;

namespace RedCompute.App.Api.Endpoints;

internal sealed record RemoteSessionStartOptions(RemoteSessionPlacement Placement, string IdempotencyKey, bool ValidateOnly);

internal static class RemoteSessionStartRequest
{
    internal static bool IsRequested(JsonElement body)
        => body.ValueKind == JsonValueKind.Object
           && body.TryGetProperty("placement", out var placement) && placement.ValueKind == JsonValueKind.Object
           && placement.TryGetProperty("sessionRuntime", out _);

    internal static RemoteSessionStartOptions Parse(JsonElement body, string? key)
    {
        if (!ControllerRemoteSessions.Identifier(key))
            throw new ControllerRemoteSessionException(422, "invalid_remote_request_key");

        if (!IsRequested(body))
            throw Invalid();
        CheckMembers(body, "placement", "provider", "model", "effort", "executionProfile", "validateOnly");
        var placement = body.GetProperty("placement");
        CheckMembers(placement, "nodeId", "sessionRuntime");
        var node = Text(placement, "nodeId", required: true)!;
        if (node.Equals("local", StringComparison.OrdinalIgnoreCase)
            || Text(body, "executionProfile") is { } profile && profile != SessionExecutionProfile.Default)
            throw Invalid();

        var target = placement.GetProperty("sessionRuntime");
        CheckMembers(target, "id", "workspace", "revision", "providerInstanceId");
        var runtimeId = Text(target, "id", required: true)!;
        if (!ForwardOpen.IsRuntimeId(runtimeId))
            throw Invalid();
        var validateOnly = body.TryGetProperty("validateOnly", out var validate) ? validate.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw Invalid(),
        } : false;

        var remote = new RemoteSessionPlacement(node, runtimeId, Text(target, "workspace", required: true)!,
            Text(target, "revision", required: true)!, Text(body, "provider", required: true)!,
            Text(target, "providerInstanceId", required: true)!, Text(body, "model"), Text(body, "effort"));
        return new(remote, key!, validateOnly);
    }

    private static void CheckMembers(JsonElement body, params string[] supported)
    {
        if (body.ValueKind != JsonValueKind.Object) throw Invalid();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var member in body.EnumerateObject())
        {
            if (!supported.Contains(member.Name, StringComparer.Ordinal) || !seen.Add(member.Name))
                throw Invalid();
        }
    }

    private static string? Text(JsonElement body, string field, bool required = false)
    {
        if (!body.TryGetProperty(field, out var value) || value.ValueKind == JsonValueKind.Null)
            return required ? throw Invalid() : null;
        if (value.ValueKind != JsonValueKind.String || !ControllerRemoteSessions.Identifier(value.GetString()))
            throw Invalid();
        return value.GetString();
    }

    private static ControllerRemoteSessionException Invalid() => new(422, "invalid_remote_placement");
}
