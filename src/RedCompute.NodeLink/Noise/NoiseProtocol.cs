using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace RedCompute.NodeLink.Noise;

/// <summary>
/// The Noise Protocol Framework's state objects (revision 34, sections 5.1-5.2) for
/// <c>Noise_IK_P256_AESGCM_SHA256</c>: the Noise state machine and the IK pattern, AES-256-GCM and
/// SHA-256 as the spec defines them, with NIST P-256 as the DH function. P-256 is not one of the spec's
/// curves (the BCL has no X25519), so public keys are 65-byte uncompressed SEC1 points and the DH output is
/// the 32-byte x-coordinate. Phase 4 design, section 1b.
/// </summary>
public static class NoiseProtocol
{
    public const string Name = "Noise_IK_P256_AESGCM_SHA256";
    public const int PublicKeyLength = 65;
    public const int HashLength = 32;
    public const int TagLength = 16;
}

/// <summary>A P-256 key pair for Noise DH. Owns its key.</summary>
public sealed class NoiseKeyPair : IDisposable
{
    private readonly ECDiffieHellman _key;

    private NoiseKeyPair(ECDiffieHellman key)
    {
        _key = key;
        var q = key.ExportParameters(includePrivateParameters: false).Q;
        PublicKey = [0x04, .. q.X!, .. q.Y!];
    }

    /// <summary>The 65-byte uncompressed SEC1 point.</summary>
    public byte[] PublicKey { get; }

    public static NoiseKeyPair Generate() => new(ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256));

    /// <summary>
    /// Wraps a P-256 key the caller made and hands over (a persisted, non-exportable CNG key on Windows): only its
    /// public part is read, the private part stays where it is. The pair owns <paramref name="key"/> from now on.
    /// </summary>
    public static NoiseKeyPair FromKey(ECDiffieHellman key)
    {
        if (key.KeySize != 256)
        {
            key.Dispose();
            throw new CryptographicException("A Noise static key is a P-256 key.");
        }
        return new NoiseKeyPair(key);
    }

    /// <summary>A key pair from its PKCS#8 private key (how a static key is stored).</summary>
    public static NoiseKeyPair FromPkcs8(ReadOnlySpan<byte> pkcs8)
    {
        var key = ECDiffieHellman.Create();
        key.ImportPkcs8PrivateKey(pkcs8, out _);
        if (key.KeySize != 256)
        {
            key.Dispose();
            throw new CryptographicException("A Noise static key is a P-256 key.");
        }
        return new NoiseKeyPair(key);
    }

    public byte[] ExportPkcs8() => _key.ExportPkcs8PrivateKey();

    /// <summary>DH(this, peer): the 32-byte x-coordinate. Throws for a point that is not on the curve.</summary>
    public byte[] Dh(ReadOnlySpan<byte> peerPublicKey)
    {
        using var peer = ImportPublic(peerPublicKey);
        return _key.DeriveRawSecretAgreement(peer.PublicKey);
    }

    /// <summary>A short, human-comparable fingerprint of a public key: the first 8 bytes of SHA-256, as 4 groups.</summary>
    public static string Fingerprint(ReadOnlySpan<byte> publicKey)
    {
        var hex = Convert.ToHexString(SHA256.HashData(publicKey)[..8]);
        return string.Join('-', Enumerable.Range(0, 4).Select(i => hex.Substring(i * 4, 4)));
    }

    /// <summary>True when <paramref name="publicKey"/> is a valid uncompressed P-256 point.</summary>
    public static bool IsValidPublicKey(ReadOnlySpan<byte> publicKey)
    {
        try
        {
            using var _ = ImportPublic(publicKey);
            return true;
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    private static ECDiffieHellman ImportPublic(ReadOnlySpan<byte> point)
    {
        if (point.Length != NoiseProtocol.PublicKeyLength || point[0] != 0x04)
            throw new CryptographicException("A P-256 public key is a 65-byte uncompressed point.");
        var key = ECDiffieHellman.Create();
        try
        {
            key.ImportParameters(new ECParameters
            {
                Curve = ECCurve.NamedCurves.nistP256,
                Q = new ECPoint { X = point[1..33].ToArray(), Y = point[33..].ToArray() },
            });
            return key;
        }
        catch (PlatformNotSupportedException error)
        {
            // CNG reports a point that is not on the curve as an unsupported curve.
            key.Dispose();
            throw new CryptographicException("The public key is not a point on P-256.", error);
        }
        catch
        {
            key.Dispose();
            throw;
        }
    }

    public void Dispose() => _key.Dispose();
}

/// <summary>Noise CipherState with AESGCM: a key and a 64-bit nonce counter (spec 5.1, 12).</summary>
public sealed class NoiseCipherState : IDisposable
{
    private byte[]? _key;
    private AesGcm? _aes;

    public bool HasKey => _key is not null;

    /// <summary>The next nonce. The spec reserves 2^64-1 for rekeying.</summary>
    public ulong Nonce { get; private set; }

    public void InitializeKey(byte[]? key)
    {
        _aes?.Dispose();
        _key = key;
        _aes = key is null ? null : new AesGcm(key, NoiseProtocol.TagLength);
        Nonce = 0;
    }

    public byte[] EncryptWithAd(ReadOnlySpan<byte> ad, ReadOnlySpan<byte> plaintext)
    {
        if (_aes is null) return plaintext.ToArray();
        var output = new byte[plaintext.Length + NoiseProtocol.TagLength];
        Encrypt(_aes, NextNonce(), ad, plaintext, output);
        return output;
    }

    /// <summary>Opens a record; throws <see cref="CryptographicException"/> when it does not authenticate. The nonce advances only on success.</summary>
    public byte[] DecryptWithAd(ReadOnlySpan<byte> ad, ReadOnlySpan<byte> ciphertext)
    {
        if (_aes is null) return ciphertext.ToArray();
        if (ciphertext.Length < NoiseProtocol.TagLength) throw new CryptographicException("The record is shorter than its tag.");
        if (Nonce == ulong.MaxValue) throw new CryptographicException("The nonce space is exhausted.");
        var plaintext = new byte[ciphertext.Length - NoiseProtocol.TagLength];
        _aes.Decrypt(NonceBytes(Nonce), ciphertext[..^NoiseProtocol.TagLength], ciphertext[^NoiseProtocol.TagLength..], plaintext, ad);
        Nonce++;
        return plaintext;
    }

    /// <summary>REKEY(k): the first 32 bytes of ENCRYPT(k, 2^64-1, empty, 32 zero bytes) (spec 4.2, 11.3). The nonce is kept.</summary>
    public void Rekey()
    {
        if (_aes is null) throw new InvalidOperationException("No key to rekey.");
        var output = new byte[32 + NoiseProtocol.TagLength];
        Encrypt(_aes, ulong.MaxValue, [], new byte[32], output);
        var nonce = Nonce;
        InitializeKey(output[..32]);
        Nonce = nonce;
    }

    private ulong NextNonce()
    {
        if (Nonce == ulong.MaxValue) throw new CryptographicException("The nonce space is exhausted.");
        return Nonce++;
    }

    private static void Encrypt(AesGcm aes, ulong nonce, ReadOnlySpan<byte> ad, ReadOnlySpan<byte> plaintext, Span<byte> output)
        => aes.Encrypt(NonceBytes(nonce), plaintext, output[..plaintext.Length], output[plaintext.Length..], ad);

    /// <summary>AESGCM's nonce: 32 zero bits, then the counter big-endian (spec 12.3).</summary>
    private static byte[] NonceBytes(ulong nonce)
    {
        var bytes = new byte[12];
        BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(4), nonce);
        return bytes;
    }

    public void Dispose() => _aes?.Dispose();
}

/// <summary>Noise SymmetricState with SHA-256 (spec 5.2).</summary>
public sealed class NoiseSymmetricState : IDisposable
{
    private byte[] _ck;

    public NoiseSymmetricState(string protocolName)
    {
        var name = Encoding.ASCII.GetBytes(protocolName);
        H = name.Length <= NoiseProtocol.HashLength ? [.. name, .. new byte[NoiseProtocol.HashLength - name.Length]] : SHA256.HashData(name);
        _ck = H.ToArray();
    }

    /// <summary>The handshake hash so far.</summary>
    public byte[] H { get; private set; }

    public NoiseCipherState Cipher { get; } = new();

    public void MixKey(ReadOnlySpan<byte> inputKeyMaterial)
    {
        var (ck, key) = Hkdf2(_ck, inputKeyMaterial);
        _ck = ck;
        Cipher.InitializeKey(key);
    }

    public void MixHash(ReadOnlySpan<byte> data) => H = SHA256.HashData([.. H, .. data]);

    public byte[] EncryptAndHash(ReadOnlySpan<byte> plaintext)
    {
        var ciphertext = Cipher.EncryptWithAd(H, plaintext);
        MixHash(ciphertext);
        return ciphertext;
    }

    public byte[] DecryptAndHash(ReadOnlySpan<byte> ciphertext)
    {
        var plaintext = Cipher.DecryptWithAd(H, ciphertext);
        MixHash(ciphertext);
        return plaintext;
    }

    /// <summary>The two transport keys: initiator→responder, then responder→initiator.</summary>
    public (byte[] InitiatorToResponder, byte[] ResponderToInitiator) Split() => Hkdf2(_ck, []);

    /// <summary>Noise's HKDF with two outputs (spec 4.3), on HMAC-SHA256.</summary>
    internal static (byte[] First, byte[] Second) Hkdf2(ReadOnlySpan<byte> chainingKey, ReadOnlySpan<byte> inputKeyMaterial)
    {
        var temp = HMACSHA256.HashData(chainingKey, inputKeyMaterial);
        var first = HMACSHA256.HashData(temp, new byte[] { 0x01 });
        var second = HMACSHA256.HashData(temp, (byte[])[.. first, 0x02]);
        return (first, second);
    }

    public void Dispose() => Cipher.Dispose();
}
