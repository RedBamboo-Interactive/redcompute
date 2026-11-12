using System.Collections.Concurrent;

namespace RedCompute.App.Services.Jobs;

/// <summary>
/// Connects a job's durable Cancelled state to the provider invocation that is doing its work.
/// Marking a job cancelled used to change only the record, so a provider kept running (and a
/// ComfyUI prompt kept holding the GPU) until it finished on its own. Every provider invocation
/// now runs under the token of its execution scope, and cancelling the job cancels that token.
/// </summary>
internal sealed class JobExecutionCancellation
{
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _executions = new();

    public JobExecutionScope Begin(Guid jobId, CancellationToken caller)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(caller);
        _executions[jobId] = source;
        return new JobExecutionScope(this, jobId, source);
    }

    public bool Cancel(Guid jobId)
    {
        if (!_executions.TryGetValue(jobId, out var source)) return false;
        try
        {
            source.Cancel();
            return true;
        }
        catch (ObjectDisposedException)
        {
            // The invocation ended between the lookup and the cancel; nothing is left to stop.
            return false;
        }
    }

    public bool IsActive(Guid jobId) => _executions.ContainsKey(jobId);

    internal void End(Guid jobId, CancellationTokenSource source)
    {
        _executions.TryRemove(new KeyValuePair<Guid, CancellationTokenSource>(jobId, source));
        source.Dispose();
    }
}

/// <summary>
/// One provider invocation of one job. Pass <see cref="Token"/> to the provider and dispose the
/// scope when the invocation returns.
/// </summary>
public sealed class JobExecutionScope : IDisposable
{
    private readonly JobExecutionCancellation _owner;
    private readonly CancellationTokenSource _source;
    private int _disposed;

    internal JobExecutionScope(JobExecutionCancellation owner, Guid jobId, CancellationTokenSource source)
    {
        _owner = owner;
        _source = source;
        JobId = jobId;
        Token = source.Token;
    }

    public Guid JobId { get; }
    public CancellationToken Token { get; }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
            _owner.End(JobId, _source);
    }
}
