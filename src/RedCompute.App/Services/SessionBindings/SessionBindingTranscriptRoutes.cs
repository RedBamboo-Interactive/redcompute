using RedCompute.PluginSdk;

namespace RedCompute.App.Services.SessionBindings;

internal sealed record LogicalTranscriptAddress(string SessionId, string StorageProvider, string OriginProvider);

/// <summary>Maps only the committed runtime for each session onto its original transcript.</summary>
internal sealed class SessionBindingTranscriptRoutes
{
    private readonly object _gate = new();
    private readonly Dictionary<string, SessionBindingSnapshot> _bindings = new(StringComparer.Ordinal);
    private Dictionary<(string Provider, string RuntimeId), LogicalTranscriptAddress?> _routes = [];

    public SessionBindingTranscriptRoutes(SessionInputQueueStore queue)
    {
        queue.BindingCommitted += Publish;
        foreach (var binding in queue.ReadBindingSnapshots()) Publish(binding);
    }

    public SessionBindingSnapshot? Binding(string logicalId)
    {
        lock (_gate) return _bindings.GetValueOrDefault(logicalId);
    }

    public LogicalTranscriptAddress? Resolve(string provider, string runtimeId)
        => Resolve(Volatile.Read(ref _routes), provider, runtimeId);

    private static LogicalTranscriptAddress? Resolve(
        IReadOnlyDictionary<(string Provider, string RuntimeId), LogicalTranscriptAddress?> view, string provider, string runtimeId)
    {
        if (view.TryGetValue((provider, runtimeId), out var route))
            return route;
        // Prepared physical threads have no visible transcript until their operation commits.
        return runtimeId.StartsWith("binding-", StringComparison.Ordinal)
            ? null : new(runtimeId, provider, provider);
    }

    public IReadOnlyList<AiMessageSnapshot> Project(IReadOnlyList<AiMessageSnapshot> messages)
    {
        var view = Volatile.Read(ref _routes);
        return messages.Select(message => Project(view, message)).OfType<AiMessageSnapshot>().ToArray();
    }

    private static AiMessageSnapshot? Project(
        IReadOnlyDictionary<(string Provider, string RuntimeId), LogicalTranscriptAddress?> view, AiMessageSnapshot message)
    {
        var route = Resolve(view, message.Provider, message.SessionId);
        if (route is null) return null;
        if (route.SessionId == message.SessionId && route.StorageProvider == message.Provider) return message;
        return message with
        {
            SessionId = route.SessionId,
            Provider = route.StorageProvider,
            OriginProvider = route.OriginProvider,
            ProviderPartId = message.ProviderPartId is { } part
                ? $"{message.Provider}/{message.SessionId}/{part}" : null,
        };
    }

    private void Publish(SessionBindingSnapshot snapshot)
    {
        lock (_gate)
        {
            if (_bindings.TryGetValue(snapshot.LogicalId, out var current)
                && current.Active.Generation >= snapshot.Active.Generation) return;
            _bindings[snapshot.LogicalId] = snapshot;
            var next = new Dictionary<(string, string), LogicalTranscriptAddress?>();
            foreach (var owner in _bindings.Values)
            {
                var rootProvider = owner.Threads[0].Native.Target.ProviderId;
                foreach (var thread in owner.Threads)
                {
                    var native = thread.Native;
                    next[(native.Target.ProviderId, native.RuntimeSessionId)] = thread.Generation == owner.Active.Generation
                        ? new(owner.LogicalId, rootProvider, native.Target.ProviderId) : null;
                }
            }
            Volatile.Write(ref _routes, next);
        }
    }
}
