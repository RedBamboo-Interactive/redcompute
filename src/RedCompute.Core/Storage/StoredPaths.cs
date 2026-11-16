namespace RedCompute.Core.Storage;

/// <summary>
/// How RedCompute stores a path that lives inside one of its own folders: relative to that folder, with
/// <c>/</c> separators, so the row stays true when the data moves. A path outside the folder (ComfyUI's
/// own output folder, a file on another drive) stays absolute and is never touched.
/// <para>
/// Readers accept both forms: <see cref="Resolve"/> returns an absolute stored value as it is and joins a
/// relative one to its root. Every match is on a folder boundary (<c>C:\Data</c> is not the parent of
/// <c>C:\Data2</c>), case-insensitive on Windows.
/// </para>
/// </summary>
public static class StoredPaths
{
    private static StringComparison Comparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    /// <summary>A value no file can have, returned by <see cref="Resolve"/> for a relative value that climbs out of its root.</summary>
    public const string Unresolvable = "?unresolvable-stored-path?";

    /// <summary>True when <paramref name="path"/> is <paramref name="folder"/> or inside it, on a folder boundary.</summary>
    public static bool IsUnder(string folder, string path)
        => RelativeTail(folder, path) is not null;

    /// <summary>
    /// The part of <paramref name="path"/> below <paramref name="folder"/> with <c>/</c> separators
    /// (empty for the folder itself), or null when it is not inside it.
    /// </summary>
    public static string? RelativeTail(string folder, string path)
    {
        if (string.IsNullOrWhiteSpace(folder) || string.IsNullOrWhiteSpace(path)) return null;
        var root = Normalize(folder);
        var value = Normalize(path);
        if (value.Length < root.Length || !value.StartsWith(root, Comparison)) return null;
        if (value.Length == root.Length) return "";
        // The boundary: the next character must be a separator (or the root itself ended with one).
        if (root.Length > 0 && value[root.Length] != '/') return null;
        return value[(root.Length + 1)..];
    }

    /// <summary>
    /// The form to store: relative to <paramref name="root"/> when <paramref name="path"/> is inside it,
    /// otherwise the value unchanged. A value that is already relative is returned as it is.
    /// </summary>
    public static string? ToStored(string root, string? path)
    {
        if (string.IsNullOrEmpty(path) || !IsAbsolute(path)) return path;
        var tail = RelativeTail(root, path);
        return string.IsNullOrEmpty(tail) ? path : tail;
    }

    /// <summary>
    /// The absolute path of a stored value: an absolute value as it is, a relative one joined to
    /// <paramref name="root"/>. A relative value that climbs out of the root resolves to
    /// <see cref="Unresolvable"/> rather than to a file elsewhere.
    /// </summary>
    public static string? Resolve(string root, string? stored)
    {
        if (string.IsNullOrEmpty(stored) || IsAbsolute(stored)) return stored;
        var combined = Path.GetFullPath(Path.Combine(root, stored.Replace('/', Path.DirectorySeparatorChar)));
        return IsUnder(root, combined) ? combined : Unresolvable;
    }

    /// <summary>A rooted path on any platform (a drive letter, a UNC path or a leading separator).</summary>
    public static bool IsAbsolute(string path)
        => path.Length > 0
           && (path[0] is '/' or '\\' || (path.Length > 1 && path[1] == ':' && char.IsLetter(path[0])));

    internal static string Normalize(string path)
    {
        var value = OperatingSystem.IsWindows() ? path.Replace('\\', '/') : path;
        // Keep a lone "/" (the filesystem root) as the empty root so "/x" splits on a boundary.
        return value.TrimEnd('/');
    }
}
