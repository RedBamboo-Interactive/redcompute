using System.Text.Json;
using RedCompute.NodeLink.Noise;
using Xunit;

namespace RedCompute.NodeLink.Tests;

public sealed class SessionRuntimeGrantTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
    private static readonly SessionRuntimeScope Scope = new("node-1", "runtime-1", "session-1", 2, "owner-1", "workspace-1");

    [Fact]
    public void A_reloaded_controller_key_signs_for_the_existing_node_pin()
    {
        using var paired = NoiseKeyPair.Generate();
        using var reloaded = NoiseKeyPair.FromPkcs8(paired.ExportPkcs8());
        var request = Issue(reloaded);
        var serialized = JsonSerializer.SerializeToUtf8Bytes(request);
        var received = JsonSerializer.Deserialize<SessionRuntimeGrant>(serialized)!;

        Assert.True(Verify(received, paired));
        Assert.Equal(Scope, received.Scope);
        Assert.Equal("command-1", received.RequestId);
    }

    [Fact]
    public void Grant_verification_binds_the_pin_route_scope_payload_and_expiry()
    {
        using var paired = NoiseKeyPair.Generate();
        using var replaced = NoiseKeyPair.Generate();
        var request = Issue(paired);

        Assert.True(Verify(request, paired));
        Assert.False(Verify(request, replaced));
        Assert.False(Verify(request, paired, nodeId: "node-2"));
        Assert.False(Verify(request, paired, runtimeId: "runtime-2"));
        Assert.False(Verify(request, paired, operation: SessionRuntimeOperation.Resume));
        Assert.False(Verify(request, paired, now: Now.AddSeconds(30)));
        Assert.False(Verify(request, paired, now: Now.AddMinutes(-5)));
        Assert.False(Verify(request with { Scope = Scope with { Generation = 3 } }, paired));
        Assert.False(Verify(request with { Scope = Scope with { OwnerId = "owner-2" } }, paired));
        Assert.False(Verify(request with { Scope = Scope with { AccessScope = "workspace-2" } }, paired));
        Assert.False(Verify(request with { RequestId = "command-2" }, paired));
        Assert.False(Verify(request with { Payload = "other"u8.ToArray() }, paired));
        Assert.False(Verify(request with { Signature = new byte[64] }, paired));
    }

    private static SessionRuntimeGrant Issue(NoiseKeyPair key) =>
        SessionRuntimeGrant.Issue(key, Scope, SessionRuntimeOperation.Start, "command-1", Now.AddSeconds(30), "{}"u8.ToArray());

    private static bool Verify(SessionRuntimeGrant grant, NoiseKeyPair key, string nodeId = "node-1",
        string runtimeId = "runtime-1", SessionRuntimeOperation operation = SessionRuntimeOperation.Start, DateTimeOffset? now = null) =>
        grant.Verify(key.PublicKey, nodeId, runtimeId, operation, now ?? Now);
}
