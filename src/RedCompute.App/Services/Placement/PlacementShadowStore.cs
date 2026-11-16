using System.IO;
using System.Text.Json;

namespace RedCompute.App.Services.Placement;

/// <summary>
/// The shadow counts on RedCompute's own disk (<c>placement-shadow.json</c> under the data root), so a
/// restart or a deploy inside the shadow window keeps the evidence. Counts and names only, never paths.
/// Written whole through a temporary file, so a crash leaves the previous counts.
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
        try
        {
            var state = JsonSerializer.Deserialize<PlacementShadowState>(File.ReadAllText(Path), Options);
            if (state is { SchemaVersion: 1 }) return state;
            log($"[Placement] {FileName} has schema {state?.SchemaVersion}: shadow counts start over", null);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            log($"[Placement] {FileName} could not be read ({ex.GetType().Name}): shadow counts start over", null);
        }
        try { File.Move(Path, Path + ".unreadable", overwrite: true); } catch { }
        return null;
    }

    public void Save(PlacementShadowState state)
    {
        var temporary = Path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(state, Options));
        File.Move(temporary, Path, overwrite: true);
    }
}
