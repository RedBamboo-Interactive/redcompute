using System.Security.Cryptography;
using RedCompute.NodeLink.Noise;

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

    public NoiseKeyPair CreateLinkKey(string keyRef, string origin, string nodeId)
    {
        var key = NoiseKeyPair.Generate();
        var bytes = key.ExportPkcs8();
        try
        {
            Write(paths.LinkKeyFile, bytes);
            return key;
        }
        catch
        {
            key.Dispose();
            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    public NoiseKeyPair? OpenLinkKey(string keyRef, string origin)
    {
        if (!File.Exists(paths.LinkKeyFile)) return null;
        var bytes = File.ReadAllBytes(paths.LinkKeyFile);
        try
        {
            return NoiseKeyPair.FromPkcs8(bytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    public bool DeleteLinkKey(string keyRef, string origin)
    {
        var existed = File.Exists(paths.LinkKeyFile);
        File.Delete(paths.LinkKeyFile);
        File.Delete(paths.LinkKeyFile + ".tmp");
        return existed;
    }

    /// <summary>A 0600 file (on Linux), written beside and moved in.</summary>
    private void Write(string file, byte[] bytes)
    {
        Directory.CreateDirectory(paths.Data);
        var temp = file + ".tmp";
        File.Delete(temp);
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using (var stream = new FileStream(temp, options))
        {
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temp, file, overwrite: true);
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
