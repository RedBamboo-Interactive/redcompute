using RedCompute.App.Services;
using Xunit;

namespace RedCompute.App.Tests;

public sealed class ThreadPoolFloorTests
{
    [Fact]
    public void Ensure_raises_the_worker_minimum_and_never_lowers_it()
    {
        ThreadPool.GetMinThreads(out var originalWorkers, out var originalCompletionPorts);
        try
        {
            var raised = ThreadPoolFloor.Ensure(originalWorkers + 16);
            Assert.Equal(originalWorkers + 16, raised.Workers);
            Assert.Equal(originalCompletionPorts, raised.CompletionPorts);

            var unchanged = ThreadPoolFloor.Ensure(originalWorkers);
            Assert.Equal(originalWorkers + 16, unchanged.Workers);
        }
        finally
        {
            ThreadPool.SetMinThreads(originalWorkers, originalCompletionPorts);
        }
    }
}
