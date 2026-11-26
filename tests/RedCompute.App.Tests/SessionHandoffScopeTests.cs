using System.Text.Json;
using RedCompute.App.Services.SessionBindings;
using RedCompute.PluginSdk;
using Xunit;

namespace RedCompute.App.Tests;

public sealed class SessionHandoffScopeTests
{
    [Fact]
    public void A_return_replaces_only_the_old_handoff_and_never_changes_another_runtime()
    {
        var original = new SessionHandoff("runtime-a", "switch-1", """{"turns":["prior"]}""");
        var returning = new SessionHandoff("runtime-a", "switch-2", """{"turns":["intervening"]}""");
        var policy = "These are the authorized application instructions.";
        using (SessionHandoffScope.Push(returning))
        {
            Assert.Null(SessionHandoffScope.For("runtime-b"));
            var applied = SessionHandoffScope.For("runtime-a")!.WithInstructions(original.WithInstructions(policy));
            Assert.StartsWith(policy, applied);
            Assert.Contains("intervening", applied);
            Assert.DoesNotContain("prior", applied[(applied.IndexOf('{'))..]);
            Assert.True(SessionHandoff.HasHandoff(applied));
            Assert.False(SessionHandoff.HasHandoff(policy));
        }
        Assert.Null(SessionHandoffScope.Current);
        Assert.Throws<ArgumentException>(() => SessionHandoffScope.Push(returning with { RuntimeSessionId = "../escape" }));
    }

    [Fact]
    public void A_prepared_runtime_id_is_stable_and_only_authorized_artifact_references_at_the_cutoff_are_included()
    {
        var target = new BindingTarget("codex", "codex-default", "local", "m", "low");
        var source = new SessionBinding(0, new NativeSessionBinding("runtime-a", "native-a", target), 0);
        var move = new SessionBindingMove("move-1", 0, target, 5, BindingMoveState.Pending);
        var request = new BindingPreparation("logical", move, source, null, [new("assistant", "turn", "Open work survives.")]);
        Assert.Equal(NativeSessionBindingPreparer.RuntimeId(request), NativeSessionBindingPreparer.RuntimeId(request));
        Assert.NotEqual(NativeSessionBindingPreparer.RuntimeId(request),
            NativeSessionBindingPreparer.RuntimeId(request with { Move = move with { Id = "move-2" } }));
        Assert.Equal("runtime-a", NativeSessionBindingPreparer.RuntimeId(request with { PreviousDestination = source }));

        using var doc = JsonDocument.Parse(NativeSessionBindingPreparer.HandoffJson(request,
            [new(3, "file", "summary.txt"), new(7, "file", "future.txt")]));
        Assert.Equal("Open work survives.", doc.RootElement.GetProperty("turns")[0].GetProperty("content").GetString());
        var artifact = Assert.Single(doc.RootElement.GetProperty("artifacts").EnumerateArray());
        Assert.Equal("summary.txt", artifact.GetProperty("name").GetString());
    }
}
