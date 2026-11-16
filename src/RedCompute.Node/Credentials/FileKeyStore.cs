using System.Security.Cryptography;

namespace RedCompute.Node.Credentials;

/// <summary>
/// The headless fallback (design 3.3): the PKCS#8 key in <c>key.p8</c> in the node's data folder,
/// created with mode 0600 on Linux. Used only with <c>--allow-file-key</c>; the Nodes tab then says
/// the key is stored in a file.
/// </summary>
public sealed class FileKeyStore(NodePaths paths) : INodeKeyStore
{
    public const string Prefix = "file:";
    private const string FileName = "key.p8";

    public string Kind => "file";

    public NodeKey Create(string origin) => new(Prefix + FileName, origin, ECDsa.Create(ECCurve.NamedCurves.nistP256));

    public void Commit(NodeKey key, string nodeId)
    {
        Directory.CreateDirectory(paths.Data);
        var bytes = key.Key.ExportPkcs8PrivateKey();
        try
        {
            var temp = paths.KeyFile + ".tmp";
            File.Delete(temp);
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var stream = new FileStream(temp, options))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temp, paths.KeyFile, overwrite: true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    public ECDsa? Open(string keyRef, string origin)
    {
        if (!File.Exists(paths.KeyFile)) return null;
        var bytes = File.ReadAllBytes(paths.KeyFile);
        try
        {
            var key = ECDsa.Create();
            key.ImportPkcs8PrivateKey(bytes, out _);
            return key;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    public bool Delete(string keyRef, string origin)
    {
        if (!Directory.Exists(paths.Data)) return false;
        var existed = File.Exists(paths.KeyFile);
        File.Delete(paths.KeyFile);
        File.Delete(paths.KeyFile + ".tmp");
        return existed;
    }
}
