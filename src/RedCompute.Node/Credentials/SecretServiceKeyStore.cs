using System.Runtime.InteropServices;
using System.Security.Cryptography;
using RedCompute.NodeLink.Noise;

namespace RedCompute.Node.Credentials;

/// <summary>
/// Linux (design 3.3, D5): the PKCS#8 key in the Secret Service over D-Bus (KWallet's <c>ksecretd</c>
/// on Plasma 6, GNOME Keyring elsewhere), through <c>libsecret-1.so.0</c>'s non-variadic
/// <c>secret_password_*v_sync</c> calls with a GLib hash table of attributes
/// <c>{app: leaf-node, origin, nodeId, keyId}</c>. No package. The key stays in memory until the server
/// paired it (<see cref="Commit"/>), so a refused pairing leaves nothing in the keyring.
/// </summary>
public sealed class SecretServiceKeyStore : INodeKeyStore
{
    public const string Prefix = "secret-service:";
    internal const string App = "leaf-node";

    /// <summary>A keyring call that waits on an unlock prompt nobody answers does not hang the agent forever.</summary>
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(60);

    public string Kind => "secret-service";

    /// <summary>Null when the Secret Service answers; otherwise why not (no libsecret, no D-Bus session, no keyring).</summary>
    public static string? Unavailable()
    {
        if (!OperatingSystem.IsLinux()) return "not Linux";
        try
        {
            return Libsecret.Load() is { } library
                ? Call(() => library.Lookup(new() { ["app"] = App, ["keyId"] = "availability-probe" })) is { Error: { } error }
                    ? error
                    : null
                : "libsecret-1.so.0 is not installed";
        }
        catch (Exception error)
        {
            return error.Message;
        }
    }

    public NodeKey Create(string origin)
        => new(Prefix + Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant(), origin,
            ECDsa.Create(ECCurve.NamedCurves.nistP256));

    public void Commit(NodeKey key, string nodeId)
    {
        var library = Require();
        var pkcs8 = key.Key.ExportPkcs8PrivateKey();
        var secret = Convert.ToBase64String(pkcs8);
        CryptographicOperations.ZeroMemory(pkcs8);
        var result = Call(() => library.Store(
            new() { ["app"] = App, ["origin"] = key.Origin, ["nodeId"] = nodeId, ["keyId"] = KeyId(key.KeyRef) },
            $"Leaf node key ({key.Origin})", secret));
        if (result.Error is not null)
            throw new NodeCommandException("secret_store_failed", $"The Secret Service did not store the node key: {result.Error}");
    }

    public ECDsa? Open(string keyRef, string origin)
    {
        var library = Require();
        var result = Call(() => library.Lookup(new() { ["app"] = App, ["keyId"] = KeyId(keyRef) }));
        if (result.Error is not null)
            throw new NodeCommandException("secret_lookup_failed", $"The Secret Service did not return the node key: {result.Error}");
        if (result.Value is null) return null;
        var bytes = Convert.FromBase64String(result.Value);
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
        var library = Require();
        var result = Call(() => library.Clear(new() { ["app"] = App, ["keyId"] = KeyId(keyRef) }));
        if (result.Error is not null)
            throw new NodeCommandException("secret_clear_failed", $"The Secret Service did not delete the node key: {result.Error}");
        return result.Value is not null;
    }

    private static string KeyId(string keyRef) => keyRef[Prefix.Length..];

    // The link key: the same keyring, the identity's attributes with keyId <id>-link.
    public NoiseKeyPair CreateLinkKey(string keyRef, string origin, string nodeId)
    {
        var library = Require();
        var key = NoiseKeyPair.Generate();
        var pkcs8 = key.ExportPkcs8();
        var secret = Convert.ToBase64String(pkcs8);
        CryptographicOperations.ZeroMemory(pkcs8);
        var result = Call(() => library.Store(
            new() { ["app"] = App, ["origin"] = origin, ["nodeId"] = nodeId, ["keyId"] = KeyId(keyRef) + "-link" },
            $"Leaf node link key ({origin})", secret));
        if (result.Error is not null)
        {
            key.Dispose();
            throw new NodeCommandException("secret_store_failed", $"The Secret Service did not store the link key: {result.Error}");
        }
        return key;
    }

    public NoiseKeyPair? OpenLinkKey(string keyRef, string origin)
    {
        var library = Require();
        var result = Call(() => library.Lookup(new() { ["app"] = App, ["keyId"] = KeyId(keyRef) + "-link" }));
        if (result.Error is not null)
            throw new NodeCommandException("secret_lookup_failed", $"The Secret Service did not return the link key: {result.Error}");
        if (result.Value is null) return null;
        var bytes = Convert.FromBase64String(result.Value);
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
        var library = Require();
        var result = Call(() => library.Clear(new() { ["app"] = App, ["keyId"] = KeyId(keyRef) + "-link" }));
        if (result.Error is not null)
            throw new NodeCommandException("secret_clear_failed", $"The Secret Service did not delete the link key: {result.Error}");
        return result.Value is not null;
    }

    private static Libsecret Require()
        => Libsecret.Load() ?? throw new NodeCommandException("no_secret_service", "libsecret-1.so.0 is not installed on this machine.");

    private static (string? Value, string? Error) Call(Func<(string? Value, string? Error)> call)
    {
        var task = Task.Run(call);
        if (!task.Wait(CallTimeout))
            return (null, "the keyring did not answer (is it locked behind a prompt?)");
        return task.Result;
    }
}

/// <summary>
/// The libsecret and GLib calls the store needs, bound at run time with <see cref="NativeLibrary"/>,
/// so the agent starts on machines without libsecret (it then needs <c>--allow-file-key</c>).
/// </summary>
internal sealed class Libsecret
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int StorevSync(IntPtr schema, IntPtr attributes, IntPtr collection, IntPtr label, IntPtr password,
        IntPtr cancellable, out IntPtr error);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr LookupvSync(IntPtr schema, IntPtr attributes, IntPtr cancellable, out IntPtr error);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int ClearvSync(IntPtr schema, IntPtr attributes, IntPtr cancellable, out IntPtr error);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void PasswordFree(IntPtr password);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr HashTableNew(IntPtr hash, IntPtr equal);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int HashTableInsert(IntPtr table, IntPtr key, IntPtr value);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void HashTableUnref(IntPtr table);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void ErrorFree(IntPtr error);

    // SecretSchema: name, flags, SecretSchemaAttribute[32] {name, type}, reserved, reserved1..7 (64-bit layout).
    private const int SchemaSize = 8 + 8 + 32 * 16 + 8 + 7 * 8;
    private static readonly string[] SchemaAttributes = ["app", "origin", "nodeId", "keyId"];

    private static readonly Lazy<Libsecret?> Instance = new(Bind);

    private readonly StorevSync _store;
    private readonly LookupvSync _lookup;
    private readonly ClearvSync _clear;
    private readonly PasswordFree _free;
    private readonly HashTableNew _tableNew;
    private readonly HashTableInsert _tableInsert;
    private readonly HashTableUnref _tableUnref;
    private readonly ErrorFree _errorFree;
    private readonly IntPtr _strHash;
    private readonly IntPtr _strEqual;
    private readonly IntPtr _schema;

    private Libsecret(IntPtr secret, IntPtr glib)
    {
        T Export<T>(IntPtr library, string name) where T : Delegate
            => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(library, name));
        _store = Export<StorevSync>(secret, "secret_password_storev_sync");
        _lookup = Export<LookupvSync>(secret, "secret_password_lookupv_sync");
        _clear = Export<ClearvSync>(secret, "secret_password_clearv_sync");
        _free = Export<PasswordFree>(secret, "secret_password_free");
        _tableNew = Export<HashTableNew>(glib, "g_hash_table_new");
        _tableInsert = Export<HashTableInsert>(glib, "g_hash_table_insert");
        _tableUnref = Export<HashTableUnref>(glib, "g_hash_table_unref");
        _errorFree = Export<ErrorFree>(glib, "g_error_free");
        _strHash = NativeLibrary.GetExport(glib, "g_str_hash");
        _strEqual = NativeLibrary.GetExport(glib, "g_str_equal");

        // The schema lives for the process: libsecret keeps no reference past a call, but it is tiny.
        _schema = Marshal.AllocHGlobal(SchemaSize);
        for (var offset = 0; offset < SchemaSize; offset += 8) Marshal.WriteInt64(_schema, offset, 0);
        Marshal.WriteIntPtr(_schema, 0, Marshal.StringToCoTaskMemUTF8("org.redbamboo.LeafNode"));
        Marshal.WriteInt32(_schema, 8, 0); // SECRET_SCHEMA_NONE
        for (var i = 0; i < SchemaAttributes.Length; i++)
        {
            Marshal.WriteIntPtr(_schema, 16 + i * 16, Marshal.StringToCoTaskMemUTF8(SchemaAttributes[i]));
            Marshal.WriteInt32(_schema, 16 + i * 16 + 8, 0); // SECRET_SCHEMA_ATTRIBUTE_STRING
        }
    }

    public static Libsecret? Load() => Instance.Value;

    private static Libsecret? Bind()
    {
        if (!OperatingSystem.IsLinux()) return null;
        if (!NativeLibrary.TryLoad("libsecret-1.so.0", out var secret)) return null;
        if (!NativeLibrary.TryLoad("libglib-2.0.so.0", out var glib)) return null;
        try
        {
            return new Libsecret(secret, glib);
        }
        catch (EntryPointNotFoundException)
        {
            return null;
        }
    }

    public (string? Value, string? Error) Store(Dictionary<string, string> attributes, string label, string password)
        => WithAttributes(attributes, table =>
        {
            var labelPtr = Marshal.StringToCoTaskMemUTF8(label);
            var passwordPtr = Marshal.StringToCoTaskMemUTF8(password);
            try
            {
                var ok = _store(_schema, table, IntPtr.Zero, labelPtr, passwordPtr, IntPtr.Zero, out var error);
                return (ok != 0 ? "stored" : null, TakeError(error) ?? (ok != 0 ? null : "the store call failed"));
            }
            finally
            {
                Marshal.FreeCoTaskMem(labelPtr);
                ZeroAndFree(passwordPtr, password.Length);
            }
        });

    public (string? Value, string? Error) Lookup(Dictionary<string, string> attributes)
        => WithAttributes(attributes, table =>
        {
            var result = _lookup(_schema, table, IntPtr.Zero, out var error);
            var message = TakeError(error);
            if (result == IntPtr.Zero) return (null, message);
            try
            {
                return (Marshal.PtrToStringUTF8(result), message);
            }
            finally
            {
                _free(result); // zeroes the secret
            }
        });

    public (string? Value, string? Error) Clear(Dictionary<string, string> attributes)
        => WithAttributes(attributes, table =>
        {
            var removed = _clear(_schema, table, IntPtr.Zero, out var error);
            return (removed != 0 ? "removed" : null, TakeError(error));
        });

    private (string? Value, string? Error) WithAttributes(Dictionary<string, string> attributes,
        Func<IntPtr, (string? Value, string? Error)> call)
    {
        var table = _tableNew(_strHash, _strEqual);
        var strings = new List<IntPtr>();
        try
        {
            foreach (var (name, value) in attributes)
            {
                var key = Marshal.StringToCoTaskMemUTF8(name);
                var text = Marshal.StringToCoTaskMemUTF8(value);
                strings.Add(key);
                strings.Add(text);
                _tableInsert(table, key, text);
            }
            return call(table);
        }
        finally
        {
            _tableUnref(table);
            foreach (var pointer in strings) Marshal.FreeCoTaskMem(pointer);
        }
    }

    // GError { GQuark domain; gint code; gchar *message; }
    private string? TakeError(IntPtr error)
    {
        if (error == IntPtr.Zero) return null;
        try
        {
            return Marshal.PtrToStringUTF8(Marshal.ReadIntPtr(error, 8)) ?? "unknown keyring error";
        }
        finally
        {
            _errorFree(error);
        }
    }

    private static void ZeroAndFree(IntPtr pointer, int length)
    {
        for (var i = 0; i < length; i++) Marshal.WriteByte(pointer, i, 0);
        Marshal.FreeCoTaskMem(pointer);
    }
}
