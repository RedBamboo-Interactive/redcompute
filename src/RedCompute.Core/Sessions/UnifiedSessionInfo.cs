namespace RedCompute.Core.Sessions;

public class UnifiedSessionInfo
{
    public required string Id { get; init; }
    public required string Provider { get; init; }
    public string? ProviderEntity { get; set; }
    public required string ProjectName { get; init; }
    public required string ProjectPath { get; init; }
    public Guid? RepositoryId { get; set; }
    public SessionStatus Status { get; set; }
    public string? StopReason { get; set; }
    public DateTimeOffset? StoppedAt { get; set; }
    public DateTimeOffset StartedAt { get; init; }
    public string? Model { get; set; }
    public string? ProviderSessionId { get; set; }
    public string? Title { get; set; }
    /// <summary>
    /// Who set <see cref="Title"/>: <c>manual</c> (a person renamed it), <c>generated</c> (an explicit
    /// fast-tier title update) or <c>provider</c> (the provider's own titling). Provider titling never
    /// replaces a manual or generated title. Null while the session has no title.
    /// </summary>
    public string? TitleSource { get; set; }
    public int MessageCount { get; set; }
    public double? CostUsd { get; set; }
    public bool CostEstimated { get; set; }
    public int? InputTokens { get; set; }
    public int? OutputTokens { get; set; }
    public int? CachedInputTokens { get; set; }
    public int? ContextTokens { get; set; }
    public int? ContextWindow { get; set; }
    public string? Effort { get; set; }
    public string? QualityTier { get; set; }
    public Guid? JobId { get; set; }
    // Provider-native mode string (Claude: plan, acceptEdits, auto, bypassPermissions). Describes
    // what the provider runs; AccessLevel is the provider-neutral setting it was derived from.
    public string? PermissionMode { get; set; }
    // SessionAccessLevels: plan, ask, auto or full. Persisted with the provider session and applied
    // on every launch. Null for a session persisted before access levels and not yet relaunched.
    public string? AccessLevel { get; set; }
    // Hard cap set at start; nothing raises the session above it. Null when uncapped.
    public string? MaxAccessLevel { get; set; }
    // The levels this session's provider can enforce, lowest first.
    public IReadOnlyList<string> SupportedAccessLevels { get; set; } = [];
    // No person watches this session: permission asks are denied at once and never park.
    public bool Unattended { get; set; }
    // The level the session returns to when it leaves plan without naming one.
    public string? AccessLevelBeforePlan { get; set; }
    // SessionAccessLevelSources: "policy" when the level came from the Agent's or global default
    // (the session follows that default when it changes), "explicit" when someone chose it,
    // "parent" when it was inherited at start from the delegating session (and caps it).
    public string? AccessLevelSource { get; set; }
    public string? Source { get; set; }
    public string? ExecutionProfile { get; set; }
    public string? UserId { get; set; }
    public string? UserName { get; set; }
    public string? UserAvatarUrl { get; set; }
    public string? OwnerAgentId { get; set; }
    public bool Confidential { get; set; }
    // Hidden from default listings (archived); POST /ai-session/sessions/{id}/restore clears it.
    public bool Dismissed { get; set; }
    public Dictionary<string, object?>? ProviderMetadata { get; set; }
    public bool SupportsSteering { get; set; }
    // Whether this session can be rolled back at all (provider capability plus any per-session
    // limit, such as a Codex thread created before paginated history). Whether it can be rolled
    // back right now also needs the session to be idle; the route reports what is in the way.
    public bool SupportsRollback { get; set; }
    // Live background work (subagents, background shells) still running. It never makes the
    // session Active. Overlaid from the provider on every read; empty when nothing runs.
    public IReadOnlyList<BackgroundTaskInfo> BackgroundTasks { get; set; } = [];
    // What the session is doing now (SessionWorkStates): working, awaiting_answer, background or
    // idle. Live projection overlaid on reads and events, never persisted; Status stays the turn state.
    public string? WorkState { get; set; }
    // Provider-reported outcome of the most recent turn (SessionTurnOutcomes) and when it ended.
    // Live provider state; an outcome older than the turn being closed is not that turn's.
    public string? LastTurnOutcome { get; set; }
    public DateTimeOffset? LastTurnEndedAt { get; set; }
    // The provider's error when that turn failed (SessionTurnErrors); null for any other outcome.
    public string? LastTurnError { get; set; }
    // RedCompute's record of the most recent turn: outcome plus the interrupt that ended it, if
    // any. Overlaid on reads and events, never provider-written.
    public SessionTurnSummary? LastTurn { get; set; }
}
