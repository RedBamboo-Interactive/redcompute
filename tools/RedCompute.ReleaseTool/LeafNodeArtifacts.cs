using System.Formats.Tar;
using System.IO.Compression;
using System.Text.RegularExpressions;

namespace RedCompute.ReleaseTool;

/// <summary>
/// The leaf-node release artifacts (Phase 3 design 2.3, R7, D3): the node agent ships with the Nightly
/// beside RedCompute, from the same source commit, as five files attached to RedCompute's release:
/// one archive per runtime, the manifest that names their hashes, and one installer per OS. They are
/// additions: nothing here reads or writes RedCompute's own artifact, evidence or descriptor.
/// </summary>
public static class LeafNodeRuntime
{
    public const string ComponentId = "leaf-node";
    public const string ManifestName = "leaf-node-manifest.v1.json";
    public const string WindowsInstaller = "leaf-node-install.ps1";
    public const string LinuxInstaller = "leaf-node-install.sh";
    public const string ProjectPath = "src/RedCompute.Node/RedCompute.Node.csproj";

    public static IReadOnlyList<string> Rids { get; } = ["win-x64", "linux-x64"];

    public static bool IsSupported(string rid) => rid is "win-x64" or "linux-x64";

    public static string OperatingSystem(string rid) => rid switch
    {
        "win-x64" => "windows",
        "linux-x64" => "linux",
        _ => throw new InvalidDataException($"Unsupported leaf-node runtime identifier '{rid}'."),
    };

    /// <summary>A zip on Windows (Expand-Archive), a gzip tarball on Linux (tar keeps the modes).</summary>
    public static string ArchiveName(string rid) => OperatingSystem(rid) == "windows"
        ? "leaf-node-win-x64.zip"
        : "leaf-node-linux-x64.tar.gz";

    public static string InstallerName(string rid) => OperatingSystem(rid) == "windows" ? WindowsInstaller : LinuxInstaller;

    /// <summary>Every file the recipe attaches, in the order it lists them.</summary>
    public static IReadOnlyList<string> AssetNames { get; } =
        [ArchiveName("win-x64"), ArchiveName("linux-x64"), ManifestName, WindowsInstaller, LinuxInstaller];

    /// <summary>What a self-contained leaf-node publish must hold.</summary>
    public static IReadOnlyList<string> RequiredFiles(string rid) => OperatingSystem(rid) == "windows"
        ? ["leaf-node.exe", "leaf-node.dll", "leaf-node.deps.json", "leaf-node.runtimeconfig.json", "hostfxr.dll", "coreclr.dll"]
        : ["leaf-node", "leaf-node.dll", "leaf-node.deps.json", "leaf-node.runtimeconfig.json", "libhostfxr.so", "libcoreclr.so"];

    public static void VerifyPublish(string root, string rid)
    {
        foreach (var relative in RequiredFiles(rid))
            if (!File.Exists(Path.Combine(root, relative)))
                throw new InvalidDataException($"leaf-node {rid} publish is incomplete: missing {relative}.");
        // The agent never carries RedCompute's server: no ASP.NET Core, no plugins (design D1).
        if (File.Exists(Path.Combine(root, "Microsoft.AspNetCore.dll")) || Directory.Exists(Path.Combine(root, "plugins")))
            throw new InvalidDataException($"leaf-node {rid} publish carries RedCompute server content.");
    }
}

/// <summary>
/// A leaf-node archive, byte-identical for the same publish folder: entries sorted by ordinal path,
/// one fixed timestamp, no owner. win-x64: the zip RedCompute's Windows archive is (no modes).
/// linux-x64: a ustar tarball in gzip, 0755 for ELF files and shared libraries, 0644 otherwise.
/// </summary>
public static class LeafNodeArchive
{
    public static readonly DateTimeOffset Timestamp = new(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public static void Create(string sourceDirectory, string outputPath, string rid)
    {
        LeafNodeRuntime.VerifyPublish(sourceDirectory, rid);
        if (LeafNodeRuntime.OperatingSystem(rid) == "windows")
        {
            DeterministicArchive.Create(sourceDirectory, outputPath);
            return;
        }

        var source = Path.GetFullPath(sourceDirectory);
        var output = Path.GetFullPath(outputPath);
        if (output.StartsWith(source + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Archive output must be outside the source directory.", nameof(outputPath));
        var files = ArchiveInputs.Enumerate(source);
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        using var stream = new FileStream(output, FileMode.Create, FileAccess.Write, FileShare.None);
        using var gzip = new GZipStream(stream, CompressionLevel.SmallestSize);
        using var writer = new TarWriter(gzip, TarEntryFormat.Ustar, leaveOpen: false);
        foreach (var file in files)
        {
            if ((File.GetAttributes(file.FullPath) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException($"Archive input became a reparse point: {file.RelativePath}");
            if (Encoding(file.RelativePath) > 255)
                throw new InvalidDataException($"Archive path is too long for ustar: {file.RelativePath}");
            using var data = new FileStream(file.FullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var entry = new UstarTarEntry(TarEntryType.RegularFile, file.RelativePath)
            {
                Mode = (UnixFileMode)UnixArchiveModes.Mode(file.RelativePath, file.FullPath),
                ModificationTime = Timestamp,
                Uid = 0,
                Gid = 0,
                UserName = "",
                GroupName = "",
                DataStream = data,
            };
            writer.WriteEntry(entry);
        }
    }

    private static int Encoding(string path) => System.Text.Encoding.UTF8.GetByteCount(path);
}

/// <summary><c>leaf-node-manifest.v1.json</c>: what the installers check a download against.</summary>
public sealed record LeafNodeManifest
{
    public required int SchemaVersion { get; init; }
    public required string ComponentId { get; init; }
    public required string Version { get; init; }
    public required string SourceCommit { get; init; }
    public required IReadOnlyList<LeafNodeManifestArtifact> Artifacts { get; init; }
}

public sealed record LeafNodeManifestArtifact
{
    public required string Rid { get; init; }
    public required string OperatingSystem { get; init; }
    public required string Architecture { get; init; }
    public required string Name { get; init; }
    public required string Sha256 { get; init; }
    public required long SizeBytes { get; init; }
}

public static partial class LeafNodeManifests
{
    public static LeafNodeManifest Build(string version, string sourceCommit, IReadOnlyDictionary<string, string> archives)
    {
        var manifest = new LeafNodeManifest
        {
            SchemaVersion = 1,
            ComponentId = LeafNodeRuntime.ComponentId,
            Version = version,
            SourceCommit = sourceCommit,
            Artifacts = LeafNodeRuntime.Rids.Select(rid =>
            {
                if (!archives.TryGetValue(rid, out var path)) throw new InvalidDataException($"The {rid} archive is missing.");
                if (!string.Equals(Path.GetFileName(path), LeafNodeRuntime.ArchiveName(rid), StringComparison.Ordinal))
                    throw new InvalidDataException($"The {rid} archive must be named {LeafNodeRuntime.ArchiveName(rid)}.");
                var facts = ComponentJson.Inspect(path);
                return new LeafNodeManifestArtifact
                {
                    Rid = rid,
                    OperatingSystem = LeafNodeRuntime.OperatingSystem(rid),
                    Architecture = "x64",
                    Name = LeafNodeRuntime.ArchiveName(rid),
                    Sha256 = facts.Sha256,
                    SizeBytes = facts.SizeBytes,
                };
            }).ToArray(),
        };
        Validate(manifest);
        return manifest;
    }

    public static void Validate(LeafNodeManifest value)
    {
        if (value.SchemaVersion != 1 || value.ComponentId != LeafNodeRuntime.ComponentId)
            throw new InvalidDataException("Unsupported leaf-node manifest contract.");
        if (!SemverPattern().IsMatch(value.Version)) throw new InvalidDataException("Invalid leaf-node version.");
        if (!CommitPattern().IsMatch(value.SourceCommit)) throw new InvalidDataException("Invalid leaf-node source commit.");
        if (value.Artifacts.Count != LeafNodeRuntime.Rids.Count)
            throw new InvalidDataException("The manifest must name exactly one archive per runtime.");
        for (var index = 0; index < LeafNodeRuntime.Rids.Count; index++)
        {
            var rid = LeafNodeRuntime.Rids[index];
            var artifact = value.Artifacts[index];
            if (artifact.Rid != rid || artifact.OperatingSystem != LeafNodeRuntime.OperatingSystem(rid)
                || artifact.Architecture != "x64" || artifact.Name != LeafNodeRuntime.ArchiveName(rid))
                throw new InvalidDataException($"Manifest entry {index} must be the {rid} archive.");
            if (!HashPattern().IsMatch(artifact.Sha256) || artifact.SizeBytes <= 0)
                throw new InvalidDataException($"Manifest entry {rid} has an invalid hash or size.");
        }
    }

    [GeneratedRegex("^(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)$")]
    private static partial Regex SemverPattern();

    [GeneratedRegex("^[0-9a-f]{40}$")]
    private static partial Regex CommitPattern();

    [GeneratedRegex("^[0-9a-f]{64}$")]
    private static partial Regex HashPattern();
}
