using System.Security.Cryptography;
using System.Text;
using RedCompute.PluginSdk.Hosting;

namespace RedCompute.Node;

/// <summary>
/// The machine's id, hashed (design 2.2, 3.4): <c>MachineGuid</c> on Windows, <c>/etc/machine-id</c> on
/// Linux, hashed with a domain prefix (<see cref="Hash"/>). The server refuses a pairing from its own
/// machine (that machine is node zero) and uses the hash for clone hints. The id itself never leaves
/// the machine.
/// </summary>
public static class MachineIdentity
{
    public static string? MachineIdHash(IExecutionHost host)
    {
        var id = RawId(host);
        return string.IsNullOrWhiteSpace(id) ? null : Hash(id);
    }

    /// <summary>
    /// The hash both the node and the kernel compute: lower-case hex SHA-256 of
    /// <c>leaf-node-machine-id:</c> + the trimmed, lower-cased id.
    /// </summary>
    public static string Hash(string machineId)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("leaf-node-machine-id:" + machineId.Trim().ToLowerInvariant())))
            .ToLowerInvariant();

    private static string? RawId(IExecutionHost host)
    {
        if (OperatingSystem.IsWindows())
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
            return key?.GetValue("MachineGuid") as string;
        }
        foreach (var file in new[] { "/etc/machine-id", "/var/lib/dbus/machine-id" })
        {
            var path = host.Path(file);
            if (host.Files.FileExists(path)) return host.Files.ReadAllText(path);
        }
        return null;
    }
}
