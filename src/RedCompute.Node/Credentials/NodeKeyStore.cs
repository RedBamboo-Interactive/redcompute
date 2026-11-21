using System.Security.Cryptography;
using RedCompute.NodeLink.Noise;

namespace RedCompute.Node.Credentials;

/// <summary>
/// A node key being paired or in use: the private key (never exportable where the OS allows it), its
/// SPKI and the reference <c>node.json</c> keeps (<c>cng:…</c>, <c>secret-service:…</c>, <c>file:key.p8</c>).
/// </summary>
public sealed class NodeKey(string keyRef, string origin, ECDsa key) : IDisposable
{
    public string KeyRef { get; } = keyRef;
    public string Origin { get; } = origin;
    public ECDsa Key { get; } = key;
    public byte[] SubjectPublicKeyInfo { get; } = key.ExportSubjectPublicKeyInfo();

    public void Dispose() => Key.Dispose();
}

/// <summary>
/// Where the node key lives (design 3.3). A key is made by <see cref="Create"/>, recorded by
/// <see cref="Commit"/> once the server paired it, opened by its reference for every assertion, and
/// deleted on revoke, unpair and uninstall. Nothing else is ever stored: no token, no provider credential.
/// </summary>
public interface INodeKeyStore
{
    /// <summary><c>cng</c>, <c>secret-service</c> or <c>file</c>: reported at pairing and shown in the Nodes tab.</summary>
    string Kind { get; }

    /// <summary>A new P-256 key for <paramref name="origin"/>. CNG persists it now; the others keep it in memory until <see cref="Commit"/>.</summary>
    NodeKey Create(string origin);

    /// <summary>Records the key (and the node id beside it) after the server accepted it.</summary>
    void Commit(NodeKey key, string nodeId);

    /// <summary>The key for a reference, or null when it is gone.</summary>
    ECDsa? Open(string keyRef, string origin);

    /// <summary>Deletes the key and its record; true when something was deleted.</summary>
    bool Delete(string keyRef, string origin);

    /// <summary>
    /// A new link key for the identity <paramref name="keyRef"/> (Phase 4 P0.5c): the P-256 key-agreement key the
    /// node's encrypted link uses as its Noise static, kept beside the identity with the same protection and
    /// replacing any earlier one. The identity signs it into every handshake, so it never leaves the node.
    /// </summary>
    NoiseKeyPair CreateLinkKey(string keyRef, string origin, string nodeId);

    /// <summary>The link key for the identity <paramref name="keyRef"/>, or null when there is none.</summary>
    NoiseKeyPair? OpenLinkKey(string keyRef, string origin);

    /// <summary>Deletes the link key; true when there was one.</summary>
    bool DeleteLinkKey(string keyRef, string origin);
}

/// <summary>Chooses the store for a new key, and the store that holds an existing reference.</summary>
public interface INodeKeyStores
{
    /// <summary>
    /// Windows: CNG + Credential Manager. Linux: the Secret Service, or a 0600 file only when
    /// <paramref name="allowFileKey"/> (headless machines). Throws with the reason otherwise.
    /// </summary>
    INodeKeyStore ForNewKey(bool allowFileKey);

    INodeKeyStore ForKeyRef(string keyRef);
}

/// <summary>The OS stores.</summary>
public sealed class SystemKeyStores(NodePaths paths) : INodeKeyStores
{
    public INodeKeyStore ForNewKey(bool allowFileKey)
    {
        if (OperatingSystem.IsWindows()) return new CngKeyStore();
        var unavailable = SecretServiceKeyStore.Unavailable();
        if (unavailable is null) return new SecretServiceKeyStore();
        if (allowFileKey) return new FileKeyStore(paths);
        throw new NodeCommandException("no_secret_service",
            $"No Secret Service is available to keep the node key ({unavailable}). On a machine without a desktop "
            + "keyring, run the command again with --allow-file-key to keep the key in a file only you can read.");
    }

    public INodeKeyStore ForKeyRef(string keyRef)
    {
        if (keyRef.StartsWith("cng:", StringComparison.Ordinal) && OperatingSystem.IsWindows()) return new CngKeyStore();
        if (keyRef.StartsWith(SecretServiceKeyStore.Prefix, StringComparison.Ordinal)) return new SecretServiceKeyStore();
        if (keyRef.StartsWith(FileKeyStore.Prefix, StringComparison.Ordinal)) return new FileKeyStore(paths);
        throw new NodeCommandException("bad_key_ref", $"node.json names a key store this machine does not have ({keyRef}).");
    }
}
