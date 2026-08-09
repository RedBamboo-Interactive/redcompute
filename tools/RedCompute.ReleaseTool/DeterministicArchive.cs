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

public static class DeterministicArchive
{
    public static void Create(string sourceDirectory, string outputPath)
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
            entry.ExternalAttributes = 0;
            using var input = new FileStream(file.FullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var destination = entry.Open();
            input.CopyTo(destination);
        }
    }

}
