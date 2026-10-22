namespace RedCompute.Plugin.ClaudeCode;

public interface IClaudeSessionStore
{
    ClaudeSessionRecord? FindSession(string sessionId);
    ClaudeSessionRecord? FindSessionByJobId(Guid jobId);
    List<ClaudeSessionRecord> GetActiveSessions();
    /// <summary>
    /// Rows written by builds before 8cd57bb when an idle process died: Error with the previous
    /// turn's "completed" reason. They are skipped by active recovery and block queued input.
    /// </summary>
    List<ClaudeSessionRecord> GetStrandedIdleExitSessions() => [];
    /// <summary>Rows that still list running background tasks; recovery settles them as lost.</summary>
    List<ClaudeSessionRecord> GetSessionsWithBackgroundTasks() => [];
    /// <summary>
    /// The logical turn of a persisted tool call, for a background child whose launching tool call
    /// is not in the current process's memory.
    /// </summary>
    string? FindToolUseMessageUid(string sessionId, string toolUseId)
        => GetMessages(sessionId).LastOrDefault(message =>
            message.EventType == "tool_use" && message.MessageId == toolUseId)?.MessageUid;
    List<ClaudeSessionRecord> GetRecentSessions(HashSet<string> excludeIds, int limit = 20, bool includeDismissed = false);
    void SaveSession(ClaudeSessionRecord record);
    void DismissSession(string sessionId);
    void AddMessage(ClaudeMessageRecord message);
    void AddMessages(List<ClaudeMessageRecord> messages);
    List<ClaudeMessageRecord> GetMessages(string sessionId, int limit = 50_000);
    DateTimeOffset GetLastMessageTimestamp(string sessionId);
    Dictionary<Guid, string> GetSessionStatusesByJobIds(IEnumerable<Guid> jobIds);
}
