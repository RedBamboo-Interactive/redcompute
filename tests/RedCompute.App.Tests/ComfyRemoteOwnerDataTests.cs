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

    /// <summary>The forward's refusal code in an exception's chain.</summary>
    private static string? CodeOf(Exception error)
    {
        for (Exception? e = error; e is not null; e = e.InnerException)
            if (e is RedCompute.NodeLink.Forward.ForwardException forward) return forward.Code;
        return null;
    }

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

    // ================================================================= GET /history/{id}

    [Fact]
    public async Task History_is_read_only_for_the_servers_own_prompts()
    {
        await using var provider = Provider();
        var mine = await ServerJobAsync(provider); // its output came from /history/{mine}: the server's own job works
        Assert.Contains(_kit.Comfy.Requests, request => request.Target == $"/history/{mine}");

        using var http = Forward();
        Assert.True((await http.GetAsync($"/history/{mine}")).IsSuccessStatusCode);
        foreach (var target in new[] { "/history/owner-run-1", "/history", "/history?max_items=100" })
        {
            var before = _kit.Comfy.Requests.Count(request => request.Target.StartsWith("/history", StringComparison.Ordinal));
            var refused = await Assert.ThrowsAnyAsync<HttpRequestException>(() => http.GetAsync(target));
            Assert.Equal("route_not_allowed", CodeOf(refused));
            Assert.Equal(before, _kit.Comfy.Requests.Count(request => request.Target.StartsWith("/history", StringComparison.Ordinal)));
        }
    }

    // ================================================================= POST /free

    private static StringContent Json(string body) => new(body, System.Text.Encoding.UTF8, "application/json");

    [Fact]
    public async Task Free_is_refused_as_runtime_busy_while_the_owners_prompts_run_and_passes_once_they_end()
    {
        await using var provider = Provider();
        var mine = await ServerJobAsync(provider);
        using var http = Forward();
        const string body = """{"unload_models":true,"free_memory":true}""";

        // The owner's prompt runs: the node refuses, whatever the server's own check said.
        _kit.Comfy.QueueJson = OwnerQueue(mine);
        Assert.Equal("runtime_busy", CodeOf(await Assert.ThrowsAnyAsync<HttpRequestException>(() => http.PostAsync("/free", Json(body)))));
        Assert.Equal(0, _kit.Comfy.Frees);

        // Only the server's own prompt is left: /free goes on.
        _kit.Comfy.QueueJson = $$"""{"queue_running":[[8,"{{mine}}",{},{},[]]],"queue_pending":[]}""";
        Assert.True((await http.PostAsync("/free", Json(body))).IsSuccessStatusCode);
        Assert.Equal(1, _kit.Comfy.Frees);

        // A queue the node cannot read cannot show the owner idle.
        _kit.Comfy.QueueJson = "not json";
        Assert.Equal("runtime_busy", CodeOf(await Assert.ThrowsAnyAsync<HttpRequestException>(() => http.PostAsync("/free", Json(body)))));

        // The provider's own cache release, end to end, on an idle runtime.
        _kit.Comfy.QueueJson = """{"queue_running":[],"queue_pending":[]}""";
        var release = await provider.ReleaseCachedModelsAsync(CancellationToken.None);
        Assert.True(release.Success, release.Detail);
        Assert.Equal(2, _kit.Comfy.Frees);
    }

    [Theory]
    [InlineData("""{"unload_models":true,"free_memory":true,"extra":1}""")]
    [InlineData("""{"unload_models":"yes"}""")]
    [InlineData("""{"free_memory":1}""")]
    [InlineData("""{"unload_models":true,"unload_models":false}""")]
    [InlineData("""[true]""")]
    [InlineData("""not json""")]
    [InlineData("")]
    public async Task Free_takes_only_its_two_booleans(string body)
    {
        using var http = Forward();
        Assert.Equal("route_not_allowed", CodeOf(await Assert.ThrowsAnyAsync<HttpRequestException>(() => http.PostAsync("/free", Json(body)))));
        Assert.Equal(0, _kit.Comfy.Frees);
        // Either member may be left out.
        Assert.True((await http.PostAsync("/free", Json("{}"))).IsSuccessStatusCode);
        Assert.True((await http.PostAsync("/free", Json("""{"free_memory":false}"""))).IsSuccessStatusCode);
        Assert.Equal(2, _kit.Comfy.Frees);
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
