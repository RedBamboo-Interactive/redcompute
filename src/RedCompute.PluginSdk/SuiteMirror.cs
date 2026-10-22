using RedCompute.Core.Sessions;

namespace RedCompute.PluginSdk;

/// <summary>
/// Unified snapshot of an AI session, provider-agnostic. Plugins publish one
/// on every session save; the app mirrors it to RedLeaf as an `ai-session`
/// entity. Fields a provider doesn't track stay null.
/// </summary>
public sealed class AiSessionSnapshot
{
    public required string Provider { get; init; }
    public string? ProviderEntity { get; init; }
    public required string Id { get; init; }
    public string? Title { get; init; }
    public string ProjectName { get; init; } = "";
    public string? ProjectPath { get; init; }
    public Guid? RepositoryId { get; init; }
    public string Status { get; init; } = "";
    public string? StopReason { get; init; }
    public DateTimeOffset StartedAt { get; init; }
    public string? Model { get; init; }
    public string? ExternalSessionId { get; init; }
    public int MessageCount { get; init; }
    public double? CostUsd { get; init; }
    public int? InputTokens { get; init; }
    public int? OutputTokens { get; init; }
    public int? CacheReadInputTokens { get; init; }
    public int? CacheCreationInputTokens { get; init; }
    public int? ContextTokens { get; init; }
    public int? ContextWindow { get; init; }
    public string? Effort { get; init; }
    public string? QualityTier { get; init; }
    public Guid? JobId { get; init; }
    public bool Dismissed { get; init; }
    public string? Source { get; init; }
    public string? ExecutionProfile { get; init; }
    public string? UserId { get; init; }
}

/// <summary>Unified snapshot of a session message/tool event.</summary>
public sealed class AiMessageSnapshot
{
    public required string Provider { get; init; }
    public required string SessionId { get; init; }
    public string Role { get; init; } = "";
    public string EventType { get; init; } = "";
    public string? Content { get; init; }
    public string? ToolName { get; init; }
    public string? ToolInput { get; init; }
    public string? ToolResult { get; init; }
    public TranscriptPayloadRef? PayloadRef { get; init; }
    public string? MessageId { get; init; }
    // Existing provider-native identity, used only to retry compact checkpoints.
    public string? ProviderPartId { get; init; }
    public string? Phase { get; init; }
    // Provider-neutral message uid minted by RedCompute; shared by all
    // events of one assistant turn and carried into the RedLeaf mirror.
    public string? MessageUid { get; init; }
    // Launching tool call of the background work this record belongs to (see
    // UnifiedStreamEvent.ParentToolUseId); mirrored as `parent_tool_use_id`.
    public string? ParentToolUseId { get; init; }
    public TurnOrigin? TurnOrigin { get; init; }
    public DateTimeOffset Timestamp { get; init; }
    public string? AttachmentsJson { get; init; }
}

/// <summary>
/// Static publish hooks plugins call from their session stores so the app can
/// mirror session data to RedLeaf without plugins referencing AppHost. The
/// app assigns the delegates at startup; publishing is fire-and-forget and a
/// failing subscriber never breaks the plugin's local write.
/// </summary>
public static class SuiteMirror
{
    public static Action<AiSessionSnapshot>? SessionUpserted { get; set; }
    public static Action<IReadOnlyList<AiMessageSnapshot>>? MessagesAdded { get; set; }

    /// <summary>Publish a complete semantic prefix before further live events.
    /// Unlike the asynchronous mirror, failure propagates so the provider can
    /// retry the same native parts and history cannot expose half a checkpoint.</summary>
    public static Action<IReadOnlyList<AiMessageSnapshot>>? CompletedMessagesAdded { get; set; }

    private static readonly object InstallGate = new();
    private static bool _installed;
    private static List<Action> _whenInstalled = [];

    /// <summary>
    /// Runs <paramref name="action"/> once the app has installed the mirror delegates, or now if it
    /// already has. Providers are constructed before the mirror exists, so durable records written
    /// by constructor-time recovery would otherwise never reach the suite read model.
    /// </summary>
    public static void WhenInstalled(Action action)
    {
        lock (InstallGate)
        {
            if (!_installed)
            {
                _whenInstalled.Add(action);
                return;
            }
        }
        RunSafely(action);
    }

    /// <summary>Called by the app after assigning the delegates; flushes deferred work in order.</summary>
    public static void MarkInstalled()
    {
        List<Action> pending;
        lock (InstallGate)
        {
            _installed = true;
            pending = _whenInstalled;
            _whenInstalled = [];
        }
        foreach (var action in pending) RunSafely(action);
    }

    /// <summary>Called by the app when it removes the delegates.</summary>
    public static void MarkUninstalled()
    {
        lock (InstallGate) _installed = false;
    }

    private static void RunSafely(Action action)
    {
        try { action(); }
        catch { /* deferred mirror work must never break startup */ }
    }

    public static void PublishCompletedMessages(IReadOnlyList<AiMessageSnapshot> messages)
    {
        if (messages.Count > 0) CompletedMessagesAdded?.Invoke(messages);
    }

    public static void PublishSession(AiSessionSnapshot snapshot)
    {
        try { SessionUpserted?.Invoke(snapshot); }
        catch { /* mirroring must never break the local write path */ }
    }

    public static void PublishMessages(IReadOnlyList<AiMessageSnapshot> messages)
    {
        if (messages.Count == 0) return;
        try { MessagesAdded?.Invoke(messages); }
        catch { /* mirroring must never break the local write path */ }
    }
}
