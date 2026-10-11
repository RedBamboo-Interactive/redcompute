using System.Collections.Concurrent;
using RedCompute.App.Services;
using Xunit;

namespace RedCompute.App.Tests;

public sealed class TranscriptDispatcherPersistenceTests
{
    [Fact]
    public async Task CompletedCheckpointCanPersistWithoutPumpingTheCallerContext()
    {
        var checkpoint = new TranscriptCheckpointCoordinator();
        var persisted = false;
        await OnNonPumpingContext(() => checkpoint.Write("synthetic", () =>
            SessionTranscriptPipeline.PersistSynchronously(async () =>
            {
                await Task.Delay(10);
                persisted = true;
            })));

        Assert.True(persisted);
        Assert.Equal("readable", await checkpoint.ReadAsync("synthetic", () => Task.FromResult("readable")));
    }

    [Fact]
    public async Task PayloadResultCanReturnWithoutPumpingTheCallerContext()
    {
        var result = 0;
        await OnNonPumpingContext(() => result = SessionTranscriptPipeline.PersistSynchronously(async () =>
        {
            await Task.Delay(10);
            return 42;
        }));
        Assert.Equal(42, result);
    }

    [Fact]
    public async Task PersistenceFailurePropagatesAndKeepsTheCheckpointClosed()
    {
        var checkpoint = new TranscriptCheckpointCoordinator();
        var failure = new InvalidOperationException("synthetic write rejected");
        await OnNonPumpingContext(() => Assert.Same(failure, Assert.Throws<InvalidOperationException>(() =>
            checkpoint.Write("synthetic", () => SessionTranscriptPipeline.PersistSynchronously(async () =>
            {
                await Task.Delay(10);
                throw failure;
            })))));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            checkpoint.ReadAsync("synthetic", () => Task.FromResult("must remain hidden")));
    }

    private static async Task OnNonPumpingContext(Action action)
    {
        var context = new NonPumpingContext();
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(context);
            try { action(); finished.TrySetResult(); }
            catch (Exception ex) { finished.TrySetException(ex); }
            finally { SynchronizationContext.SetSynchronizationContext(null); }
        }) { IsBackground = true };
        thread.Start();
        try { await finished.Task.WaitAsync(TimeSpan.FromSeconds(3)); }
        finally
        {
            // Recover the deliberately blocked baseline after timeout as well.
            context.Release();
            thread.Join(TimeSpan.FromSeconds(1));
        }
    }

    private sealed class NonPumpingContext : SynchronizationContext
    {
        private readonly ConcurrentQueue<(SendOrPostCallback Callback, object? State)> _pending = new();
        private int _released;
        public override void Post(SendOrPostCallback callback, object? state)
        {
            _pending.Enqueue((callback, state));
            if (Volatile.Read(ref _released) != 0) Drain();
        }
        public void Release() { Volatile.Write(ref _released, 1); Drain(); }
        private void Drain()
        {
            while (_pending.TryDequeue(out var work))
                ThreadPool.QueueUserWorkItem(_ => work.Callback(work.State));
        }
    }
}
