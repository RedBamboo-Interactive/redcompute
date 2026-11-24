using RedCompute.Core.Sessions;

namespace RedCompute.Plugin.ClaudeCode;

public class ClaudeSessionRecord
{
    public required string Id { get; set; }
    public required string ProjectName { get; set; }
    public required string ProjectPath { get; set; }
    public string? ScratchDirectory { get; set; }
    public Guid? RepositoryId { get; set; }
    public required string Status { get; set; }
    public string? StopReason { get; set; }
    public DateTimeOffset? StoppedAt { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public string? Model { get; set; }
    public string? ClaudeSessionId { get; set; }
    public string? Title { get; set; }
    public int MessageCount { get; set; }
    public double? CostUsd { get; set; }
    public int? InputTokens { get; set; }
    public PromptCacheUsage? PromptCache { get; set; }
    public int? OutputTokens { get; set; }
    public int? CacheReadInputTokens { get; set; }
    public int? CacheCreationInputTokens { get; set; }
    public int? ContextTokens { get; set; }
    public int? ContextWindow { get; set; }
    public string? Effort { get; set; }
    public string? QualityTier { get; set; }
    public string? ProviderEntity { get; set; }
    /// <summary>
    /// The Claude provider instance that runs this session: a dedicated provider's slug, or null for
    /// the shared instance. Several instances share claude.db, and each claims only its own rows, so a
    /// dedicated account's session is never resumed under another instance's binary. Written once at
    /// insert by <see cref="ClaudeSessionStore"/>; saving a session never changes it.
    /// </summary>
    public string? RuntimeOwner { get; set; }
    /// <summary>
    /// The node the session runs on (node zero's id), or null for a row written before node ids or
    /// by a run whose node identity was not persisted: null reads as the legacy node. Written once at
    /// insert by <see cref="ClaudeSessionStore"/>; saving a session never changes it.
    /// </summary>
    public string? NodeId { get; set; }
    /// <summary>The placement references the session was started with (JSON text), set at insert and never changed.</summary>
    public string? Placement { get; set; }
    /// <summary>
    /// Provider-native developer instructions supplied when the session was created. Reapplied
    /// as <c>--append-system-prompt</c> on every CLI launch for this session (start and resume).
    /// </summary>
    public string? DeveloperInstructions { get; set; }
    public Guid? JobId { get; set; }
    public bool Dismissed { get; set; }
    public string? Source { get; set; }
    public string? UserId { get; set; }
    public string? UserName { get; set; }
    public string? UserAvatarUrl { get; set; }
    public int? ProcessId { get; set; }
    public DateTimeOffset? LastActivity { get; set; }
    /// <summary>
    /// Background tasks running when the row was last saved (JSON list of BackgroundTaskInfo).
    /// Recovery reads it to settle tasks that died with their CLI process as `lost`.
    /// </summary>
    public string? BackgroundTasksJson { get; set; }
    /// <summary>
    /// A rollback the CLI has not applied yet (<see cref="ClaudeForkTarget"/>). Written only through
    /// <see cref="IClaudeSessionStore.SetForkTarget"/>, so saving a session never clears it by accident.
    /// </summary>
    public string? ForkTargetJson { get; set; }
    /// <summary>
    /// Access level (plan, ask, auto, full) applied on every launch. Null for a row written before
    /// access levels: it resolves fresh from policy, and the old hardcoded bypass is never trusted.
    /// </summary>
    public string? AccessLevel { get; set; }
    public string? MaxAccessLevel { get; set; }
    public string? AccessLevelBeforePlan { get; set; }
    public bool Unattended { get; set; }
    /// <summary>Output is delivered as one message: the CLI launches without the silent-turn reminder.</summary>
    public bool DeliveredAsOneMessage { get; set; }
    /// <summary>
    /// <c>policy</c> or <c>explicit</c> (<see cref="Core.Sessions.SessionAccessLevelSources"/>). Null on
    /// rows written before sources existed, which count as policy.
    /// </summary>
    public string? AccessLevelSource { get; set; }
}
