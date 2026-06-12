namespace RedCompute.PluginSdk;

/// <summary>
/// Unified snapshot of an AI session, provider-agnostic. Plugins publish one
/// on every session save; the app mirrors it to RedLeaf as an `ai-session`
/// entity. Fields a provider doesn't track stay null.
/// </summary>
public sealed class AiSessionSnapshot
{
    public required string Provider { get; init; }
    public required string Id { get; init; }
    public string? Title { get; init; }
    public string ProjectName { get; init; } = "";
    public string? ProjectPath { get; init; }
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
    public Guid? JobId { get; init; }
    public bool Dismissed { get; init; }
    public string? Source { get; init; }
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
    public string? MessageId { get; init; }
    public DateTimeOffset Timestamp { get; init; }
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
