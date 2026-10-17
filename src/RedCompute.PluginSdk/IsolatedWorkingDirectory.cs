namespace RedCompute.PluginSdk;

/// <summary>
/// A unique, empty process working directory whose lifetime is bounded to one operation.
/// </summary>
public sealed class IsolatedWorkingDirectory : IDisposable
{
    public string Path { get; }

    private IsolatedWorkingDirectory(string prefix)
    {
        var safePrefix = string.Concat(prefix.Where(ch => char.IsAsciiLetterOrDigit(ch) || ch == '-'));
        if (string.IsNullOrEmpty(safePrefix)) safePrefix = "redcompute";
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"{safePrefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path);
    }

    public static IsolatedWorkingDirectory Create(string prefix) => new(prefix);

    public void Dispose()
    {
        if (Directory.Exists(Path))
            Directory.Delete(Path, recursive: true);
    }
}
