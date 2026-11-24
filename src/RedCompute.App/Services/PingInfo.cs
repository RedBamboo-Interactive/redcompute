using System.Reflection;
using System.Text.RegularExpressions;

namespace RedCompute.App.Services;

/// <summary>
/// The body of <c>GET /ping</c>: the AppHost shape (<c>ok</c>, <c>service</c>, <c>version</c>)
/// plus <c>commit</c>, the 40-hex commit after '+' in the entry assembly's
/// AssemblyInformationalVersion (null when absent). Release Manager compares it with
/// origin/main to list commits that have not run live yet. <c>features</c> lists the contracts this running
/// RedCompute honours that a caller has to detect (<c>placement.v1</c>, while the kernel resolves placements);
/// it is absent from the body of a build with none.
/// </summary>
public sealed record PingInfo(bool Ok, string Service, string Version, string? Commit,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<string>? Features = null)
{
    private static readonly Regex CommitPattern = new(@"\+([0-9a-fA-F]{40})(?![0-9a-zA-Z])", RegexOptions.Compiled);

    public static PingInfo Create(string service, string version, Assembly? entryAssembly)
        => new(true, service, version, ParseCommit(
            entryAssembly?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion));

    /// <summary>The 40-hex commit after '+' (lower-cased), or null when there is none.</summary>
    public static string? ParseCommit(string? informationalVersion)
    {
        if (string.IsNullOrEmpty(informationalVersion)) return null;
        var plus = informationalVersion.IndexOf('+');
        if (plus < 0) return null;
        var m = CommitPattern.Match(informationalVersion, plus);
        return m.Success ? m.Groups[1].Value.ToLowerInvariant() : null;
    }
}
