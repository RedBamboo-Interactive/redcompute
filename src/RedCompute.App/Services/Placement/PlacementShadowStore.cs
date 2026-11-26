using System.IO;
using System.Text.Json;
using RedCompute.Core.Storage;

namespace RedCompute.App.Services.Placement;

/// <summary>
/// The shadow counts on RedCompute's own disk (<c>placement-shadow.json</c> under the data root), so a
/// restart or a deploy inside the shadow window keeps the evidence. Counts and names only, never paths.
/// Replaced durably, so a crash or a power cut leaves the previous counts.
/// </summary>
public sealed class PlacementShadowStore(string path)
{
    public const string FileName = "placement-shadow.json";

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public string Path { get; } = path;

    /// <summary>The saved counts; a missing file is none, an unreadable one is set aside and counting starts over.</summary>
    public PlacementShadowState? Load(Action<string, Guid?> log)
    {
        if (!File.Exists(Path)) return null;
        string reason;
        try
        {
            var state = JsonSerializer.Deserialize<PlacementShadowState>(File.ReadAllText(Path), Options);
            if (state is { SchemaVersion: 1 }) return state;
            reason = $"has schema {state?.SchemaVersion}";
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            reason = $"could not be read ({ex.GetType().Name})";
        }
        // Each one kept: a second bad file never replaces the first.
        log($"[Placement] {FileName} {reason}; {DurableFile.SetAsideAndDescribe(Path, DateTimeOffset.UtcNow)}. Shadow counts start over", null);
        return null;
    }

    public void Save(PlacementShadowState state) => DurableFile.WriteAllText(Path, JsonSerializer.Serialize(state, Options));
}
