namespace RedCompute.PluginSdk;

/// <summary>
/// Request-scoped delivery of a session being started: whether everything the model writes is
/// delivered to its reader as one message (Discord and similar transports). A provider persists the
/// choice with the session and applies it on every launch. Not a permission setting: it says nothing
/// about <c>unattended</c>.
/// </summary>
public static class SessionDeliveryScope
{
    private static readonly AsyncLocal<bool> CurrentValue = new();

    /// <summary>True when the session being started is delivered as one message.</summary>
    public static bool DeliveredAsOneMessage => CurrentValue.Value;

    public static IDisposable Push(bool deliveredAsOneMessage)
    {
        var previous = CurrentValue.Value;
        CurrentValue.Value = deliveredAsOneMessage;
        return new Restore(() => CurrentValue.Value = previous);
    }

    private sealed class Restore(Action restore) : IDisposable
    {
        public void Dispose() => restore();
    }
}
