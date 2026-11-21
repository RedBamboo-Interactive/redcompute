using RedCompute.App.Services.Nodes;
using RedCompute.Plugin.ComfyUI;
using Xunit;

namespace RedCompute.App.Tests;

/// <summary>
/// What a remote node's ComfyUI sends back is bounded on the server (review P4-P7, item 1): <c>/view</c> at the output
/// cap, every JSON answer at the JSON cap. Over it the exchange is aborted (the runtime stops writing) and the job fails
/// saying which answer and which cap. The answers come from a ComfyUI that streams without end, through the real
/// forward (NodeHub, the link, the node's RuntimeForwardTarget).
/// </summary>
public sealed class ComfyRemoteCapTests : IAsyncLifetime
{
    private ComfyNodeKit _kit = null!;
    private readonly List<string> _log = [];

    public async Task InitializeAsync() => _kit = await ComfyNodeKit.StartAsync();

    public async Task DisposeAsync() => await _kit.DisposeAsync();

    private ComfyUIProvider Provider() => new(RemoteProviderConfig.For("ComfyUI", null, _kit.Runtime), "image-gen",
        line => { lock (_log) _log.Add(line); }, null, _kit.Host)
    {
        MaxOutputBytes = 1L << 20,
        MaxJsonBytes = 64L << 10,
    };

    [Fact]
    public async Task A_normal_job_runs_end_to_end_over_the_forward()
    {
        await using var provider = Provider();
        var result = await provider.ExecuteAsync(ComfyNodeKit.Request());

        Assert.True(result!.Success, result.ErrorMessage);
        var output = new MemoryStream();
        await result.OutputStream!.CopyToAsync(output);
        Assert.Equal(_kit.Comfy.Output, output.ToArray());
    }

    [Theory]
    [InlineData("/view", "1 MiB")]
    [InlineData("/history", "64 KiB")]
    public async Task An_endless_answer_is_cut_at_its_cap_and_the_job_fails_saying_so(string path, string cap)
    {
        _kit.Comfy.Endless = path;
        await using var provider = Provider();

        var result = await provider.ExecuteAsync(ComfyNodeKit.Request()).WaitAsync(TimeSpan.FromSeconds(60));

        Assert.False(result!.Success);
        Assert.Contains(path, result.ErrorMessage);
        Assert.Contains($"{cap} cap", result.ErrorMessage);
        // The exchange was aborted: the runtime saw the server go away, and the node had forwarded no more than its
        // channel's window and buffers (8 MiB at most) past the cap, nothing like a gigabyte.
        await _kit.Comfy.EndlessAborted.Task.WaitAsync(TimeSpan.FromSeconds(30));
        var exchange = await ForwardedAsync(path);
        Assert.NotEqual("ok", exchange.Outcome);
        Assert.True(exchange.ResponseBytes < 16L << 20, $"the node forwarded {exchange.ResponseBytes} bytes of {path}");
    }

    [Fact]
    public async Task An_endless_queue_is_refused_at_the_json_cap_before_a_cache_release()
    {
        _kit.Comfy.Endless = "/queue";
        await using var provider = Provider();

        var release = await provider.ReleaseCachedModelsAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(60));

        Assert.False(release.Success);
        Assert.Contains("/queue", release.Detail);
        // The node reads a /queue answer whole to redact it (review item 5), so its own cap cuts an endless one before
        // anything reaches the server's JSON cap.
        Assert.Contains("over 32 MiB", release.Detail);
        await _kit.Comfy.EndlessAborted.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.NotEqual("ok", (await ForwardedAsync("/queue")).Outcome);
    }

    private async Task<RedCompute.NodeLink.Forward.ForwardAudit> ForwardedAsync(string path)
    {
        RedCompute.NodeLink.Forward.ForwardAudit? found = null;
        await NodeTestKit.WaitAsync(() => (found = _kit.Node.Forwarded.FirstOrDefault(e => e.Path.StartsWith(path, StringComparison.Ordinal))) is not null,
            $"the node's audit of {path}");
        return found!;
    }
}
