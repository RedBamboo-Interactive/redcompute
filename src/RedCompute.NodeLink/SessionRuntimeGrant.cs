using System.Security.Cryptography;
using System.Text.Json;
using RedCompute.NodeLink.Noise;

namespace RedCompute.NodeLink;

public enum SessionRuntimeOperation
{
    Start,
    Resume,
    SendInput,
    Park,
    Inspect,
    ReadEvents,
    AcknowledgeEvents,
    PrepareMove,
    SealMoveSource,
    ActivateMove,
    DiscardMove,
}

public sealed record SessionRuntimeScope(string NodeId, string RuntimeId, string LogicalSessionId, long Generation,
    string OwnerId, string AccessScope);

/// <summary>A short-lived command signed by the controller key pinned for the current node pairing.</summary>
public sealed record SessionRuntimeGrant(SessionRuntimeScope Scope, SessionRuntimeOperation Operation,
    string RequestId, long ExpiresAtUnixSeconds, byte[] Payload, byte[] Signature)
{
    public const int MaxLifetimeSeconds = 60;
    public const int MaxPayloadBytes = 1024 * 1024;

    public static SessionRuntimeGrant Issue(NoiseKeyPair key, SessionRuntimeScope scope, SessionRuntimeOperation operation,
        string requestId, DateTimeOffset expiresAt, byte[] payload)
    {
        var grant = new SessionRuntimeGrant(scope, operation, requestId, expiresAt.ToUnixTimeSeconds(), payload, []);
        if (!grant.HasValidFields()) throw new ArgumentException("Invalid session runtime grant fields.");
        return grant with { Signature = key.SignSessionGrant(grant.SignatureInput(key.PublicKey)) };
    }

    /// <summary>
    /// Checks the authenticated link's pin and the operation's routing. The runtime checks the stored owner, access
    /// scope, generation and request receipt before acting.
    /// </summary>
    public bool Verify(ReadOnlySpan<byte> pinnedControllerKey, string nodeId, string runtimeId,
        SessionRuntimeOperation operation, DateTimeOffset now)
    {
        if (!HasValidFields() || Scope.NodeId != nodeId || Scope.RuntimeId != runtimeId || Operation != operation
            || Signature is not { Length: NoiseBinding.SignatureLength }
            || !NoiseKeyPair.IsValidPublicKey(pinnedControllerKey))
            return false;

        var nowSeconds = now.ToUnixTimeSeconds();
        if (ExpiresAtUnixSeconds <= nowSeconds || ExpiresAtUnixSeconds > nowSeconds + MaxLifetimeSeconds)
            return false;

        using var verifier = ECDsa.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = pinnedControllerKey[1..33].ToArray(), Y = pinnedControllerKey[33..65].ToArray() },
        });
        return verifier.VerifyData(SignatureInput(pinnedControllerKey), Signature, HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
    }

    private bool HasValidFields() =>
        Scope is { Generation: >= 0 } && IsIdentifier(Scope.NodeId) && Forward.ForwardOpen.IsRuntimeId(Scope.RuntimeId)
        && IsIdentifier(Scope.LogicalSessionId) && IsIdentifier(Scope.OwnerId) && IsIdentifier(Scope.AccessScope)
        && Enum.IsDefined(Operation) && IsIdentifier(RequestId) && Payload is { Length: > 0 and <= MaxPayloadBytes };

    private byte[] SignatureInput(ReadOnlySpan<byte> pairedKey) =>
        JsonSerializer.SerializeToUtf8Bytes(new SignedFields(
            "leaf.node-link.ai-session.command.v1",
            NodeLinkKeyInfo.For(pairedKey).KeyId,
            Scope, Operation, RequestId, ExpiresAtUnixSeconds, Convert.ToHexString(SHA256.HashData(Payload))));

    private static bool IsIdentifier(string? value) =>
        value is { Length: > 0 and <= 200 } && !value.Any(char.IsControl) && !string.IsNullOrWhiteSpace(value);

    private sealed record SignedFields(string Domain, string PairingKeyId, SessionRuntimeScope Scope,
        SessionRuntimeOperation Operation, string RequestId, long ExpiresAtUnixSeconds, string PayloadDigest);
}
