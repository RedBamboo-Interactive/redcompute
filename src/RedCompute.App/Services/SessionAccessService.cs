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
        var current = await ReadPolicyAsync(ct);
        if (parent is not null)
        {
            // A session in plan is meant to look, not act; work it hands off would act. Say so
            // rather than start a worker capped at plan that cannot do the job.
            if (parent.AccessLevel == SessionAccessLevels.Plan)
                return new SessionAccessStart(null, SessionAccessResolver.ParentInPlan,
                    "The session delegating this work is in plan mode, so it can't hand off work that changes anything. "
                    + "Leave plan in that session first (from its Info panel, or by approving the plan), then delegate again.");
            var inherited = SessionAccessResolver.ResolveFromParent(requested, parent.AccessLevel, current, supported, maxAccessLevel);
            // A worker of a session nobody watches has nobody to approve its asks either: they
            // would park in a Code session no one looks at.
            return inherited.Level is null
                ? new SessionAccessStart(null, inherited.Error, inherited.Message)
                : new SessionAccessStart(new SessionAccessState(inherited.Level, inherited.Cap, unattended || parent.Unattended,
                    AccessLevelSource: SessionAccessLevelSources.Parent));
        }
        var resolved = SessionAccessResolver.ResolveStart(requested, agentEntityId, personPresent, current, supported, maxAccessLevel);
        if (resolved.Level is null) return new SessionAccessStart(null, resolved.Error, resolved.Message);
        // Started in plan: approving the plan returns to what the session would have run at
        // without it, as entering plan later remembers the level it left. Without this, an
        // approved plan landed on Ask and pinned the session there.
        var beforePlan = resolved.Level == SessionAccessLevels.Plan
            ? SessionAccessResolver.ResolveStart(null, agentEntityId, personPresent, current, supported, maxAccessLevel).Level
            : null;
        return new SessionAccessStart(new SessionAccessState(resolved.Level, maxAccessLevel, unattended,
            beforePlan == SessionAccessLevels.Plan ? null : beforePlan,
            requested is null ? SessionAccessLevelSources.Policy : SessionAccessLevelSources.Explicit));
    }

    /// <summary>The context kind a session process's signed identity uses to name its own session.</summary>
    public const string ParentContextKind = "session";

    /// <summary>
    /// The session delegating a new one, read from the start identity's signed context alone (never
    /// a request field): a session process's token names exactly one session, its own. Null (the
    /// start resolves from policy) when no session is named. When sessions are named but the parent
    /// cannot be established (several, unknown, unreadable to this caller), the start must not fall
    /// back to the policy default, which may be Full: it gets a stand-in parent at Ask
    /// (<see cref="UnresolvedParentProvider"/>). A parent persisted before access levels (no level)
    /// reads as Ask.
    /// </summary>
    public SessionAccessParent? FindParent(IEnumerable<string?> signedSessionReferences,
        Func<UnifiedSessionInfo, bool> canRead)
    {
        var named = signedSessionReferences.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal).ToList();
        if (named.Count == 0) return null;
        if (named.Count > 1) return Unresolved(string.Join(",", named), "several sessions are named");
        var id = named[0]!;
        foreach (var provider in providers())
        {
            try
            {
                if (provider.SupportedAccessLevels.Count == 0 || provider.GetAccessState(id) is not { } state) continue;
                if (provider.GetSessionInfo(id) is not { } info) continue;
                if (!canRead(info)) return Unresolved(id, "this caller cannot read it");
                // Every launch since access levels records one, so a running parent has a level;
                // one without is a stopped legacy row, and Ask is the safe reading.
                return new SessionAccessParent(provider.ProviderId, id,
                    state.AccessLevel is { } level && SessionAccessLevels.IsKnown(level) ? level : SessionAccessLevels.Ask,
                    state.Unattended);
            }
            catch (Exception ex)
            {
                log($"[Access] Could not read {provider.ProviderId} session {id} as a delegating parent: {ex.Message}", null);
                return Unresolved(id, "it could not be read");
            }
        }
        return Unresolved(id, "no session with access levels has that id");
    }

    /// <summary>The provider id of a stand-in parent whose real session could not be established.</summary>
    public const string UnresolvedParentProvider = "unresolved";

    private SessionAccessParent Unresolved(string id, string why)
    {
        log($"[Access] The caller's signed identity names session {id} but {why}; the new work starts at Ask, not the policy default", null);
        return new SessionAccessParent(UnresolvedParentProvider, id, SessionAccessLevels.Ask);
    }

    /// <summary>
    /// The level a one-shot runs at: requested, clamped exactly like a session start. A one-shot run
    /// by a session (its signed identity names it, <paramref name="parent"/>) stays within that
    /// session's level, so a Discord session capped at Auto cannot get a Full one-shot.
    /// </summary>
    public async Task<SessionAccessStart> ResolveOneshotAsync(ISessionProvider provider, string? requested,
        bool personPresent, string? agentEntityId, CancellationToken ct = default, SessionAccessParent? parent = null,
        string? cap = null)
    {
        var supported = provider.SupportedAccessLevels;
        if (supported.Count == 0) return new SessionAccessStart(null);
        var current = await ReadPolicyAsync(ct);
        if (parent is not null)
        {
            var inherited = SessionAccessResolver.ResolveFromParent(requested, parent.AccessLevel, current, supported, cap);
            return inherited.Level is null
                ? new SessionAccessStart(null, inherited.Error, inherited.Message)
                : new SessionAccessStart(new SessionAccessState(inherited.Level, inherited.Cap, Unattended: true,
                    AccessLevelSource: SessionAccessLevelSources.Parent));
        }
        var resolved = SessionAccessResolver.ResolveStart(requested, agentEntityId, personPresent, current, supported, cap);
        return resolved.Level is null
            ? new SessionAccessStart(null, resolved.Error, resolved.Message)
            : new SessionAccessStart(new SessionAccessState(resolved.Level, cap, Unattended: true));
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

        var current = await ReadPolicyAsync(ct);
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
            var current = await ReadPolicyAsync(ct);
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
        if (!stored.Unattended && next.Unattended) DenyParkedPermissions(provider, info, next);
        return SessionAccessLimitsResult.Done(next, outcome == LoweringOutcome.Stopped);
    }

    /// <summary>
    /// A session that just became unattended has nobody to answer what it already parked: each
    /// waiting permission is denied now, with the same explanation an unattended ask gets, instead
    /// of holding the session's turn until it expires. Ordinary questions are left alone.
    /// </summary>
    private void DenyParkedPermissions(ISessionProvider provider, UnifiedSessionInfo info, SessionAccessState state)
    {
        IReadOnlyList<PendingSessionQuestion> parked;
        try { parked = provider.GetPendingQuestions(info.Id); }
        catch (Exception ex)
        {
            log($"[Access] Could not read the approvals parked on {info.Provider}/{info.Id}: {ex.Message}", info.JobId);
            return;
        }
        foreach (var question in parked)
        {
            if (question.Kind != PendingSessionQuestion.PermissionKind) continue;
            var what = question.Permission?.ToolName is { Length: > 0 } tool ? $"'{tool}'" : "this";
            try
            {
                var result = provider.SubmitQuestionAnswer(info.Id, new SessionQuestionAnswer
                {
                    RequestId = question.RequestId,
                    Decision = SessionPermissionDecisions.Deny,
                    DeclineReason = SessionAccessNotices.UnattendedDenial(state.AccessLevel, what),
                });
                log($"[Access] Session {info.Provider}/{info.Id} is now unattended; parked approval {question.RequestId} ({what}): {result}", info.JobId);
            }
            catch (Exception ex)
            {
                log($"[Access] Could not deny parked approval {question.RequestId} on {info.Provider}/{info.Id}: {ex.Message}", info.JobId);
            }
        }
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
        var info = provider.GetSessionInfo(sessionId);
        var current = await ReadPolicyAsync(ct);
        var agent = info is null ? null : AgentEntityId(info);

        // RedLeaf unreadable (restarting, a timeout): a level the session already holds is kept,
        // never rewritten from the fail-closed defaults, which would pin an owner's explicit or
        // inherited Full at Ask for good. Only a session with no level at all gets one (ask).
        var next = current.Available ? Target(state, agent, current, supported) : state;
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
        => await ApplyPolicyPassAsync(announced: true, revision, ct);

    private readonly SemaphoreSlim passGate = new(1, 1);
    private long appliedRevision = -1;
    private int passQueued;

    /// <summary>
    /// Reads the policy for a decision, and notices a write RedLeaf could not announce (RedCompute
    /// was down, the notice failed, a restore): a readable policy whose revision differs from the
    /// last one applied queues the same pass an announcement runs.
    /// </summary>
    private async Task<SessionAccessPolicy> ReadPolicyAsync(CancellationToken ct)
    {
        var current = await policy.GetAsync(ct: ct);
        if (current.Available && current.Revision is { } seen && seen != Interlocked.Read(ref appliedRevision)
            && Interlocked.CompareExchange(ref passQueued, 1, 0) == 0)
            _ = Task.Run(async () =>
            {
                try { await ApplyPolicyPassAsync(announced: false, seen, CancellationToken.None); }
                catch (Exception ex) { log($"[Access] Policy pass for revision {seen} failed: {ex.Message}", null); }
                finally { Interlocked.Exchange(ref passQueued, 0); }
            });
        return current;
    }

    private async Task<SessionAccessPolicyChangeResult> ApplyPolicyPassAsync(bool announced, long? revision, CancellationToken ct)
    {
        await passGate.WaitAsync(ct);
        try
        {
            var current = await policy.GetAsync(refresh: announced, ct);
            if (!announced && (current.Revision is null || current.Revision == Interlocked.Read(ref appliedRevision)))
                return new SessionAccessPolicyChangeResult([], []);
            log($"[Access] {(announced ? "RedLeaf announced a policy change" : "The policy changed since it was last applied")}"
                + $"{(revision is { } r ? $" (revision {r})" : "")}; "
                + (current.Available
                    ? $"default {current.DefaultFor(null)}{(current.Stored ? "" : " (no policy saved)")}, Full access {(current.FullAccessEnabled ? "enabled" : "disabled")}"
                    : "policy unavailable: nothing changes until it can be read"), null);
            // The fail-closed defaults are a stand-in for a decision, not a policy to apply: moving
            // sessions to them would demote and pin an owner's Full sessions until a person fixes each.
            if (!current.Available) return new SessionAccessPolicyChangeResult([], []);
            var result = await ApplyPolicyToSessionsAsync(current, ct);
            if (current.Revision is { } applied) Interlocked.Exchange(ref appliedRevision, applied);
            return result;
        }
        finally { passGate.Release(); }
    }

    private async Task<SessionAccessPolicyChangeResult> ApplyPolicyToSessionsAsync(SessionAccessPolicy current, CancellationToken ct)
    {
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
/// <summary>The session delegating a new one: its level, and whether anyone watches it.</summary>
public sealed record SessionAccessParent(string ProviderId, string SessionId, string AccessLevel, bool Unattended = false);

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
