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
    List<ClaudeSessionRecord> GetRecentSessions(HashSet<string> excludeIds, int limit = 20, bool includeDismissed = false);
    void SaveSession(ClaudeSessionRecord record);
    void DismissSession(string sessionId);
    void AddMessage(ClaudeMessageRecord message);
    void AddMessages(List<ClaudeMessageRecord> messages);
    List<ClaudeMessageRecord> GetMessages(string sessionId, int limit = 50_000);
    DateTimeOffset GetLastMessageTimestamp(string sessionId);
    Dictionary<Guid, string> GetSessionStatusesByJobIds(IEnumerable<Guid> jobIds);
}
