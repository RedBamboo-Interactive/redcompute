using RedCompute.Core.Sessions;
using RedCompute.Plugin.OpenCode;
using RedCompute.PluginSdk;
using Xunit;
using MemoryJobTracker = RedCompute.App.Tests.ClaudeBackgroundWorkTests.MemoryJobTracker;

namespace RedCompute.App.Tests;

// The OpenCode model list is what `opencode models` reports on this machine, not a list written into the code.
public sealed class OpenCodeModelCatalogTests
{
    // Shaped like the real output on 25 Nov 2026: one provider/model per line, plus whatever else a CLI prints.
    private const string Listing = "\u001b[0mopencode/big-pickle\r\n" +
        "opencode/deepseek-v4.1-flash-free\r\n" +
        "cerebras/qwen-3.8-27b\r\n" +
        "\r\n" +
        "Warning: a newer opencode is available\r\n" +
        "meta/muse-spark-1.3\r\n" +
        "openrouter/anthropic/claude-sonnet-4.5\r\n" +
        "cerebras/qwen-3.8-27b\r\n";

    [Fact]
    public void Every_model_line_is_kept_in_order_and_anything_else_is_ignored()
    {
        Assert.Equal(
            ["opencode/big-pickle", "opencode/deepseek-v4.1-flash-free", "cerebras/qwen-3.8-27b",
             "meta/muse-spark-1.3", "openrouter/anthropic/claude-sonnet-4.5"],
            OpenCodeModelCatalog.Parse(Listing).Select(model => model.Id));
    }

    [Fact]
    public async Task The_catalog_asks_the_resolved_cli_for_its_models()
    {
        var runner = new Runner(new CliCommandResult(0, Listing, ""));
        var catalog = new OpenCodeModelCatalog(() => @"C:\tools\opencode.cmd", (_, _) => { }, runner);

        var models = await catalog.GetAsync();

        Assert.Equal(5, models.Count);
        var command = Assert.Single(runner.Commands);
        Assert.Equal(@"C:\tools\opencode.cmd", command.FileName);
        Assert.Equal(["models"], command.Arguments);
        Assert.Null(catalog.LastError);
    }

    [Fact]
    public async Task A_failed_refresh_keeps_the_last_known_list_and_says_why()
    {
        var runner = new Runner(new CliCommandResult(0, Listing, ""), new CliCommandResult(1, "", "auth store locked"));
        var catalog = new OpenCodeModelCatalog(() => "opencode", (_, _) => { }, runner);
        await catalog.GetAsync();

        var models = await catalog.GetAsync(forceRefresh: true);

        Assert.Equal(5, models.Count);
        Assert.Contains("auth store locked", catalog.LastError);
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("opencode", "no model lines at all")]
    public async Task No_cli_or_no_models_gives_an_empty_list_with_a_reason(string? cli, string output)
    {
        var catalog = new OpenCodeModelCatalog(() => cli, (_, _) => { }, new Runner(new CliCommandResult(0, output, "")));

        Assert.Empty(await catalog.GetAsync());
        Assert.NotNull(catalog.LastError);
    }

    [Fact]
    public async Task The_provider_serves_the_catalog_not_a_built_in_list()
    {
        var runner = new Runner(new CliCommandResult(0, Listing, ""));
        var catalog = new OpenCodeModelCatalog(() => "opencode", (_, _) => { }, runner);
        await catalog.GetAsync();
        var service = new OpenCodeSessionService(new OpenCodeConfig(), new MemoryJobTracker(), new OpenCodeSessionStore(), (_, _) => { },
            new OpenCodeNativeSessionReader(Path.Combine(Path.GetTempPath(), $"opencode-none-{Guid.NewGuid():N}.db")));
        ISessionProvider provider = new OpenCodeProvider("ai-session", service, new OpenCodeSessionStore(),
            new MemoryJobTracker(), (_, _) => { }, catalog);

        var ids = provider.GetAvailableModels().Select(model => model.Id).ToList();

        Assert.Equal(OpenCodeModelCatalog.Parse(Listing).Select(model => model.Id), ids);
        Assert.DoesNotContain("anthropic/claude-sonnet-4-20250514", ids);
    }

    // The host starts only the default provider (Codex), so the list has to fill from warm-up, not from StartAsync:
    // after a restart the first read used to come back empty (25 Nov 2026).
    [Fact]
    public async Task Warm_up_fills_the_list_before_anything_reads_it_and_start_runs_no_cli()
    {
        var runner = new Runner(new CliCommandResult(0, Listing, ""));
        var catalog = new OpenCodeModelCatalog(() => "opencode", (_, _) => { }, runner);
        var service = new OpenCodeSessionService(new OpenCodeConfig(), new MemoryJobTracker(), new OpenCodeSessionStore(), (_, _) => { },
            new OpenCodeNativeSessionReader(Path.Combine(Path.GetTempPath(), $"opencode-none-{Guid.NewGuid():N}.db")));
        var provider = new OpenCodeProvider("ai-session", service, new OpenCodeSessionStore(),
            new MemoryJobTracker(), (_, _) => { }, catalog);

        Assert.True(await provider.StartAsync());
        Assert.Empty(runner.Commands);

        await ((IProviderWarmup)provider).WarmAsync();

        Assert.Equal(OpenCodeModelCatalog.Parse(Listing).Select(model => model.Id),
            ((ISessionProvider)provider).GetAvailableModels().Select(model => model.Id));
        Assert.Single(runner.Commands);
    }

    [Fact]
    public async Task Reading_an_empty_cache_starts_one_refresh_without_waiting_for_it()
    {
        var runner = new Runner(new CliCommandResult(0, Listing, "")) { Gate = new TaskCompletionSource() };
        var catalog = new OpenCodeModelCatalog(() => "opencode", (_, _) => { }, runner);

        Assert.Empty(catalog.Cached);   // returns at once while the CLI is still running
        Assert.Empty(catalog.Cached);   // a second read does not start a second run
        runner.Gate.SetResult();
        await runner.Finished.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await WaitUntil(() => catalog.Cached.Count == 5);

        Assert.Single(runner.Commands);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "the background refresh never filled the cache");
            await Task.Delay(10);
        }
    }

    private sealed class Runner(params CliCommandResult[] results) : ICliCommandRunner
    {
        private int _next;
        public List<CliCommand> Commands { get; } = [];
        public TaskCompletionSource? Gate { get; init; }
        public TaskCompletionSource Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<CliCommandResult> RunAsync(CliCommand command, CancellationToken ct = default)
        {
            lock (Commands) Commands.Add(command);
            if (Gate is not null) await Gate.Task.WaitAsync(ct);
            var result = results[Math.Min(_next++, results.Length - 1)];
            Finished.TrySetResult();
            return result;
        }
    }
}
