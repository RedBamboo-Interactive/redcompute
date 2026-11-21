using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using RedCompute.NodeLink.Noise;

namespace RedCompute.Node.Credentials;

/// <summary>
/// Windows (design 3.3): the private key in the CNG user key store (Microsoft Software KSP, per user,
/// DPAPI-protected, <b>non-exportable</b>: the agent can sign, nobody can copy it out), named
/// <c>LeafNode/&lt;origin&gt;/&lt;random&gt;</c>; the key's name and the node id in a Credential Manager
/// generic credential <c>LeafNode:&lt;origin&gt;</c>, persisted to this machine only (it does not roam).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class CngKeyStore : INodeKeyStore
{
    public const string Prefix = "cng:";

    public string Kind => "cng";

    public static string CredentialTarget(string origin) => "LeafNode:" + origin;

    public NodeKey Create(string origin)
    {
        var name = $"LeafNode/{origin}/{Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant()}";
        using var key = CngKey.Create(CngAlgorithm.ECDsaP256, name, new CngKeyCreationParameters
        {
            Provider = CngProvider.MicrosoftSoftwareKeyStorageProvider,
            ExportPolicy = CngExportPolicies.None,
            KeyUsage = CngKeyUsages.Signing,
            KeyCreationOptions = CngKeyCreationOptions.None, // the user's store, not the machine's
        });
        return new NodeKey(Prefix + name, origin, new ECDsaCng(key));
    }

    public void Commit(NodeKey key, string nodeId)
    {
        var blob = new JsonObject { ["keyName"] = key.KeyRef[Prefix.Length..], ["nodeId"] = nodeId }.ToJsonString();
        CredentialManager.Write(CredentialTarget(key.Origin), "leaf-node", Encoding.UTF8.GetBytes(blob),
            "Leaf node key reference (the key itself stays in the Windows key store)");
    }

    public ECDsa? Open(string keyRef, string origin)
    {
        var name = keyRef[Prefix.Length..];
        if (!CngKey.Exists(name, CngProvider.MicrosoftSoftwareKeyStorageProvider)) return null;
        using var key = CngKey.Open(name, CngProvider.MicrosoftSoftwareKeyStorageProvider);
        return new ECDsaCng(key);
    }

    public bool Delete(string keyRef, string origin)
    {
        var deleted = CredentialManager.Delete(CredentialTarget(origin));
        var name = keyRef[Prefix.Length..];
        if (CngKey.Exists(name, CngProvider.MicrosoftSoftwareKeyStorageProvider))
        {
            using var key = CngKey.Open(name, CngProvider.MicrosoftSoftwareKeyStorageProvider);
            key.Delete();
            deleted = true;
        }
        return deleted;
    }

    public NoiseKeyPair CreateLinkKey(string keyRef, string origin, string nodeId)
    {
        DeleteLinkKey(keyRef, origin);
        using var key = CngKey.Create(CngAlgorithm.ECDiffieHellmanP256, LinkKeyName(keyRef), new CngKeyCreationParameters
        {
            Provider = CngProvider.MicrosoftSoftwareKeyStorageProvider,
            ExportPolicy = CngExportPolicies.None,
            KeyUsage = CngKeyUsages.KeyAgreement,
            KeyCreationOptions = CngKeyCreationOptions.None,
        });
        return NoiseKeyPair.FromKey(new ECDiffieHellmanCng(key));
    }

    public NoiseKeyPair? OpenLinkKey(string keyRef, string origin)
    {
        var name = LinkKeyName(keyRef);
        if (!CngKey.Exists(name, CngProvider.MicrosoftSoftwareKeyStorageProvider)) return null;
        using var key = CngKey.Open(name, CngProvider.MicrosoftSoftwareKeyStorageProvider);
        return NoiseKeyPair.FromKey(new ECDiffieHellmanCng(key));
    }

    public bool DeleteLinkKey(string keyRef, string origin)
    {
        var name = LinkKeyName(keyRef);
        if (!CngKey.Exists(name, CngProvider.MicrosoftSoftwareKeyStorageProvider)) return false;
        using var key = CngKey.Open(name, CngProvider.MicrosoftSoftwareKeyStorageProvider);
        key.Delete();
        return true;
    }

    /// <summary><c>LeafNode/&lt;origin&gt;/&lt;random&gt;/link</c>: the identity key's name with <c>/link</c>.</summary>
    internal static string LinkKeyName(string keyRef) => keyRef[Prefix.Length..] + "/link";

    /// <summary>Whether the key and the credential still exist (status, tests, the uninstall check).</summary>
    public static (bool Key, bool Credential) Exists(string keyRef, string origin)
        => (CngKey.Exists(keyRef[Prefix.Length..], CngProvider.MicrosoftSoftwareKeyStorageProvider),
            CredentialManager.Read(CredentialTarget(origin)) is not null);
}

/// <summary>Generic credentials through <c>advapi32</c> (CredWriteW/CredReadW/CredDeleteW). No package.</summary>
[SupportedOSPlatform("windows")]
internal static class CredentialManager
{
    private const uint CredTypeGeneric = 1;
    private const uint CredPersistLocalMachine = 2;
    private const int ErrorNotFound = 1168;

    [StructLayout(LayoutKind.Sequential)]
    private struct Credential
    {
        public uint Flags;
        public uint Type;
        public IntPtr TargetName;
        public IntPtr Comment;
        public uint LastWrittenLow;
        public uint LastWrittenHigh;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public IntPtr TargetAlias;
        public IntPtr UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", SetLastError = true)]
    private static extern bool CredWrite(ref Credential credential, uint flags);

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CredRead(string target, uint type, uint flags, out IntPtr credential);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CredDelete(string target, uint type, uint flags);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr buffer);

    public static void Write(string target, string userName, byte[] blob, string comment)
    {
        var targetPtr = Marshal.StringToCoTaskMemUni(target);
        var userPtr = Marshal.StringToCoTaskMemUni(userName);
        var commentPtr = Marshal.StringToCoTaskMemUni(comment);
        var blobPtr = Marshal.AllocCoTaskMem(blob.Length);
        try
        {
            Marshal.Copy(blob, 0, blobPtr, blob.Length);
            var credential = new Credential
            {
                Type = CredTypeGeneric,
                TargetName = targetPtr,
                Comment = commentPtr,
                CredentialBlobSize = (uint)blob.Length,
                CredentialBlob = blobPtr,
                Persist = CredPersistLocalMachine,
                UserName = userPtr,
            };
            if (!CredWrite(ref credential, 0))
                throw new NodeCommandException("credential_write_failed",
                    $"Windows Credential Manager refused the node's entry (error {Marshal.GetLastWin32Error()}).");
        }
        finally
        {
            Marshal.FreeCoTaskMem(targetPtr);
            Marshal.FreeCoTaskMem(userPtr);
            Marshal.FreeCoTaskMem(commentPtr);
            Marshal.FreeCoTaskMem(blobPtr);
        }
    }

    public static byte[]? Read(string target)
    {
        if (!CredRead(target, CredTypeGeneric, 0, out var pointer)) return null;
        try
        {
            var credential = Marshal.PtrToStructure<Credential>(pointer);
            var blob = new byte[credential.CredentialBlobSize];
            if (blob.Length > 0) Marshal.Copy(credential.CredentialBlob, blob, 0, blob.Length);
            return blob;
        }
        finally
        {
            CredFree(pointer);
        }
    }

    public static bool Delete(string target)
    {
        if (CredDelete(target, CredTypeGeneric, 0)) return true;
        var error = Marshal.GetLastWin32Error();
        if (error == ErrorNotFound) return false;
        throw new NodeCommandException("credential_delete_failed", $"Windows Credential Manager could not delete '{target}' (error {error}).");
    }
}
