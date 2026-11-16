using RedCompute.Core.Storage;

namespace RedCompute.App.Data;

/// <summary>
/// The one place <c>Jobs.OutputLocation</c> is read. Writers store the absolute path as always (a rolled-back
/// build must read it); a relative value, which a later release may write, resolves against
/// <see cref="ComputeStorage.Outputs"/>. <see cref="RedComputeDbContext"/> applies <see cref="Resolve"/> as the
/// column's read conversion, so the output endpoint, cleanup and the artifact listing all see an absolute path.
/// </summary>
public static class JobOutputPaths
{
    // Tests point this at a temp folder without touching the process-wide storage.
    private static readonly AsyncLocal<string?> RootOverride = new();

    internal static string? OverrideRoot
    {
        get => RootOverride.Value;
        set => RootOverride.Value = value;
    }

    /// <summary>The folder stored output paths are relative to.</summary>
    public static string Root => RootOverride.Value ?? ComputeStorage.Current.Outputs;

    /// <summary>The absolute path of a stored value, whichever form it has.</summary>
    public static string? Resolve(string? stored) => StoredPaths.Resolve(Root, stored);
}
