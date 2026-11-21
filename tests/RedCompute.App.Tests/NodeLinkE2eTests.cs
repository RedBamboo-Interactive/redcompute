using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using RedBamboo.AppHost.Auth;
using RedCompute.App.Services.Nodes;
using RedCompute.Core.Nodes;
using RedCompute.NodeLink;
using RedCompute.NodeLink.Noise;
using Xunit;
using static RedCompute.App.Tests.NodeTestKit;

namespace RedCompute.App.Tests;

/// <summary>
/// NodeHub's end of the encrypted node link (Phase 4 P0.5b): RedCompute's link key, the identity binding
/// checked against the SPKI the kernel lists, no plaintext from a node whose link must be encrypted, and the
/// kernel-only <c>GET /nodes/link-key</c>. A node that never pinned the key links in plaintext as before.
/// </summary>
public sealed class NodeLinkE2eTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rc-link-e2e-" + Guid.NewGuid().ToString("N"));
    private readonly string _node = RemoteId(1);
    private readonly ECDsa _identity = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly NoiseKeyPair _nodeLinkKey = NoiseKeyPair.Generate();
    private NodeRegistry _registry = null!;
    private NodeLinkKeyStore _keys = null!;
    private NodeHub _hub = null!;

    public Task InitializeAsync()
    {
        _registry = Registry(Path.Combine(_root, "nodes"));
        _keys = new NodeLinkKeyStore(Path.Combine(_root, "node-link-key"), _ => { });
        _hub = new NodeHub(_registry, _ => { }, new NodeHubOptions { LinkKeys = _keys });
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _hub.DisposeAsync();
        _keys.Dispose();
        _identity.Dispose();
        _nodeLinkKey.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private byte[] Spki => _identity.ExportSubjectPublicKeyInfo();

    private KernelNode Paired(bool required = true, byte[]? spki = null)
        => Kernel(_node) with { PublicKeySpki = spki ?? Spki, LinkE2eRequired = required };

    /// <summary>The agent's side as P0.5c wires it: the node's link key, the pinned server key, its identity signing the binding.</summary>
    private Func<ILinkTransport, CancellationToken, Task<ILinkTransport>> Pinned(byte[]? serverKey = null, ECDsa? identity = null)
        => async (transport, ct) => await NoiseLink.InitiateAsync(transport, new NoiseInitiatorOptions
        {
            NodeId = _node,
            LocalStatic = _nodeLinkKey,
            ServerStatic = serverKey ?? _keys.Keys()[0].PublicKey,
            Binding = binding => NoiseBinding.Payload(binding,
                bytes => (identity ?? _identity).SignData(bytes, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)),
        }, ct);

    private static async Task WaitAsync(Func<bool> condition, string what, int ms = 5_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(ms);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Timed out waiting for " + what);
            await Task.Delay(10);
        }
    }

    // ================================================================= the link key

    [Fact]
    public void The_link_key_is_created_once_and_reopens_as_the_same_key()
    {
        var first = _keys.Describe();
        using var reopened = new NodeLinkKeyStore(_keys.Directory, _ => { });
        Assert.Equal(first.PublicKey, reopened.Describe().PublicKey);
        Assert.Equal(NoiseProtocol.Name, first.Protocol);
        Assert.Matches("^lk_[0-9a-f]{12}$", first.KeyId);
        Assert.Equal(NoiseKeyPair.Fingerprint(_keys.Keys()[0].PublicKey), first.Fingerprint);
    }

    [Fact]
    public void The_private_key_is_never_stored_in_the_clear_on_windows()
    {
        _keys.Describe();
        var file = Path.Combine(_keys.Directory, NodeLinkKeyStore.CurrentFile);
        var document = JsonNode.Parse(File.ReadAllText(file))!;
        var pkcs8 = _keys.Keys()[0].ExportPkcs8();
        var stored = Convert.FromBase64String(document["privateKey"]!.GetValue<string>());
        if (OperatingSystem.IsWindows())
        {
            Assert.Equal("dpapi-user", document["protection"]!.GetValue<string>());
            Assert.False(stored.AsSpan().SequenceEqual(pkcs8));
            Assert.True(stored.AsSpan().IndexOf(pkcs8.AsSpan(pkcs8.Length - 32)) < 0, "the scalar appears in the file");
        }
        else
        {
            Assert.Equal("file-0600", document["protection"]!.GetValue<string>());
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(file));
        }
    }

    [Fact]
    public void A_key_file_that_does_not_open_is_an_error_never_a_silent_new_key()
    {
        _keys.Describe();
        var file = Path.Combine(_keys.Directory, NodeLinkKeyStore.CurrentFile);
        var damaged = File.ReadAllText(file).Replace("\"privateKey\":\"", "\"privateKey\":\"AAAA");
        File.WriteAllText(file, damaged);
        using var reopened = new NodeLinkKeyStore(_keys.Directory, _ => { });
        Assert.ThrowsAny<Exception>(() => reopened.Keys());
        Assert.Equal(damaged, File.ReadAllText(file));
    }

    [Fact]
    public void A_previous_key_is_still_accepted_after_a_rotation()
    {
        var old = _keys.Describe();
        File.Move(Path.Combine(_keys.Directory, NodeLinkKeyStore.CurrentFile), Path.Combine(_keys.Directory, NodeLinkKeyStore.PreviousFile));
        using var rotated = new NodeLinkKeyStore(_keys.Directory, _ => { });
        var keys = rotated.Keys();
        Assert.Equal(2, keys.Count);
        Assert.NotEqual(old.PublicKey, rotated.Describe().PublicKey);
        Assert.Equal(old.PublicKey, NodeLinkKeyInfo.Base64Url(keys[1].PublicKey));
    }

    // ================================================================= kernel and registry

    [Fact]
    public void The_kernel_list_carries_the_identity_key_and_the_e2e_flag()
    {
        var spki = Spki;
        var parsed = KernelNode.Parse(new JsonObject
        {
            ["id"] = _node, ["kind"] = "remote", ["status"] = "online", ["ownerId"] = Owner,
            ["publicKeySpki"] = NodeLinkKeyInfo.Base64Url(spki), ["linkE2e"] = "required",
        })!;
        Assert.Equal(spki, parsed.PublicKeySpki);
        Assert.True(parsed.LinkE2eRequired);

        var standardBase64 = KernelNode.Parse(new JsonObject { ["id"] = _node, ["kind"] = "remote", ["publicKeySpki"] = Convert.ToBase64String(spki) })!;
        Assert.Equal(spki, standardBase64.PublicKeySpki);
        var older = KernelNode.Parse(new JsonObject { ["id"] = _node, ["kind"] = "remote" })!;
        Assert.Null(older.PublicKeySpki);
        Assert.False(older.LinkE2eRequired);
        var garbage = KernelNode.Parse(new JsonObject { ["id"] = _node, ["kind"] = "remote", ["publicKeySpki"] = "bm90IGEga2V5" })!;
        Assert.Null(garbage.PublicKeySpki);
    }

    [Fact]
    public void Required_only_ever_turns_on_and_survives_a_restart_from_the_cache()
    {
        _registry.ApplyKernelNodes([Paired(required: true)]);
        _registry.ApplyKernelNodes([Kernel(_node)]); // an older kernel, or one that stopped saying it
        Assert.Equal(new NodeLinkSecurity(Spki, true), _registry.LinkSecurity(_node), new SecurityComparer());
        Assert.True(_registry.Find(_node)!.E2eRequired);

        var restarted = Registry(Path.Combine(_root, "nodes"));
        Assert.Equal(new NodeLinkSecurity(Spki, true), restarted.LinkSecurity(_node), new SecurityComparer());
    }

    [Fact]
    public void Seen_encrypted_is_kept_across_a_restart()
    {
        _registry.ApplyKernelNodes([Paired(required: false)]);
        Assert.False(_registry.LinkSecurity(_node)!.E2eRequired);
        _registry.MarkSeenEncrypted(_node);
        var restarted = Registry(Path.Combine(_root, "nodes"));
        Assert.True(restarted.LinkSecurity(_node)!.E2eRequired);
    }

    private sealed class SecurityComparer : IEqualityComparer<NodeLinkSecurity?>
    {
        public bool Equals(NodeLinkSecurity? a, NodeLinkSecurity? b)
            => a is not null && b is not null && a.E2eRequired == b.E2eRequired
               && (a.IdentitySpki ?? []).AsSpan().SequenceEqual(b.IdentitySpki ?? []);

        public int GetHashCode(NodeLinkSecurity? value) => 0;
    }

    // ================================================================= NodeHub

    [Fact]
    public async Task A_node_that_never_pinned_the_key_still_links_in_plaintext()
    {
        _registry.ApplyKernelNodes([Kernel(_node)]);
        await using var node = new PipedNode(_hub, _node);
        await WaitAsync(() => _registry.Find(_node)?.Status == NodeStatus.Online, "the link");
        Assert.False(_registry.Find(_node)!.Link!.Encrypted);
        Assert.False(_registry.Find(_node)!.E2eRequired);
    }

    [Fact]
    public async Task A_pinned_node_links_encrypted_and_from_then_on_plaintext_from_it_is_refused()
    {
        _registry.ApplyKernelNodes([Paired(required: false)]);
        await using (var node = new PipedNode(_hub, _node, secure: Pinned()))
        {
            await WaitAsync(() => _registry.Find(_node)?.Status == NodeStatus.Online, "the encrypted link");
            Assert.True(_registry.Find(_node)!.Link!.Encrypted);
            Assert.True(_registry.Find(_node)!.E2eRequired);
            var session = _hub.Server.FindSession(_node)!;
            Assert.Equal(JsonValueKind.Object, (await session.CallAsync(NodeHub.DescribeMethod)).ValueKind);
        }
        await WaitAsync(() => _registry.Find(_node)?.Status == NodeStatus.Offline, "the link to go down");

        await using var downgraded = new PipedNode(_hub, _node);
        await WaitAsync(() => downgraded.Client.Status == LinkClientStatus.NeedsRepin, "the refusal");
        Assert.Contains(NoiseLink.E2eRequired, downgraded.Client.StatusReason);
        Assert.NotEqual(NodeStatus.Online, _registry.Find(_node)!.Status);
    }

    [Fact]
    public async Task Plaintext_from_a_node_the_kernel_says_is_encrypted_is_refused_4496()
    {
        _registry.ApplyKernelNodes([Paired(required: true)]);
        await using var node = new PipedNode(_hub, _node);
        await WaitAsync(() => node.Client.Status == LinkClientStatus.NeedsRepin, "the refusal");
        Assert.Contains("4496", node.Client.StatusReason);
        Assert.Null(_hub.Server.FindSession(_node));
    }

    [Fact]
    public async Task A_binding_by_another_identity_is_refused_4497()
    {
        _registry.ApplyKernelNodes([Paired()]);
        using var stranger = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        await using var node = new PipedNode(_hub, _node, secure: Pinned(identity: stranger));
        await WaitAsync(() => node.Client.Status == LinkClientStatus.NeedsRepin, "the refusal");
        Assert.Contains(NoiseLink.IdentityInvalid, node.Client.StatusReason);
        Assert.Null(_hub.Server.FindSession(_node));
    }

    [Fact]
    public async Task A_node_pinned_to_another_key_is_refused_4495_and_keeps_retrying_with_its_pin()
    {
        _registry.ApplyKernelNodes([Paired()]);
        using var other = NoiseKeyPair.Generate();
        await using var node = new PipedNode(_hub, _node, secure: Pinned(serverKey: other.PublicKey));
        await WaitAsync(() => node.Client.Status == LinkClientStatus.NeedsRepin, "the refusal");
        Assert.Contains(NoiseLink.KeyUnknown, node.Client.StatusReason);
        Assert.Null(_hub.Server.FindSession(_node));
    }

    [Fact]
    public async Task Until_the_kernel_lists_the_identity_key_an_encrypted_node_is_asked_to_retry_not_refused()
    {
        _registry.ApplyKernelNodes([Kernel(_node)]); // no SPKI yet
        await using var node = new PipedNode(_hub, _node, secure: Pinned());
        await WaitAsync(() => node.Client.StatusReason?.Contains("link_identity_unavailable") == true, "the retry");
        Assert.NotEqual(LinkClientStatus.NeedsRepin, node.Client.Status);

        _registry.ApplyKernelNodes([Paired()]);
        await WaitAsync(() => _registry.Find(_node)?.Status == NodeStatus.Online, "the link once the kernel lists the key");
        Assert.True(_registry.Find(_node)!.Link!.Encrypted);
    }

    [Fact]
    public async Task A_hub_without_a_link_key_does_not_answer_a_handshake_and_the_node_never_falls_back()
    {
        await using var plainHub = new NodeHub(_registry, _ => { });
        _registry.ApplyKernelNodes([Paired()]);
        await using var node = new PipedNode(plainHub, _node, secure: Pinned());
        await WaitAsync(() => node.Client.Status == LinkClientStatus.NeedsUpdate, "needs update");
        Assert.Contains(NoiseLink.ServerUnsupported, node.Client.StatusReason);
        Assert.Equal(0, node.Client.Connections);
    }

    [Fact]
    public async Task The_nodes_view_says_whether_a_node_s_link_is_encrypted()
    {
        _registry.ApplyKernelNodes([Paired(required: true)]);
        await using var node = new PipedNode(_hub, _node, secure: Pinned());
        await WaitAsync(() => _registry.Find(_node)?.Status == NodeStatus.Online, "the link");
        var view = await Api.Endpoints.NodeEndpoints.BuildAsync(_registry, _registry.Find(_node)!, null!, null);
        Assert.Equal("required", view.Encryption);
        Assert.True(view.Link!.Encrypted);
    }

    // ================================================================= GET /nodes/link-key

    private async Task<(int Status, string Body)> GetLinkKeyAsync(System.Security.Claims.ClaimsPrincipal user)
    {
        var app = WebApplication.CreateSlimBuilder().Build();
        NodeHub.MapLinkKeyRoute(app, _keys);
        var endpoint = ((IEndpointRouteBuilder)app).DataSources.SelectMany(d => d.Endpoints).OfType<RouteEndpoint>()
            .Single(e => e.RoutePattern.RawText == NodeHub.LinkKeyRoute);
        Assert.NotNull(endpoint.Metadata.GetMetadata<Microsoft.AspNetCore.Routing.IExcludeFromDescriptionMetadata>());
        var context = new DefaultHttpContext { RequestServices = app.Services, User = user };
        context.Connection.RemoteIpAddress = IPAddress.Loopback;
        context.Response.Body = new MemoryStream();
        await endpoint.RequestDelegate!(context);
        context.Response.Body.Position = 0;
        return (context.Response.StatusCode, await new StreamReader(context.Response.Body).ReadToEndAsync());
    }

    [Fact]
    public async Task Only_the_kernel_reads_the_link_key_and_it_is_hidden_from_discovery()
    {
        var jwt = new JwtService(JwtOptions());
        var (status, body) = await GetLinkKeyAsync(RedLeafService(jwt));
        Assert.Equal(200, status);
        var key = JsonNode.Parse(body)!;
        Assert.Equal(_keys.Describe().PublicKey, key["publicKey"]!.GetValue<string>());
        Assert.Equal(_keys.Describe().Fingerprint, key["fingerprint"]!.GetValue<string>());
        Assert.DoesNotContain("privateKey", body);

        foreach (var user in new[]
                 {
                     LocalDefault(),
                     jwt.ValidateToken(jwt.GenerateAccessToken(Owner, "owner@example.test", "Owner", ["admin"]))!,
                     jwt.ValidateToken(jwt.GenerateServiceAccessToken("redcompute", roles: ["service", "admin"]))!,
                 })
            Assert.Equal(403, (await GetLinkKeyAsync(user)).Status);
    }
}
