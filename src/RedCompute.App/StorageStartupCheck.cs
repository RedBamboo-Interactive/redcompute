using RedCompute.Core.Storage;

namespace RedCompute.App;

/// <summary>
/// Resolves <see cref="ComputeStorage"/> and reads the rebase map before anything else runs.
/// <c>RedComputeHost</c>'s static state captures paths when its type loads, so an invalid
/// <c>REDCOMPUTE_DATA_ROOT</c> would otherwise surface as a nested type initialization failure instead
/// of its own message. A malformed <c>REDCOMPUTE_REBASE_MAP</c> is the same kind of configuration error.
/// </summary>
internal static class StorageStartupCheck
{
    /// <summary>The map the kernel passed (<c>REDCOMPUTE_REBASE_MAP</c>), or null when none: nothing to rebase.</summary>
    internal static RebaseMap? PendingRebase { get; private set; }

    /// <summary>True when storage resolves; otherwise writes the reason to stderr and returns false.</summary>
    internal static bool Run() => Run(Environment.GetEnvironmentVariable);

    internal static bool Run(Func<string, string?> getVariable)
    {
        try
        {
            ComputeStorage.EnsureConfigured();
            PendingRebase = RebaseMap.FromEnvironment(getVariable);
            return true;
        }
        catch (Exception ex) when (ex is ComputeStorageConfigurationException or RebaseMapException)
        {
            Console.Error.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [App] Refusing to start: {ex.Message}");
            return false;
        }
    }
}
