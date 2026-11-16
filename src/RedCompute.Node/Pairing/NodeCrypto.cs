using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace RedCompute.Node.Pairing;

/// <summary>
/// The node key's uses (design 3.2, 5.1), in the formats the kernel verifies: the fingerprint both
/// sides show, the pairing proof and the short-lived assertions. ECDSA P-256 only; signatures are IEEE
/// P1363 (r || s, 64 bytes), base64url, as JWS ES256.
/// </summary>
public static class NodeCrypto
{
    /// <summary>An assertion's lifetime (<c>exp = iat + 60</c>; the kernel accepts at most 120 s).</summary>
    public static readonly TimeSpan AssertionLifetime = TimeSpan.FromSeconds(60);

    /// <summary>The header every node assertion carries (the kernel refuses any other <c>typ</c>).</summary>
    public const string AssertionType = "leaf-node+jwt";

    /// <summary>The first 16 hex digits of SHA-256(SPKI), upper case, grouped <c>3FA2-91C0-77DE-0B14</c>.</summary>
    public static string Fingerprint(ReadOnlySpan<byte> subjectPublicKeyInfo)
    {
        var hex = Convert.ToHexString(SHA256.HashData(subjectPublicKeyInfo))[..16];
        return $"{hex[..4]}-{hex[4..8]}-{hex[8..12]}-{hex[12..16]}";
    }

    /// <summary>
    /// What the key signs to prove, at pairing, that the node holds it (the kernel verifies the same
    /// message): <c>leaf-node-pair.v1</c>, the code without its hyphen, the public key (SPKI, base64)
    /// and the server origin, one per line (<c>\n</c>), in UTF-8.
    /// </summary>
    public static string ProofMessage(string code, string subjectPublicKeyInfoBase64, string origin)
        => string.Join('\n', "leaf-node-pair.v1", code.Replace("-", ""), subjectPublicKeyInfoBase64, origin);

    /// <summary>The pairing proof: the key's signature over <see cref="ProofMessage"/>, base64url.</summary>
    public static string Proof(ECDsa key, string code, ReadOnlySpan<byte> subjectPublicKeyInfo, string origin)
        => Base64Url(key.SignData(Encoding.UTF8.GetBytes(ProofMessage(code, Convert.ToBase64String(subjectPublicKeyInfo), origin)),
            HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));

    /// <summary>
    /// A compact JWS (ES256) the node signs for one request: header <c>{alg: ES256, typ: leaf-node+jwt}</c>,
    /// payload <c>{ iss: nodeId, aud, iat, exp: iat + 60, jti: 128-bit random }</c>. <paramref name="now"/>
    /// is the node's clock corrected by the offset the server last reported.
    /// </summary>
    public static string Assertion(ECDsa key, string nodeId, string audience, DateTimeOffset now, string? jti = null)
    {
        var header = new JsonObject { ["alg"] = "ES256", ["typ"] = AssertionType };
        var iat = now.ToUnixTimeSeconds();
        var payload = new JsonObject
        {
            ["iss"] = nodeId,
            ["aud"] = audience,
            ["iat"] = iat,
            ["exp"] = iat + (long)AssertionLifetime.TotalSeconds,
            ["jti"] = jti ?? Base64Url(RandomNumberGenerator.GetBytes(16)),
        };
        var signingInput = Base64Url(Encoding.UTF8.GetBytes(header.ToJsonString())) + "."
            + Base64Url(Encoding.UTF8.GetBytes(payload.ToJsonString()));
        var signature = key.SignData(Encoding.ASCII.GetBytes(signingInput), HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return signingInput + "." + Base64Url(signature);
    }

    /// <summary>Verifies an ES256 compact JWS against an SPKI and returns its payload (tests, and the shape the kernel checks).</summary>
    public static JsonObject? VerifyAssertion(string token, ReadOnlySpan<byte> subjectPublicKeyInfo)
    {
        var parts = token.Split('.');
        if (parts.Length != 3) return null;
        using var key = ECDsa.Create();
        key.ImportSubjectPublicKeyInfo(subjectPublicKeyInfo, out _);
        var header = JsonNode.Parse(FromBase64Url(parts[0])) as JsonObject;
        if (header?["alg"]?.GetValue<string>() != "ES256" || header["typ"]?.GetValue<string>() != AssertionType) return null;
        if (!key.VerifyData(Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]), FromBase64Url(parts[2]),
                HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
            return null;
        return JsonNode.Parse(FromBase64Url(parts[1])) as JsonObject;
    }

    public static string Base64Url(ReadOnlySpan<byte> bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static byte[] FromBase64Url(string text)
    {
        var padded = text.Replace('-', '+').Replace('_', '/');
        padded += (padded.Length % 4) switch { 2 => "==", 3 => "=", _ => "" };
        return Convert.FromBase64String(padded);
    }
}
