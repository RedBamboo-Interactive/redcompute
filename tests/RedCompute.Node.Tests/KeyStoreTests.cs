using System.Security.Cryptography;
using System.Text;
using RedCompute.Node.Credentials;
using RedCompute.Tests;
using Xunit;

namespace RedCompute.Node.Tests;

/// <summary>Design 8.3: the credential store round-trips on each OS and leaves nothing once deleted.</summary>
public sealed class KeyStoreTests : IDisposable
{
    private const string Origin = "https://leaf-node-tests.invalid";
    private readonly NodeHome _home = new("keys");

    public void Dispose() => _home.Dispose();

    [Fact]
    public void The_file_store_keeps_the_key_readable_only_by_the_user_and_deletes_it()
    {
        var store = new FileKeyStore(_home.Paths);
        using var key = store.Create(Origin);
        Assert.False(File.Exists(_home.Paths.KeyFile)); // nothing on disk until the server paired it

        store.Commit(key, "node_01JTESTTESTTESTTESTTESTTES");
        Assert.True(File.Exists(_home.Paths.KeyFile));
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(_home.Paths.KeyFile));
        using (var opened = store.Open(key.KeyRef, Origin)!)
            AssertSameKey(key.Key, opened);

        Assert.True(store.Delete(key.KeyRef, Origin));
        Assert.False(File.Exists(_home.Paths.KeyFile));
        Assert.Null(store.Open(key.KeyRef, Origin));
        Assert.False(store.Delete(key.KeyRef, Origin));
    }

    [WindowsFact("CNG user key store and Credential Manager")]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public void Windows_keeps_a_non_exportable_cng_key_and_its_credential_manager_entry()
    {
        var store = new CngKeyStore();
        var origin = Origin + "/" + Guid.NewGuid().ToString("N")[..8]; // a target no real node uses
        using var key = store.Create(origin);
        try
        {
            Assert.StartsWith("cng:LeafNode/" + origin + "/", key.KeyRef);
            // Non-exportable: the agent can sign, nobody can copy the private key out.
            Assert.ThrowsAny<CryptographicException>(() => key.Key.ExportPkcs8PrivateKey());
            Assert.Equal((true, false), CngKeyStore.Exists(key.KeyRef, origin));

            store.Commit(key, "node_01JTESTTESTTESTTESTTESTTES");
            Assert.Equal((true, true), CngKeyStore.Exists(key.KeyRef, origin));
            var blob = Encoding.UTF8.GetString(CredentialManager.Read(CngKeyStore.CredentialTarget(origin))!);
            Assert.Contains("node_01JTESTTESTTESTTESTTESTTES", blob);
            Assert.Contains(key.KeyRef["cng:".Length..], blob);

            using (var opened = store.Open(key.KeyRef, origin)!)
                AssertSameKey(key.Key, opened);
        }
        finally
        {
            store.Delete(key.KeyRef, origin);
        }
        Assert.Equal((false, false), CngKeyStore.Exists(key.KeyRef, origin));
        Assert.Null(store.Open(key.KeyRef, origin));
        Assert.False(store.Delete(key.KeyRef, origin));
    }

    [SecretServiceFact]
    public void Linux_keeps_the_key_in_the_secret_service_and_deletes_it()
    {
        var store = new SecretServiceKeyStore();
        using var key = store.Create(Origin);
        Assert.Null(store.Open(key.KeyRef, Origin)); // in memory until the server paired it
        try
        {
            store.Commit(key, "node_01JTESTTESTTESTTESTTESTTES");
            using var opened = store.Open(key.KeyRef, Origin)!;
            AssertSameKey(key.Key, opened);
        }
        finally
        {
            store.Delete(key.KeyRef, Origin);
        }
        Assert.Null(store.Open(key.KeyRef, Origin));
    }

    [Fact]
    public void A_new_key_goes_to_the_os_store_and_to_a_file_only_when_allowed()
    {
        var stores = new SystemKeyStores(_home.Paths);
        if (OperatingSystem.IsWindows())
        {
            Assert.Equal("cng", stores.ForNewKey(allowFileKey: true).Kind);
            Assert.Equal("cng", stores.ForNewKey(allowFileKey: false).Kind);
        }
        else if (SecretServiceKeyStore.Unavailable() is null)
        {
            Assert.Equal("secret-service", stores.ForNewKey(allowFileKey: false).Kind);
        }
        else
        {
            Assert.Equal("no_secret_service", Assert.Throws<NodeCommandException>(() => stores.ForNewKey(allowFileKey: false)).Code);
            Assert.Equal("file", stores.ForNewKey(allowFileKey: true).Kind);
        }
        Assert.Equal("file", stores.ForKeyRef("file:key.p8").Kind);
        Assert.Equal("bad_key_ref", Assert.Throws<NodeCommandException>(() => stores.ForKeyRef("tpm:1")).Code);
    }

    private static void AssertSameKey(ECDsa expected, ECDsa actual)
    {
        Assert.Equal(expected.ExportSubjectPublicKeyInfo(), actual.ExportSubjectPublicKeyInfo());
        var data = Encoding.UTF8.GetBytes("leaf-node");
        Assert.True(expected.VerifyData(data, actual.SignData(data, HashAlgorithmName.SHA256), HashAlgorithmName.SHA256));
    }
}

/// <summary>Runs where a Secret Service answers (a desktop Linux session); skipped elsewhere.</summary>
internal sealed class SecretServiceFactAttribute : FactAttribute
{
    public SecretServiceFactAttribute()
    {
        if (!OperatingSystem.IsLinux()) Skip = "Linux only: the Secret Service";
        else if (SecretServiceKeyStore.Unavailable() is { } reason) Skip = "No Secret Service here: " + reason;
    }
}
