namespace RedCompute.PluginSdk;

public static class SessionScratch
{
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
        => directory == null ? null : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["REDLEAF_SCRATCH_ROOT"] = directory,
            ["REDLEAF_SCRATCH_DIR"] = directory,
            ["TEMP"] = directory,
            ["TMP"] = directory,
            ["TMPDIR"] = directory,
        };
}
