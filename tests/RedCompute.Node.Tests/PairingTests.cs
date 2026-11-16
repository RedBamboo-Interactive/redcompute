using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using RedCompute.Node.Credentials;
using RedCompute.Node.Pairing;
using RedCompute.NodeLink;
using Xunit;

namespace RedCompute.Node.Tests;

public sealed class PairingTests : IDisposable
{
    private const string Origin = "https://leaf.example.test";
    private static readonly MachineDescription Machine = new("leaflinux", "ab12", "linux", "x64", "Fedora Linux 45");
    private static readonly AgentInfo Agent = new("0.1.0", "linux", "x64");
    private static readonly DateTimeOffset Now = new(2026, 11, 16, 12, 0, 0, TimeSpan.Zero);

    private readonly NodeHome _home = new("pair");

    public void Dispose() => _home.Dispose();

    [Theory]
    [InlineData("ABCD-EFGH", "ABCD-EFGH")]
    [InlineData("abcdefgh", "ABCD-EFGH")]
    [InlineData(" ab cd-ef gh ", "ABCD-EFGH")]
    [InlineData("0O1I-L234", "0011-1234")]
    public void Codes_are_normalized(string input, string expected) => Assert.Equal(expected, PairingCode.Normalize(input));

    [Theory]
    [InlineData("ABCD-EFG")]
    [InlineData("ABCD-EFGHJ")]
    [InlineData("ABCU-EFGH")] // U is never a symbol
    [InlineData("ABCD_EFGH")]
    [InlineData("")]
    public void Bad_codes_are_refused(string input)
    {
        Assert.False(PairingCode.TryNormalize(input, out _));
        Assert.Equal("bad_code", Assert.Throws<NodeCommandException>(() => PairingCode.Normalize(input)).Code);
    }

    [Theory]
    [InlineData("https://Leaf.Example.com", "https://leaf.example.com")]
    [InlineData("https://leaf.example.com/", "https://leaf.example.com")]
    [InlineData("https://leaf.example.com:8443", "https://leaf.example.com:8443")]
    [InlineData("http://127.0.0.1:5000", "http://127.0.0.1:5000")]
    [InlineData("http://localhost:5000/", "http://localhost:5000")]
    public void Origins_are_normalized(string input, string expected) => Assert.Equal(expected, ServerOrigin.Normalize(input));

    [Theory]
    [InlineData("http://leaf.example.com", "insecure_server")]
    [InlineData("https://leaf.example.com/leaf", "bad_server")]
    [InlineData("https://leaf.example.com/?x=1", "bad_server")]
    [InlineData("https://user@leaf.example.com", "bad_server")]
    [InlineData("ftp://leaf.example.com", "bad_server")]
    [InlineData("leaf.example.com", "bad_server")]
    public void Unsafe_origins_are_refused(string input, string code)
        => Assert.Equal(code, Assert.Throws<NodeCommandException>(() => ServerOrigin.Normalize(input)).Code);

    [Fact]
    public void The_link_address_follows_the_origin_scheme()
    {
        Assert.Equal("wss://leaf.example.com/api/nodes/link", ServerOrigin.LinkUri("https://leaf.example.com", "/api/nodes/link").ToString());
        Assert.Equal("ws://127.0.0.1:5000/api/nodes/link", ServerOrigin.LinkUri("http://127.0.0.1:5000", "/api/nodes/link").ToString());
        Assert.False(ServerOrigin.IsSafePath("//evil.example/x"));
        Assert.False(ServerOrigin.IsSafePath("/api/../admin"));
        Assert.False(ServerOrigin.IsSafePath("https://evil.example/x"));
    }

    [Fact]
    public void The_fingerprint_is_16_hex_digits_of_the_spki_hash()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var spki = key.ExportSubjectPublicKeyInfo();
        var fingerprint = NodeCrypto.Fingerprint(spki);
        Assert.Matches("^[0-9A-F]{4}-[0-9A-F]{4}-[0-9A-F]{4}-[0-9A-F]{4}$", fingerprint);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(spki))[..16], fingerprint.Replace("-", ""));
    }

    [Fact]
    public void The_proof_message_and_machine_hash_are_the_kernels()
    {
        Assert.Equal("leaf-node-pair.v1\nABCDEFGH\nMFkw\nhttps://leaf.example.test",
            NodeCrypto.ProofMessage("ABCD-EFGH", "MFkw", "https://leaf.example.test"));
        // SHA-256("leaf-node-machine-id:" + trimmed, lower-cased id), lower-case hex.
        Assert.Equal(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("leaf-node-machine-id:0f1e-abcd"))).ToLowerInvariant(),
            MachineIdentity.Hash("  0F1E-ABCD\n"));
    }

    [Fact]
    public void An_assertion_is_an_es256_jws_bound_to_its_audience_for_60_seconds()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var spki = key.ExportSubjectPublicKeyInfo();
        var first = NodeCrypto.Assertion(key, "node_01JTESTTESTTESTTESTTESTTES", Origin + "/api/nodes/link", Now);
        var second = NodeCrypto.Assertion(key, "node_01JTESTTESTTESTTESTTESTTES", Origin + "/api/nodes/link", Now);

        var claims = NodeCrypto.VerifyAssertion(first, spki)!;
        var header = System.Text.Json.Nodes.JsonNode.Parse(NodeCrypto.FromBase64Url(first.Split('.')[0]))!;
        Assert.Equal("ES256", header["alg"]!.GetValue<string>());
        Assert.Equal("leaf-node+jwt", header["typ"]!.GetValue<string>());
        Assert.Equal("node_01JTESTTESTTESTTESTTESTTES", claims["iss"]!.GetValue<string>());
        Assert.Equal(Origin + "/api/nodes/link", claims["aud"]!.GetValue<string>());
        Assert.Equal(Now.ToUnixTimeSeconds(), claims["iat"]!.GetValue<long>());
        Assert.Equal(Now.ToUnixTimeSeconds() + 60, claims["exp"]!.GetValue<long>());
        Assert.Equal(16, NodeCrypto.FromBase64Url(claims["jti"]!.GetValue<string>()).Length);
        Assert.NotEqual(claims["jti"]!.GetValue<string>(), NodeCrypto.VerifyAssertion(second, spki)!["jti"]!.GetValue<string>());

        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Assert.Null(NodeCrypto.VerifyAssertion(first, other.ExportSubjectPublicKeyInfo()));
        var tampered = first.Split('.');
        tampered[1] = NodeCrypto.Base64Url(Encoding.UTF8.GetBytes(claims.ToJsonString().Replace("link", "pair")));
        Assert.Null(NodeCrypto.VerifyAssertion(string.Join('.', tampered), spki));
    }

    [Fact]
    public async Task Pairing_redeems_the_code_with_a_proof_and_keeps_the_key()
    {
        string? fingerprint = null;
        var http = new StubHttp((request, body) =>
        {
            var json = JsonNode.Parse(body)!;
            fingerprint = NodeCrypto.Fingerprint(Convert.FromBase64String(json["publicKey"]!.GetValue<string>()));
            return Json(HttpStatusCode.Created, new JsonObject
            {
                ["nodeId"] = "node_01JTESTTESTTESTTESTTESTTES",
                ["serverOrigin"] = Origin,
                ["linkPath"] = "/api/nodes/link",
                ["serverInstallId"] = "install-1",
                ["fingerprint"] = fingerprint,
            });
        });
        using var client = new NodeServerClient(http);
        var store = new FileKeyStore(_home.Paths);

        var pairing = await client.PairAsync(Origin, "ABCD-EFGH", "leaflinux", store, Machine, Agent, Now, CancellationToken.None);

        var (request, sent) = Assert.Single(http.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal(Origin + "/api/nodes/link/pair", request.RequestUri!.ToString());
        var json = JsonNode.Parse(sent)!;
        Assert.Equal("ABCD-EFGH", json["code"]!.GetValue<string>());
        Assert.Equal("ab12", json["machine"]!["machineIdHash"]!.GetValue<string>());
        Assert.Equal("Fedora Linux 45", json["machine"]!["osDescription"]!.GetValue<string>());
        Assert.Equal(1, json["agent"]!["protocol"]!["min"]!.GetValue<int>());
        Assert.Equal("file", json["credentialStore"]!.GetValue<string>());
        // The proof: the key signs "leaf-node-pair.v1\n{CODE}\n{publicKey}\n{origin}" (the kernel's message), base64url.
        var spki = Convert.FromBase64String(json["publicKey"]!.GetValue<string>());
        var proof = NodeCrypto.FromBase64Url(json["proof"]!.GetValue<string>());
        Assert.Equal(64, proof.Length);
        using (var verifier = ECDsa.Create())
        {
            verifier.ImportSubjectPublicKeyInfo(spki, out _);
            Assert.True(verifier.VerifyData(Encoding.UTF8.GetBytes($"leaf-node-pair.v1\nABCDEFGH\n{json["publicKey"]}\n{Origin}"),
                proof, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
            Assert.False(verifier.VerifyData(Encoding.UTF8.GetBytes($"leaf-node-pair.v1\nABCDEFGH\n{json["publicKey"]}\nhttps://evil.example"),
                proof, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
        }

        Assert.Equal("node_01JTESTTESTTESTTESTTESTTES", pairing.NodeId);
        Assert.Equal(Origin, pairing.ServerOrigin);
        Assert.Equal("/api/nodes/link", pairing.LinkPath);
        Assert.Equal("install-1", pairing.ServerInstallId);
        Assert.Equal(fingerprint, pairing.Fingerprint);
        Assert.Equal("file", pairing.CredentialStore);
        using var stored = store.Open(pairing.KeyRef, Origin)!;
        Assert.Equal(spki, stored.ExportSubjectPublicKeyInfo());
    }

    [Theory]
    [InlineData(HttpStatusCode.Conflict, "code_used", "already used")]
    [InlineData(HttpStatusCode.Gone, "code_expired", "expired")]
    [InlineData(HttpStatusCode.BadRequest, "code_invalid", "not valid")]
    [InlineData(HttpStatusCode.Conflict, "same_machine", "node zero")]
    [InlineData(HttpStatusCode.TooManyRequests, null, "Wait a minute")]
    [InlineData(HttpStatusCode.NotFound, null, "does not support nodes")]
    public async Task A_refused_pairing_explains_itself_and_keeps_no_key(HttpStatusCode status, string? code, string says)
    {
        var http = new StubHttp((_, _) => Json(status, code is null ? new JsonObject() : new JsonObject { ["error"] = code }));
        using var client = new NodeServerClient(http);

        var error = await Assert.ThrowsAsync<NodeCommandException>(() =>
            client.PairAsync(Origin, "ABCD-EFGH", null, new FileKeyStore(_home.Paths), Machine, Agent, Now, CancellationToken.None));

        Assert.Contains(says, error.Message);
        if (code is not null) Assert.Equal(code, error.Code);
        Assert.False(File.Exists(_home.Paths.KeyFile));
        Assert.Single(http.Requests); // no unpair: the server created nothing
    }

    [Fact]
    public async Task A_fingerprint_mismatch_cancels_the_pairing_on_both_sides()
    {
        byte[]? spki = null;
        var http = new StubHttp((request, body) =>
        {
            if (request.RequestUri!.AbsolutePath == "/api/nodes/link/pair")
            {
                spki = Convert.FromBase64String(JsonNode.Parse(body)!["publicKey"]!.GetValue<string>());
                return Json(HttpStatusCode.Created, new JsonObject
                {
                    ["nodeId"] = "node_01JTESTTESTTESTTESTTESTTES",
                    ["fingerprint"] = "0000-0000-0000-0000",
                });
            }
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        });
        using var client = new NodeServerClient(http);

        var error = await Assert.ThrowsAsync<NodeCommandException>(() =>
            client.PairAsync(Origin, "ABCD-EFGH", null, new FileKeyStore(_home.Paths), Machine, Agent, Now, CancellationToken.None));

        Assert.Equal("fingerprint_mismatch", error.Code);
        Assert.False(File.Exists(_home.Paths.KeyFile));
        // The node the server created is unpaired with the same key, for <origin>/api/nodes/link/unpair.
        var (unpair, body) = http.Requests[1];
        Assert.Equal("/api/nodes/link/unpair", unpair.RequestUri!.AbsolutePath);
        Assert.Equal("LeafNode", unpair.Headers.Authorization!.Scheme);
        var claims = NodeCrypto.VerifyAssertion(unpair.Headers.Authorization.Parameter!, spki)!;
        Assert.Equal(Origin + "/api/nodes/link/unpair", claims["aud"]!.GetValue<string>());
        Assert.Equal("node_01JTESTTESTTESTTESTTESTTES", claims["iss"]!.GetValue<string>());
        Assert.Equal("unpaired_by_node", JsonNode.Parse(body)!["reason"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_server_naming_another_origin_or_a_strange_link_path_is_refused()
    {
        foreach (var (field, value, code) in new[]
                 {
                     ("serverOrigin", "https://other.example", "origin_mismatch"),
                     ("linkPath", "//evil.example/link", "bad_response"),
                     ("nodeId", "node_x", "bad_response"),
                 })
        {
            var response = new JsonObject { ["nodeId"] = "node_01JTESTTESTTESTTESTTESTTES" };
            response[field] = value;
            using var client = new NodeServerClient(new StubHttp((request, _) =>
                request.RequestUri!.AbsolutePath == "/api/nodes/link/pair"
                    ? Json(HttpStatusCode.Created, (JsonObject)response.DeepClone())
                    : new HttpResponseMessage(HttpStatusCode.NoContent)));
            var error = await Assert.ThrowsAsync<NodeCommandException>(() =>
                client.PairAsync(Origin, "ABCD-EFGH", null, new FileKeyStore(_home.Paths), Machine, Agent, Now, CancellationToken.None));
            Assert.Equal(code, error.Code);
            Assert.False(File.Exists(_home.Paths.KeyFile));
        }
    }

    [Fact]
    public async Task An_unreachable_server_is_said_so()
    {
        using var client = new NodeServerClient(new StubHttp((_, _) => throw new HttpRequestException("connection refused")));
        var error = await Assert.ThrowsAsync<NodeCommandException>(() =>
            client.PairAsync(Origin, "ABCD-EFGH", null, new FileKeyStore(_home.Paths), Machine, Agent, Now, CancellationToken.None));
        Assert.Equal("server_unreachable", error.Code);
        Assert.Contains("leaf.example.test", error.Message);
    }

    internal static HttpResponseMessage Json(HttpStatusCode status, JsonObject body)
        => new(status) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
}
