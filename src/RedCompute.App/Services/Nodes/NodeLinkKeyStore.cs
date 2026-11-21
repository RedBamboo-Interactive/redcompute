using System.IO;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RedCompute.NodeLink.Noise;

namespace RedCompute.App.Services.Nodes;

/// <summary>
/// RedCompute's static link key (Phase 4 design, 1b.4): the P-256 key every node pins at pairing and
/// encrypts its link to. It lives in <c>&lt;data root&gt;/nodes/link-key/current.json</c>. The private key is
/// protected with DPAPI (CurrentUser) on Windows and kept in a 0600 file in a 0700 folder elsewhere.
/// <para>
/// It is created the first time it is asked for (the kernel's <c>GET /nodes/link-key</c> at a pairing, or a
/// node's handshake). It is never replaced on its own: a file that does not open is an error the operator
/// sees, never a silent new key, because a new key would orphan every pinned node.
/// <c>previous.json</c>, when present, is still accepted (a rotation's grace, 1b.7).
/// </para>
/// </summary>
public sealed class NodeLinkKeyStore : IDisposable
{
    public const string CurrentFile = "current.json";
    public const string PreviousFile = "previous.json";

    private static readonly byte[] Entropy = Encoding.ASCII.GetBytes("RedCompute node link key v1");

    private readonly string _directory;
    private readonly Action<string> _log;
    private readonly object _gate = new();
    private IReadOnlyList<NoiseKeyPair>? _keys;
    private DateTimeOffset _createdAt;

    public NodeLinkKeyStore(string directory, Action<string> log)
    {
        _directory = directory;
        _log = log;
    }

    public string Directory => _directory;

    /// <summary>The keys a handshake may be for: the current one first, then a rotation's previous one.</summary>
    public IReadOnlyList<NoiseKeyPair> Keys()
    {
        lock (_gate) return _keys ??= LoadOrCreate();
    }

    /// <summary>The current key as the kernel hands it to a pairing node.</summary>
    public NodeLinkKeyView Describe()
    {
        var current = Keys()[0];
        var info = NodeLinkKeyInfo.For(current.PublicKey);
        return new NodeLinkKeyView(info.Protocol, info.PublicKey, info.Fingerprint, info.KeyId, _createdAt);
    }

    /// <summary><see cref="Describe"/> without creating the key: null before it exists, or when it does not open.</summary>
    public NodeLinkKeyView? DescribeIfCreated()
    {
        lock (_gate)
            if (_keys is null && !File.Exists(Path.Combine(_directory, CurrentFile)))
                return null;
        try
        {
            return Describe();
        }
        catch (Exception error) when (error is CryptographicException or IOException or JsonException or FormatException
                                          or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private IReadOnlyList<NoiseKeyPair> LoadOrCreate()
    {
        var current = Path.Combine(_directory, CurrentFile);
        if (!File.Exists(current)) Create(current);
        var (key, createdAt) = Load(current);
        _createdAt = createdAt;
        var keys = new List<NoiseKeyPair> { key };
        var previous = Path.Combine(_directory, PreviousFile);
        if (File.Exists(previous))
        {
            try
            {
                keys.Add(Load(previous).Key);
            }
            catch (Exception error) when (error is CryptographicException or IOException or JsonException or FormatException)
            {
                _log($"[NodeHub] The previous link key does not open ({error.Message}); nodes still on it need a re-pin.");
            }
        }
        return keys;
    }

    private void Create(string path)
    {
        CreatePrivateDirectory(_directory);
        using var key = NoiseKeyPair.Generate();
        var pkcs8 = key.ExportPkcs8();
        var document = new JsonObject
        {
            ["version"] = 1,
            ["protocol"] = NoiseProtocol.Name,
            ["createdAt"] = DateTimeOffset.UtcNow,
            ["publicKey"] = NodeLinkKeyInfo.Base64Url(key.PublicKey),
        };
        if (OperatingSystem.IsWindows())
        {
            document["protection"] = "dpapi-user";
            document["privateKey"] = Convert.ToBase64String(ProtectWindows(pkcs8));
        }
        else
        {
            document["protection"] = "file-0600";
            document["privateKey"] = Convert.ToBase64String(pkcs8);
        }
        CryptographicOperations.ZeroMemory(pkcs8);

        // Written beside, then moved without overwrite: a key that already exists is never replaced.
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using (var stream = new FileStream(temp, options))
            stream.Write(Encoding.UTF8.GetBytes(document.ToJsonString()));
        try
        {
            File.Move(temp, path, overwrite: false);
            _log($"[NodeHub] Created the node link key {NoiseKeyPair.Fingerprint(key.PublicKey)}.");
        }
        catch (IOException) when (File.Exists(path))
        {
            File.Delete(temp);
        }
    }

    private static (NoiseKeyPair Key, DateTimeOffset CreatedAt) Load(string path)
    {
        var document = JsonNode.Parse(File.ReadAllText(path)) as JsonObject
            ?? throw new JsonException($"{path} is not a link key file.");
        var protection = document["protection"]?.GetValue<string>();
        var sealedKey = Convert.FromBase64String(document["privateKey"]?.GetValue<string>() ?? "");
        byte[] pkcs8 = protection switch
        {
            "dpapi-user" when OperatingSystem.IsWindows() => UnprotectWindows(sealedKey),
            "file-0600" => sealedKey,
            _ => throw new CryptographicException($"{path}: link key protection '{protection}' cannot be opened here."),
        };
        try
        {
            var key = NoiseKeyPair.FromPkcs8(pkcs8);
            var stated = document["publicKey"]?.GetValue<string>();
            if (stated is not null && stated != NodeLinkKeyInfo.Base64Url(key.PublicKey))
            {
                key.Dispose();
                throw new CryptographicException($"{path}: the private key does not match the public key it states.");
            }
            var createdAt = document["createdAt"] is JsonValue value && value.TryGetValue<DateTimeOffset>(out var at) ? at : DateTimeOffset.UnixEpoch;
            return (key, createdAt);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pkcs8);
        }
    }

    private static void CreatePrivateDirectory(string directory)
    {
        if (OperatingSystem.IsWindows()) System.IO.Directory.CreateDirectory(directory);
        else System.IO.Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    [SupportedOSPlatform("windows")]
    private static byte[] ProtectWindows(byte[] data) => ProtectedData.Protect(data, Entropy, DataProtectionScope.CurrentUser);

    [SupportedOSPlatform("windows")]
    private static byte[] UnprotectWindows(byte[] data) => ProtectedData.Unprotect(data, Entropy, DataProtectionScope.CurrentUser);

    public void Dispose()
    {
        lock (_gate)
        {
            if (_keys is null) return;
            foreach (var key in _keys) key.Dispose();
            _keys = null;
        }
    }
}

/// <summary><c>GET /nodes/link-key</c>: the current server link key, for the kernel to hand a pairing node.</summary>
public sealed record NodeLinkKeyView(string Protocol, string PublicKey, string Fingerprint, string KeyId, DateTimeOffset CreatedAt);
