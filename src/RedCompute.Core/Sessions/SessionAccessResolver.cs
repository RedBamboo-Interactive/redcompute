namespace RedCompute.Core.Sessions;

/// <summary>
/// The one implementation of the access-level contract's resolution and clamping rules. Pure: the
/// caller supplies whether a person is present, the policy, the provider's supported levels and the
/// session's cap.
/// </summary>
public static class SessionAccessResolver
{
    public const string RaiseRequiresPerson = "raise_requires_person";
    public const string FullAccessNotEnabled = "full_access_not_enabled";
    public const string AboveSessionCap = "above_session_cap";
    public const string InvalidAccessLevel = "invalid_access_level";
    public const string CapBelowSupported = "access_level_cap_unsupported";

    /// <summary>
    /// The level a session starts at (and a legacy session resumes at, with no request):
    /// requested ?? the Agent's default ?? the global default ?? ask; then a caller who is not a
    /// person is clamped to that configured default; full needs Full enabled (else auto); a level the
    /// provider cannot enforce becomes ask; and the cap clamps last, to the highest supported level
    /// at or below it. Nothing here ever raises a level. Fails only when the cap is below every
    /// level the provider supports, since no level could honour it.
    /// </summary>
    public static SessionAccessResolution ResolveStart(
        string? requested,
        string? agentEntityId,
        bool personPresent,
        SessionAccessPolicy policy,
        IReadOnlyCollection<string> supported,
        string? maxAccessLevel)
    {
        if (requested is not null && !SessionAccessLevels.IsKnown(requested))
            return SessionAccessResolution.Refused(InvalidAccessLevel, $"'{requested}' is not an access level");
        if (maxAccessLevel is not null && !SessionAccessLevels.IsKnown(maxAccessLevel))
            return SessionAccessResolution.Refused(InvalidAccessLevel, $"'{maxAccessLevel}' is not an access level");

        var configured = policy.DefaultFor(agentEntityId);
        var level = requested ?? configured;
        if (!personPresent) level = SessionAccessLevels.Lower(level, configured);
        return Clamp(level, policy, supported, maxAccessLevel);
    }

    /// <summary>
    /// The level a delegated session starts at: its parent session's current level, which is also
    /// its cap (lowered further by a requested cap). A requested level can only lower it. The parent
    /// stands in for the configured default, so the person-present rule does not apply; full still
    /// needs Full enabled (else auto), a level the provider cannot enforce becomes ask, and the cap
    /// clamps last. Fails only when the cap is below every level the provider supports.
    /// </summary>
    public static SessionAccessResolution ResolveFromParent(
        string? requested,
        string parentLevel,
        SessionAccessPolicy policy,
        IReadOnlyCollection<string> supported,
        string? maxAccessLevel)
    {
        if (requested is not null && !SessionAccessLevels.IsKnown(requested))
            return SessionAccessResolution.Refused(InvalidAccessLevel, $"'{requested}' is not an access level");
        if (maxAccessLevel is not null && !SessionAccessLevels.IsKnown(maxAccessLevel))
            return SessionAccessResolution.Refused(InvalidAccessLevel, $"'{maxAccessLevel}' is not an access level");
        if (!SessionAccessLevels.IsKnown(parentLevel))
            return SessionAccessResolution.Refused(InvalidAccessLevel, $"'{parentLevel}' is not an access level");

        var cap = maxAccessLevel is null ? parentLevel : SessionAccessLevels.Lower(parentLevel, maxAccessLevel);
        var level = requested is null ? parentLevel : SessionAccessLevels.Lower(requested, parentLevel);
        return Clamp(level, policy, supported, cap) with { Cap = cap };
    }

    /// <summary>
    /// The level a session is demoted to when Full stops being enabled, and the level a legacy
    /// session resolves to: its configured default, clamped like a start.
    /// </summary>
    public static SessionAccessResolution ResolveDefault(
        string? agentEntityId,
        SessionAccessPolicy policy,
        IReadOnlyCollection<string> supported,
        string? maxAccessLevel)
        => ResolveStart(null, agentEntityId, personPresent: false, policy, supported, maxAccessLevel);

    /// <summary>
    /// A change to a running or stopped session. A null <paramref name="requested"/> means "leave
    /// plan": it returns to the level stored on entering plan (else the configured default). An
    /// unsupported level becomes ask. Lowering, or staying, is open to anyone with write access.
    /// Raising needs a person, Full enabled for full, and stays at or below the cap. Entering plan
    /// remembers the level it left; leaving plan forgets it.
    /// </summary>
    public static SessionAccessChange ResolveChange(
        SessionAccessState current,
        string? requested,
        string? agentEntityId,
        bool personPresent,
        SessionAccessPolicy policy,
        IReadOnlyCollection<string> supported)
    {
        if (requested is not null && !SessionAccessLevels.IsKnown(requested))
            return SessionAccessChange.Refused(InvalidAccessLevel, $"'{requested}' is not an access level");

        var currentLevel = current.AccessLevel is { } known && SessionAccessLevels.IsKnown(known)
            ? known
            : SessionAccessLevels.Ask;
        if (requested is null)
        {
            if (currentLevel != SessionAccessLevels.Plan)
                return SessionAccessChange.Unchanged(current with { AccessLevel = currentLevel });
            requested = current.AccessLevelBeforePlan is { } remembered && SessionAccessLevels.IsKnown(remembered)
                ? remembered
                : policy.DefaultFor(agentEntityId);
        }

        var target = ClampToSupported(requested, supported);
        if (SessionAccessLevels.IsAbove(target, currentLevel))
        {
            if (!personPresent)
                return SessionAccessChange.Refused(RaiseRequiresPerson,
                    "Raising a session's access level needs you, signed in. Agents can't raise it, and neither can automatic local access, because any program on this computer can use that.");
            if (target == SessionAccessLevels.Full && !policy.FullAccessEnabled)
                return SessionAccessChange.Refused(FullAccessNotEnabled,
                    "Full access is not enabled. Turn it on in RedLeaf Settings, Agent permissions.");
            if (current.MaxAccessLevel is { } cap && SessionAccessLevels.IsAbove(target, cap))
                return SessionAccessChange.Refused(AboveSessionCap,
                    $"This session is capped at '{cap}'.");
        }

        var before = target == SessionAccessLevels.Plan
            ? currentLevel == SessionAccessLevels.Plan ? current.AccessLevelBeforePlan : currentLevel
            : null;
        var next = current with { AccessLevel = target, AccessLevelBeforePlan = before };
        return next == current ? SessionAccessChange.Unchanged(next) : SessionAccessChange.Changed(next);
    }

    /// <summary>
    /// What a session whose level came from the policy moves to under <paramref name="policy"/>: its
    /// configured default, resolved and clamped exactly like a start without a request (a non-person
    /// start gets the default; full needs Full enabled; provider support; the cap). It may raise the
    /// level, because a person set the policy. A session in plan that entered it from another level
    /// stays in plan, and only the level it returns to follows the default. Returns null when the
    /// session's level is explicit, nothing changes, or no supported level honours its cap. The cap,
    /// <c>unattended</c> and the source are kept as they are.
    /// </summary>
    public static SessionAccessState? FollowPolicy(
        SessionAccessState current,
        string? agentEntityId,
        SessionAccessPolicy policy,
        IReadOnlyCollection<string> supported)
    {
        if (!current.FollowsPolicy) return null;
        if (ResolveDefault(agentEntityId, policy, supported, current.MaxAccessLevel).Level is not { } resolved) return null;

        var next = current.AccessLevel == SessionAccessLevels.Plan && current.AccessLevelBeforePlan is not null
            ? current with
            {
                AccessLevelBeforePlan = resolved == SessionAccessLevels.Plan ? current.AccessLevelBeforePlan : resolved,
            }
            : current with { AccessLevel = resolved, AccessLevelBeforePlan = null };
        return next == current ? null : next;
    }

    /// <summary>
    /// What a session keeps when Full is no longer enabled: an unchanged state when nothing it holds
    /// is full, otherwise full (current, or remembered for after plan) replaced by its resolved default.
    /// </summary>
    public static SessionAccessState? DemoteIfFullRevoked(
        SessionAccessState current,
        string? agentEntityId,
        SessionAccessPolicy policy,
        IReadOnlyCollection<string> supported)
    {
        if (policy.FullAccessEnabled) return null;
        var holdsFull = current.AccessLevel == SessionAccessLevels.Full;
        var remembersFull = current.AccessLevelBeforePlan == SessionAccessLevels.Full;
        if (!holdsFull && !remembersFull) return null;

        var fallback = ResolveDefault(agentEntityId, policy, supported, current.MaxAccessLevel).Level
            ?? LowestSupported(supported);
        return current with
        {
            AccessLevel = holdsFull ? fallback : current.AccessLevel,
            AccessLevelBeforePlan = remembersFull ? fallback : current.AccessLevelBeforePlan,
        };
    }

    /// <summary>A level the provider cannot enforce becomes ask (never upward to anything else).</summary>
    public static string ClampToSupported(string level, IReadOnlyCollection<string> supported)
        => supported.Contains(level) ? level
            : supported.Contains(SessionAccessLevels.Ask) ? SessionAccessLevels.Ask
            : LowestSupported(supported);

    private static SessionAccessResolution Clamp(
        string level, SessionAccessPolicy policy, IReadOnlyCollection<string> supported, string? cap)
    {
        if (level == SessionAccessLevels.Full && !policy.FullAccessEnabled)
            level = SessionAccessLevels.Auto;
        level = ClampToSupported(level, supported);
        if (cap is null || !SessionAccessLevels.IsAbove(level, cap))
            return SessionAccessResolution.Resolved(level);

        var capped = SessionAccessLevels.All
            .Where(candidate => supported.Contains(candidate) && !SessionAccessLevels.IsAbove(candidate, cap))
            .LastOrDefault();
        return capped is null
            ? SessionAccessResolution.Refused(CapBelowSupported,
                $"maxAccessLevel '{cap}' is below every level this provider supports ({string.Join(", ", Ordered(supported))})")
            : SessionAccessResolution.Resolved(capped);
    }

    private static string LowestSupported(IReadOnlyCollection<string> supported)
        => Ordered(supported).FirstOrDefault() ?? SessionAccessLevels.Ask;

    private static IEnumerable<string> Ordered(IReadOnlyCollection<string> supported)
        => SessionAccessLevels.All.Where(supported.Contains);
}

/// <param name="Cap">The cap the resolution imposes, when it sets one (a delegated session's parent level).</param>
public sealed record SessionAccessResolution(string? Level, string? Error = null, string? Message = null, string? Cap = null)
{
    public static SessionAccessResolution Resolved(string level) => new(level);
    public static SessionAccessResolution Refused(string error, string message) => new(null, error, message);
}

public sealed record SessionAccessChange(SessionAccessState? State, bool IsChange, string? Error = null, string? Message = null)
{
    public static SessionAccessChange Changed(SessionAccessState state) => new(state, true);
    public static SessionAccessChange Unchanged(SessionAccessState state) => new(state, false);
    public static SessionAccessChange Refused(string error, string message) => new(null, false, error, message);
}
