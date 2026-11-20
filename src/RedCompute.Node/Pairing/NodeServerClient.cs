using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using RedCompute.Node.Credentials;
using RedCompute.NodeLink;

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
                };
            }
        }
        catch
        {
            if (pairedNodeId is not null)
                await UnpairAsync(origin, pairedNodeId, key.Key, now, CancellationToken.None);
            try
            {
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
