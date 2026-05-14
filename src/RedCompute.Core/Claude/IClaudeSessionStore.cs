namespace RedCompute.Core.Claude;

public interface IClaudeSessionStore
{
    ClaudeSessionRecord? FindSession(string sessionId);
    ClaudeSessionRecord? FindSessionByJobId(Guid jobId);
    List<ClaudeSessionRecord> GetActiveSessions();
    List<ClaudeSessionRecord> GetRecentSessions(HashSet<string> excludeIds, int limit = 20);
    void SaveSession(ClaudeSessionRecord record);
    void DismissSession(string sessionId);
    void AddMessage(ClaudeMessageRecord message);
    void AddMessages(List<ClaudeMessageRecord> messages);
    List<ClaudeMessageRecord> GetMessages(string sessionId, int limit = 50_000);
    DateTimeOffset GetLastMessageTimestamp(string sessionId);
    Dictionary<Guid, string> GetSessionStatusesByJobIds(IEnumerable<Guid> jobIds);
}
