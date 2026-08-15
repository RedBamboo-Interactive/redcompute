namespace RedCompute.PluginSdk;

public static class SessionScratch
{
    private static readonly AsyncLocal<string?> CurrentExecutionToken = new();

    public static bool TryResolveDirectory(string? value, out string? directory)
    {
        directory = null;
        if (string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value)) return false;
        try
        {
            var full = Path.GetFullPath(value);
            if (!Directory.Exists(full)) return false;
            directory = full;
            return true;
        }
        catch { return false; }
    }

    public static IReadOnlyDictionary<string, string>? Environment(string? directory)
    {
        if (directory is null && CurrentExecutionToken.Value is null) return null;
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (directory is not null)
        {
            environment["REDLEAF_SCRATCH_ROOT"] = directory;
            environment["REDLEAF_SCRATCH_DIR"] = directory;
            environment["TEMP"] = directory;
            environment["TMP"] = directory;
            environment["TMPDIR"] = directory;
        }
        if (CurrentExecutionToken.Value is { } token)
            environment["REDLEAF_EXECUTION_TOKEN"] = token;
        return environment;
    }

    public static IDisposable PushExecutionToken(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
            throw new ArgumentException("Execution token is required", nameof(token));
        var previous = CurrentExecutionToken.Value;
        CurrentExecutionToken.Value = token;
        return new Restore(() => CurrentExecutionToken.Value = previous);
    }

    private sealed class Restore(Action restore) : IDisposable
    {
        public void Dispose() => restore();
    }
}
