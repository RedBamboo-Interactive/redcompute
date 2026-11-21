using System.Security.Cryptography;

namespace RedCompute.NodeLink.Noise;

/// <summary>
/// The identity binding both ends agree on (Phase 4 design, 1b.4): the first handshake message's payload is
/// an ECDSA P-256 / SHA-256 signature, IEEE P1363 (r ‖ s, 64 bytes), by the node's paired identity key over
/// <see cref="NoiseBindingContext.Message"/>. The server verifies it against the node's
/// <c>public_key_spki</c> as the kernel lists it.
/// </summary>
public static class NoiseBinding
{
    public const int SignatureLength = 64;

    /// <summary>The payload for <paramref name="binding"/>, from a signer of raw bytes (the node's key store).</summary>
    public static byte[] Payload(NoiseBindingContext binding, Func<byte[], byte[]> signP1363)
    {
        var signature = signP1363(binding.Message());
        if (signature.Length != SignatureLength)
            throw new CryptographicException($"The binding signature is a {SignatureLength}-byte P1363 ECDSA P-256 signature.");
        return signature;
    }

    /// <summary>True when <paramref name="payload"/> is the identity key's signature over this handshake's binding.</summary>
    public static bool Verify(ReadOnlySpan<byte> identitySpki, NoiseBindingContext binding, ReadOnlySpan<byte> payload)
    {
        if (payload.Length != SignatureLength) return false;
        try
        {
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(identitySpki, out var read);
            if (read != identitySpki.Length || key.KeySize != 256) return false;
            return key.VerifyData(binding.Message(), payload, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        catch (CryptographicException)
        {
            return false;
        }
    }
}

/// <summary>
/// A server static link key as the kernel hands it to a pairing node (<c>linkKey</c> in the pair response):
/// the 65-byte SEC1 point in base64url, its fingerprint, and a short key id.
/// </summary>
public sealed record NodeLinkKeyInfo(string Protocol, string PublicKey, string Fingerprint, string KeyId)
{
    public static NodeLinkKeyInfo For(ReadOnlySpan<byte> publicKey) => new(
        NoiseProtocol.Name,
        Base64Url(publicKey),
        NoiseKeyPair.Fingerprint(publicKey),
        "lk_" + Convert.ToHexString(SHA256.HashData(publicKey)[..6]).ToLowerInvariant());

    /// <summary>The point, or null when <see cref="PublicKey"/> is not a valid P-256 point.</summary>
    public byte[]? PublicKeyBytes()
    {
        try
        {
            var bytes = FromBase64Url(PublicKey);
            return NoiseKeyPair.IsValidPublicKey(bytes) ? bytes : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    public static string Base64Url(ReadOnlySpan<byte> bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>Base64url or standard base64, padded or not.</summary>
    public static byte[] FromBase64Url(string text)
    {
        var s = text.Trim().Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(s.PadRight(s.Length + (4 - s.Length % 4) % 4, '='));
    }
}
