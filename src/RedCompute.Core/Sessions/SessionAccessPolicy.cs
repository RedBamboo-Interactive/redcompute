namespace RedCompute.Core.Sessions;

/// <summary>
/// RedLeaf's Agent permission policy as RedCompute reads it (<c>GET /api/agent-permissions/policy</c>).
/// RedLeaf owns it; RedCompute never writes it. No record (an installation whose owner never saved
/// Agent permissions) means default <c>auto</c> with Full disabled (<see cref="NoRecord"/>): a provider
/// without auto still resolves that to ask. An unreachable RedLeaf reads as default <c>ask</c> with
/// Full disabled (<see cref="FailClosed"/>).
/// </summary>
public sealed record SessionAccessPolicy
{
    /// <summary>The default level when RedLeaf has no policy record.</summary>
    public const string NoRecordDefaultLevel = SessionAccessLevels.Auto;

    /// <summary>The policy's shape version (always 1 today), not a revision.</summary>
    public int Version { get; init; }
    /// <summary>
    /// RedLeaf's write counter: 0 when it holds no record, then +1 per write. Null from an older
    /// RedLeaf that does not send it, and for the fail-closed defaults.
    /// </summary>
    public long? Revision { get; init; }
    public string? DefaultLevel { get; init; }
    /// <summary>Per-Agent default level, keyed by Agent entity id.</summary>
    public IReadOnlyDictionary<string, string> Agents { get; init; } = new Dictionary<string, string>();
    public FullAccessAcknowledgement? FullAccess { get; init; }
    /// <summary>False when the policy could not be read and these are the fail-closed defaults.</summary>
    public bool Available { get; init; } = true;
    /// <summary>False when RedLeaf answered that it holds no policy record and these are the no-record defaults.</summary>
    public bool Stored { get; init; } = true;

    public static SessionAccessPolicy NoRecord { get; } = new() { DefaultLevel = NoRecordDefaultLevel, Stored = false };
    public static SessionAccessPolicy FailClosed { get; } = new() { Available = false, Stored = false };

    /// <summary>
    /// RedLeaf's effective value, taken as is: it is already false when Full access was accepted on
    /// an older warning text, so RedCompute never compares text versions itself.
    /// </summary>
    public bool FullAccessEnabled => FullAccess is { Enabled: true };

    /// <summary>The configured default for an Agent: its own entry, else the global default, else ask.</summary>
    public string DefaultFor(string? agentEntityId)
    {
        if (agentEntityId is not null
            && Agents.TryGetValue(agentEntityId, out var own)
            && SessionAccessLevels.TryNormalize(own, out var ownLevel))
            return ownLevel;
        return SessionAccessLevels.TryNormalize(DefaultLevel, out var global) ? global : SessionAccessLevels.Ask;
    }
}

public sealed record FullAccessAcknowledgement(
    bool Enabled,
    int? TextVersion,
    DateTimeOffset? AcknowledgedAt = null,
    string? AcknowledgedBy = null);
