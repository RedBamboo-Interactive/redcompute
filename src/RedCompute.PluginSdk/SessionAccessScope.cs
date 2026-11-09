using RedCompute.Core.Sessions;

namespace RedCompute.PluginSdk;

/// <summary>
/// The access state RedCompute resolved for a session it is starting, or for a one-shot it is
/// running. Set only by RedCompute around <c>StartSessionAsync</c> and <c>ExecuteAsync</c>, never
/// from a request body, so a provider reads an authorized and clamped value. Providers read it
/// once at the call's entry: work a launch gate defers may run outside the scope.
/// </summary>
public static class SessionAccessScope
{
    private static readonly AsyncLocal<SessionAccessState?> CurrentValue = new();

    /// <summary>The resolved state, or null when RedCompute resolved none (a provider then fails closed to ask).</summary>
    public static SessionAccessState? Current => CurrentValue.Value;

    public static IDisposable Push(SessionAccessState? state)
    {
        var previous = CurrentValue.Value;
        CurrentValue.Value = state;
        return new Restore(() => CurrentValue.Value = previous);
    }

    private sealed class Restore(Action restore) : IDisposable
    {
        public void Dispose() => restore();
    }
}
