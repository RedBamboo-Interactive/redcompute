using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using RedCompute.Core.Jobs;

namespace RedCompute.App.Services.Jobs;

internal sealed record JobArtifactLocation(string Name, string Path, string ContentType, string? FileName);

internal static partial class JobArtifactStore
{
    private sealed record ArtifactManifest(int SchemaVersion, ArtifactEntry[] Artifacts);
    private sealed record ArtifactEntry(string Name, string StoredFile, string ContentType, string? FileName);

    public static void Save(
        Guid jobId,
        string outputDirectory,
        string primaryPath,
        string? primaryContentType,
        string? primaryName,
        string? primaryFileName,
        IReadOnlyList<JobOutputPart>? extras)
    {
        var entries = new List<ArtifactEntry>
        {
            Entry("primary", primaryPath, primaryContentType, primaryFileName ?? Path.GetFileName(primaryPath)),
            Entry("clip-0", primaryPath, primaryContentType, primaryFileName ?? Path.GetFileName(primaryPath)),
        };
        var names = new HashSet<string>(entries.Select(entry => entry.Name), StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(primaryName))
        {
            var alias = primaryName.Trim();
            ValidateName(alias);
            if (names.Add(alias))
                entries.Add(Entry(alias, primaryPath, primaryContentType, primaryFileName ?? Path.GetFileName(primaryPath)));
        }

        try
        {
            if (extras is not null)
            {
                for (var index = 0; index < extras.Count; index++)
                {
                    var extra = extras[index];
                    ValidateSuffix(extra.Suffix);
                    var name = string.IsNullOrWhiteSpace(extra.Name) ? $"clip-{index + 1}" : extra.Name.Trim();
                    ValidateName(name);
                    if (!names.Add(name)) throw new InvalidDataException($"Duplicate job artifact name '{name}'");
                    var path = Path.Combine(outputDirectory, $"{jobId}{extra.Suffix}{ExtensionFor(extra.ContentType)}");
                    using (var file = File.Create(path))
                    {
                        if (extra.Data.CanSeek) extra.Data.Position = 0;
                        extra.Data.CopyTo(file);
                    }
                    entries.Add(Entry(name, path, extra.ContentType, extra.FileName));
                }
            }

            var manifestPath = ManifestPath(outputDirectory, jobId);
            var temporaryPath = manifestPath + $".{Guid.NewGuid():N}.tmp";
            try
            {
                File.WriteAllText(temporaryPath, JsonSerializer.Serialize(new ArtifactManifest(1, entries.ToArray())));
                File.Move(temporaryPath, manifestPath, overwrite: true);
            }
            finally
            {
                try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
                catch { }
            }
        }
        finally
        {
            if (extras is not null)
                foreach (var extra in extras) extra.Data.Dispose();
        }
    }

    public static JobArtifactLocation? Resolve(
        Guid jobId,
        string outputDirectory,
        string primaryPath,
        string? primaryContentType,
        string? artifact,
        int? clip)
    {
        var requested = clip.HasValue ? $"clip-{clip.Value}" : string.IsNullOrWhiteSpace(artifact) ? "primary" : artifact.Trim();
        ValidateName(requested);
        var manifestPath = ManifestPath(outputDirectory, jobId);
        if (File.Exists(manifestPath))
        {
            var manifest = JsonSerializer.Deserialize<ArtifactManifest>(File.ReadAllText(manifestPath));
            var entry = manifest?.Artifacts.FirstOrDefault(row =>
                row.Name.Equals(requested, StringComparison.OrdinalIgnoreCase));
            if (entry is null) return null;
            var path = ResolveStoredPath(outputDirectory, entry.StoredFile);
            return File.Exists(path) ? new JobArtifactLocation(entry.Name, path, entry.ContentType, SafeFileName(entry.FileName)) : null;
        }

        // Compatibility with jobs written before named artifact manifests existed.
        if (requested is "primary" or "clip-0")
            return File.Exists(primaryPath)
                ? new JobArtifactLocation(requested, primaryPath, primaryContentType ?? ContentTypeFor(primaryPath), Path.GetFileName(primaryPath))
                : null;
        if (requested.StartsWith("clip-", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(requested[5..], out var legacyClip) && legacyClip > 0)
        {
            var dir = Path.GetDirectoryName(primaryPath) ?? outputDirectory;
            var fileName = Path.GetFileNameWithoutExtension(primaryPath);
            var extension = Path.GetExtension(primaryPath);
            var path = Path.Combine(dir, $"{fileName}_clip{legacyClip}{extension}");
            return File.Exists(path)
                ? new JobArtifactLocation(requested, path, primaryContentType ?? ContentTypeFor(path), Path.GetFileName(path))
                : null;
        }
        return null;
    }

    private static ArtifactEntry Entry(string name, string path, string? contentType, string? fileName)
        => new(name, Path.GetFileName(path), contentType ?? ContentTypeFor(path), SafeFileName(fileName));

    private static string? SafeFileName(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return null;
        var safe = Path.GetFileName(fileName).Trim();
        safe = new string(safe.Where(character => !char.IsControl(character) && character is not ('"' or '\\')).ToArray());
        return safe.Length == 0 ? null : safe.Length <= 160 ? safe : safe[..160];
    }

    private static string ManifestPath(string outputDirectory, Guid jobId)
        => Path.Combine(outputDirectory, $"{jobId}_artifacts.json");

    private static string ResolveStoredPath(string outputDirectory, string storedFile)
    {
        if (Path.GetFileName(storedFile) != storedFile)
            throw new InvalidDataException("Job artifact manifest contains a non-local file name");
        var root = Path.GetFullPath(outputDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(outputDirectory, storedFile));
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Job artifact path escapes the output directory");
        return path;
    }

    private static void ValidateName(string name)
    {
        if (!ArtifactNameRegex().IsMatch(name))
            throw new InvalidDataException($"Invalid job artifact name '{name}'");
    }

    private static void ValidateSuffix(string suffix)
    {
        if (!ArtifactSuffixRegex().IsMatch(suffix))
            throw new InvalidDataException($"Invalid job artifact suffix '{suffix}'");
    }

    private static string ExtensionFor(string? contentType) => contentType?.ToLowerInvariant() switch
    {
        "image/png" => ".png",
        "image/jpeg" => ".jpg",
        "image/webp" => ".webp",
        "video/mp4" => ".mp4",
        "audio/wav" or "audio/x-wav" => ".wav",
        "audio/mpeg" or "audio/mp3" => ".mp3",
        "audio/ogg" => ".ogg",
        "audio/flac" => ".flac",
        "application/json" => ".json",
        _ => ".bin",
    };

    private static string ContentTypeFor(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".webp" => "image/webp",
        ".mp4" => "video/mp4",
        ".wav" => "audio/wav",
        ".mp3" => "audio/mpeg",
        ".ogg" => "audio/ogg",
        ".flac" => "audio/flac",
        ".json" => "application/json",
        _ => "application/octet-stream",
    };

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,79}$", RegexOptions.CultureInvariant)]
    private static partial Regex ArtifactNameRegex();

    [GeneratedRegex("^_[A-Za-z0-9_-]{1,80}$", RegexOptions.CultureInvariant)]
    private static partial Regex ArtifactSuffixRegex();
}
