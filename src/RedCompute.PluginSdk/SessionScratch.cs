using RedCompute.Core.Jobs;
using RedCompute.PluginSdk.Hosting;
using RedCompute.PluginSdk.Hosting.Local;

namespace RedCompute.PluginSdk;

/// <summary>Verifies a session's existing scratch directory before a provider grants it write access.</summary>
public interface ISessionScratchWriteResolver
{
    Task<string?> ResolveWritableAsync(IExecutionHost host, JobProvenance? provenance, string? scratchDirectory);
}

public static class SessionScratch
{
    private static readonly AsyncLocal<Func<string?, string?>?> CurrentExecutionToken = new();

    /// <summary>The suite variable that names RedLeaf's base URL for the process (R6).</summary>
    public const string RedLeafUrlVariable = "REDLEAF_URL";

    /// <summary>The suite variable that names RedCompute's own base URL for the process (R6).</summary>
    public const string RedComputeUrlVariable = "REDCOMPUTE_URL";

    private static volatile string _redLeafUrl = "http://127.0.0.1:18804";
    private static volatile string _redComputeUrl = "http://127.0.0.1:18800";

    /// <summary>
    /// Sets the base URLs the suite variables carry: RedLeaf's (<c>--redleaf-url</c>) and RedCompute's own bound
    /// address. Until it is called they are the suite's standard loopback ports, which is what every Skill and
    /// prompt writes out by hand today.
    /// </summary>
    public static void ConfigureSuiteUrls(string redLeafUrl, string redComputeUrl)
    {
        _redLeafUrl = redLeafUrl.TrimEnd('/');
        _redComputeUrl = redComputeUrl.TrimEnd('/');
    }

    /// <summary>
    /// The two URL variables a process that holds a suite execution token gets, so agents read where RedLeaf and
    /// RedCompute are instead of writing a loopback address into their commands (placement design 5.3): they are
    /// what a node's own relay would answer differently.
    /// </summary>
    public static IReadOnlyDictionary<string, string> SuiteUrlVariables() => new Dictionary<string, string>
    {
        [RedLeafUrlVariable] = _redLeafUrl,
        [RedComputeUrlVariable] = _redComputeUrl,
    };

    /// <summary><see cref="TryResolveDirectory(IExecutionHost, string?, out string?)"/> on this machine.</summary>
    public static bool TryResolveDirectory(string? value, out string? directory)
        => TryResolveDirectory(LocalExecutionHost.Shared, value, out directory);

    /// <summary>
    /// An existing absolute directory on <paramref name="host"/>, as its full path. Qualification and
    /// the full form follow the host's own path rules, not this server's: the folder is on that node.
    /// </summary>
    public static bool TryResolveDirectory(IExecutionHost host, string? value, out string? directory)
    {
        directory = null;
        if (string.IsNullOrWhiteSpace(value) || !host.PathRules.IsPathFullyQualified(value)) return false;
        try
        {
            var full = host.PathRules.GetFullPath(value);
            if (!host.Files.DirectoryExists(host.Path(full))) return false;
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
        {
            environment["REDLEAF_EXECUTION_TOKEN"] = token;
            foreach (var (name, url) in SuiteUrlVariables()) environment[name] = url;
        }
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
