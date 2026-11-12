using System.IO.Compression;

namespace RedCompute.ReleaseTool;

public sealed record ArchiveInput(string FullPath, string RelativePath);

public static class ArchiveInputs
{
    public static IReadOnlyList<ArchiveInput> Enumerate(string sourceDirectory)
        => Enumerate(Path.GetFullPath(sourceDirectory), File.GetAttributes, Directory.EnumerateFileSystemEntries);

    public static IReadOnlyList<ArchiveInput> Enumerate(
        string sourceDirectory,
        Func<string, FileAttributes> attributes,
        Func<string, IEnumerable<string>> entries)
    {
        var source = Path.GetFullPath(sourceDirectory);
        var files = new List<ArchiveInput>();
        Visit(source, source, files, attributes, entries);
        return files.OrderBy(x => x.RelativePath, StringComparer.Ordinal).ToArray();
    }

    private static void Visit(string root, string path, List<ArchiveInput> files,
        Func<string, FileAttributes> attributes, Func<string, IEnumerable<string>> entries)
    {
        var value = attributes(path);
        if ((value & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException($"Archive input is a reparse point: {Relative(root, path)}");
        if ((value & FileAttributes.Directory) == 0)
        {
            files.Add(new ArchiveInput(path, Relative(root, path)));
            return;
        }
        foreach (var child in entries(path)) Visit(root, child, files, attributes, entries);
    }

    private static string Relative(string root, string path)
        => Path.GetRelativePath(root, path).Replace('\\', '/');
}

/// <summary>
/// The RedCompute builds the release ships: win-x64 (as before) and linux-x64. Per build: the
/// files a self-contained publish must hold, the NuGet lock files it was restored from, and
/// the operating system its descriptor names.
/// </summary>
public static class RedComputeRuntime
{
    public static bool IsSupported(string rid) => rid is "win-x64" or "linux-x64";

    public static string OperatingSystem(string rid) => rid switch
    {
        "win-x64" => "windows",
        "linux-x64" => "linux",
        _ => throw new InvalidDataException($"Unsupported RedCompute runtime identifier '{rid}'."),
    };

    public static string LockFileName(string rid)
        => OperatingSystem(rid) == "windows" ? "packages.lock.json" : "packages.linux.lock.json";

    public static IReadOnlyList<string> RequiredFiles(string rid) => OperatingSystem(rid) == "windows"
        ? ["RedCompute.exe", "RedCompute.deps.json", "RedCompute.runtimeconfig.json", "hostfxr.dll", "coreclr.dll"]
        : ["RedCompute", "RedCompute.deps.json", "RedCompute.runtimeconfig.json", "libhostfxr.so", "libcoreclr.so"];
}

/// <summary>Unix file modes a linux-x64 archive stores (ExternalAttributes = mode &lt;&lt; 16).</summary>
public static class UnixArchiveModes
{
    public const int Executable = 0x1ED; // 0755
    public const int Regular = 0x1A4;    // 0644

    /// <summary>0755 for the RedCompute apphost, shared libraries and other ELF files; 0644 otherwise.</summary>
    public static int Mode(string relativePath, string fullPath)
    {
        var name = Path.GetFileName(relativePath);
        if (relativePath == "RedCompute" || name.EndsWith(".so", StringComparison.Ordinal)
            || name.Contains(".so.", StringComparison.Ordinal))
            return Executable;
        using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        Span<byte> magic = stackalloc byte[4];
        return stream.Read(magic) == 4 && magic[0] == 0x7F && magic[1] == (byte)'E' && magic[2] == (byte)'L'
               && magic[3] == (byte)'F'
            ? Executable
            : Regular;
    }

    public static int ExternalAttributes(int mode) => unchecked((int)((uint)(0x8000 | (mode & 0x1FF)) << 16));
}

public static class DeterministicArchive
{
    /// <param name="unixModes">
    /// False (the default): a Windows archive, no entry stores a mode, exactly as before. True:
    /// a linux-x64 archive whose entries store <see cref="UnixArchiveModes.Mode"/>.
    /// </param>
    public static void Create(string sourceDirectory, string outputPath, bool unixModes = false)
    {
        var source = Path.GetFullPath(sourceDirectory);
        var output = Path.GetFullPath(outputPath);
        if (output.StartsWith(source + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Archive output must be outside the source directory.", nameof(outputPath));

        var files = ArchiveInputs.Enumerate(source);
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        using var stream = new FileStream(output, FileMode.Create, FileAccess.Write, FileShare.None);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: false);
        var zipTimestamp = new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
        foreach (var file in files)
        {
            if ((File.GetAttributes(file.FullPath) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException($"Archive input became a reparse point: {file.RelativePath}");
            var entry = archive.CreateEntry(file.RelativePath, CompressionLevel.Optimal);
            entry.LastWriteTime = zipTimestamp;
            entry.ExternalAttributes = unixModes
                ? UnixArchiveModes.ExternalAttributes(UnixArchiveModes.Mode(file.RelativePath, file.FullPath))
                : 0;
            using var input = new FileStream(file.FullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var destination = entry.Open();
            input.CopyTo(destination);
        }
    }

}
