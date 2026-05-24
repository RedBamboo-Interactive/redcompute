namespace RedCompute.Plugin.OpenCode;

public interface IOpenCodeSessionStore
{
    OpenCodeSessionRecord? FindSession(string sessionId);
    OpenCodeSessionRecord? FindSessionByJobId(Guid jobId);
    List<OpenCodeSessionRecord> GetActiveSessions();
    List<OpenCodeSessionRecord> GetRecentSessions(HashSet<string> excludeIds, int limit = 20, bool includeDismissed = false);
    void SaveSession(OpenCodeSessionRecord record);
    void DismissSession(string sessionId);
    void AddMessage(OpenCodeMessageRecord message);
    void AddMessages(List<OpenCodeMessageRecord> messages);
    List<OpenCodeMessageRecord> GetMessages(string sessionId, int limit = 50_000);
    Dictionary<Guid, string> GetSessionStatusesByJobIds(IEnumerable<Guid> jobIds);
}
