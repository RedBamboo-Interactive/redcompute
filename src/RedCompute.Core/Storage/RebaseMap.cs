using System.Text.Json;

namespace RedCompute.Core.Storage;

/// <summary>One folder that moved: everything under <see cref="From"/> now lives under <see cref="To"/>.</summary>
public sealed record RebaseEntry(string From, string To);

/// <summary>The value of <c>REDCOMPUTE_REBASE_MAP</c> is not a rebase map RedCompute can use.</summary>
public sealed class RebaseMapException(string message) : InvalidOperationException(message);

/// <summary>
/// A move, as the Leaf kernel describes it: a generation number and an ordered list of absolute folder
/// pairs. Stored values under a <c>from</c> folder are rewritten under its <c>to</c> folder; values under
/// none of them are never touched (ComfyUI on another drive, models on F:, a backup folder elsewhere).
/// <code>{ "generation": 3, "map": [ { "from": "C:\\Users\\a\\AppData\\Local\\RedCompute", "to": "D:\\Leaf\\RedCompute" } ] }</code>
/// </summary>
public sealed class RebaseMap
{
    /// <summary>The environment variable the kernel sets while a rebase is pending.</summary>
    public const string Variable = "REDCOMPUTE_REBASE_MAP";

    public RebaseMap(long generation, IReadOnlyList<RebaseEntry> entries)
    {
        Generation = generation;
        Entries = entries;
    }

    public long Generation { get; }
    public IReadOnlyList<RebaseEntry> Entries { get; }

    /// <summary>
    /// The map of this process, or null when <see cref="Variable"/> is absent (nothing to do). Throws
    /// <see cref="RebaseMapException"/> for a value that is present but malformed.
    /// </summary>
    public static RebaseMap? FromEnvironment(Func<string, string?> getVariable)
    {
        var raw = getVariable(Variable);
        return raw is null ? null : Parse(raw);
    }

    /// <summary>Parses the JSON form; every failure is a <see cref="RebaseMapException"/> that names what is wrong.</summary>
    public static RebaseMap Parse(string json)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = false });
        }
        catch (JsonException ex)
        {
            throw new RebaseMapException($"{Variable} is not valid JSON: {ex.Message}");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new RebaseMapException($"{Variable} must be a JSON object with \"generation\" and \"map\".");

            JsonElement generationElement = default, mapElement = default;
            foreach (var property in root.EnumerateObject())
            {
                if (property.NameEquals("generation") || property.Name.Equals("generation", StringComparison.OrdinalIgnoreCase))
                    generationElement = property.Value;
                else if (property.Name.Equals("map", StringComparison.OrdinalIgnoreCase))
                    mapElement = property.Value;
            }

            if (generationElement.ValueKind != JsonValueKind.Number || !generationElement.TryGetInt64(out var generation)
                || generation < 0)
                throw new RebaseMapException($"{Variable} needs a non-negative integer \"generation\".");
            if (mapElement.ValueKind != JsonValueKind.Array)
                throw new RebaseMapException($"{Variable} needs a \"map\" array of {{ \"from\", \"to\" }} pairs.");

            var entries = new List<RebaseEntry>();
            var index = 0;
            foreach (var item in mapElement.EnumerateArray())
            {
                var from = ReadFolder(item, "from", index);
                var to = ReadFolder(item, "to", index);
                entries.Add(new RebaseEntry(from, to));
                index++;
            }
            return new RebaseMap(generation, entries);
        }
    }

    private static string ReadFolder(JsonElement item, string name, int index)
    {
        if (item.ValueKind != JsonValueKind.Object)
            throw new RebaseMapException($"{Variable} map[{index}] must be an object with \"from\" and \"to\".");
        foreach (var property in item.EnumerateObject())
        {
            if (!property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
            var value = property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() : null;
            if (string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value))
                throw new RebaseMapException(
                    $"{Variable} map[{index}].{name} must be an absolute folder path, but it is '{value}'.");
            return value;
        }
        throw new RebaseMapException($"{Variable} map[{index}] is missing \"{name}\".");
    }

    /// <summary>
    /// The value under its new folder, or null when no <c>from</c> folder contains it (or it is already
    /// under the matching <c>to</c>, which keeps a second run from rewriting its own output). The most
    /// specific (longest) <c>from</c> wins when folders nest.
    /// </summary>
    public string? Rebase(string? path)
    {
        if (string.IsNullOrEmpty(path) || !StoredPaths.IsAbsolute(path)) return null;
        RebaseEntry? best = null;
        var bestLength = -1;
        foreach (var entry in Entries)
        {
            if (!StoredPaths.IsUnder(entry.From, path)) continue;
            var length = StoredPaths.Normalize(entry.From).Length;
            if (length > bestLength) { best = entry; bestLength = length; }
        }
        if (best is null) return null;
        // Already moved (the new folder lies inside the old one, or the value was rebased before).
        if (StoredPaths.IsUnder(best.To, path)) return null;

        var tail = StoredPaths.RelativeTail(best.From, path)!;
        if (tail.Length == 0) return best.To;
        var separator = best.To.Contains('\\') ? '\\' : '/';
        var target = best.To.TrimEnd('\\', '/');
        return target + separator + tail.Replace('/', separator);
    }

    /// <summary>True when <paramref name="path"/> is under any <c>from</c> folder.</summary>
    public bool IsUnderFrom(string? path)
        => !string.IsNullOrEmpty(path) && Entries.Any(entry => StoredPaths.IsUnder(entry.From, path));
}
