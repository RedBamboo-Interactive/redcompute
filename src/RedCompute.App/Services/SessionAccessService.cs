using RedCompute.Core.Jobs;
using RedCompute.Core.Sessions;
using RedCompute.PluginSdk;

namespace RedCompute.App.Services;

/// <summary>
/// Applies the access-level contract across providers: resolves a starting level, authorizes
/// changes, re-resolves sessions whose level came from the policy before they launch and whenever
/// RedLeaf announces a policy change, and demotes Full sessions when RedLeaf stops enabling Full.
/// The rules themselves live in <see cref="SessionAccessResolver"/>; whether a person is present
/// comes from the auth layer (<c>ComputeResourceAccess.IsPersonPresent</c>).
/// </summary>
public sealed class SessionAccessService(
    ISessionAccessPolicySource policy,
    Func<IEnumerable<ISessionProvider>> providers,
    Func<Guid, JobRecord?> jobs,
    Action<string, Guid?> log)
{
    public ISessionAccessPolicySource Policy => policy;

    /// <summary>
    /// The state a new session starts with; null for a provider without access levels. A session
    /// delegated by another (<paramref name="parent"/>) inherits that session's level as its level
    /// and cap (source parent). Otherwise a requested level is explicit and kept across policy
    /// changes, and without one the level comes from the policy and follows it.
    /// </summary>
    public async Task<SessionAccessStart> ResolveStartAsync(ISessionProvider provider, string? requested,
        string? maxAccessLevel, bool unattended, bool personPresent, string? agentEntityId, CancellationToken ct = default,
        SessionAccessParent? parent = null)
    {
        var supported = provider.SupportedAccessLevels;
        if (supported.Count == 0) return new SessionAccessStart(null);
        var current = await policy.GetAsync(ct: ct);
        if (parent is not null)
        {
            var inherited = SessionAccessResolver.ResolveFromParent(requested, parent.AccessLevel, current, supported, maxAccessLevel);
            return inherited.Level is null
                ? new SessionAccessStart(null, inherited.Error, inherited.Message)
                : new SessionAccessStart(new SessionAccessState(inherited.Level, inherited.Cap, unattended,
                    AccessLevelSource: SessionAccessLevelSources.Parent));
        }
        var resolved = SessionAccessResolver.ResolveStart(requested, agentEntityId, personPresent, current, supported, maxAccessLevel);
        return resolved.Level is null
            ? new SessionAccessStart(null, resolved.Error, resolved.Message)
            : new SessionAccessStart(new SessionAccessState(resolved.Level, maxAccessLevel, unattended,
                AccessLevelSource: requested is null ? SessionAccessLevelSources.Policy : SessionAccessLevelSources.Explicit));
    }

    /// <summary>The context kind a session process's signed identity uses to name its own session.</summary>
    public const string ParentContextKind = "session";

    /// <summary>
    /// The session delegating a new one, read from the start identity's signed context alone (never
    /// a request field): a session process's token names exactly one session, its own. Null, so the
    /// start resolves from policy, unless exactly one session is named, it exists on a provider with
    /// access levels, <paramref name="canRead"/> lets this caller read it, and it holds a known level.
    /// </summary>
    public SessionAccessParent? FindParent(IEnumerable<string?> signedSessionReferences,
        Func<UnifiedSessionInfo, bool> canRead)
    {
        var named = signedSessionReferences.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal).ToList();
        if (named.Count != 1) return null;
        var id = named[0]!;
        foreach (var provider in providers())
        {
            try
            {
                if (provider.SupportedAccessLevels.Count == 0 || provider.GetAccessState(id) is not { } state) continue;
                if (provider.GetSession(id).Info is not { } info) continue;
                if (!canRead(info)) return null;
                return state.AccessLevel is { } level && SessionAccessLevels.IsKnown(level)
                    ? new SessionAccessParent(provider.ProviderId, id, level)
                    : null;
            }
            catch (Exception ex)
            {
                log($"[Access] Could not read {provider.ProviderId} session {id} as a delegating parent: {ex.Message}", null);
            }
        }
        return null;
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
    /// access. A null <paramref name="requested"/> leaves plan for the level it entered from. The
    /// resulting level is explicit: it no longer follows the policy (a request naming the level the
    /// session already has pins it too).
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
        var next = change.IsChange || requested is not null ? change.State.AsExplicit() : change.State;
        if (next != provider.GetAccessState(info.Id)
            && await provider.ApplyAccessStateAsync(info.Id, next, ct) is { Applied: false } refused)
        {
            log($"[Access] Session {info.Provider}/{info.Id} stays at {state.AccessLevel}: {provider.ProviderDisplayName} did not switch to "
                + $"{next.AccessLevel}" + (refused.Reason is { } why ? $" ({why})" : ""), info.JobId);
            return SessionAccessChangeResult.Refused(409, ProviderRefusedLevel,
                RefusedMessage(provider, state.AccessLevel, next.AccessLevel, refused.Reason), state.AccessLevel);
        }
        if (change.IsChange)
            log($"[Access] Session {info.Provider}/{info.Id} access level {state.AccessLevel} -> {next.AccessLevel}"
                + (personPresent ? " (person)" : ""), info.JobId);
        return new SessionAccessChangeResult(next.AccessLevel, true, 200);
    }

    /// <summary>
    /// Tightens (or, for a person, loosens) an existing session's limits: its cap and whether it is
    /// unattended (<see cref="SessionAccessResolver.ResolveLimits"/>). The caller has already checked
    /// write access. A cap below the current level lowers it through the harness; a live harness that
    /// will not switch down is stopped, and the lower level is persisted for its next launch, so it
    /// never keeps running above its new cap. The level's source is kept.
    /// </summary>
    public async Task<SessionAccessLimitsResult> ChangeLimitsAsync(ISessionProvider provider, UnifiedSessionInfo info,
        bool capProvided, string? cap, bool? unattended, bool personPresent, CancellationToken ct = default)
    {
        var supported = provider.SupportedAccessLevels;
        if (supported.Count == 0)
            return SessionAccessLimitsResult.Refused(422, "access_levels_not_supported",
                $"Provider '{provider.ProviderId}' does not support access levels");
        var stored = provider.GetAccessState(info.Id);
        if (stored is null)
            return SessionAccessLimitsResult.Refused(404, "not_found", $"Session '{info.Id}' not found");

        var state = stored;
        // A session persisted before access levels: judge the cap against what it would really run at.
        if (state.AccessLevel is null)
        {
            var current = await policy.GetAsync(ct: ct);
            state = state with
            {
                AccessLevel = SessionAccessResolver.ResolveDefault(AgentEntityId(info), current, supported, state.MaxAccessLevel).Level
                    ?? SessionAccessResolver.ClampToSupported(SessionAccessLevels.Ask, supported),
            };
        }
        var change = SessionAccessResolver.ResolveLimits(state, capProvided, cap, unattended, personPresent, supported);
        if (change.State is null)
            return SessionAccessLimitsResult.Refused(
                change.Error is SessionAccessResolver.InvalidAccessLevel or SessionAccessResolver.CapBelowSupported ? 422 : 403,
                change.Error!, change.Message!, stored);

        var next = change.State;
        if (next == stored) return SessionAccessLimitsResult.Done(next, stopped: false);
        var outcome = await ApplyLoweringAsync(provider, info, state, next, "its new access limits", ct);
        if (outcome == LoweringOutcome.NotApplied)
            return SessionAccessLimitsResult.Refused(409, ProviderRefusedLevel,
                RefusedMessage(provider, state.AccessLevel, next.AccessLevel, null), stored);
        log($"[Access] Session {info.Provider}/{info.Id} limits: cap {stored.MaxAccessLevel ?? "none"} -> {next.MaxAccessLevel ?? "none"}, "
            + $"unattended {stored.Unattended} -> {next.Unattended}, level {state.AccessLevel} -> {next.AccessLevel}"
            + (outcome == LoweringOutcome.Stopped ? " (stopped: the harness would not switch down)" : "")
            + (personPresent ? " (person)" : ""), info.JobId);
        return SessionAccessLimitsResult.Done(next, outcome == LoweringOutcome.Stopped);
    }

    /// <summary>
    /// Run before a session launches from a stopped state. A session whose level came from the
    /// policy (including one persisted before access levels) re-resolves from the current policy; a
    /// session holding Full that RedLeaf no longer enables is demoted. Either is persisted, so the
    /// provider's launch applies it.
    /// </summary>
    public async Task EnsureLaunchStateAsync(ISessionProvider provider, string sessionId, CancellationToken ct = default)
    {
        var supported = provider.SupportedAccessLevels;
        if (supported.Count == 0 || provider.GetAccessState(sessionId) is not { } state) return;
        var info = provider.GetSession(sessionId).Info;
        var current = await policy.GetAsync(ct: ct);
        var agent = info is null ? null : AgentEntityId(info);

        var next = Target(state, agent, current, supported);
        if (next.AccessLevel is null)
            next = next with { AccessLevel = SessionAccessResolver.ClampToSupported(SessionAccessLevels.Ask, supported) };
        if (next == state) return;
        if ((await provider.ApplyAccessStateAsync(sessionId, next, ct)).Applied)
            log($"[Access] Session {provider.ProviderId}/{sessionId} launches at {next.AccessLevel} ({Why(state, current)})",
                info?.JobId);
    }

    /// <summary>
    /// RedLeaf changed the policy: read it fresh, move every session whose level came from the
    /// policy to its re-resolved default (raising as well as lowering, within its cap), and, when
    /// Full is no longer enabled, demote every other session holding Full to its resolved default.
    /// A live session changes through its harness and is recorded only once the harness confirmed;
    /// one that keeps running above its new level is stopped. Approvals already parked stay parked.
    /// </summary>
    public async Task<SessionAccessPolicyChangeResult> PolicyChangedAsync(CancellationToken ct = default, long? revision = null)
    {
        var current = await policy.GetAsync(refresh: true, ct);
        log($"[Access] RedLeaf announced a policy change{(revision is { } r ? $" (revision {r})" : "")}; "
            + (current.Available
                ? $"default {current.DefaultFor(null)}{(current.Stored ? "" : " (no policy saved)")}, Full access {(current.FullAccessEnabled ? "enabled" : "disabled")}"
                : "policy unavailable, failing closed"), null);
        var demoted = new List<string>();
        var followed = new List<string>();
        foreach (var provider in providers())
        {
            var supported = provider.SupportedAccessLevels;
            if (supported.Count == 0) continue;
            List<UnifiedSessionInfo> sessions;
            try { sessions = provider.GetSessions(10_000); }
            catch (Exception ex)
            {
                log($"[Access] Could not list {provider.ProviderId} sessions to apply the policy: {ex.Message}", null);
                continue;
            }
            foreach (var session in sessions)
            {
                if (provider.GetAccessState(session.Id) is not { } state) continue;
                var next = Target(state, AgentEntityId(session), current, supported);
                if (next == state) continue;
                if (!await ApplyPolicyAsync(provider, session, state, next, ct)) continue;
                if (!current.FullAccessEnabled && HoldsFull(state) && !HoldsFull(next)) demoted.Add(session.Id);
                if (state.FollowsPolicy) followed.Add(session.Id);
                log($"[Access] Session {provider.ProviderId}/{session.Id} access level {state.AccessLevel ?? "unset"} -> {next.AccessLevel} "
                    + $"({Why(state, current)})", session.JobId);
            }
        }
        return new SessionAccessPolicyChangeResult(demoted, followed);
    }

    private static bool HoldsFull(SessionAccessState state)
        => state.AccessLevel == SessionAccessLevels.Full || state.AccessLevelBeforePlan == SessionAccessLevels.Full;

    /// <summary>
    /// The state a session should hold under <paramref name="current"/>: a policy-sourced one
    /// follows its default, and nothing keeps a Full that RedLeaf no longer enables.
    /// </summary>
    private static SessionAccessState Target(SessionAccessState state, string? agent, SessionAccessPolicy current,
        IReadOnlyCollection<string> supported)
    {
        var next = SessionAccessResolver.FollowPolicy(state, agent, current, supported) ?? state;
        return SessionAccessResolver.DemoteIfFullRevoked(next, agent, current, supported) ?? next;
    }

    private static string Why(SessionAccessState before, SessionAccessPolicy current)
        => before.AccessLevel is null ? "resolved from policy; it predates access levels"
            : before.FollowsPolicy ? current.Available ? "follows the configured default" : "follows the fail-closed default, RedLeaf unreachable"
            : "Full is no longer enabled";

    /// <summary>
    /// Applies a policy-driven state. A live session whose harness will not switch down is stopped,
    /// so it never keeps running above the level the policy now gives it, and the lower level is
    /// persisted for its next launch. A refused raise keeps the level it had: that is safe, and its
    /// next launch re-resolves it anyway.
    /// </summary>
    private async Task<bool> ApplyPolicyAsync(ISessionProvider provider, UnifiedSessionInfo session,
        SessionAccessState state, SessionAccessState next, CancellationToken ct)
        => await ApplyLoweringAsync(provider, session, state, next, "the policy", ct) != LoweringOutcome.NotApplied;

    private enum LoweringOutcome { Applied, Stopped, NotApplied }

    /// <summary>
    /// Applies a state that may lower the level. A live harness that will not switch down is
    /// stopped and the lower level persisted, so nothing keeps running above it. A refused raise
    /// keeps the level it had. <paramref name="why"/> names what asked for the change, for the log.
    /// </summary>
    private async Task<LoweringOutcome> ApplyLoweringAsync(ISessionProvider provider, UnifiedSessionInfo session,
        SessionAccessState state, SessionAccessState next, string why, CancellationToken ct)
    {
        var applied = await provider.ApplyAccessStateAsync(session.Id, next, ct);
        if (applied.Applied) return LoweringOutcome.Applied;
        if (applied.Reason is null) return LoweringOutcome.NotApplied;
        var held = state.AccessLevel ?? SessionAccessLevels.Ask;
        var target = next.AccessLevel ?? SessionAccessLevels.Ask;
        if (!SessionAccessLevels.IsAbove(held, target))
        {
            log($"[Access] {provider.ProviderDisplayName} did not switch session {provider.ProviderId}/{session.Id} from {held} to {target} "
                + $"({applied.Reason}); it stays at {held} until its next launch", session.JobId);
            return LoweringOutcome.NotApplied;
        }
        log($"[Access] {provider.ProviderDisplayName} did not lower session {provider.ProviderId}/{session.Id} from {held} to {target} "
            + $"for {why} ({applied.Reason}); stopping it so it relaunches at {target}", session.JobId);
        try { await provider.StopSessionAsync(session.Id); }
        catch (Exception ex)
        {
            log($"[Access] Could not stop {provider.ProviderId}/{session.Id} to lower it: {ex.Message}", session.JobId);
            return LoweringOutcome.NotApplied;
        }
        return (await provider.ApplyAccessStateAsync(session.Id, next, ct)).Applied ? LoweringOutcome.Stopped : LoweringOutcome.NotApplied;
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
        session.AccessLevelSource = state.Source;
        return session;
    }
}

/// <param name="Demoted">Sessions moved off full because Full is no longer enabled.</param>
/// <param name="Followed">Sessions whose level came from the policy and changed to follow it (raised or lowered).</param>
public sealed record SessionAccessPolicyChangeResult(IReadOnlyList<string> Demoted, IReadOnlyList<string> Followed);

/// <summary>The session that delegated a new one, and the level it held when it did.</summary>
public sealed record SessionAccessParent(string ProviderId, string SessionId, string AccessLevel);

public sealed record SessionAccessStart(SessionAccessState? State, string? Error = null, string? Message = null);

/// <param name="State">The state now in force; on a refusal, the state kept.</param>
/// <param name="Stopped">The live harness would not switch down, so the session was stopped and relaunches within its limits.</param>
public sealed record SessionAccessLimitsResult(SessionAccessState? State, bool Ok, int Status, bool Stopped = false,
    string? Error = null, string? Message = null)
{
    public static SessionAccessLimitsResult Done(SessionAccessState state, bool stopped) => new(state, true, 200, stopped);
    public static SessionAccessLimitsResult Refused(int status, string error, string message, SessionAccessState? kept = null)
        => new(kept, false, status, false, error, message);
}

public sealed record SessionAccessChangeResult(string? AccessLevel, bool Ok, int Status, string? Error = null, string? Message = null)
{
    /// <param name="accessLevel">The level the session keeps, when known.</param>
    public static SessionAccessChangeResult Refused(int status, string error, string message, string? accessLevel = null)
        => new(accessLevel, false, status, error, message);
}
