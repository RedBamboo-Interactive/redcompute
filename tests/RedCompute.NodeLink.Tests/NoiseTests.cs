using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using RedCompute.NodeLink.Noise;
using Xunit;

namespace RedCompute.NodeLink.Tests;

/// <summary>
/// End-to-end encryption of the node link (Phase 4 design, section 1b): the Noise IK handshake checked
/// against an independent implementation and a pinned vector, every bit flip refused, the identity
/// binding, records refused when tampered, replayed, reordered or dropped, rekeying, and the skew and
/// downgrade rules.
/// </summary>
public sealed class NoiseTests
{
    private const string NodeId = LinkPair.NodeId;

    // ---- primitives -------------------------------------------------------------------------------

    [Fact]
    public void The_noise_hkdf_is_rfc5869_hkdf_with_the_chaining_key_as_salt()
    {
        var ck = RandomNumberGenerator.GetBytes(32);
        var ikm = RandomNumberGenerator.GetBytes(32);
        var (first, second) = NoiseSymmetricState.Hkdf2(ck, ikm);
        var expected = HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, 64, ck, []);
        Assert.Equal(expected[..32], first);
        Assert.Equal(expected[32..], second);
    }

    [Fact]
    public void The_protocol_name_fits_the_hash_and_is_zero_padded()
    {
        using var state = new NoiseSymmetricState(NoiseProtocol.Name);
        Assert.Equal([.. Encoding.ASCII.GetBytes(NoiseProtocol.Name), .. new byte[32 - NoiseProtocol.Name.Length]], state.H);
    }

    [Fact]
    public void A_cipher_state_counts_nonces_and_never_advances_on_a_failed_open()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        using var send = new NoiseCipherState();
        using var receive = new NoiseCipherState();
        send.InitializeKey(key);
        receive.InitializeKey(key);
        var first = send.EncryptWithAd([], [1, 2, 3]);
        var second = send.EncryptWithAd([], [4, 5, 6]);
        Assert.Throws<AuthenticationTagMismatchException>(() => receive.DecryptWithAd([], second));
        Assert.Equal(0UL, receive.Nonce);
        Assert.Equal([1, 2, 3], receive.DecryptWithAd([], first));
        Assert.Equal([4, 5, 6], receive.DecryptWithAd([], second));
        Assert.Equal(2UL, receive.Nonce);
    }

    [Fact]
    public void Public_keys_are_65_byte_points_on_the_curve()
    {
        using var key = NoiseKeyPair.Generate();
        Assert.Equal(65, key.PublicKey.Length);
        Assert.True(NoiseKeyPair.IsValidPublicKey(key.PublicKey));
        var offCurve = key.PublicKey.ToArray();
        offCurve[64] ^= 1;
        Assert.False(NoiseKeyPair.IsValidPublicKey(offCurve));
        Assert.False(NoiseKeyPair.IsValidPublicKey(key.PublicKey.AsSpan(1)));
        Assert.Matches("^[0-9A-F]{4}-[0-9A-F]{4}-[0-9A-F]{4}-[0-9A-F]{4}$", NoiseKeyPair.Fingerprint(key.PublicKey));
        using var again = NoiseKeyPair.FromPkcs8(key.ExportPkcs8());
        Assert.Equal(key.PublicKey, again.PublicKey);
    }

    // ---- the handshake ----------------------------------------------------------------------------

    /// <summary>Fixed P-256 keys from fixed scalars, so the whole handshake is deterministic.</summary>
    private static NoiseKeyPair Fixed(byte seed)
    {
        var d = SHA256.HashData([seed]); // below the group order with overwhelming probability, fixed forever
        using var key = ECDiffieHellman.Create(new ECParameters { Curve = ECCurve.NamedCurves.nistP256, D = d });
        return NoiseKeyPair.FromPkcs8(key.ExportPkcs8PrivateKey());
    }

    private sealed record Transcript(byte[] Message1, byte[] Message2, byte[] Record, byte[] Hash);

    private static async Task<Transcript> FixedHandshakeAsync()
    {
        using var serverStatic = Fixed(1);
        using var nodeStatic = Fixed(2);
        var payload = Encoding.ASCII.GetBytes("binding");
        var (node, mid1) = LinkPipe.Create();
        var (mid2, server) = LinkPipe.Create();
        var initiate = NoiseLink.InitiateAsync(node, new NoiseInitiatorOptions
        {
            NodeId = NodeId, LocalStatic = nodeStatic, ServerStatic = serverStatic.PublicKey, Binding = _ => payload,
            Ephemeral = () => Fixed(3),
        }, CancellationToken.None);
        var accept = NoiseLink.AcceptAsync(server, new NoiseResponderOptions
        {
            NodeId = NodeId, LocalStatics = [serverStatic], AllowPlaintext = _ => ValueTask.FromResult(false),
            Verify = (_, p, _) => ValueTask.FromResult(p.AsSpan().SequenceEqual(payload)), Ephemeral = () => Fixed(4),
        }, CancellationToken.None);
        var message1 = await mid1.ReceiveAsync(CancellationToken.None);
        await mid2.SendAsync(message1, CancellationToken.None);
        var message2 = await mid2.ReceiveAsync(CancellationToken.None);
        await mid1.SendAsync(message2, CancellationToken.None);
        var nodeTransport = await initiate;
        var serverTransport = (await accept).Transport;
        await nodeTransport.SendAsync(Encoding.ASCII.GetBytes("frame"), CancellationToken.None);
        var record = await mid1.ReceiveAsync(CancellationToken.None);
        await mid2.SendAsync(record, CancellationToken.None);
        Assert.Equal("frame", Encoding.ASCII.GetString(await serverTransport.ReceiveAsync(CancellationToken.None)));
        Assert.Equal(nodeTransport.HandshakeHash, ((NoiseLinkTransport)serverTransport).HandshakeHash);
        return new Transcript(message1, message2, record, nodeTransport.HandshakeHash);
    }

    [Fact]
    public async Task The_handshake_matches_an_independent_noise_ik_implementation()
    {
        var transcript = await FixedHandshakeAsync();
        using var rs = Fixed(1);
        using var s = Fixed(2);
        using var e = Fixed(3);
        using var re = Fixed(4);

        // Noise rev. 34 by hand: BCL SHA-256, RFC 5869 HKDF, AES-GCM; only the DH is NoiseKeyPair's.
        byte[] h = [.. Encoding.ASCII.GetBytes(NoiseProtocol.Name), .. new byte[32 - NoiseProtocol.Name.Length]];
        var ck = h.ToArray();
        byte[] k = [];
        ulong n = 0;
        void MixHash(byte[] data) => h = SHA256.HashData([.. h, .. data]);
        void MixKey(byte[] ikm)
        {
            var okm = HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, 64, ck, []);
            (ck, k, n) = (okm[..32], okm[32..], 0);
        }
        byte[] Seal(byte[] key, ulong nonce, byte[] ad, byte[] plaintext)
        {
            var iv = new byte[12];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(iv.AsSpan(4), nonce);
            var output = new byte[plaintext.Length + 16];
            using var aes = new AesGcm(key, 16);
            aes.Encrypt(iv, plaintext, output.AsSpan(0, plaintext.Length), output.AsSpan(plaintext.Length), ad);
            return output;
        }
        byte[] EncryptAndHash(byte[] plaintext)
        {
            var c = Seal(k, n++, h, plaintext);
            MixHash(c);
            return c;
        }

        MixHash(Encoding.UTF8.GetBytes("leaf-node-link.v1\n" + NodeId));
        MixHash(rs.PublicKey);
        MixHash(e.PublicKey);
        MixKey(e.Dh(rs.PublicKey));
        var encryptedStatic = EncryptAndHash(s.PublicKey);
        MixKey(s.Dh(rs.PublicKey));
        var encryptedPayload = EncryptAndHash(Encoding.ASCII.GetBytes("binding"));
        Assert.Equal([0x10, .. e.PublicKey, .. encryptedStatic, .. encryptedPayload], transcript.Message1);

        MixHash(re.PublicKey);
        MixKey(re.Dh(e.PublicKey));
        MixKey(re.Dh(s.PublicKey));
        var encryptedEmpty = EncryptAndHash([]);
        Assert.Equal([0x11, .. re.PublicKey, .. encryptedEmpty], transcript.Message2);
        Assert.Equal(h, transcript.Hash);

        var split = HKDF.DeriveKey(HashAlgorithmName.SHA256, [], 64, ck, []);
        Assert.Equal([0x12, .. Seal(split[..32], 0, [0x12], Encoding.ASCII.GetBytes("frame"))], transcript.Record);
    }

    [Fact]
    public async Task The_handshake_is_pinned_by_a_fixed_vector()
    {
        // Regression pin: any change to the wire format, the prologue, the labels or the key schedule moves it.
        var transcript = await FixedHandshakeAsync();
        var digest = Convert.ToHexString(SHA256.HashData([.. transcript.Message1, .. transcript.Message2, .. transcript.Record]));
        Assert.True(digest == PinnedVector, $"the handshake transcript digest is {digest}");
    }

    private const string PinnedVector = "F35F72AF275C8D57003AB462F084E83A28A3D70D9D8A4A7D0EF48F4DF42F2CE8";

    // The three messages as the P0.5a crypto review's independent implementation (Python, pyca/cryptography on
    // OpenSSL) produced them, byte for byte: Vectors/noise_ik_p256.py prints them (python -I noise_ik_p256.py).
    private const string ReviewMessage1 =
        "10047D7B6BF7668908BB617046AD0A2C8FC604BD97FB684913F19B423D4C279CBA4B076C97DB8278A0EC0B05F0A56617E840D604B95A6FB27060F37C46"
        + "3287098979129A6D0ABA8F7C44CC38E0F409CC43B1058DE927AFC5E31C4AAAABE11F3487A50CBD8B02F52710D23D3547BCD9039E16D8E7983DEF885982"
        + "6875DE117655E225DD3193F38C683E1C4A6B2E3FDA6618D8E46F7886D10D2C476006C0C75F0A6DAF263D445814819A4D";
    private const string ReviewMessage2 =
        "110433EB981AC8539C1AD426DFB119B0F790B725BA068908E94DA1422B3D90FBDBF983B8F6CE156D2561FDDA704088EA0DBC78EDCBB5F995A1D9D15C3707"
        + "10439A3C243A476072AEB07226C0ABCEEF4EA28B";
    private const string ReviewRecord = "12AEB85EE4583E9C67C65FA72B95D848AB393460B8F8";

    [Fact]
    public async Task The_handshake_bytes_are_the_independent_implementation_s_byte_for_byte()
    {
        var transcript = await FixedHandshakeAsync();
        Assert.Equal(ReviewMessage1, Convert.ToHexString(transcript.Message1));
        Assert.Equal(ReviewMessage2, Convert.ToHexString(transcript.Message2));
        Assert.Equal(ReviewRecord, Convert.ToHexString(transcript.Record));
        Assert.Equal(PinnedVector, Convert.ToHexString(SHA256.HashData(Convert.FromHexString(ReviewMessage1 + ReviewMessage2 + ReviewRecord))));
    }

    /// <summary>RFC 5903 section 8.1 (ECDH on P-256): the DH output is the shared point's x-coordinate, both ways.</summary>
    [Fact]
    public void The_dh_is_rfc5903_section_8_1()
    {
        static NoiseKeyPair FromScalar(string hex)
        {
            using var key = ECDiffieHellman.Create(new ECParameters { Curve = ECCurve.NamedCurves.nistP256, D = Convert.FromHexString(hex) });
            return NoiseKeyPair.FromPkcs8(key.ExportPkcs8PrivateKey());
        }
        using var i = FromScalar("C88F01F510D9AC3F70A292DAA2316DE544E9AAB8AFE84049C62A9C57862D1433");
        using var r = FromScalar("C6EF9C5D78AE012A011164ACB397CE2088685D8F06BF9BE0B283AB46476BEE53");
        Assert.Equal("04DAD0B65394221CF9B051E1FECA5787D098DFE637FC90B9EF945D0C37725811805271A0461CDB8252D61F1C456FA3E59AB1F45B33ACCF5F58389E0577B8990BB3",
            Convert.ToHexString(i.PublicKey));
        Assert.Equal("04D12DFB5289C8D4F81208B70270398C342296970A0BCCB74C736FC7554494BF6356FBF3CA366CC23E8157854C13C58D6AAC23F046ADA30F8353E74F33039872AB",
            Convert.ToHexString(r.PublicKey));
        const string girx = "D6840F6B42F6EDAFD13116E0E12565202FEF8E9ECE7DCE03812464D04B9442DE";
        Assert.Equal(girx, Convert.ToHexString(i.Dh(r.PublicKey)));
        Assert.Equal(girx, Convert.ToHexString(r.Dh(i.PublicKey)));
    }

    /// <summary>
    /// A first message whose encrypted static key opens (the AEAD is right) but is not a point on P-256: refused at the
    /// curve check, before the <c>ss</c> DH and before the binding, as an unknown key, never as a crash.
    /// </summary>
    [Fact]
    public async Task A_well_encrypted_static_key_that_is_off_the_curve_is_refused_before_any_binding_check()
    {
        using var keys = new E2eKeys();
        var offCurve = keys.NodeLink.PublicKey.ToArray();
        offCurve[64] ^= 1;
        Assert.False(NoiseKeyPair.IsValidPublicKey(offCurve));

        using var e = NoiseKeyPair.Generate();
        using var state = new NoiseSymmetricState(NoiseProtocol.Name);
        state.MixHash(NoiseLink.Prologue(NodeId));
        state.MixHash(keys.ServerStatic.PublicKey);
        state.MixHash(e.PublicKey);
        state.MixDh(e, keys.ServerStatic.PublicKey);
        var encryptedStatic = state.EncryptAndHash(offCurve);
        var encryptedPayload = state.EncryptAndHash(new byte[64]); // ss cannot be computed with an off-curve s
        byte[] message = [NoiseLink.InitiatorHandshake, .. e.PublicKey, .. encryptedStatic, .. encryptedPayload];

        var (a, b) = LinkPipe.Create();
        await a.SendAsync(message, CancellationToken.None);
        var verified = 0;
        var options = keys.ResponderOptions(NodeId);
        var error = await Assert.ThrowsAsync<LinkProtocolException>(() => NoiseLink.AcceptAsync(b, new NoiseResponderOptions
        {
            NodeId = NodeId, LocalStatics = options.LocalStatics, AllowPlaintext = options.AllowPlaintext,
            Verify = (x, y, ct) => { Interlocked.Increment(ref verified); return options.Verify(x, y, ct); },
        }, CancellationToken.None));
        Assert.Equal(LinkCloseCodes.LinkKeyUnknown, error.CloseCode);
        Assert.Equal(0, verified);

        // The same message with the node's real (on-curve) key is the control: it gets as far as the payload.
        using var control = new NoiseSymmetricState(NoiseProtocol.Name);
        control.MixHash(NoiseLink.Prologue(NodeId));
        control.MixHash(keys.ServerStatic.PublicKey);
        control.MixHash(e.PublicKey);
        control.MixDh(e, keys.ServerStatic.PublicKey);
        var realStatic = control.EncryptAndHash(keys.NodeLink.PublicKey);
        control.MixDh(keys.NodeLink, keys.ServerStatic.PublicKey);
        var payload = control.EncryptAndHash(new byte[64]);
        (a, b) = LinkPipe.Create();
        await a.SendAsync((byte[])[NoiseLink.InitiatorHandshake, .. e.PublicKey, .. realStatic, .. payload], CancellationToken.None);
        Assert.Equal(LinkCloseCodes.LinkIdentityInvalid, (await Assert.ThrowsAsync<LinkProtocolException>(() =>
            NoiseLink.AcceptAsync(b, keys.ResponderOptions(NodeId), CancellationToken.None))).CloseCode);
    }

    private static async Task<LinkProtocolException> AnswerAsync(E2eKeys keys, Func<byte[], byte[]> answer)
    {
        var (initiate, accept, mid1, mid2) = Start(keys);
        await mid2.SendAsync(await mid1.ReceiveAsync(CancellationToken.None), CancellationToken.None);
        var genuine = await mid2.ReceiveAsync(CancellationToken.None);
        _ = accept.ContinueWith(_ => { });
        await mid1.SendAsync(answer(genuine), CancellationToken.None);
        return await Assert.ThrowsAsync<LinkProtocolException>(() => initiate);
    }

    /// <summary>
    /// A server that holds the key and answers correctly, but with a payload in message 2: it authenticates, so only
    /// the rule "message 2 is exactly e and the tag" refuses it. The empty payload is the control.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(16)]
    public async Task The_answer_is_exactly_e_and_the_tag_even_when_a_longer_one_would_authenticate(int payloadLength)
    {
        using var keys = new E2eKeys();
        Assert.Equal(82, NoiseLink.ResponderHandshakeLength);
        var (node, peer) = LinkPipe.Create();
        var attempt = NoiseLink.InitiateAsync(node, new NoiseInitiatorOptions
        {
            NodeId = NodeId, LocalStatic = keys.NodeLink, ServerStatic = keys.ServerStatic.PublicKey, Binding = _ => new byte[64],
        }, CancellationToken.None);
        var message1 = await peer.ReceiveAsync(CancellationToken.None);

        using var state = new NoiseSymmetricState(NoiseProtocol.Name);
        state.MixHash(NoiseLink.Prologue(NodeId));
        state.MixHash(keys.ServerStatic.PublicKey);
        var re = message1[1..66];
        state.MixHash(re);
        state.MixDh(keys.ServerStatic, re);
        var rs = state.DecryptAndHash(message1.AsSpan(66, 81));
        state.MixDh(keys.ServerStatic, rs);
        state.DecryptAndHash(message1.AsSpan(147));
        using var e = NoiseKeyPair.Generate();
        state.MixHash(e.PublicKey);
        state.MixDh(e, re);
        state.MixDh(e, rs);
        await peer.SendAsync((byte[])[NoiseLink.ResponderHandshake, .. e.PublicKey, .. state.EncryptAndHash(new byte[payloadLength])], CancellationToken.None);

        if (payloadLength == 0)
        {
            Assert.Equal(state.H, (await attempt).HandshakeHash);
            return;
        }
        Assert.Equal(LinkCloseCodes.LinkKeyUnknown, (await Assert.ThrowsAsync<LinkProtocolException>(() => attempt)).CloseCode);
    }

    [Fact]
    public async Task A_pinned_node_answered_in_plaintext_never_falls_back()
    {
        using var keys = new E2eKeys();
        var welcome = LinkHandshake.WelcomeFrame(new LinkWelcome(1, "s1", false, "epoch", [], 20, new LinkLimits(FrameCodec.MaxPayload, 64), null, null));
        Assert.Equal(FrameCodec.Format, welcome[0]);
        var error = await AnswerAsync(keys, _ => welcome);
        Assert.Equal(LinkCloseCodes.LinkKeyUnknown, error.CloseCode);
        Assert.Contains(NoiseLink.KeyMismatch, error.Message);
    }

    [Theory]
    [InlineData(0x13)]
    [InlineData(0x1F)]
    public async Task A_newer_handshake_answer_means_update_never_plaintext_or_an_older_handshake(byte type)
    {
        using var keys = new E2eKeys();
        var error = await AnswerAsync(keys, genuine => [type, .. genuine[1..]]);
        Assert.Equal(LinkCloseCodes.UpgradeRequired, error.CloseCode);
        Assert.Contains(NoiseLink.ServerNewer, error.Message);
        Assert.Equal(DialAction.NeedsUpdate, DialOutcomes.Classify(null, null, error.CloseCode));
    }

    public static TheoryData<string> VerifyFaults => new() { "crypto", "fault", "protocol" };

    /// <summary>A Verify that throws: a binding that does not parse is 4497; a fault of the server's is 1011 (retry); the caller's own code passes.</summary>
    [Theory]
    [MemberData(nameof(VerifyFaults))]
    public async Task A_verify_that_throws_is_typed_never_untyped(string kind)
    {
        using var keys = new E2eKeys();
        var options = keys.ResponderOptions(NodeId);
        var (initiate, _, mid1, _) = Start(keys);
        var message1 = await mid1.ReceiveAsync(CancellationToken.None);
        _ = initiate.ContinueWith(_ => { });
        var (a, b) = LinkPipe.Create();
        await a.SendAsync(message1, CancellationToken.None);
        var error = await Assert.ThrowsAsync<LinkProtocolException>(() => NoiseLink.AcceptAsync(b, new NoiseResponderOptions
        {
            NodeId = NodeId, LocalStatics = options.LocalStatics, AllowPlaintext = options.AllowPlaintext,
            Verify = (_, _, _) => kind switch
            {
                "crypto" => throw new CryptographicException("ASN1 corrupted data."),
                "fault" => throw new InvalidOperationException("the registry is not ready"),
                _ => throw new LinkProtocolException(LinkCloseCodes.ServiceRestart, "link_identity_unavailable: later"),
            },
        }, CancellationToken.None));
        Assert.Equal(kind switch { "crypto" => LinkCloseCodes.LinkIdentityInvalid, "fault" => LinkCloseCodes.InternalError, _ => LinkCloseCodes.ServiceRestart },
            error.CloseCode);
        if (kind == "fault") Assert.Contains(NoiseLink.IdentityUnverifiable, error.Message);
        // Only a binding that is really bad sends the node to re-pin; a fault of the server's is retried with backoff.
        Assert.Equal(kind == "crypto" ? DialAction.NeedsRepin : DialAction.Retry, DialOutcomes.Classify(null, null, error.CloseCode));
    }

    [Fact]
    public async Task A_secure_hook_that_throws_closes_1011_and_the_server_carries_on()
    {
        var calls = 0;
        await using var server = new LinkServer(new LinkServerOptions
        {
            ServerEpoch = "e", HandlerFactory = _ => new TestLinkHandler(), Link = LinkPair.FastOptions,
            Secure = (_, _, _) => Interlocked.Increment(ref calls) == 1 ? throw new InvalidOperationException("boom") : throw new IOException("disk"),
        });
        for (var i = 0; i < 2; i++)
        {
            var (node, end) = LinkPipe.Create();
            Assert.Null(await server.RunAsync(end, NodeId, CancellationToken.None));
            var closed = await Assert.ThrowsAsync<LinkClosedException>(async () => await node.ReceiveAsync(CancellationToken.None));
            Assert.Equal(LinkCloseCodes.InternalError, closed.Code);
            Assert.Equal(DialAction.Retry, DialOutcomes.Classify(null, null, closed.Code));
        }
    }

    private static (Task<NoiseLinkTransport> Initiate, Task<NoiseAcceptResult> Accept, ILinkTransport Mid1, ILinkTransport Mid2) Start(
        E2eKeys keys, string serverNodeId = NodeId, Func<NoiseBindingContext, byte[]>? binding = null, long? nodeRekey = null)
    {
        var (node, mid1) = LinkPipe.Create();
        var (mid2, server) = LinkPipe.Create();
        var initiate = NoiseLink.InitiateAsync(node, new NoiseInitiatorOptions
        {
            NodeId = NodeId, LocalStatic = keys.NodeLink, ServerStatic = keys.Pinned ?? keys.ServerStatic.PublicKey,
            Binding = binding ?? (b => E2eKeys.Sign(keys.NodeIdentity, b)), RekeyEvery = nodeRekey ?? keys.RekeyEvery,
        }, CancellationToken.None);
        var accept = NoiseLink.AcceptAsync(server, keys.ResponderOptions(serverNodeId), CancellationToken.None);
        return (initiate, accept, mid1, mid2);
    }

    private static async Task<(NoiseLinkTransport Node, ILinkTransport Server, ILinkTransport Mid1, ILinkTransport Mid2)> ConnectAsync(
        E2eKeys keys, long? nodeRekey = null)
    {
        var (initiate, accept, mid1, mid2) = Start(keys, nodeRekey: nodeRekey);
        await mid2.SendAsync(await mid1.ReceiveAsync(CancellationToken.None), CancellationToken.None);
        await mid1.SendAsync(await mid2.ReceiveAsync(CancellationToken.None), CancellationToken.None);
        return (await initiate, (await accept).Transport, mid1, mid2);
    }

    [Fact]
    public async Task Every_bit_flip_in_the_first_message_is_refused_before_the_binding_is_checked()
    {
        using var keys = new E2eKeys();
        var (initiate, _, mid1, _) = Start(keys);
        var message1 = await mid1.ReceiveAsync(CancellationToken.None);
        _ = initiate.ContinueWith(_ => { });
        var verified = 0;
        for (var bit = 0; bit < message1.Length * 8; bit++)
        {
            var flipped = message1.ToArray();
            flipped[bit / 8] ^= (byte)(1 << (bit % 8));
            var (a, b) = LinkPipe.Create();
            await a.SendAsync(flipped, CancellationToken.None);
            var options = keys.ResponderOptions(NodeId);
            var error = await Assert.ThrowsAsync<LinkProtocolException>(() => NoiseLink.AcceptAsync(b, new NoiseResponderOptions
            {
                NodeId = options.NodeId, LocalStatics = options.LocalStatics, AllowPlaintext = options.AllowPlaintext,
                Verify = (x, y, ct) => { Interlocked.Increment(ref verified); return options.Verify(x, y, ct); },
            }, CancellationToken.None));
            Assert.Equal(bit < 8 ? LinkCloseCodes.ProtocolError : LinkCloseCodes.LinkKeyUnknown, error.CloseCode);
        }
        Assert.Equal(0, verified);
    }

    [Fact]
    public async Task Every_bit_flip_in_the_answer_is_refused_by_the_node_as_a_key_mismatch()
    {
        using var keys = new E2eKeys();
        var (initiate, accept, mid1, mid2) = Start(keys);
        await mid2.SendAsync(await mid1.ReceiveAsync(CancellationToken.None), CancellationToken.None);
        var message2 = await mid2.ReceiveAsync(CancellationToken.None);
        await mid1.SendAsync(message2, CancellationToken.None);
        await initiate;
        await accept;

        for (var bit = 0; bit < message2.Length * 8; bit++)
        {
            var flipped = message2.ToArray();
            flipped[bit / 8] ^= (byte)(1 << (bit % 8));
            var (node, peer) = LinkPipe.Create();
            var attempt = NoiseLink.InitiateAsync(node, new NoiseInitiatorOptions
            {
                NodeId = NodeId, LocalStatic = keys.NodeLink, ServerStatic = keys.ServerStatic.PublicKey, Binding = _ => [],
            }, CancellationToken.None);
            await peer.ReceiveAsync(CancellationToken.None);
            await peer.SendAsync(flipped, CancellationToken.None);
            var error = await Assert.ThrowsAsync<LinkProtocolException>(() => attempt);
            if (NoiseLink.IsFutureHandshake(flipped[0]))
            {
                // A type byte flipped into the range of a later handshake version (0x13, 0x19): fail closed as "update".
                Assert.Equal(LinkCloseCodes.UpgradeRequired, error.CloseCode);
                Assert.Contains(NoiseLink.ServerNewer, error.Message);
                continue;
            }
            Assert.Equal(LinkCloseCodes.LinkKeyUnknown, error.CloseCode);
            Assert.Contains(NoiseLink.KeyMismatch, error.Message);
        }
    }

    [Fact]
    public async Task A_node_that_pinned_another_key_is_link_key_unknown()
    {
        using var keys = new E2eKeys();
        using var other = NoiseKeyPair.Generate();
        keys.Pinned = other.PublicKey;
        var (initiate, accept, mid1, mid2) = Start(keys);
        await mid2.SendAsync(await mid1.ReceiveAsync(CancellationToken.None), CancellationToken.None);
        Assert.Equal(LinkCloseCodes.LinkKeyUnknown, (await Assert.ThrowsAsync<LinkProtocolException>(() => accept)).CloseCode);
        _ = initiate.ContinueWith(_ => { });
    }

    [Fact]
    public async Task A_handshake_for_another_node_id_does_not_open()
    {
        using var keys = new E2eKeys();
        var (initiate, accept, mid1, mid2) = Start(keys, serverNodeId: "node_OTHER");
        await mid2.SendAsync(await mid1.ReceiveAsync(CancellationToken.None), CancellationToken.None);
        Assert.Equal(LinkCloseCodes.LinkKeyUnknown, (await Assert.ThrowsAsync<LinkProtocolException>(() => accept)).CloseCode);
        _ = initiate.ContinueWith(_ => { });
    }

    [Fact]
    public async Task During_a_rotation_the_server_accepts_the_previous_key_and_says_which()
    {
        using var keys = new E2eKeys();
        using var next = NoiseKeyPair.Generate();
        var (node, mid) = LinkPipe.Create();
        var initiate = NoiseLink.InitiateAsync(node, new NoiseInitiatorOptions
        {
            NodeId = NodeId, LocalStatic = keys.NodeLink, ServerStatic = keys.ServerStatic.PublicKey, Binding = b => E2eKeys.Sign(keys.NodeIdentity, b),
        }, CancellationToken.None);
        var options = keys.ResponderOptions(NodeId);
        var result = await NoiseLink.AcceptAsync(mid, new NoiseResponderOptions
        {
            NodeId = NodeId, LocalStatics = [next, keys.ServerStatic], Verify = options.Verify, AllowPlaintext = options.AllowPlaintext,
        }, CancellationToken.None);
        await initiate;
        Assert.Equal(1, result.Accepted!.ServerKeyIndex);
        Assert.Equal(keys.NodeLink.PublicKey, result.Accepted.NodeLinkKey);
    }

    public static TheoryData<string> BadBindings => new() { "other-identity", "other-handshake", "other-server-key", "other-node", "garbage" };

    [Theory]
    [MemberData(nameof(BadBindings))]
    public async Task A_binding_that_is_not_this_node_s_for_this_handshake_is_link_identity_invalid(string kind)
    {
        using var keys = new E2eKeys();
        using var otherIdentity = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var otherServer = NoiseKeyPair.Generate();
        byte[]? captured = null;
        if (kind == "other-handshake")
        {
            // A signature taken from an earlier, valid handshake of the same node.
            var (i, a, m1, m2) = Start(keys, binding: b => captured = E2eKeys.Sign(keys.NodeIdentity, b));
            await m2.SendAsync(await m1.ReceiveAsync(CancellationToken.None), CancellationToken.None);
            await m1.SendAsync(await m2.ReceiveAsync(CancellationToken.None), CancellationToken.None);
            await i;
            await a;
        }
        Func<NoiseBindingContext, byte[]> binding = kind switch
        {
            "other-identity" => b => E2eKeys.Sign(otherIdentity, b),
            "other-handshake" => _ => captured!,
            "other-server-key" => b => E2eKeys.Sign(keys.NodeIdentity, b with { ServerStaticKey = otherServer.PublicKey }),
            "other-node" => b => E2eKeys.Sign(keys.NodeIdentity, b with { NodeId = "node_OTHER" }),
            _ => _ => RandomNumberGenerator.GetBytes(64),
        };
        var (initiate, accept, mid1, mid2) = Start(keys, binding: binding);
        await mid2.SendAsync(await mid1.ReceiveAsync(CancellationToken.None), CancellationToken.None);
        var error = await Assert.ThrowsAsync<LinkProtocolException>(() => accept);
        Assert.Equal(LinkCloseCodes.LinkIdentityInvalid, error.CloseCode);
        Assert.Contains(NoiseLink.IdentityInvalid, error.Message);
        _ = initiate.ContinueWith(_ => { });
    }

    [Fact]
    public async Task The_server_verifies_the_binding_against_the_paired_identity_spki()
    {
        using var keys = new E2eKeys();
        using var stranger = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        keys.IdentitySpki = stranger.ExportSubjectPublicKeyInfo(); // the kernel lists another node's key
        var (initiate, accept, mid1, mid2) = Start(keys);
        await mid2.SendAsync(await mid1.ReceiveAsync(CancellationToken.None), CancellationToken.None);
        Assert.Equal(LinkCloseCodes.LinkIdentityInvalid, (await Assert.ThrowsAsync<LinkProtocolException>(() => accept)).CloseCode);
        _ = initiate.ContinueWith(_ => { });
    }

    // ---- records ----------------------------------------------------------------------------------

    private static async Task<byte[]> RelayAsync(ILinkTransport from, ILinkTransport to)
    {
        var record = await from.ReceiveAsync(CancellationToken.None);
        await to.SendAsync(record, CancellationToken.None);
        return record;
    }

    [Fact]
    public async Task Records_flow_both_ways_and_rekey_on_both_sides_at_the_same_count()
    {
        using var keys = new E2eKeys { RekeyEvery = 3 };
        var (node, server, mid1, mid2) = await ConnectAsync(keys);
        for (var i = 0; i < 20; i++)
        {
            var up = RandomNumberGenerator.GetBytes(1 + i * 37);
            await node.SendAsync(up, CancellationToken.None);
            await RelayAsync(mid1, mid2);
            Assert.Equal(up, await server.ReceiveAsync(CancellationToken.None));
            var down = RandomNumberGenerator.GetBytes(1 + i * 11);
            await server.SendAsync(down, CancellationToken.None);
            await RelayAsync(mid2, mid1);
            Assert.Equal(down, await node.ReceiveAsync(CancellationToken.None));
        }
    }

    [Fact]
    public async Task A_side_that_did_not_rekey_cannot_open_the_record_after_the_rekey()
    {
        using var keys = new E2eKeys { RekeyEvery = 3 };
        var (node, server, mid1, mid2) = await ConnectAsync(keys, nodeRekey: NoiseLinkTransport.DefaultRekeyEvery);
        for (var i = 0; i < 3; i++)
        {
            await node.SendAsync(new byte[] { (byte)i }, CancellationToken.None);
            await RelayAsync(mid1, mid2);
            Assert.Equal([(byte)i], await server.ReceiveAsync(CancellationToken.None));
        }
        await node.SendAsync(new byte[] { 3 }, CancellationToken.None);
        await RelayAsync(mid1, mid2);
        await Assert.ThrowsAsync<LinkProtocolException>(async () => await server.ReceiveAsync(CancellationToken.None));
    }

    public static TheoryData<string> RecordFaults => new() { "flip", "replay", "reorder", "drop", "truncate", "type", "reflect", "empty" };

    [Theory]
    [MemberData(nameof(RecordFaults))]
    public async Task A_record_that_was_tampered_replayed_reordered_or_dropped_is_refused_and_so_is_everything_after(string fault)
    {
        using var keys = new E2eKeys();
        var (node, server, mid1, mid2) = await ConnectAsync(keys);
        await node.SendAsync(Encoding.ASCII.GetBytes("first"), CancellationToken.None);
        await node.SendAsync(Encoding.ASCII.GetBytes("second"), CancellationToken.None);
        var first = await mid1.ReceiveAsync(CancellationToken.None);
        var second = await mid1.ReceiveAsync(CancellationToken.None);
        var target = server;

        switch (fault)
        {
            case "flip":
                var flipped = first.ToArray();
                flipped[^5] ^= 0x20;
                await mid2.SendAsync(flipped, CancellationToken.None);
                break;
            case "replay":
                await mid2.SendAsync(first, CancellationToken.None);
                Assert.Equal("first", Encoding.ASCII.GetString(await server.ReceiveAsync(CancellationToken.None)));
                await mid2.SendAsync(first, CancellationToken.None);
                break;
            case "reorder":
                await mid2.SendAsync(second, CancellationToken.None);
                break;
            case "drop":
                await mid2.SendAsync(second, CancellationToken.None); // first never arrives
                break;
            case "truncate":
                await mid2.SendAsync(first[..^1], CancellationToken.None);
                break;
            case "type":
                await mid2.SendAsync((byte[])[0x13, .. first[1..]], CancellationToken.None);
                break;
            case "reflect":
                await mid1.SendAsync(first, CancellationToken.None); // the node's own record, back to the node
                target = node;
                break;
            case "empty":
                await mid2.SendAsync(Array.Empty<byte>(), CancellationToken.None);
                break;
        }
        var error = await Assert.ThrowsAsync<LinkProtocolException>(async () => await target.ReceiveAsync(CancellationToken.None));
        Assert.Equal(LinkCloseCodes.ProtocolError, error.CloseCode);
        Assert.StartsWith(NoiseLink.FrameRejected, error.Message);

        // Nothing is delivered after a refused record, not even the genuine next one.
        if (target == server)
        {
            await mid2.SendAsync(fault == "reorder" ? first : second, CancellationToken.None);
            await Assert.ThrowsAsync<LinkProtocolException>(async () => await server.ReceiveAsync(CancellationToken.None));
        }
    }

    [Fact]
    public async Task A_full_frame_sealed_fits_the_relay_cap_and_one_byte_more_is_refused()
    {
        using var keys = new E2eKeys();
        var (node, server, mid1, mid2) = await ConnectAsync(keys);
        var frame = FrameCodec.Encode(new LinkFrame(FrameKind.Data, FrameFlags.Fin, 1, 1, 0, new byte[FrameCodec.MaxSendPayload]));
        await node.SendAsync(frame, CancellationToken.None);
        Assert.Equal(FrameCodec.MaxFrame, (await RelayAsync(mid1, mid2)).Length);
        Assert.Equal(frame, await server.ReceiveAsync(CancellationToken.None));
        Assert.Equal(LinkCloseCodes.MessageTooBig, (await Assert.ThrowsAsync<LinkProtocolException>(
            async () => await node.SendAsync(new byte[frame.Length + 1], CancellationToken.None))).CloseCode);
    }

    // ---- negotiation, skew, downgrade -------------------------------------------------------------

    [Fact]
    public async Task A_plaintext_first_frame_passes_through_only_where_plaintext_is_allowed()
    {
        using var keys = new E2eKeys { AllowPlaintext = true };
        var hello = LinkHandshake.HelloFrame(new LinkHello(NodeId, LinkProtocol.Supported, new AgentInfo("0.1.0", "linux", "x64"), []));
        var (a, b) = LinkPipe.Create();
        await a.SendAsync(hello, CancellationToken.None);
        var result = await NoiseLink.AcceptAsync(b, keys.ResponderOptions(NodeId), CancellationToken.None);
        Assert.False(result.Encrypted);
        Assert.Equal(hello, await result.Transport.ReceiveAsync(CancellationToken.None));

        keys.AllowPlaintext = false;
        (a, b) = LinkPipe.Create();
        await a.SendAsync(hello, CancellationToken.None);
        var error = await Assert.ThrowsAsync<LinkProtocolException>(() => NoiseLink.AcceptAsync(b, keys.ResponderOptions(NodeId), CancellationToken.None));
        Assert.Equal(LinkCloseCodes.E2eRequired, error.CloseCode);
    }

    [Theory]
    [InlineData(LinkCloseCodes.LinkKeyUnknown)]
    [InlineData(LinkCloseCodes.E2eRequired)]
    [InlineData(LinkCloseCodes.LinkIdentityInvalid)]
    public void The_e2e_refusals_mean_re_pin_never_stop(int code)
        => Assert.Equal(DialAction.NeedsRepin, DialOutcomes.Classify(null, null, code));

    [Fact]
    public async Task An_encrypted_link_carries_the_whole_protocol_and_resumes_across_reconnects()
    {
        using var keys = new E2eKeys();
        await using var pair = new LinkPair(clientSecure: keys.ClientSecure(NodeId), serverSecure: keys.ServerSecure).Start();
        var node = await pair.OnlineAsync();
        Assert.Equal(5, (await node.CallAsync("test.add", new JsonObject { ["a"] = 2, ["b"] = 3 })).GetInt32());
        await pair.Client.DropConnectionAsync();
        await Wait.UntilAsync(() => pair.Client.ResumedConnections == 1 && pair.Client.Status == LinkClientStatus.Online);
        Assert.Same(node, pair.Client.Session);
        Assert.Equal(7, (await node.CallAsync("test.add", new JsonObject { ["a"] = 3, ["b"] = 4 })).GetInt32());
        Assert.Equal(2, keys.Encrypted);
        Assert.Equal(0, keys.Plaintext);
    }

    [Fact]
    public async Task A_pinned_node_and_a_server_without_e2e_never_fall_back_to_plaintext()
    {
        using var keys = new E2eKeys();
        var relay = new RecordingRelay();
        LinkPair? pair = null;
        await using (pair = new LinkPair(clientSecure: keys.ClientSecure(NodeId),
            dial: _ => relay.DialAsync(t => pair!.Server.RunAsync(t, NodeId, CancellationToken.None))).Start())
        {
            await Wait.UntilAsync(() => pair.Client.Status == LinkClientStatus.NeedsUpdate, what: "needs update");
            Assert.Contains(NoiseLink.ServerUnsupported, pair.Client.StatusReason);
            Assert.All(relay.Messages.Where(m => m.FromNode), m => Assert.Equal(NoiseLink.InitiatorHandshake, m.Message[0]));
            Assert.Equal(0, pair.Client.Connections);
        }
    }

    [Fact]
    public async Task A_pinned_node_facing_another_server_key_needs_a_re_pin_and_keeps_its_pin()
    {
        using var keys = new E2eKeys();
        var pinned = keys.ServerStatic.PublicKey;
        keys.Pinned = pinned;
        var server = new E2eKeys(); // a server that holds a different key
        await using var pair = new LinkPair(clientSecure: keys.ClientSecure(NodeId), serverSecure: server.ServerSecure).Start();
        await Wait.UntilAsync(() => pair.Client.Status == LinkClientStatus.NeedsRepin, what: "needs re-pin");
        Assert.Contains("4495", pair.Client.StatusReason);
        Assert.Contains(NoiseLink.KeyUnknown, pair.Client.StatusReason);
        Assert.Equal(0, pair.Client.Connections);
        Assert.Same(pinned, keys.Pinned);
        Assert.False(pair.Server.IsOnline(NodeId));
        server.Dispose();
    }

    [Fact]
    public async Task A_plaintext_node_whose_link_is_required_encrypted_is_refused_4496()
    {
        using var keys = new E2eKeys { AllowPlaintext = false };
        await using var pair = new LinkPair(serverSecure: keys.ServerSecure).Start();
        await Wait.UntilAsync(() => pair.Client.Status == LinkClientStatus.NeedsRepin, what: "needs re-pin");
        Assert.Contains(NoiseLink.E2eRequired, pair.Client.StatusReason);
        Assert.Null(pair.Server.FindSession(NodeId));
    }

    [Fact]
    public async Task A_node_never_paired_for_e2e_still_connects_in_plaintext_where_allowed()
    {
        using var keys = new E2eKeys { AllowPlaintext = true };
        await using var pair = new LinkPair(serverSecure: keys.ServerSecure).Start();
        await pair.OnlineAsync();
        Assert.Equal(1, keys.Plaintext);
    }

    [Fact]
    public async Task A_binding_the_server_refuses_needs_a_re_pin_and_never_comes_online()
    {
        using var keys = new E2eKeys();
        using var stranger = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        keys.IdentitySpki = stranger.ExportSubjectPublicKeyInfo();
        await using var pair = new LinkPair(clientSecure: keys.ClientSecure(NodeId), serverSecure: keys.ServerSecure).Start();
        await Wait.UntilAsync(() => pair.Client.Status == LinkClientStatus.NeedsRepin, what: "needs re-pin");
        Assert.Contains(NoiseLink.IdentityInvalid, pair.Client.StatusReason);
        Assert.Null(pair.Server.FindSession(NodeId));
    }

    // ---- what the relay sees ----------------------------------------------------------------------

    private static async Task<IReadOnlyList<(bool FromNode, byte[] Message)>> RelayedRunAsync(byte[] marker, bool encrypted)
    {
        using var keys = new E2eKeys();
        var relay = new RecordingRelay();
        LinkPair? pair = null;
        await using (pair = new LinkPair(
            clientSecure: encrypted ? keys.ClientSecure(NodeId) : null,
            serverSecure: encrypted ? keys.ServerSecure : null,
            dial: _ => relay.DialAsync(t => pair!.Server.RunAsync(t, NodeId, CancellationToken.None))).Start())
        {
            var node = await pair.OnlineAsync();
            Assert.Equal(5, (await node.CallAsync("test.add", new JsonObject { ["a"] = 2, ["b"] = 3 })).GetInt32());
            await node.NotifyAsync("node.report", new JsonObject { ["nodeId"] = NodeId, ["marker"] = Convert.ToHexString(marker) });

            // An echo channel carrying the marker in compressible plaintext, 256 KiB each way.
            var server = pair.Server.FindSession(NodeId)!;
            var received = new RecordingHandler { CloseWhenRemoteCloses = false };
            var channel = await server.OpenChannelAsync(new ChannelOpenRequest(ChannelClasses.DiagEcho.Name), received);
            var chunk = new byte[4096];
            for (var i = 0; i < chunk.Length; i += 64) marker.CopyTo(chunk, i);
            for (var i = 0; i < 64; i++) await channel.SendAsync(chunk);
            channel.Close();
            await received.RemoteClosed.Task.WaitAsync(TimeSpan.FromSeconds(20));
            Assert.Equal(64 * chunk.Length, received.Messages.Sum(m => m.Length));
        }
        return relay.Messages;
    }

    private static bool Contains(IEnumerable<byte[]> haystack, byte[] needle)
        => haystack.Any(message => message.AsSpan().IndexOf(needle) >= 0);

    [Fact]
    public async Task The_relay_never_sees_plaintext_on_an_encrypted_link()
    {
        var marker = RandomNumberGenerator.GetBytes(32);
        var messages = (await RelayedRunAsync(marker, encrypted: true)).Select(m => m.Message).ToList();
        var secrets = new[]
        {
            marker, Encoding.ASCII.GetBytes(Convert.ToHexString(marker)), Encoding.UTF8.GetBytes(NodeId),
            Encoding.ASCII.GetBytes(LinkHandshake.HelloMethod), Encoding.ASCII.GetBytes("test.add"),
            Encoding.ASCII.GetBytes(ChannelClasses.DiagEcho.Name), Encoding.ASCII.GetBytes("node.report"),
        };
        foreach (var secret in secrets) Assert.False(Contains(messages, secret), $"the relay saw {Encoding.ASCII.GetString(secret)}");

        // Every message is a handshake message or a record: no frame header (format byte 1) ever crosses.
        Assert.Equal(NoiseLink.InitiatorHandshake, messages[0][0]);
        Assert.All(messages.Skip(1), m => Assert.Contains(m[0], new[] { NoiseLink.ResponderHandshake, NoiseLink.Record }));

        // The record bodies look random: about 8 bits of entropy per byte over the run (the plaintext was mostly zeros).
        var bodies = messages.Where(m => m[0] == NoiseLink.Record).SelectMany(m => m.Skip(1)).ToArray();
        Assert.True(bodies.Length > 256 * 1024, $"{bodies.Length} bytes relayed");
        Assert.True(Entropy(bodies) > 7.99, $"entropy {Entropy(bodies):F4} bits per byte");
    }

    [Fact]
    public async Task The_same_run_in_plaintext_shows_the_relay_everything_so_the_check_above_is_not_vacuous()
    {
        var marker = RandomNumberGenerator.GetBytes(32);
        var messages = (await RelayedRunAsync(marker, encrypted: false)).Select(m => m.Message).ToList();
        Assert.True(Contains(messages, marker));
        Assert.True(Contains(messages, Encoding.UTF8.GetBytes(NodeId)));
        Assert.True(Contains(messages, Encoding.ASCII.GetBytes(LinkHandshake.HelloMethod)));
        Assert.True(Entropy(messages.SelectMany(m => m).ToArray()) < 7.0);
    }

    private static double Entropy(byte[] bytes)
    {
        var counts = new long[256];
        foreach (var b in bytes) counts[b]++;
        double entropy = 0;
        foreach (var count in counts)
        {
            if (count == 0) continue;
            var p = (double)count / bytes.Length;
            entropy -= p * Math.Log2(p);
        }
        return entropy;
    }
}
