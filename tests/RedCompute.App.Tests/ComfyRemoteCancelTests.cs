using RedCompute.App.Services.Nodes;
using RedCompute.Plugin.ComfyUI;
using Xunit;

namespace RedCompute.App.Tests;

/// <summary>
/// The real ComfyUIProvider's cancel path through the node's real RuntimeForwardTarget (review P4-P7, item 4): its
/// <c>/api/jobs/{id}/cancel</c> and <c>/queue</c> delete reach the node's ComfyUI for a prompt it submitted there, and
/// nothing that names any other prompt (the owner's own) ever does.
/// </summary>
public sealed class ComfyRemoteCancelTests : IAsyncLifetime
{
    private ComfyNodeKit _kit = null!;

    public async Task InitializeAsync() => _kit = await ComfyNodeKit.StartAsync();

    public async Task DisposeAsync() => await _kit.DisposeAsync();

    private ComfyUIProvider Provider() => new(RemoteProviderConfig.For("ComfyUI", null, _kit.Runtime), "image-gen", _ => { }, null, _kit.Host)
    {
        PromptCancelTimeout = TimeSpan.FromSeconds(10),
    };

    [Fact]
    public async Task A_prompt_the_server_submitted_is_cancelled_through_the_node_and_the_owners_are_never_touched()
    {
        await using var provider = Provider();
        Assert.True((await provider.ExecuteAsync(ComfyNodeKit.Request())).Success);
        var mine = Assert.Single(_kit.Comfy.PromptIds);

        var outcome = await provider.CancelPromptAsync(mine, "test");
        Assert.True(outcome.Settled);
        Assert.Contains(_kit.Comfy.Requests, r => r.Method == "POST" && r.Target == $"/api/jobs/{mine}/cancel");
        Assert.Contains(_kit.Comfy.Requests, r => r.Method == "POST" && r.Target == "/queue" && r.Text.Contains(mine));

        // An id the server never submitted through this node: the node refuses each route that would act on it.
        await provider.CancelPromptAsync("owners-prompt-7", "test");
        Assert.DoesNotContain(_kit.Comfy.Requests, r => r.Text.Contains("owners-prompt-7"));
        Assert.Contains("route_not_allowed", _kit.Node.AuditText());
    }
}
