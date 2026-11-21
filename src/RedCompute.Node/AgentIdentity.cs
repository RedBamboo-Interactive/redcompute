using System.Reflection;
using System.Runtime.InteropServices;
using RedCompute.NodeLink;

namespace RedCompute.Node;

/// <summary>This agent: its version, OS and architecture, and the link features it implements.</summary>
public static class AgentIdentity
{
    public static string Version { get; } = ReadVersion();

    public static string Os => OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsLinux() ? "linux" : "other";

    public static string Arch => RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant();

    public static AgentInfo Info => new(Version, Os, Arch);

    /// <summary>
    /// What this agent answers (design 4.8, 4.9; Phase 4 design 2.1). <c>spool.disk</c> is not listed: the disk spool
    /// exists in the protocol library, but no stream uses it. The Phase 4 features only take effect with a server that
    /// lists them too.
    /// </summary>
    public static IReadOnlyList<string> Features { get; } =
        [LinkFeatures.DescribeV1, LinkFeatures.HardwareV1, LinkFeatures.CliV1, LinkFeatures.DiagEcho,
         LinkFeatures.RuntimeV1, LinkFeatures.ForwardHttpV1, LinkFeatures.ForwardWsV1];

    private static string ReadVersion()
    {
        var informational = typeof(AgentIdentity).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        var version = informational?.Split('+')[0];
        return AgentVersions.IsValid(version) ? version! : "0.0.0";
    }
}
