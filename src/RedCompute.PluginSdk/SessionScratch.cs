namespace RedCompute.PluginSdk;

public static class SessionScratch
{
    private static readonly AsyncLocal<Func<string?, string?>?> CurrentExecutionToken = new();

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

    /// <summary>
    /// Builds the provider process environment. Pass the RedCompute session id the process belongs
    /// to so the signed execution identity can name that session from the first turn.
    /// </summary>
    public static IReadOnlyDictionary<string, string>? Environment(string? directory, bool suiteAccess = true,
        string? sessionId = null)
    {
        var token = suiteAccess ? CurrentExecutionToken.Value?.Invoke(sessionId) : null;
        if (directory is null && token is null && suiteAccess) return null;
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (directory is not null)
        {
            environment["REDLEAF_SCRATCH_ROOT"] = directory;
            environment["REDLEAF_SCRATCH_DIR"] = directory;
            environment["TEMP"] = directory;
            environment["TMP"] = directory;
            environment["TMPDIR"] = directory;
        }
        if (!suiteAccess)
            environment["REDLEAF_EXECUTION_TOKEN"] = "";
        else if (token is not null)
            environment["REDLEAF_EXECUTION_TOKEN"] = token;
        return environment;
    }

    public static IDisposable PushExecutionToken(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
            throw new ArgumentException("Execution token is required", nameof(token));
        return PushExecutionTokenIssuer(_ => token);
    }

    /// <summary>
    /// Defers token issuance until a provider builds its process environment. Persistent providers
    /// allocate their session id inside StartSessionAsync, so only then can the issuer stamp it.
    /// </summary>
    public static IDisposable PushExecutionTokenIssuer(Func<string?, string?> issue)
    {
        ArgumentNullException.ThrowIfNull(issue);
        var previous = CurrentExecutionToken.Value;
        CurrentExecutionToken.Value = issue;
        return new Restore(() => CurrentExecutionToken.Value = previous);
    }

    private sealed class Restore(Action restore) : IDisposable
    {
        public void Dispose() => restore();
    }
}
