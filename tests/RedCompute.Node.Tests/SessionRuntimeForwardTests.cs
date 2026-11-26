using System.Security.Cryptography;
using System.Text.Json;
using RedCompute.Node.Runtimes;
using RedCompute.NodeLink;
using RedCompute.NodeLink.Forward;
using RedCompute.NodeLink.Noise;
using RedCompute.PluginSdk.Hosting;
using Xunit;

namespace RedCompute.Node.Tests;

public sealed class SessionRuntimeForwardTests
{
    [Fact]
    public async Task Only_a_command_signed_for_the_current_pairing_and_route_reaches_the_runtime()
    {
        const string runtimeId = "sessions";
        const string startPath = "/ai-session/v1/start";
        using var home = new NodeHome("session-runtime-forward");
        using var signer = NoiseKeyPair.Generate();
        using var rotated = NoiseKeyPair.Generate();
        await using var upstream = await FakeComfy.StartAsync();
        new RuntimeRegistry(home.Host, home.Paths).Add(new RuntimeRegistration
        {
            Id = runtimeId, Kind = SessionRuntimeProtocol.Kind, Port = upstream.Port, Managed = HostRuntimeManagement.Attach,
        });
        await using var runtimes = new RuntimeManager(home.Environment());
        runtimes.Load();
        await runtimes.ProbeOnceAsync(CancellationToken.None);
        Assert.Equal(SessionRuntimeProtocol.HealthPath, Assert.Single(upstream.Requests).Target);
        upstream.Requests.Clear();

        var now = TimeProvider.System.GetUtcNow();
        var pairing = Pairing(signer, now);
        var target = new RuntimeForwardTarget(runtimes, activePairing: () => pairing);
        var grant = SessionRuntimeGrant.Issue(signer, new("node-1", runtimeId, "session-1", 0, "owner-1", "workspace-1"),
            SessionRuntimeOperation.Start, "command-1", now.AddSeconds(50), "{}"u8.ToArray());

        Assert.Equal(ForwardErrorCodes.RouteNotAllowed, target.Decide(ForwardOpen.Http(runtimeId, "POST",
            "/ai-session/v1/shell", "", [], 3)).RefusalCode);
        Assert.Equal(ForwardErrorCodes.RouteNotAllowed, target.Decide(ForwardOpen.Http(runtimeId, "POST",
            startPath, "x=1", [], 3)).RefusalCode);
        Assert.Equal(ForwardErrorCodes.RouteNotAllowed, target.Decide(ForwardOpen.Http(runtimeId, "GET",
            startPath, "", [], 0)).RefusalCode);

        var operation = await Assert.ThrowsAsync<ForwardTargetException>(() => ExchangeAsync(startPath, grant with { Signature = [] }));
        Assert.Equal(ForwardErrorCodes.RouteNotAllowed, operation.Code);
        await Assert.ThrowsAsync<ForwardTargetException>(() => ExchangeAsync("/ai-session/v1/resume", grant));
        Assert.Empty(upstream.Requests);

        await using var reply = await ExchangeAsync(startPath, grant);
        Assert.Equal(200, reply.Status);
        var seen = Assert.Single(upstream.Requests);
        Assert.Equal(startPath, seen.Target);
        Assert.Equal("application/json", seen.Headers["content-type"]);
        Assert.False(seen.Headers.ContainsKey("authorization"));
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(grant))), seen.BodySha256);

        pairing = Pairing(rotated, now);
        await Assert.ThrowsAsync<ForwardTargetException>(() => ExchangeAsync(startPath, grant));
        pairing = null;
        var revoked = target.Decide(ForwardOpen.Http(runtimeId, "POST", startPath, "", [], 3));
        Assert.Equal(ForwardErrorCodes.RouteNotAllowed, revoked.RefusalCode);
        Assert.Single(upstream.Requests);

        async Task<ForwardUpstreamResponse> ExchangeAsync(string path, SessionRuntimeGrant request)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(request);
            var open = ForwardOpen.Http(runtimeId, "POST", path, "",
                [new("authorization", "Bearer test-header-must-be-stripped")], bytes.Length);
            var decision = target.Decide(open);
            Assert.True(decision.Accepted, decision.RefusalMessage);
            using var body = new MemoryStream(bytes);
            return await target.ExchangeAsync(open, decision, body, CancellationToken.None);
        }
    }

    private static NodePairing? Pairing(NoiseKeyPair key, DateTimeOffset now)
    {
        var pin = NodeLinkKeyInfo.For(key.PublicKey);
        return new NodePairing
        {
            NodeId = "node-1", ServerOrigin = "https://leaf.invalid", LinkPath = "/nodes/link",
            KeyRef = "fixture", CredentialStore = "fixture", Fingerprint = "fixture", PairedAt = now,
            LinkPin = new NodeLinkPin(pin.PublicKey, pin.Fingerprint, pin.KeyId, now),
        };
    }
}
