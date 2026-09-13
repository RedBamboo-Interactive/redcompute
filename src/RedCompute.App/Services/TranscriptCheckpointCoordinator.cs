using System.Collections.Concurrent;

namespace RedCompute.App.Services;

/// <summary>Serializes canonical history reads with compact semantic writes.
/// A failed write keeps reads closed until its pending prefix is repaired.</summary>
internal sealed class TranscriptCheckpointCoordinator
{
    private sealed class State
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public bool Incomplete { get; set; }
    }

    private readonly ConcurrentDictionary<string, State> _sessions = new();

    public async Task<T> ReadAsync<T>(string sessionId, Func<Task<T>> read, CancellationToken ct = default)
    {
        var state = _sessions.GetOrAdd(sessionId, _ => new State());
        await state.Gate.WaitAsync(ct);
        try
        {
            if (state.Incomplete)
                throw new InvalidOperationException("The session transcript checkpoint has not finished persisting");
            return await read();
        }
        finally { state.Gate.Release(); }
    }

    public void Write(string sessionId, Action write)
    {
        var state = _sessions.GetOrAdd(sessionId, _ => new State());
        state.Gate.Wait();
        try
        {
            state.Incomplete = true;
            write();
            state.Incomplete = false;
        }
        finally { state.Gate.Release(); }
    }
}
