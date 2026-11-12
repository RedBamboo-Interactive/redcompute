using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using RedCompute.Core.Configuration;
using RedCompute.Core.Jobs;
using RedCompute.Plugin.ComfyUI;
using RedCompute.PluginSdk;
using Xunit;

namespace RedCompute.App.Tests;

/// <summary>
/// A cancelled ComfyUI-backed job must take its own prompt out of ComfyUI (interrupt it when it is
/// running, dequeue it when it is pending) before it lets go of the accelerator, and must never
/// stop a prompt that belongs to someone else.
/// </summary>
public sealed class ComfyUIPromptCancellationTests
{
    private const string Mine = "prompt-mine";
    private const string Other = "prompt-other";

    [Fact]
    public async Task Cancelling_a_running_prompt_uses_the_per_id_cancel_and_holds_the_gpu_until_ComfyUI_releases_it()
    {
        await using var comfy = await FakeComfy.StartAsync(supportsCancelById: true);
        var admission = new RecordingAdmission(comfy.Events);
        var provider = Provider(comfy.Port, admission);

        using var job = new CancellationTokenSource();
        var execution = provider.ExecuteAsync(Request(() => job.Cancel()), job.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution.WaitAsync(TimeSpan.FromSeconds(15)));

        Assert.Contains($"POST /api/jobs/{Mine}/cancel", comfy.Requests);
        Assert.DoesNotContain(comfy.Requests, r => r.StartsWith("POST /interrupt"));
        Assert.Empty(comfy.Running);
        Assert.Empty(comfy.Pending);
        AssertOrdered(comfy.Events, $"cancelled:{Mine}", "lease-released");
        await provider.DisposeAsync();
    }

    [Fact]
    public async Task Older_builds_dequeue_then_interrupt_only_after_seeing_their_own_prompt_running()
    {
        await using var comfy = await FakeComfy.StartAsync(supportsCancelById: false);
        var admission = new RecordingAdmission(comfy.Events);
        var provider = Provider(comfy.Port, admission);

        using var job = new CancellationTokenSource();
        var execution = provider.ExecuteAsync(Request(() => job.Cancel()), job.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution.WaitAsync(TimeSpan.FromSeconds(15)));

        var delete = Array.IndexOf(comfy.Requests.ToArray(), "POST /queue");
        var interrupt = comfy.Requests.ToList().FindIndex(r => r.StartsWith("POST /interrupt"));
        Assert.True(delete >= 0, "the pending queue entry is deleted");
        Assert.True(interrupt > delete, "the interrupt comes after the queue was checked");
        Assert.Equal(Mine, comfy.InterruptTargets.Single());
        Assert.Empty(comfy.Running);
        AssertOrdered(comfy.Events, $"cancelled:{Mine}", "lease-released");
        await provider.DisposeAsync();
    }

    [Fact]
    public async Task A_pending_prompt_is_dequeued_without_interrupting_the_prompt_that_is_running()
    {
        await using var comfy = await FakeComfy.StartAsync(supportsCancelById: false, otherPromptRunning: true);
        var provider = Provider(comfy.Port);

        var outcome = await QueueThenCancelAsync(provider, comfy);

        Assert.True(outcome.Settled);
        Assert.Empty(comfy.InterruptTargets);
        Assert.DoesNotContain(comfy.Requests, r => r.StartsWith("POST /interrupt"));
        Assert.Equal([Other], comfy.Running);
        Assert.Empty(comfy.Pending);
        await provider.DisposeAsync();
    }

    [Fact]
    public async Task A_prompt_that_already_finished_is_left_alone()
    {
        await using var comfy = await FakeComfy.StartAsync(supportsCancelById: false, otherPromptRunning: true);
        var provider = Provider(comfy.Port);

        var outcome = await provider.CancelPromptAsync(Mine, "test");

        Assert.True(outcome.Settled);
        Assert.Empty(comfy.InterruptTargets);
        Assert.Equal([Other], comfy.Running);
        await provider.DisposeAsync();
    }

    [Fact]
    public async Task A_prompt_ComfyUI_never_releases_is_reported_unsettled_within_the_budget()
    {
        await using var comfy = await FakeComfy.StartAsync(supportsCancelById: false, ignoreInterrupts: true);
        var provider = Provider(comfy.Port, cancelTimeout: TimeSpan.FromMilliseconds(600));
        comfy.Running.Add(Mine);

        var outcome = await provider.CancelPromptAsync(Mine, "test").WaitAsync(TimeSpan.FromSeconds(10));

        Assert.False(outcome.Settled);
        Assert.Equal("interrupt", outcome.Method);
        await provider.DisposeAsync();
    }

    private static async Task<ComfyUIProvider.PromptCancelOutcome> QueueThenCancelAsync(ComfyUIProvider provider, FakeComfy comfy)
    {
        comfy.Pending.Add(Mine);
        return await provider.CancelPromptAsync(Mine, "test").WaitAsync(TimeSpan.FromSeconds(10));
    }

    private static ComfyUIProvider Provider(int port, IAcceleratorAdmissionCoordinator? admission = null,
        TimeSpan? cancelTimeout = null) => new(new ProviderConfig
        {
            Type = "ComfyUI",
            BackendPort = port,
            Extra = new Dictionary<string, object?> { ["Host"] = "127.0.0.1" },
        }, "image-gen", _ => { }, admission)
        {
            PromptCancelTimeout = cancelTimeout ?? TimeSpan.FromSeconds(10),
            PromptCancelPollInterval = TimeSpan.FromMilliseconds(20),
        };

    private static JobRequest Request(Action onExecuting) => new()
    {
        JobId = Guid.NewGuid(),
        CapabilitySlug = "image-gen",
        Parameters = new Dictionary<string, object?>
        {
            ["workflow_json"] = "{\"1\":{\"class_type\":\"SaveImage\",\"inputs\":{}}}",
            ["output_node"] = "1",
            ["media_type"] = "image",
        },
        // The first progress report means ComfyUI is executing our prompt: cancel the job then.
        ProgressCallback = _ => onExecuting(),
    };

    private static void AssertOrdered(ConcurrentQueue<string> events, string first, string second)
    {
        var list = events.ToList();
        var a = list.IndexOf(first);
        var b = list.IndexOf(second);
        Assert.True(a >= 0, $"missing {first} in [{string.Join(", ", list)}]");
        Assert.True(b > a, $"{second} must follow {first} in [{string.Join(", ", list)}]");
    }

    private sealed class RecordingAdmission(ConcurrentQueue<string> events) : IAcceleratorAdmissionCoordinator
    {
        public IDisposable RegisterPreemptibleResident(AcceleratorResidentRegistration registration) => new Nothing();

        public ValueTask<IAsyncDisposable> AcquireAsync(AcceleratorWorkload workload, CancellationToken cancellationToken = default)
        {
            events.Enqueue("lease-acquired");
            return ValueTask.FromResult<IAsyncDisposable>(new Lease(events));
        }

        private sealed class Lease(ConcurrentQueue<string> events) : IAsyncDisposable
        {
            public ValueTask DisposeAsync()
            {
                events.Enqueue("lease-released");
                return ValueTask.CompletedTask;
            }
        }

        private sealed class Nothing : IDisposable { public void Dispose() { } }
    }

    /// <summary>The slice of ComfyUI's HTTP and WebSocket API the provider uses.</summary>
    private sealed class FakeComfy : IAsyncDisposable
    {
        private readonly WebApplication _app;
        private readonly object _gate = new();

        public ConcurrentQueue<string> Requests { get; } = new();
        public ConcurrentQueue<string> Events { get; } = new();
        public List<string> Running { get; } = [];
        public List<string> Pending { get; } = [];
        public List<string> InterruptTargets { get; } = [];
        public int Port { get; private set; }

        private FakeComfy(WebApplication app) => _app = app;

        public static async Task<FakeComfy> StartAsync(
            bool supportsCancelById, bool otherPromptRunning = false, bool ignoreInterrupts = false)
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            var app = builder.Build();
            var fake = new FakeComfy(app);
            if (otherPromptRunning) fake.Running.Add(Other);
            var queued = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            app.UseWebSockets();
            app.Use(async (context, next) =>
            {
                fake.Requests.Enqueue($"{context.Request.Method} {context.Request.Path}");
                await next();
            });

            app.Map("/ws", async context =>
            {
                using var socket = await context.WebSockets.AcceptWebSocketAsync();
                await queued.Task;
                var executing = Encoding.UTF8.GetBytes(
                    $"{{\"type\":\"executing\",\"data\":{{\"node\":\"1\",\"prompt_id\":\"{Mine}\"}}}}");
                await socket.SendAsync(executing, WebSocketMessageType.Text, true, CancellationToken.None);
                var buffer = new byte[256];
                try
                {
                    while (socket.State == WebSocketState.Open)
                        await socket.ReceiveAsync(buffer, context.RequestAborted);
                }
                catch (Exception) { /* the client aborting the socket is the expected end */ }
            });

            app.MapPost("/prompt", () =>
            {
                lock (fake._gate)
                {
                    if (fake.Running.Count == 0) fake.Running.Add(Mine);
                    else fake.Pending.Add(Mine);
                }
                queued.TrySetResult();
                return Results.Json(new { prompt_id = Mine, number = 1 });
            });

            app.MapGet("/queue", () =>
            {
                lock (fake._gate)
                {
                    static JsonArray Items(IEnumerable<string> ids) =>
                        new(ids.Select((id, n) => (JsonNode)new JsonArray(n, id, new JsonObject(), new JsonObject(), new JsonArray())).ToArray());
                    return Results.Json(new JsonObject
                    {
                        ["queue_running"] = Items(fake.Running),
                        ["queue_pending"] = Items(fake.Pending),
                    });
                }
            });

            app.MapPost("/queue", async (HttpContext context) =>
            {
                var body = await JsonNode.ParseAsync(context.Request.Body);
                lock (fake._gate)
                {
                    foreach (var id in body?["delete"]?.AsArray() ?? [])
                        if (fake.Pending.Remove(id!.GetValue<string>()))
                            fake.Events.Enqueue($"cancelled:{id}");
                }
                return Results.Ok();
            });

            app.MapPost("/interrupt", async (HttpContext context) =>
            {
                var body = await JsonNode.ParseAsync(context.Request.Body);
                var target = body?["prompt_id"]?.GetValue<string>();
                lock (fake._gate)
                {
                    // A build without targeted interrupts would stop whatever is running; record
                    // the target so a test can prove the provider only ever names its own prompt.
                    fake.InterruptTargets.Add(target ?? "<global>");
                    if (!ignoreInterrupts && target != null && fake.Running.Remove(target))
                        fake.Events.Enqueue($"cancelled:{target}");
                }
                return Results.Ok();
            });

            app.MapPost("/api/jobs/{id}/cancel", (string id) =>
            {
                if (!supportsCancelById) return Results.NotFound();
                lock (fake._gate)
                {
                    var cancelled = fake.Running.Remove(id) | fake.Pending.Remove(id);
                    if (cancelled) fake.Events.Enqueue($"cancelled:{id}");
                    return Results.Json(new { cancelled });
                }
            });

            await app.StartAsync();
            fake.Port = new Uri(app.Urls.Single()).Port;
            return fake;
        }

        public async ValueTask DisposeAsync()
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }
}
