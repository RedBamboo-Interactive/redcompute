using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using RedCompute.Node.Credentials;
using RedCompute.NodeLink;
using RedCompute.NodeLink.Noise;

namespace RedCompute.Node.Pairing;

/// <summary>The machine a node describes at pairing (design 3.2). The machine id goes only as a hash.</summary>
/// <param name="Chassis">What kind of machine its firmware says it is, when it says: the kernel's default icon comes from it.</param>
public sealed record MachineDescription(string Name, string? MachineIdHash, string Os, string Arch, string OsDescription,
    string? Chassis = null);

/// <summary>
/// The node's two plain HTTPS calls to the kernel (design 3.2, 3.5): redeeming a pairing code with a
/// new key, and the signed unpair. Everything else goes over the link. Both live under
/// <c>/api/nodes/link/*</c>, the only routes the kernel lets a node reach without a badge; each
/// authenticates the node itself (the pairing code and proof, or a signed assertion).
/// <para>
/// Errors come back as <c>{ "error": "&lt;code&gt;", "message": "…" }</c>; the codes the CLI explains
/// are <c>code_invalid</c>, <c>code_expired</c>, <c>code_used</c>, <c>code_burned</c> (too many wrong
/// tries), <c>same_machine</c> and <c>bad_proof</c>.
/// </para>
/// </summary>
public sealed partial class NodeServerClient(HttpMessageHandler? handler = null, TimeSpan? timeout = null) : IDisposable
{
    public const string PairPath = "/api/nodes/link/pair";
    public const string DefaultLinkPath = "/api/nodes/link";
    public const string UnpairPath = "/api/nodes/link/unpair";

    /// <summary>K0: the node confirms the server link key it pinned (signed proof, assertion-authenticated).</summary>
    public const string PinPath = "/api/nodes/link/pin";

    /// <summary>K0: an owner-issued re-pin code for this node buys the current server link key.</summary>
    public const string RepinPath = "/api/nodes/link/repin";

    private readonly HttpClient _http = new(handler ?? new SocketsHttpHandler { AllowAutoRedirect = false }, disposeHandler: handler is null)
    {
        Timeout = timeout ?? TimeSpan.FromSeconds(30),
    };

    [GeneratedRegex("^node_[0-9A-HJKMNP-TV-Z]{26}$")]
    private static partial Regex NodeIdPattern();

    public static bool IsNodeId(string? text) => text is not null && NodeIdPattern().IsMatch(text);

    /// <summary>
    /// Makes a key in <paramref name="store"/>, redeems <paramref name="code"/> with it and records it.
    /// On any failure the key is deleted, and a node the server already created is unpaired with the
    /// same key, so a failed pairing leaves nothing behind on either side.
    /// </summary>
    public async Task<NodePairing> PairAsync(string origin, string code, string? name, INodeKeyStore store,
        MachineDescription machine, AgentInfo agent, DateTimeOffset now, CancellationToken ct)
    {
        using var key = store.Create(origin);
        string? pairedNodeId = null;
        try
        {
            var spki = key.SubjectPublicKeyInfo;
            var request = new JsonObject
            {
                ["code"] = code,
                ["publicKey"] = Convert.ToBase64String(spki),
                ["proof"] = NodeCrypto.Proof(key.Key, code, spki, origin),
                ["name"] = name,
                ["credentialStore"] = store.Kind,
                ["machine"] = new JsonObject
                {
                    ["name"] = machine.Name,
                    ["machineIdHash"] = machine.MachineIdHash,
                    ["os"] = machine.Os,
                    ["arch"] = machine.Arch,
                    ["osDescription"] = machine.OsDescription,
                    ["chassis"] = machine.Chassis,
                },
                ["agent"] = new JsonObject
                {
                    ["version"] = agent.Version,
                    ["protocol"] = new JsonObject { ["min"] = LinkProtocol.Supported.Min, ["max"] = LinkProtocol.Supported.Max },
                },
            };

            HttpResponseMessage response;
            try
            {
                response = await _http.PostAsync(origin + PairPath, JsonContent.Create(request), ct);
            }
            catch (Exception error) when (error is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                throw new NodeCommandException("server_unreachable", $"Could not reach {origin}: {error.Message}", inner: error);
            }

            using (response)
            {
                var body = await ReadJsonAsync(response, ct);
                if (!response.IsSuccessStatusCode) throw PairingRefused(origin, response.StatusCode, body);

                var nodeId = body?["nodeId"]?.GetValue<string>();
                if (!IsNodeId(nodeId))
                    throw new NodeCommandException("bad_response", $"{origin} answered the pairing without a node id.");
                pairedNodeId = nodeId;

                var serverOrigin = body!["serverOrigin"]?.GetValue<string>();
                if (serverOrigin is not null && ServerOrigin.Normalize(serverOrigin) != origin)
                    throw new NodeCommandException("origin_mismatch",
                        $"The server says its address is {serverOrigin}, not {origin}. Pair with the address Leaf shows.");
                var linkPath = body["linkPath"]?.GetValue<string>() ?? DefaultLinkPath;
                if (!ServerOrigin.IsSafePath(linkPath))
                    throw new NodeCommandException("bad_response", $"{origin} named an invalid link path.");
                var fingerprint = NodeCrypto.Fingerprint(spki);
                var serverFingerprint = body["fingerprint"]?.GetValue<string>();
                if (serverFingerprint is not null && !string.Equals(serverFingerprint, fingerprint, StringComparison.OrdinalIgnoreCase))
                    throw new NodeCommandException("fingerprint_mismatch",
                        $"The server recorded key {serverFingerprint}, but this node's key is {fingerprint}. Pairing cancelled.");

                store.Commit(key, nodeId!);

                // E2E (K0): a kernel that hands RedCompute's link key gets it pinned, and confirmed with a signed
                // proof; a refused proof (the kernel saw another fingerprint) cancels the pairing. An older kernel
                // sends none: the node links in plaintext until its owner re-pins it.
                NodeLinkPin? pin = null;
                if (body["linkKey"] is JsonObject linkKey)
                {
                    var info = ReadLinkKey(origin, linkKey);
                    store.CreateLinkKey(key.KeyRef, origin, nodeId!).Dispose();
                    await PinAsync(origin, nodeId!, key.Key, code, info, now, ct);
                    pin = new NodeLinkPin(info.PublicKey, info.Fingerprint, info.KeyId, now);
                }
                return new NodePairing
                {
                    NodeId = nodeId!,
                    ServerOrigin = origin,
                    LinkPath = linkPath,
                    ServerInstallId = body["serverInstallId"]?.GetValue<string>(),
                    KeyRef = key.KeyRef,
                    CredentialStore = store.Kind,
                    Fingerprint = fingerprint,
                    Name = name,
                    PairedAt = now,
                    LinkPin = pin,
                };
            }
        }
        catch
        {
            if (pairedNodeId is not null)
                await UnpairAsync(origin, pairedNodeId, key.Key, now, CancellationToken.None);
            try
            {
                store.DeleteLinkKey(key.KeyRef, origin);
                store.Delete(key.KeyRef, origin);
            }
            catch
            {
                // the original failure is what the person needs to see
            }
            throw;
        }
    }

    /// <summary>
    /// The signed unpair (design 3.5): <c>POST /api/nodes/link/unpair</c> with an assertion for
    /// <c>&lt;origin&gt;/api/nodes/link/unpair</c> (its <c>iss</c> names the node). Best effort: true when
    /// the server confirmed or already forgot the node, false when it could not be reached (the node then
    /// shows offline until revoked).
    /// </summary>
    public async Task<bool> UnpairAsync(string origin, string nodeId, ECDsa key, DateTimeOffset now, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, origin + UnpairPath)
            {
                Content = JsonContent.Create(new JsonObject { ["reason"] = "unpaired_by_node" }),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("LeafNode",
                NodeCrypto.Assertion(key, nodeId, origin + UnpairPath, now));
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            using var response = await _http.SendAsync(request, timeout.Token);
            return response.IsSuccessStatusCode || response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden;
        }
        catch (Exception error) when (error is HttpRequestException or OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>
    /// <c>POST /api/nodes/link/repin</c> with an assertion and the owner's re-pin code: the server link key to pin
    /// (design 1b.7). The caller then pins it with <see cref="PinAsync"/>.
    /// </summary>
    public async Task<NodeLinkKeyInfo> RepinAsync(string origin, string nodeId, ECDsa key, string code, DateTimeOffset now, CancellationToken ct)
    {
        var body = await SignedPostAsync(origin, RepinPath, nodeId, key, new JsonObject { ["code"] = code }, now, ct, "repin");
        return body?["linkKey"] is JsonObject linkKey
            ? ReadLinkKey(origin, linkKey)
            : throw new NodeCommandException("bad_response", $"{origin} answered the re-pin without a link key.");
    }

    /// <summary>
    /// <c>POST /api/nodes/link/pin</c>: confirms the pinned key with <see cref="NodeCrypto.LinkPinProof"/>. Any
    /// refusal is an error: the kernel did not record this pin, so the server would refuse plaintext and the
    /// node would not know why.
    /// </summary>
    public async Task PinAsync(string origin, string nodeId, ECDsa key, string code, NodeLinkKeyInfo info, DateTimeOffset now, CancellationToken ct)
        => await SignedPostAsync(origin, PinPath, nodeId, key, new JsonObject
        {
            ["code"] = code,
            ["fingerprint"] = info.Fingerprint,
            ["keyId"] = info.KeyId,
            ["proof"] = NodeCrypto.LinkPinProof(key, nodeId, code, info.Fingerprint),
        }, now, ct, "pin");

    private async Task<JsonObject?> SignedPostAsync(string origin, string path, string nodeId, ECDsa key, JsonObject body,
        DateTimeOffset now, CancellationToken ct, string what)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, origin + path) { Content = JsonContent.Create(body) };
        request.Headers.Authorization = new AuthenticationHeaderValue("LeafNode", NodeCrypto.Assertion(key, nodeId, origin + path, now));
        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, ct);
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            throw new NodeCommandException("server_unreachable", $"Could not reach {origin}: {error.Message}", inner: error);
        }
        using (response)
        {
            var answer = await ReadJsonAsync(response, ct);
            if (response.IsSuccessStatusCode) return answer;
            var code = ErrorCode(answer);
            throw new NodeCommandException(code ?? $"http_{(int)response.StatusCode}", code switch
            {
                "code_invalid" or "code_expired" or "code_used" or "code_burned" =>
                    "That re-pin code is not valid any more. Ask the node's owner for a new one (Leaf, Nodes, Re-pin).",
                "code_wrong_node" => "That re-pin code was made for another node.",
                "link_key_mismatch" =>
                    "The server says the link key this node received is not RedCompute's. Something on the network may have "
                    + "replaced it: nothing was pinned. Compare the fingerprints Leaf shows, then try again.",
                _ when response.StatusCode == HttpStatusCode.NotFound =>
                    $"{origin} does not support encrypted node links yet. Update Leaf, then try again.",
                _ => $"{origin} refused the {what} ({(int)response.StatusCode}{(code is null ? "" : " " + code)}).",
            });
        }
    }

    /// <summary>The kernel's <c>linkKey</c>: a valid P-256 point whose stated fingerprint, if any, is its own.</summary>
    internal static NodeLinkKeyInfo ReadLinkKey(string origin, JsonObject linkKey)
    {
        var publicKey = linkKey["publicKey"]?.GetValue<string>();
        var stated = new NodeLinkKeyInfo(NoiseProtocol.Name, publicKey ?? "", "", "");
        var bytes = publicKey is null ? null : stated.PublicKeyBytes();
        if (bytes is null)
            throw new NodeCommandException("bad_response", $"{origin} sent a link key that is not a P-256 key.");
        var protocol = linkKey["protocol"]?.GetValue<string>();
        if (protocol is not null && protocol != NoiseProtocol.Name)
            throw new NodeCommandException("link_protocol_unsupported",
                $"{origin} encrypts node links with {protocol}; this leaf-node speaks {NoiseProtocol.Name}. Update leaf-node.");
        var info = NodeLinkKeyInfo.For(bytes);
        var fingerprint = linkKey["fingerprint"]?.GetValue<string>();
        if (fingerprint is not null && !string.Equals(fingerprint, info.Fingerprint, StringComparison.OrdinalIgnoreCase))
            throw new NodeCommandException("link_key_mismatch",
                $"{origin} sent link key {info.Fingerprint} but named it {fingerprint}. Nothing was pinned.");
        return info;
    }

    private static NodeCommandException PairingRefused(string origin, HttpStatusCode status, JsonObject? body)
    {
        var code = ErrorCode(body);
        var message = code switch
        {
            "code_invalid" => "That code is not valid. Check it, or make a new one in Leaf (Nodes, Add a node).",
            "code_expired" => "That code expired (codes last 10 minutes). Make a new one in Leaf.",
            "code_used" => "That code was already used. Make a new one in Leaf.",
            "code_burned" => "Too many wrong tries for that code. Make a new one in Leaf.",
            "same_machine" => "This machine runs the Leaf server: it is already node zero and cannot also pair as a node.",
            "bad_proof" => "The server could not verify this node's key. Try again; if it repeats, update leaf-node.",
            _ when status == HttpStatusCode.TooManyRequests => "Too many pairing attempts. Wait a minute and try again.",
            _ when status == HttpStatusCode.NotFound => $"{origin} does not support nodes yet. Update Leaf, then try again.",
            _ => $"{origin} refused the pairing ({(int)status}{(code is null ? "" : " " + code)}).",
        };
        return new NodeCommandException(code ?? $"http_{(int)status}", message);
    }

    internal static string? ErrorCode(JsonObject? body)
        => body?["error"] switch
        {
            JsonValue value when value.TryGetValue<string>(out var text) => text,
            JsonObject inner => inner["code"]?.GetValue<string>(),
            _ => body?["code"] is JsonValue code && code.TryGetValue<string>(out var text) ? text : null,
        };

    private static async Task<JsonObject?> ReadJsonAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var text = await response.Content.ReadAsStringAsync(ct);
            return text.Length is 0 or > 64 * 1024 ? null : JsonNode.Parse(text) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public void Dispose() => _http.Dispose();
}
