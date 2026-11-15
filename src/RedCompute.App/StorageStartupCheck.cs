using RedCompute.Core.Storage;

namespace RedCompute.App;

/// <summary>
/// Resolves <see cref="ComputeStorage"/> before anything else runs. <c>RedComputeHost</c>'s static
/// state captures paths when its type loads, so an invalid <c>REDCOMPUTE_DATA_ROOT</c> would
/// otherwise surface as a nested type initialization failure instead of its own message.
/// </summary>
internal static class StorageStartupCheck
{
    /// <summary>True when storage resolves; otherwise writes the reason to stderr and returns false.</summary>
    internal static bool Run()
    {
        try
        {
            ComputeStorage.EnsureConfigured();
            return true;
        }
        catch (ComputeStorageConfigurationException ex)
        {
            Console.Error.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [App] Refusing to start: {ex.Message}");
            return false;
        }
    }
}
