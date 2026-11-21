using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using RedCompute.Node.Credentials;
using RedCompute.Node.Pairing;
using RedCompute.NodeLink;
using RedCompute.NodeLink.Noise;
using RedCompute.Tests;
using Xunit;

namespace RedCompute.Node.Tests;

/// <summary>
/// The agent's end of the encrypted link (Phase 4 P0.5c): the server link key pinned at pairing and confirmed
/// with a signed proof, the link key beside the identity, <c>leaf-node repin</c>, a pinned node that never sends
/// plaintext, and the needs-repin status that never re-pins by itself.
/// </summary>
public sealed class NodeLinkE2eTests : IDisposable
{
    private const string Origin = "https://leaf.example.test";
    private const string NodeId = NodeAgentLinkTests.NodeId;
    private static readonly MachineDescription Machine = new("leaflinux", "ab12", "linux", "x64", "Fedora Linux 45");
    private static readonly AgentInfo Agent = new("0.1.0", "linux", "x64");
    private static readonly DateTimeOffset Now = new(2026, 11, 21, 12, 0, 0, TimeSpan.Zero);

    private readonly NodeHome _home = new("e2e");
    private readonly NoiseKeyPair _serverKey = NoiseKeyPair.Generate();

    public void Dispose()
    {
        _serverKey.Dispose();
        _home.Dispose();
    }

    private JsonObject LinkKey(NoiseKeyPair? key = null, string? fingerprint = null)
    {
        var info = NodeLinkKeyInfo.For((key ?? _serverKey).PublicKey);
        return new JsonObject
        {
            ["protocol"] = info.Protocol, ["publicKey"] = info.PublicKey,
            ["fingerprint"] = fingerprint ?? info.Fingerprint, ["keyId"] = info.KeyId,
        };
    }

    /// <summary>The kernel of K0 in miniature: pair answers with the link key, pin and repin answer as told.</summary>
    private StubHttp Kernel(Func<HttpRequestMessage, string, HttpResponseMessage>? pin = null, JsonObject? linkKey = null,
        Func<HttpRequestMessage, string, HttpResponseMessage>? repin = null, bool offerLinkKey = true)
        => new((request, body) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == NodeServerClient.PairPath)
            {
                var answer = new JsonObject
                {
                    ["nodeId"] = NodeId,
                    ["fingerprint"] = NodeCrypto.Fingerprint(Convert.FromBase64String(JsonNode.Parse(body)!["publicKey"]!.GetValue<string>())),
                };
                if (offerLinkKey) answer["linkKey"] = linkKey ?? LinkKey();
                return PairingTests.Json(HttpStatusCode.Created, answer);
            }
            if (path == NodeServerClient.PinPath) return pin?.Invoke(request, body) ?? PairingTests.Json(HttpStatusCode.OK, new JsonObject { ["ok"] = true });
            if (path == NodeServerClient.RepinPath)
                return repin?.Invoke(request, body) ?? PairingTests.Json(HttpStatusCode.OK, new JsonObject { ["linkKey"] = LinkKey() });
            if (path == NodeServerClient.UnpairPath) return PairingTests.Json(HttpStatusCode.OK, new JsonObject());
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

    private byte[] NodeSpki()
    {
        var pairing = _home.Environment().StateStore.Load().Pairing!;
        using var key = new FileKeyStore(_home.Paths).Open(pairing.KeyRef, pairing.ServerOrigin)!;
        return key.ExportSubjectPublicKeyInfo();
    }

    // ================================================================= pairing pins the key

    [Fact]
    public async Task Pairing_pins_the_server_link_key_and_confirms_it_with_a_signed_proof()
    {
        var http = Kernel();
        using var client = new NodeServerClient(http);
        var store = new FileKeyStore(_home.Paths);

        var pairing = await client.PairAsync(Origin, "ABCD-EFGH", "leaflinux", store, Machine, Agent, Now, CancellationToken.None);

        var info = NodeLinkKeyInfo.For(_serverKey.PublicKey);
        Assert.Equal(new NodeLinkPin(info.PublicKey, info.Fingerprint, info.KeyId, Now), pairing.LinkPin);
        using (var linkKey = store.OpenLinkKey(pairing.KeyRef, Origin)) Assert.NotNull(linkKey);

        var (request, body) = Assert.Single(http.Requests, r => r.Request.RequestUri!.AbsolutePath == NodeServerClient.PinPath);
        var pin = JsonNode.Parse(body)!;
        Assert.Equal(info.Fingerprint, pin["fingerprint"]!.GetValue<string>());
        Assert.Equal("ABCD-EFGH", pin["code"]!.GetValue<string>());
        using var identity = store.Open(pairing.KeyRef, Origin)!;
        var spki = identity.ExportSubjectPublicKeyInfo();
        using (var verifier = ECDsa.Create())
        {
            verifier.ImportSubjectPublicKeyInfo(spki, out _);
            Assert.True(verifier.VerifyData(Encoding.UTF8.GetBytes($"leaf-node-link-pin.v1\n{NodeId}\nABCDEFGH\n{info.Fingerprint}"),
                NodeCrypto.FromBase64Url(pin["proof"]!.GetValue<string>()), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
        }
        // Assertion-authenticated, for exactly that route.
        Assert.Equal("LeafNode", request.Headers.Authorization!.Scheme);
        var claims = NodeCrypto.VerifyAssertion(request.Headers.Authorization.Parameter!, spki)!;
        Assert.Equal(Origin + NodeServerClient.PinPath, claims["aud"]!.GetValue<string>());
        Assert.Equal(NodeId, claims["iss"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_kernel_without_k0_pairs_without_a_pin()
    {
        var http = Kernel(offerLinkKey: false);
        using var client = new NodeServerClient(http);
        var pairing = await client.PairAsync(Origin, "ABCD-EFGH", null, new FileKeyStore(_home.Paths), Machine, Agent, Now, CancellationToken.None);
        Assert.Null(pairing.LinkPin);
        Assert.Single(http.Requests);
        Assert.False(File.Exists(_home.Paths.LinkKeyFile));
    }

    public static TheoryData<string> BadPins => new() { "refused", "fingerprint", "not-a-point", "protocol" };

    [Theory]
    [MemberData(nameof(BadPins))]
    public async Task A_pin_that_does_not_hold_cancels_the_pairing_and_leaves_nothing_behind(string kind)
    {
        using var other = NoiseKeyPair.Generate();
        var linkKey = kind switch
        {
            "fingerprint" => LinkKey(fingerprint: NodeLinkKeyInfo.For(other.PublicKey).Fingerprint),
            "not-a-point" => new JsonObject { ["publicKey"] = NodeLinkKeyInfo.Base64Url(new byte[65]) },
            "protocol" => new JsonObject { ["publicKey"] = NodeLinkKeyInfo.For(_serverKey.PublicKey).PublicKey, ["protocol"] = "Noise_XX_25519_ChaChaPoly_BLAKE2s" },
            _ => null,
        };
        var http = Kernel(linkKey: linkKey,
            pin: (_, _) => PairingTests.Json(HttpStatusCode.Conflict, new JsonObject { ["error"] = "link_key_mismatch" }));
        using var client = new NodeServerClient(http);

        var error = await Assert.ThrowsAsync<NodeCommandException>(() =>
            client.PairAsync(Origin, "ABCD-EFGH", null, new FileKeyStore(_home.Paths), Machine, Agent, Now, CancellationToken.None));

        Assert.Equal(kind switch { "not-a-point" => "bad_response", "protocol" => "link_protocol_unsupported", _ => "link_key_mismatch" }, error.Code);
        Assert.False(File.Exists(_home.Paths.KeyFile));
        Assert.False(File.Exists(_home.Paths.LinkKeyFile));
        Assert.Contains(http.Requests, r => r.Request.RequestUri!.AbsolutePath == NodeServerClient.UnpairPath);
    }

    // ================================================================= the encrypted link

    private sealed class Hub : IAsyncDisposable
    {
        public required LinkServer Server { get; init; }
        public List<byte> FirstBytes { get; } = [];
        public int Encrypted;
        public ValueTask DisposeAsync() => Server.DisposeAsync();
    }

    /// <summary>A LinkServer with NodeHub's responder: our key, the binding checked against the node's identity.</summary>
    private Hub StartHub(Func<byte[]> nodeSpki, NoiseKeyPair? serverKey = null)
    {
        Hub? hub = null;
        hub = new Hub
        {
            Server = new LinkServer(new LinkServerOptions
            {
                ServerEpoch = "epoch-1",
                HandlerFactory = _ => new ServerSide(),
                Features = [LinkFeatures.DescribeV1, LinkFeatures.HardwareV1, LinkFeatures.CliV1, LinkFeatures.DiagEcho],
                Secure = async (transport, nodeId, ct) =>
                {
                    var recording = new FirstByteTransport(transport, b => { lock (hub!.FirstBytes) hub.FirstBytes.Add(b); });
                    var result = await NoiseLink.AcceptAsync(recording, new NoiseResponderOptions
                    {
                        NodeId = nodeId,
                        LocalStatics = [serverKey ?? _serverKey],
                        Verify = (binding, payload, _) => ValueTask.FromResult(NoiseBinding.Verify(nodeSpki(), binding, payload)),
                        AllowPlaintext = _ => ValueTask.FromResult(true),
                    }, ct);
                    if (result.Encrypted) Interlocked.Increment(ref hub!.Encrypted);
                    return result.Transport;
                },
            }),
        };
        return hub;
    }

    private async Task PairPinnedAsync()
    {
        await LinkedAgent.PairLocallyAsync(_home);
        var env = _home.Environment();
        var state = env.StateStore.Load();
        var pairing = state.Pairing!;
        new FileKeyStore(_home.Paths).CreateLinkKey(pairing.KeyRef, pairing.ServerOrigin, pairing.NodeId).Dispose();
        var info = NodeLinkKeyInfo.For(_serverKey.PublicKey);
        env.StateStore.Save(state with { Pairing = pairing with { LinkPin = new NodeLinkPin(info.PublicKey, info.Fingerprint, info.KeyId, Now) } });
    }

    private (NodeAgent Agent, Task Run, CancellationTokenSource Stop) StartAgent(Hub hub)
    {
        var env = _home.Environment(dial: (pairing, key) => token =>
        {
            var (node, server) = LinkPipe.Create();
            _ = hub.Server.RunAsync(server, pairing.NodeId, CancellationToken.None);
            return Task.FromResult(node);
        });
        var agent = new NodeAgent(env);
        var stop = new CancellationTokenSource();
        return (agent, agent.RunAsync(stop.Token), stop);
    }

    private static async Task StopAsync((NodeAgent Agent, Task Run, CancellationTokenSource Stop) running)
    {
        running.Stop.Cancel();
        await running.Agent.DisposeAsync();
        try { await running.Run.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (Exception error) when (error is OperationCanceledException or TimeoutException) { }
    }

    [Fact]
    public async Task A_pinned_node_links_encrypted_and_never_sends_plaintext()
    {
        await PairPinnedAsync();
        await using var hub = StartHub(NodeSpki);
        var running = StartAgent(hub);
        try
        {
            await Wait.UntilAsync(() => hub.Server.IsOnline(NodeId), "the encrypted link");
            Assert.Equal(1, hub.Encrypted);
            var describe = await hub.Server.FindSession(NodeId)!.CallAsync("node.describe");
            Assert.Equal(NodeId, describe.GetProperty("nodeId").GetString());
            lock (hub.FirstBytes)
            {
                // A handshake first, then records only: no plaintext frame (format byte 1) ever reaches the server.
                Assert.Equal(NoiseLink.InitiatorHandshake, hub.FirstBytes[0]);
                Assert.All(hub.FirstBytes, b => Assert.Contains(b, new[] { NoiseLink.InitiatorHandshake, NoiseLink.Record }));
                Assert.DoesNotContain(FrameCodec.Format, hub.FirstBytes);
            }
            var status = running.Agent.Status();
            Assert.Equal("on", status["linkEncryption"]!.GetValue<string>());
            Assert.Equal(NodeLinkKeyInfo.For(_serverKey.PublicKey).Fingerprint, status["serverLinkFingerprint"]!.GetValue<string>());
        }
        finally
        {
            await StopAsync(running);
        }
    }

    [Fact]
    public async Task A_server_with_another_key_puts_the_node_in_needs_repin_and_its_pin_stays()
    {
        await PairPinnedAsync();
        var before = _home.Environment().StateStore.Load().Pairing!.LinkPin;
        using var rotated = NoiseKeyPair.Generate();
        await using var hub = StartHub(NodeSpki, serverKey: rotated);
        var running = StartAgent(hub);
        try
        {
            await Wait.UntilAsync(() => running.Agent.Status()["link"]?["status"]?.GetValue<string>() == "needs-repin", "needs-repin");
            Assert.False(hub.Server.IsOnline(NodeId));
            var text = NodeCommands.DescribeStatus(running.Agent.Status());
            Assert.Contains("leaf-node repin --code", text);
            Assert.Contains("not the one this node pinned", text);
            var after = _home.Environment().StateStore.Load();
            Assert.Equal(before, after.Pairing!.LinkPin); // never re-pinned by itself
            Assert.Null(after.Ended); // and never forgotten: not a revocation
            lock (hub.FirstBytes) Assert.All(hub.FirstBytes, b => Assert.Equal(NoiseLink.InitiatorHandshake, b));
        }
        finally
        {
            await StopAsync(running);
        }
    }

    [Fact]
    public async Task A_pinned_node_whose_link_key_is_gone_says_so_and_does_not_dial()
    {
        await PairPinnedAsync();
        File.Delete(_home.Paths.LinkKeyFile);
        await using var hub = StartHub(NodeSpki);
        var running = StartAgent(hub);
        try
        {
            await Wait.UntilAsync(() => running.Agent.Status()["link"]?["status"]?.GetValue<string>() == "error", "the error");
            Assert.Contains("re-pin", running.Agent.Status()["link"]!["reason"]!.GetValue<string>());
            Assert.Empty(hub.FirstBytes);
        }
        finally
        {
            await StopAsync(running);
        }
    }

    // ================================================================= leaf-node repin

    [Fact]
    public async Task Repin_pins_the_current_key_with_a_new_link_key_and_keeps_the_node_id()
    {
        await LinkedAgent.PairLocallyAsync(_home); // paired before E2E: no pin
        var http = Kernel();
        var env = _home.Environment(server: () => new NodeServerClient(http));
        var output = new StringWriter();

        Assert.Equal(0, await new NodeCommands(env, output).RunAsync(["repin", "--code", "wxyz-2345"], CancellationToken.None));

        var pairing = env.StateStore.Load().Pairing!;
        Assert.Equal(NodeId, pairing.NodeId);
        Assert.Equal(NodeLinkKeyInfo.For(_serverKey.PublicKey).Fingerprint, pairing.LinkPin!.Fingerprint);
        Assert.True(File.Exists(_home.Paths.LinkKeyFile));
        Assert.Contains("end-to-end encrypted", output.ToString());

        var spki = NodeSpki();
        var repin = Assert.Single(http.Requests, r => r.Request.RequestUri!.AbsolutePath == NodeServerClient.RepinPath);
        Assert.Equal("WXYZ-2345", JsonNode.Parse(repin.Body)!["code"]!.GetValue<string>());
        Assert.Equal(Origin + NodeServerClient.RepinPath,
            NodeCrypto.VerifyAssertion(repin.Request.Headers.Authorization!.Parameter!, spki)!["aud"]!.GetValue<string>());
        Assert.Single(http.Requests, r => r.Request.RequestUri!.AbsolutePath == NodeServerClient.PinPath);
        Assert.Contains(env.Audit.Read(), line => line["name"]!.GetValue<string>() == "repin");
    }

    [Fact]
    public async Task A_refused_repin_code_changes_nothing()
    {
        await PairPinnedAsync();
        var before = _home.Environment().StateStore.Load();
        var linkKeyBefore = File.ReadAllBytes(_home.Paths.LinkKeyFile);
        var http = Kernel(repin: (_, _) => PairingTests.Json(HttpStatusCode.Gone, new JsonObject { ["error"] = "code_expired" }));
        var output = new StringWriter();

        Assert.NotEqual(0, await new NodeCommands(_home.Environment(server: () => new NodeServerClient(http)), output)
            .RunAsync(["repin", "--code", "WXYZ-2345"], CancellationToken.None));

        Assert.Contains("re-pin code is not valid", output.ToString());
        Assert.Equal(before, _home.Environment().StateStore.Load());
        Assert.Equal(linkKeyBefore, File.ReadAllBytes(_home.Paths.LinkKeyFile));
    }

    [Fact]
    public async Task Unpairing_deletes_the_link_key_too()
    {
        await PairPinnedAsync();
        var env = _home.Environment(server: () => new NodeServerClient(Kernel()));
        await NodeLifecycle.ForgetAsync(env, env.StateStore.Load().Pairing!, "unpaired", callServer: true, CancellationToken.None);
        Assert.False(File.Exists(_home.Paths.LinkKeyFile));
        Assert.False(File.Exists(_home.Paths.KeyFile));
    }

    [WindowsFact("CNG user key store")]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public void Windows_keeps_the_link_key_as_a_non_exportable_cng_key_that_agrees()
    {
        var store = new CngKeyStore();
        var origin = Origin + "/" + Guid.NewGuid().ToString("N")[..8];
        using var identity = store.Create(origin);
        try
        {
            using var created = store.CreateLinkKey(identity.KeyRef, origin, NodeId);
            Assert.ThrowsAny<CryptographicException>(() => created.ExportPkcs8());
            using var opened = store.OpenLinkKey(identity.KeyRef, origin)!;
            Assert.Equal(created.PublicKey, opened.PublicKey);
            using var peer = NoiseKeyPair.Generate();
            Assert.Equal(peer.Dh(opened.PublicKey), opened.Dh(peer.PublicKey));
        }
        finally
        {
            Assert.True(store.DeleteLinkKey(identity.KeyRef, origin));
            store.Delete(identity.KeyRef, origin);
        }
        Assert.Null(store.OpenLinkKey(identity.KeyRef, origin));
    }
}

/// <summary>Records the first byte of every message the node sends (what a relay would see first).</summary>
internal sealed class FirstByteTransport(ILinkTransport inner, Action<byte> first) : ILinkTransport
{
    public ValueTask SendAsync(ReadOnlyMemory<byte> message, CancellationToken ct) => inner.SendAsync(message, ct);

    public async ValueTask<byte[]> ReceiveAsync(CancellationToken ct)
    {
        var message = await inner.ReceiveAsync(ct);
        if (message.Length > 0) first(message[0]);
        return message;
    }

    public ValueTask CloseAsync(int code, string? reason, CancellationToken ct) => inner.CloseAsync(code, reason, ct);
    public ValueTask DisposeAsync() => inner.DisposeAsync();
}
