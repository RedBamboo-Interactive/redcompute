using System.Text.Json;
using System.Text.Json.Nodes;
using RedCompute.App.Services.Nodes;
using RedCompute.Plugin.ComfyUI;
using Xunit;

namespace RedCompute.App.Tests;

/// <summary>
/// What the server may read of the node owner's own ComfyUI work (review item 5), through the real forward (NodeHub, the
/// link, the node's real RuntimeForwardTarget) against a fake ComfyUI holding the owner's prompts and files. Each route
/// redacts or refuses the owner's data, and the real ComfyUIProvider's own job still works over the same routes.
/// </summary>
public sealed class ComfyRemoteOwnerDataTests : IAsyncLifetime
{
    private const string OwnerSecret = "owner-secret-7c1d";
    private ComfyNodeKit _kit = null!;

    public async Task InitializeAsync() => _kit = await ComfyNodeKit.StartAsync();

    public async Task DisposeAsync() => await _kit.DisposeAsync();

    private ComfyUIProvider Provider() => new(RemoteProviderConfig.For("ComfyUI", null, _kit.Runtime), "image-gen", _ => { }, null, _kit.Host)
    {
        PromptCancelTimeout = TimeSpan.FromSeconds(10),
    };

    /// <summary>The server's raw view of the runtime: an HTTP client over the node's forward.</summary>
    private HttpClient Forward() => new(_kit.Host.Network.CreateServiceHandler(new Uri($"http://127.0.0.1:{_kit.Runtime.Ports[0]}/")))
    {
        BaseAddress = new Uri($"http://127.0.0.1:{_kit.Runtime.Ports[0]}/"),
    };

    /// <summary>A job of the server's own, run end to end: its prompt id.</summary>
    private async Task<string> ServerJobAsync(ComfyUIProvider provider)
    {
        var result = await provider.ExecuteAsync(ComfyNodeKit.Request()).WaitAsync(TimeSpan.FromSeconds(60));
        Assert.True(result!.Success, result.ErrorMessage);
        return _kit.Comfy.PromptIds.Last();
    }

    private static string OwnerQueue(string? serverPrompt = null) => new JsonObject
    {
        ["queue_running"] = new JsonArray(new JsonArray(7, "owner-run-1",
            new JsonObject { ["3"] = new JsonObject { ["inputs"] = new JsonObject { ["text"] = OwnerSecret } } },
            new JsonObject { ["client_id"] = "owner-client", ["extra_pnginfo"] = OwnerSecret }, new JsonArray("9"))),
        ["queue_pending"] = serverPrompt is null
            ? new JsonArray()
            : new JsonArray(new JsonArray(8, serverPrompt, new JsonObject(), new JsonObject(), new JsonArray()),
                new JsonArray(9, "owner-pend-2", new JsonObject { ["x"] = OwnerSecret }, new JsonObject(), new JsonArray())),
        ["owner_note"] = OwnerSecret,
    }.ToJsonString();

    // ================================================================= GET /queue

    [Fact]
    public async Task The_queue_shows_the_servers_prompts_by_id_and_the_owners_only_as_other()
    {
        await using var provider = Provider();
        var mine = await ServerJobAsync(provider);
        _kit.Comfy.QueueJson = OwnerQueue(mine);

        using var http = Forward();
        var text = await http.GetStringAsync("/queue");

        Assert.DoesNotContain(OwnerSecret, text);
        Assert.DoesNotContain("owner-run-1", text);
        Assert.DoesNotContain("owner-pend-2", text);
        Assert.DoesNotContain("owner-client", text);
        var queue = JsonNode.Parse(text)!.AsObject();
        Assert.Equal(["queue_running", "queue_pending"], queue.Select(member => member.Key));
        Assert.Equal("""[[7,"other"]]""", queue["queue_running"]!.ToJsonString());
        Assert.Equal($$"""[[8,"{{mine}}"],[9,"other"]]""", queue["queue_pending"]!.ToJsonString());
    }

    [Fact]
    public async Task The_provider_still_reads_the_redacted_queue_for_its_busy_check_and_its_cancel()
    {
        await using var provider = Provider();
        var mine = await ServerJobAsync(provider);

        // The owner's prompts still count: the cache release is refused while they run.
        _kit.Comfy.QueueJson = OwnerQueue(mine);
        var release = await provider.ReleaseCachedModelsAsync(CancellationToken.None);
        Assert.False(release.Success);
        Assert.Contains("1 running, 2 pending", release.Detail);

        // Its own prompt is still found by id: once it leaves the queue, its cancel is settled.
        _kit.Comfy.QueueJson = OwnerQueue();
        var cancel = await provider.CancelPromptAsync(mine, "test");
        Assert.True(cancel.Settled);
    }
}
