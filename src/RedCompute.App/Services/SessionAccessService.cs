using RedCompute.Core.Jobs;
using RedCompute.Core.Sessions;
using RedCompute.PluginSdk;

namespace RedCompute.App.Services;

/// <summary>
/// Applies the access-level contract across providers: resolves a starting level, authorizes
/// changes, resolves legacy sessions before they launch, and demotes Full sessions when RedLeaf
/// stops enabling Full. The rules themselves live in <see cref="SessionAccessResolver"/>; whether a
/// person is present comes from the auth layer (<c>ComputeResourceAccess.IsPersonPresent</c>).
/// </summary>
public sealed class SessionAccessService(
    ISessionAccessPolicySource policy,
    Func<IEnumerable<ISessionProvider>> providers,
    Func<Guid, JobRecord?> jobs,
    Action<string, Guid?> log)
{
    public ISessionAccessPolicySource Policy => policy;

    /// <summary>The state a new session starts with; null for a provider without access levels.</summary>
    public async Task<SessionAccessStart> ResolveStartAsync(ISessionProvider provider, string? requested,
        string? maxAccessLevel, bool unattended, bool personPresent, string? agentEntityId, CancellationToken ct = default)
    {
        var supported = provider.SupportedAccessLevels;
        if (supported.Count == 0) return new SessionAccessStart(null);
        var current = await policy.GetAsync(ct: ct);
        var resolved = SessionAccessResolver.ResolveStart(requested, agentEntityId, personPresent, current, supported, maxAccessLevel);
        return resolved.Level is null
            ? new SessionAccessStart(null, resolved.Error, resolved.Message)
            : new SessionAccessStart(new SessionAccessState(resolved.Level, maxAccessLevel, unattended));
    }

    /// <summary>The level a one-shot runs at: requested, clamped exactly like a session start.</summary>
    public async Task<SessionAccessStart> ResolveOneshotAsync(ISessionProvider provider, string? requested,
        bool personPresent, string? agentEntityId, CancellationToken ct = default)
    {
        var supported = provider.SupportedAccessLevels;
        if (supported.Count == 0) return new SessionAccessStart(null);
        var current = await policy.GetAsync(ct: ct);
        var resolved = SessionAccessResolver.ResolveStart(requested, agentEntityId, personPresent, current, supported, null);
        return resolved.Level is null
            ? new SessionAccessStart(null, resolved.Error, resolved.Message)
            : new SessionAccessStart(new SessionAccessState(resolved.Level, Unattended: true));
    }

    /// <summary>
    /// Changes a session's level under the contract's rules. The caller has already checked write
    /// access. A null <paramref name="requested"/> leaves plan for the level it entered from.
    /// </summary>
    public async Task<SessionAccessChangeResult> ChangeAsync(ISessionProvider provider, UnifiedSessionInfo info,
        string? requested, bool personPresent, CancellationToken ct = default)
    {
        var supported = provider.SupportedAccessLevels;
        if (supported.Count == 0)
            return SessionAccessChangeResult.Refused(422, "access_levels_not_supported",
                $"Provider '{provider.ProviderId}' does not support access levels");
        var state = provider.GetAccessState(info.Id);
        if (state is null)
            return SessionAccessChangeResult.Refused(404, "not_found", $"Session '{info.Id}' not found");

        var current = await policy.GetAsync(ct: ct);
        var agent = AgentEntityId(info);
        // A session persisted before access levels has no trusted level. Resolve it first, so a
        // change is judged against what it would really run at, never against a stored bypass.
        if (state.AccessLevel is null)
            state = state with { AccessLevel = SessionAccessResolver.ResolveDefault(agent, current, supported, state.MaxAccessLevel).Level };
        var change = SessionAccessResolver.ResolveChange(state, requested, agent, personPresent, current, supported);
        if (change.State is null)
            return SessionAccessChangeResult.Refused(change.Error == SessionAccessResolver.InvalidAccessLevel ? 422 : 403,
                change.Error!, change.Message!);
        if (change.State != provider.GetAccessState(info.Id)
            && await provider.ApplyAccessStateAsync(info.Id, change.State, ct) is { Applied: false } refused)
        {
            log($"[Access] Session {info.Provider}/{info.Id} stays at {state.AccessLevel}: {provider.ProviderDisplayName} did not switch to "
                + $"{change.State.AccessLevel}" + (refused.Reason is { } why ? $" ({why})" : ""), info.JobId);
            return SessionAccessChangeResult.Refused(409, ProviderRefusedLevel,
                RefusedMessage(provider, state.AccessLevel, change.State.AccessLevel, refused.Reason), state.AccessLevel);
        }
        if (change.IsChange)
            log($"[Access] Session {info.Provider}/{info.Id} access level {state.AccessLevel} -> {change.State.AccessLevel}"
                + (personPresent ? " (person)" : ""), info.JobId);
        return new SessionAccessChangeResult(change.State.AccessLevel, true, 200);
    }

    /// <summary>
    /// Run before a session launches from a stopped state. A legacy session resolves fresh from
    /// policy; a session holding Full that RedLeaf no longer enables is demoted. Either is persisted,
    /// so the provider's launch applies it.
    /// </summary>
    public async Task EnsureLaunchStateAsync(ISessionProvider provider, string sessionId, CancellationToken ct = default)
    {
        var supported = provider.SupportedAccessLevels;
        if (supported.Count == 0 || provider.GetAccessState(sessionId) is not { } state) return;
        var info = provider.GetSession(sessionId).Info;
        var current = await policy.GetAsync(ct: ct);
        var agent = info is null ? null : AgentEntityId(info);

        var next = state;
        if (state.AccessLevel is null)
            next = state with
            {
                AccessLevel = SessionAccessResolver.ResolveDefault(agent, current, supported, state.MaxAccessLevel).Level
                    ?? SessionAccessResolver.ClampToSupported(SessionAccessLevels.Ask, supported),
            };
        next = SessionAccessResolver.DemoteIfFullRevoked(next, agent, current, supported) ?? next;
        if (next == state) return;
        if ((await provider.ApplyAccessStateAsync(sessionId, next, ct)).Applied)
            log($"[Access] Session {provider.ProviderId}/{sessionId} launches at {next.AccessLevel}"
                + (state.AccessLevel is null ? " (resolved from policy; it predates access levels)" : " (Full is no longer enabled)"),
                info?.JobId);
    }

    /// <summary>
    /// RedLeaf changed the policy: read it fresh and, when Full is no longer enabled, demote every
    /// session holding Full to its resolved default. Returns the demoted session ids.
    /// </summary>
    public async Task<IReadOnlyList<string>> PolicyChangedAsync(CancellationToken ct = default, long? revision = null)
    {
        var current = await policy.GetAsync(refresh: true, ct);
        log($"[Access] RedLeaf announced a policy change{(revision is { } r ? $" (revision {r})" : "")}; "
            + (current.Available ? $"Full access {(current.FullAccessEnabled ? "enabled" : "disabled")}" : "policy unavailable, failing closed"), null);
        var demoted = new List<string>();
        if (current.FullAccessEnabled) return demoted;
        foreach (var provider in providers())
        {
            var supported = provider.SupportedAccessLevels;
            if (supported.Count == 0) continue;
            List<UnifiedSessionInfo> sessions;
            try { sessions = provider.GetSessions(10_000); }
            catch (Exception ex)
            {
                log($"[Access] Could not list {provider.ProviderId} sessions to demote Full: {ex.Message}", null);
                continue;
            }
            foreach (var session in sessions)
            {
                if (provider.GetAccessState(session.Id) is not { } state) continue;
                if (SessionAccessResolver.DemoteIfFullRevoked(state, AgentEntityId(session), current, supported) is not { } next)
                    continue;
                if (!await DemoteAsync(provider, session, next, ct)) continue;
                demoted.Add(session.Id);
                log($"[Access] Full access is no longer enabled; session {provider.ProviderId}/{session.Id} demoted to {next.AccessLevel}", session.JobId);
            }
        }
        return demoted;
    }

    /// <summary>
    /// Demotes one session. A live session whose harness will not switch down is stopped, so Full
    /// stops running at once, and its lower level is persisted for its next launch.
    /// </summary>
    private async Task<bool> DemoteAsync(ISessionProvider provider, UnifiedSessionInfo session, SessionAccessState next, CancellationToken ct)
    {
        var applied = await provider.ApplyAccessStateAsync(session.Id, next, ct);
        if (applied.Applied) return true;
        if (applied.Reason is null) return false;
        log($"[Access] Full access is no longer enabled and {provider.ProviderDisplayName} did not lower session "
            + $"{provider.ProviderId}/{session.Id} ({applied.Reason}); stopping it so it relaunches at {next.AccessLevel}", session.JobId);
        try { await provider.StopSessionAsync(session.Id); }
        catch (Exception ex)
        {
            log($"[Access] Could not stop {provider.ProviderId}/{session.Id} to demote it: {ex.Message}", session.JobId);
            return false;
        }
        return (await provider.ApplyAccessStateAsync(session.Id, next, ct)).Applied;
    }

    public const string ProviderRefusedLevel = "provider_refused_level";

    /// <summary>What the Info modal shows when the harness would not switch: plain words, then its own.</summary>
    internal static string RefusedMessage(ISessionProvider provider, string? from, string? to, string? reason)
    {
        var message = $"{provider.ProviderDisplayName} didn't switch this session to {Title(to)}, so it stays at {Title(from)}.";
        return reason is null ? message : $"{message} {reason}";
    }

    private static string Title(string? level) => level switch
    {
        SessionAccessLevels.Plan => "Plan",
        SessionAccessLevels.Ask => "Ask",
        SessionAccessLevels.Auto => "Auto",
        SessionAccessLevels.Full => "Full",
        _ => level ?? "its current level",
    };

    /// <summary>The Agent a session belongs to, from its job's verified creation identity.</summary>
    public string? AgentEntityId(UnifiedSessionInfo info)
        => info.JobId is { } jobId ? AgentEntityId(jobs(jobId)?.CreationProvenance) : null;

    /// <summary>
    /// The Agent entity of a verified identity: its actor when an Agent acts, else the Agent the
    /// identity names in its context. Unverified provenance names no Agent.
    /// </summary>
    public static string? AgentEntityId(JobProvenance? provenance)
    {
        if (provenance is null || provenance.Assurance is not (JobProvenanceAssurance.Verified or JobProvenanceAssurance.BackfilledExact))
            return null;
        if (provenance.Actor.Kind.Equals("agent", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(provenance.Actor.EntityId))
            return provenance.Actor.EntityId;
        return provenance.Context.FirstOrDefault(reference =>
            reference.Kind.Equals("agent", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(reference.EntityId))?.EntityId;
    }

    /// <summary>
    /// Copies the provider's access state onto a session read or event. A provider that cannot
    /// report it leaves the read as it was: the overlay never fails a read.
    /// </summary>
    public static UnifiedSessionInfo Overlay(UnifiedSessionInfo session, ISessionProvider? provider)
    {
        if (provider is null) return session;
        SessionAccessState? state;
        try
        {
            var supported = provider.SupportedAccessLevels;
            if (supported.Count == 0) return session;
            session.SupportedAccessLevels = supported;
            state = provider.GetAccessState(session.Id);
        }
        catch { return session; }
        if (state is null) return session;
        session.AccessLevel = state.AccessLevel;
        session.MaxAccessLevel = state.MaxAccessLevel;
        session.Unattended = state.Unattended;
        session.AccessLevelBeforePlan = state.AccessLevelBeforePlan;
        return session;
    }
}

public sealed record SessionAccessStart(SessionAccessState? State, string? Error = null, string? Message = null);

public sealed record SessionAccessChangeResult(string? AccessLevel, bool Ok, int Status, string? Error = null, string? Message = null)
{
    /// <param name="accessLevel">The level the session keeps, when known.</param>
    public static SessionAccessChangeResult Refused(int status, string error, string message, string? accessLevel = null)
        => new(accessLevel, false, status, error, message);
}
