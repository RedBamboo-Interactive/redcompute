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
        // The exchange was aborted: the runtime saw the server go away, long before anything like a gigabyte.
        await _kit.Comfy.EndlessAborted.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(Interlocked.Read(ref _kit.Comfy.EndlessBytesWritten) < 128L << 20);
    }

    [Fact]
    public async Task An_endless_queue_is_refused_at_the_json_cap_before_a_cache_release()
    {
        _kit.Comfy.Endless = "/queue";
        await using var provider = Provider();

        var release = await provider.ReleaseCachedModelsAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(60));

        Assert.False(release.Success);
        Assert.Contains("/queue", release.Detail);
        Assert.Contains("64 KiB cap", release.Detail);
        await _kit.Comfy.EndlessAborted.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }
}
