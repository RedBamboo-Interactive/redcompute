using System.Security.Cryptography;
using RedCompute.NodeLink.Noise;

namespace RedCompute.NodeLink.Tests;

/// <summary>
/// The keys of one encrypted node link, and the two Secure hooks over them, as P0.5b/c will wire them:
/// the server's static link key, the node's link key, and the node's paired identity (an ECDSA key whose
/// SPKI the server verifies the binding against).
/// </summary>
internal sealed class E2eKeys : IDisposable
{
    public NoiseKeyPair ServerStatic { get; } = NoiseKeyPair.Generate();
    public NoiseKeyPair NodeLink { get; } = NoiseKeyPair.Generate();
    public ECDsa NodeIdentity { get; } = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    /// <summary>What the node pinned; the server's key unless a test pins another.</summary>
    public byte[]? Pinned { get; set; }

    /// <summary>The SPKI the server checks the binding against; the node's own unless a test swaps it.</summary>
    public byte[]? IdentitySpki { get; set; }

    public bool AllowPlaintext { get; set; }
    public long RekeyEvery { get; set; } = NoiseLinkTransport.DefaultRekeyEvery;

    public int Encrypted;
    public int Plaintext;

    public static byte[] Sign(ECDsa identity, NoiseBindingContext binding) => identity.SignData(binding.Message(), HashAlgorithmName.SHA256);

    public static bool Verify(byte[] spki, NoiseBindingContext binding, byte[] signature)
    {
        using var key = ECDsa.Create();
        key.ImportSubjectPublicKeyInfo(spki, out _);
        return key.VerifyData(binding.Message(), signature, HashAlgorithmName.SHA256);
    }

    public Func<ILinkTransport, CancellationToken, Task<ILinkTransport>> ClientSecure(string nodeId)
        => async (transport, ct) => await Noise.NoiseLink.InitiateAsync(transport, new NoiseInitiatorOptions
        {
            NodeId = nodeId,
            LocalStatic = NodeLink,
            ServerStatic = Pinned ?? ServerStatic.PublicKey,
            Binding = binding => Sign(NodeIdentity, binding),
            RekeyEvery = RekeyEvery,
        }, ct);

    public Func<ILinkTransport, string, CancellationToken, Task<ILinkTransport>> ServerSecure => async (transport, nodeId, ct) =>
    {
        var result = await Noise.NoiseLink.AcceptAsync(transport, ResponderOptions(nodeId), ct);
        if (result.Encrypted) Interlocked.Increment(ref Encrypted);
        else Interlocked.Increment(ref Plaintext);
        return result.Transport;
    };

    public NoiseResponderOptions ResponderOptions(string nodeId) => new()
    {
        NodeId = nodeId,
        LocalStatics = [ServerStatic],
        Verify = (binding, signature, _) => ValueTask.FromResult(Verify(IdentitySpki ?? NodeIdentity.ExportSubjectPublicKeyInfo(), binding, signature)),
        AllowPlaintext = _ => ValueTask.FromResult(AllowPlaintext),
        RekeyEvery = RekeyEvery,
    };

    public void Dispose()
    {
        ServerStatic.Dispose();
        NodeLink.Dispose();
        NodeIdentity.Dispose();
    }
}

/// <summary>
/// The kernel's bridge in miniature: copies every message both ways, unchanged, and keeps a copy of
/// each, so a test can assert what a relay (the kernel, Cloudflare) gets to see.
/// </summary>
internal sealed class RecordingRelay
{
    private readonly List<(bool FromNode, byte[] Message)> _messages = [];

    public IReadOnlyList<(bool FromNode, byte[] Message)> Messages { get { lock (_messages) return _messages.ToList(); } }

    public Task<ILinkTransport> DialAsync(Func<ILinkTransport, Task> accept)
    {
        var (node, relayNode) = LinkPipe.Create();
        var (relayServer, server) = LinkPipe.Create();
        _ = Task.Run(() => accept(server));
        _ = Task.Run(() => PumpAsync(relayNode, relayServer, fromNode: true));
        _ = Task.Run(() => PumpAsync(relayServer, relayNode, fromNode: false));
        return Task.FromResult(node);
    }

    private async Task PumpAsync(ILinkTransport from, ILinkTransport to, bool fromNode)
    {
        try
        {
            while (true)
            {
                var message = await from.ReceiveAsync(CancellationToken.None);
                lock (_messages) _messages.Add((fromNode, message));
                await to.SendAsync(message, CancellationToken.None);
            }
        }
        catch (LinkClosedException closed)
        {
            await to.CloseAsync(closed.Code ?? LinkCloseCodes.Abnormal, closed.Reason, CancellationToken.None);
            await from.CloseAsync(closed.Code ?? LinkCloseCodes.Abnormal, closed.Reason, CancellationToken.None);
        }
    }
}
