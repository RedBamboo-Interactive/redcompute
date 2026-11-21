using System.Security.Cryptography;
using System.Text;

namespace RedCompute.NodeLink.Noise;

/// <summary>
/// End-to-end encryption of a node link (Phase 4 design, section 1b): a <c>Noise_IK_P256_AESGCM_SHA256</c>
/// handshake as the connection's first two messages, then one AEAD record per message. The node is the
/// initiator and knows the server's static key (pinned at pairing); RedCompute's NodeHub is the responder.
/// Everything is sealed, the frame header included, so whatever relays the messages (the kernel's bridge,
/// Cloudflare) sees their lengths only.
/// <para>
/// Messages, each one WebSocket message:
/// <list type="bullet">
/// <item><c>0x10 ‖ e (65) ‖ enc(s) (81) ‖ enc(payload)</c>: the node's <c>-> e, es, s, ss</c>; the payload is the
/// identity binding (opaque here: the caller signs it, the responder verifies it).</item>
/// <item><c>0x11 ‖ e (65) ‖ enc(payload)</c>: the server's <c>&lt;- e, ee, se</c>; the payload is empty.</item>
/// <item><c>0x12 ‖ AEAD(frame)</c>: one link frame, header included.</item>
/// </list>
/// The nonce of a record is the per-direction Noise counter, never the channel seq: the link retransmits
/// a (channel, seq) with a changed ack, which would reuse a nonce. A record that does not open (tampered,
/// replayed, reordered, dropped before it) closes the connection with 4400 <c>e2e_frame_rejected</c>;
/// the link reconnects and resumes as for any cut.
/// </para>
/// </summary>
public static class NoiseLink
{
    public const byte InitiatorHandshake = 0x10;
    public const byte ResponderHandshake = 0x11;
    public const byte Record = 0x12;

    /// <summary>What sealing adds to a frame: the record type and the AEAD tag.</summary>
    public const int RecordOverhead = 1 + NoiseProtocol.TagLength;

    /// <summary>The largest handshake message accepted (the binding payload is a signature, far below this).</summary>
    public const int MaxHandshakeMessage = 4096;

    public const string FrameRejected = "e2e_frame_rejected";
    public const string KeyUnknown = "link_key_unknown";
    public const string KeyMismatch = "link_key_mismatch";
    public const string E2eRequired = "e2e_required";
    public const string IdentityInvalid = "link_identity_invalid";
    public const string ServerUnsupported = "server_e2e_unsupported";
    public const string ServerNewer = "server_e2e_newer";
    public const string IdentityUnverifiable = "link_identity_unverifiable";

    /// <summary>
    /// The handshake message types a later version of this protocol may use (0x13-0x1F). A node never answers one by
    /// falling back: it fails closed with 4426 (update leaf-node), and the encrypted link is never replaced by plaintext.
    /// </summary>
    public static bool IsFutureHandshake(byte type) => type is >= 0x13 and <= 0x1F;

    /// <summary>The only valid answer: the type, the server's ephemeral key and the tag over an empty payload.</summary>
    public const int ResponderHandshakeLength = 1 + NoiseProtocol.PublicKeyLength + NoiseProtocol.TagLength;

    /// <summary>The prologue both sides mix in: the protocol label and the node id the link is for.</summary>
    public static byte[] Prologue(string nodeId) => Encoding.UTF8.GetBytes("leaf-node-link.v1\n" + nodeId);

    /// <summary>
    /// The bytes the node's identity key signs to bind its link key to this handshake:
    /// <c>"leaf-node-link-key.v1" 0 nodeId 0 ‖ node link key ‖ server static key ‖ h</c>, with <c>h</c> the
    /// handshake hash after <c>ss</c>. Bound to <c>h</c>, a signature cannot be replayed into another handshake.
    /// </summary>
    public static byte[] BindingMessage(string nodeId, ReadOnlySpan<byte> nodeLinkKey, ReadOnlySpan<byte> serverStaticKey, ReadOnlySpan<byte> handshakeHash)
        => [.. Encoding.ASCII.GetBytes("leaf-node-link-key.v1"), 0, .. Encoding.UTF8.GetBytes(nodeId), 0,
            .. nodeLinkKey, .. serverStaticKey, .. handshakeHash];

    /// <summary>
    /// The node's side: sends the first handshake message to the pinned <paramref name="serverStatic"/>,
    /// reads the answer and returns the encrypted transport. A server that cannot be the pinned key's holder
    /// fails with <see cref="LinkCloseCodes.LinkKeyUnknown"/>; a server that does not know the handshake
    /// (it closes 4400 at once) fails with <see cref="LinkCloseCodes.UpgradeRequired"/>
    /// (<see cref="ServerUnsupported"/>). The node never falls back to plaintext.
    /// </summary>
    public static async Task<NoiseLinkTransport> InitiateAsync(ILinkTransport inner, NoiseInitiatorOptions options, CancellationToken ct)
    {
        if (!NoiseKeyPair.IsValidPublicKey(options.ServerStatic))
            throw new ArgumentException("The pinned server key is not a P-256 point.", nameof(options));
        var state = new NoiseSymmetricState(NoiseProtocol.Name);
        using var e = options.Ephemeral?.Invoke() ?? NoiseKeyPair.Generate();
        byte[] message;
        try
        {
            state.MixHash(Prologue(options.NodeId));
            state.MixHash(options.ServerStatic);

            state.MixHash(e.PublicKey);
            state.MixDh(e, options.ServerStatic);
            var encryptedStatic = state.EncryptAndHash(options.LocalStatic.PublicKey);
            state.MixDh(options.LocalStatic, options.ServerStatic);
            var payload = options.Binding(new NoiseBindingContext(options.NodeId, options.LocalStatic.PublicKey, options.ServerStatic, state.H.ToArray()));
            var encryptedPayload = state.EncryptAndHash(payload);
            message = [InitiatorHandshake, .. e.PublicKey, .. encryptedStatic, .. encryptedPayload];
        }
        catch
        {
            state.Dispose();
            throw;
        }

        byte[] answer;
        try
        {
            await inner.SendAsync(message, ct);
            answer = await inner.ReceiveAsync(ct);
        }
        catch (LinkClosedException closed) when (closed.Code == LinkCloseCodes.ProtocolError)
        {
            state.Dispose();
            // A server from before E2E reads 0x10 as an unknown frame format.
            throw new LinkProtocolException(LinkCloseCodes.UpgradeRequired,
                $"{ServerUnsupported}: the server does not support encrypted links; update Leaf.");
        }
        catch
        {
            state.Dispose();
            throw;
        }

        if (answer.Length > 0 && IsFutureHandshake(answer[0]))
        {
            state.Dispose();
            throw new LinkProtocolException(LinkCloseCodes.UpgradeRequired,
                $"{ServerNewer}: the server answered with a newer encrypted handshake (0x{answer[0]:X2}); update leaf-node.");
        }
        try
        {
            // Exactly e and the tag over an empty payload: anything longer or shorter is not this protocol's answer.
            if (answer.Length != ResponderHandshakeLength || answer[0] != ResponderHandshake)
                throw new CryptographicException("Not a handshake answer.");
            var re = answer.AsSpan(1, NoiseProtocol.PublicKeyLength);
            state.MixHash(re);
            state.MixDh(e, re);
            state.MixDh(options.LocalStatic, re);
            state.DecryptAndHash(answer.AsSpan(1 + NoiseProtocol.PublicKeyLength));
        }
        catch (CryptographicException)
        {
            state.Dispose();
            throw new LinkProtocolException(LinkCloseCodes.LinkKeyUnknown,
                $"{KeyMismatch}: the server did not prove the pinned link key {NoiseKeyPair.Fingerprint(options.ServerStatic)}.");
        }

        var (send, receive) = state.Split();
        var hash = state.H.ToArray();
        state.Dispose();
        try
        {
            return new NoiseLinkTransport(inner, send, receive, hash, options.ServerStatic, options.RekeyEvery);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(send);
            CryptographicOperations.ZeroMemory(receive);
        }
    }

    /// <summary>
    /// The server's side: reads the connection's first message. A handshake is answered and the encrypted
    /// transport returned. A plaintext frame is passed through (re-delivered first) when
    /// <see cref="NoiseResponderOptions.AllowPlaintext"/> says so, and refused with
    /// <see cref="LinkCloseCodes.E2eRequired"/> otherwise. A handshake to a key the server does not hold is
    /// <see cref="LinkCloseCodes.LinkKeyUnknown"/>; a binding the caller refuses is
    /// <see cref="LinkCloseCodes.LinkIdentityInvalid"/>. Errors are thrown as <see cref="LinkProtocolException"/>;
    /// the caller closes the connection with their code.
    /// </summary>
    public static async Task<NoiseAcceptResult> AcceptAsync(ILinkTransport inner, NoiseResponderOptions options, CancellationToken ct)
    {
        var first = await inner.ReceiveAsync(ct);
        if (first.Length > 0 && first[0] == FrameCodec.Format)
        {
            if (!await options.AllowPlaintext(ct))
                throw new LinkProtocolException(LinkCloseCodes.E2eRequired,
                    $"{E2eRequired}: this node's link is end-to-end encrypted; plaintext is refused.");
            return new NoiseAcceptResult(new PrefixedTransport(inner, first), null);
        }
        if (first.Length == 0 || first[0] != InitiatorHandshake)
            throw new LinkProtocolException(LinkCloseCodes.ProtocolError, "The first message is neither a frame nor a handshake.");
        if (first.Length > MaxHandshakeMessage)
            throw new LinkProtocolException(LinkCloseCodes.ProtocolError, "The handshake message is too large.");

        const int staticLength = NoiseProtocol.PublicKeyLength + NoiseProtocol.TagLength;
        if (first.Length < 1 + NoiseProtocol.PublicKeyLength + staticLength + NoiseProtocol.TagLength)
            throw new LinkProtocolException(LinkCloseCodes.LinkKeyUnknown, $"{KeyUnknown}: the handshake does not open.");
        var re = first.AsSpan(1, NoiseProtocol.PublicKeyLength).ToArray();
        var encryptedStatic = first.AsSpan(1 + NoiseProtocol.PublicKeyLength, staticLength).ToArray();
        var encryptedPayload = first.AsSpan(1 + NoiseProtocol.PublicKeyLength + staticLength).ToArray();

        // During a rotation's grace a node may still use the previous key: try each key the server holds.
        for (var index = 0; index < options.LocalStatics.Count; index++)
        {
            var local = options.LocalStatics[index];
            var state = new NoiseSymmetricState(NoiseProtocol.Name);
            byte[] rs, payload, h;
            try
            {
                state.MixHash(Prologue(options.NodeId));
                state.MixHash(local.PublicKey);
                state.MixHash(re);
                state.MixDh(local, re);
                rs = state.DecryptAndHash(encryptedStatic);
                state.MixDh(local, rs);
                h = state.H.ToArray();
                payload = state.DecryptAndHash(encryptedPayload);
            }
            catch (CryptographicException)
            {
                state.Dispose();
                continue;
            }

            try
            {
                var binding = new NoiseBindingContext(options.NodeId, rs, local.PublicKey, h);
                bool verified;
                try
                {
                    verified = await options.Verify(binding, payload, ct);
                }
                catch (Exception error) when (error is not (LinkProtocolException or OperationCanceledException))
                {
                    // A binding that does not parse is a bad binding (4497); anything else is the server's own fault
                    // (a lookup that failed), never a verdict on the node: 1011, and the node retries.
                    if (error is not CryptographicException)
                        throw new LinkProtocolException(LinkCloseCodes.InternalError,
                            $"{IdentityUnverifiable}: the server could not check the node's identity binding ({error.GetType().Name}).");
                    verified = false;
                }
                if (!verified)
                    throw new LinkProtocolException(LinkCloseCodes.LinkIdentityInvalid,
                        $"{IdentityInvalid}: the node's link key is not bound to its paired identity.");

                using var e = options.Ephemeral?.Invoke() ?? NoiseKeyPair.Generate();
                state.MixHash(e.PublicKey);
                state.MixDh(e, re);
                state.MixDh(e, rs);
                var encrypted = state.EncryptAndHash([]);
                await inner.SendAsync((byte[])[ResponderHandshake, .. e.PublicKey, .. encrypted], ct);
                var (initiatorToResponder, responderToInitiator) = state.Split();
                try
                {
                    var transport = new NoiseLinkTransport(inner, responderToInitiator, initiatorToResponder, state.H.ToArray(), rs,
                        options.RekeyEvery);
                    return new NoiseAcceptResult(transport, new NoiseAccepted(rs, index, payload));
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(initiatorToResponder);
                    CryptographicOperations.ZeroMemory(responderToInitiator);
                }
            }
            finally
            {
                state.Dispose();
            }
        }
        throw new LinkProtocolException(LinkCloseCodes.LinkKeyUnknown,
            $"{KeyUnknown}: the handshake is not for a link key this server holds.");
    }
}

/// <summary>What the identity binding covers (<see cref="NoiseLink.BindingMessage"/>).</summary>
public sealed record NoiseBindingContext(string NodeId, byte[] NodeLinkKey, byte[] ServerStaticKey, byte[] HandshakeHash)
{
    public byte[] Message() => NoiseLink.BindingMessage(NodeId, NodeLinkKey, ServerStaticKey, HandshakeHash);
}

public sealed class NoiseInitiatorOptions
{
    public required string NodeId { get; init; }

    /// <summary>The node's static link key.</summary>
    public required NoiseKeyPair LocalStatic { get; init; }

    /// <summary>The server static key pinned at pairing.</summary>
    public required byte[] ServerStatic { get; init; }

    /// <summary>The first message's payload: the identity binding, signed by the node's paired identity key.</summary>
    public required Func<NoiseBindingContext, byte[]> Binding { get; init; }

    public long RekeyEvery { get; init; } = NoiseLinkTransport.DefaultRekeyEvery;

    /// <summary>Tests only: fixed ephemeral keys for deterministic vectors.</summary>
    internal Func<NoiseKeyPair>? Ephemeral { get; init; }
}

public sealed class NoiseResponderOptions
{
    /// <summary>The node id the ingress authenticated (it is in the prologue).</summary>
    public required string NodeId { get; init; }

    /// <summary>The static keys the server holds, the current one first.</summary>
    public required IReadOnlyList<NoiseKeyPair> LocalStatics { get; init; }

    /// <summary>Checks the binding payload against the node's paired identity. False closes 4497.</summary>
    public required Func<NoiseBindingContext, byte[], CancellationToken, ValueTask<bool>> Verify { get; init; }

    /// <summary>Whether this node may still connect in plaintext (it has never been pinned). False closes 4496.</summary>
    public required Func<CancellationToken, ValueTask<bool>> AllowPlaintext { get; init; }

    public long RekeyEvery { get; init; } = NoiseLinkTransport.DefaultRekeyEvery;

    /// <summary>Tests only: fixed ephemeral keys for deterministic vectors.</summary>
    internal Func<NoiseKeyPair>? Ephemeral { get; init; }
}

/// <summary>The handshake the server accepted: the node's link key, which of the server's keys it used, its binding.</summary>
public sealed record NoiseAccepted(byte[] NodeLinkKey, int ServerKeyIndex, byte[] Binding);

/// <summary>The connection to run the link over; <see cref="Accepted"/> is null for a plaintext connection.</summary>
public sealed record NoiseAcceptResult(ILinkTransport Transport, NoiseAccepted? Accepted)
{
    public bool Encrypted => Accepted is not null;
}

/// <summary>
/// A link transport over an established Noise session: seals each message sent, opens each one received.
/// Rekeys each direction every <c>RekeyEvery</c> records (the counter tells both sides when).
/// </summary>
public sealed class NoiseLinkTransport : ILinkTransport
{
    /// <summary>Each direction rekeys after this many records (about 64 GiB of full frames).</summary>
    public const long DefaultRekeyEvery = 1L << 20;

    private readonly ILinkTransport _inner;
    private readonly NoiseCipherState _send = new();
    private readonly NoiseCipherState _receive = new();
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly ulong _rekeyEvery;
    private int _failed;

    internal NoiseLinkTransport(ILinkTransport inner, byte[] sendKey, byte[] receiveKey, byte[] handshakeHash, byte[] remoteStatic, long rekeyEvery)
    {
        if (rekeyEvery < 1) throw new ArgumentOutOfRangeException(nameof(rekeyEvery));
        _inner = inner;
        _send.InitializeKey(sendKey);
        _receive.InitializeKey(receiveKey);
        HandshakeHash = handshakeHash;
        RemoteStatic = remoteStatic;
        _rekeyEvery = (ulong)rekeyEvery;
    }

    /// <summary>The final handshake hash: a channel binding both sides share.</summary>
    public byte[] HandshakeHash { get; }

    /// <summary>The peer's static key (the server's on the node, the node's link key on the server).</summary>
    public byte[] RemoteStatic { get; }

    public ILinkTransport Inner => _inner;

    public async ValueTask SendAsync(ReadOnlyMemory<byte> message, CancellationToken ct)
    {
        if (message.Length > FrameCodec.MaxFrame - NoiseLink.RecordOverhead)
            throw new LinkProtocolException(LinkCloseCodes.MessageTooBig,
                $"A sealed message is at most {FrameCodec.MaxFrame - NoiseLink.RecordOverhead} bytes; got {message.Length}.");
        await _sendGate.WaitAsync(ct);
        try
        {
            // Sealed and sent under one gate, so the wire order is the nonce order.
            var sealedBytes = _send.EncryptWithAd([NoiseLink.Record], message.Span);
            if (_send.Nonce % _rekeyEvery == 0) _send.Rekey();
            await _inner.SendAsync((byte[])[NoiseLink.Record, .. sealedBytes], ct);
        }
        finally
        {
            _sendGate.Release();
        }
    }

    public async ValueTask<byte[]> ReceiveAsync(CancellationToken ct)
    {
        if (Volatile.Read(ref _failed) != 0)
            throw new LinkProtocolException(LinkCloseCodes.ProtocolError, $"{NoiseLink.FrameRejected}: the connection already refused a record.");
        var record = await _inner.ReceiveAsync(ct);
        try
        {
            if (record.Length < NoiseLink.RecordOverhead || record[0] != NoiseLink.Record)
                throw new CryptographicException("Not a record.");
            var frame = _receive.DecryptWithAd([NoiseLink.Record], record.AsSpan(1));
            if (_receive.Nonce % _rekeyEvery == 0) _receive.Rekey();
            return frame;
        }
        catch (CryptographicException)
        {
            // A record that does not open is never delivered, and nothing after it is either.
            Volatile.Write(ref _failed, 1);
            throw new LinkProtocolException(LinkCloseCodes.ProtocolError, $"{NoiseLink.FrameRejected}: a record did not authenticate.");
        }
    }

    public ValueTask CloseAsync(int code, string? reason, CancellationToken ct) => _inner.CloseAsync(code, reason, ct);

    public async ValueTask DisposeAsync()
    {
        _send.Dispose();
        _receive.Dispose();
        await _inner.DisposeAsync();
    }
}

/// <summary>A transport whose first received message was already read (the server's plaintext sniff).</summary>
public sealed class PrefixedTransport(ILinkTransport inner, byte[] first) : ILinkTransport
{
    private byte[]? _first = first;

    public ILinkTransport Inner => inner;

    public ValueTask SendAsync(ReadOnlyMemory<byte> message, CancellationToken ct) => inner.SendAsync(message, ct);

    public ValueTask<byte[]> ReceiveAsync(CancellationToken ct)
        => Interlocked.Exchange(ref _first, null) is { } first ? ValueTask.FromResult(first) : inner.ReceiveAsync(ct);

    public ValueTask CloseAsync(int code, string? reason, CancellationToken ct) => inner.CloseAsync(code, reason, ct);

    public ValueTask DisposeAsync() => inner.DisposeAsync();
}
