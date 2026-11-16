using System.Text.Json;
using System.Text.Json.Nodes;

namespace RedCompute.PluginSdk;

/// <summary>
/// The placement references of the session being started (ids only, never paths), pushed by the
/// session endpoint around <c>StartSessionAsync</c> as <see cref="SessionDeliveryScope"/> is. Providers
/// read it where they read the access scope, store it on the session row at insert and never change it:
/// a session is pinned where it started, and a resume never re-resolves it.
/// </summary>
public static class SessionPlacementScope
{
    private static readonly AsyncLocal<string?> CurrentValue = new();

    /// <summary>The canonical JSON text of the placement, or null when the start named none.</summary>
    public static string? Current => CurrentValue.Value;

    public static IDisposable Push(string? placementJson)
    {
        var previous = CurrentValue.Value;
        CurrentValue.Value = placementJson;
        return new Restore(() => CurrentValue.Value = previous);
    }

    /// <summary>A stored placement as JSON; null for none, or for text that is not a JSON object.</summary>
    public static JsonObject? Parse(string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored)) return null;
        try { return JsonNode.Parse(stored) as JsonObject; }
        catch (JsonException) { return null; }
    }

    private sealed class Restore(Action restore) : IDisposable
    {
        public void Dispose() => restore();
    }
}
