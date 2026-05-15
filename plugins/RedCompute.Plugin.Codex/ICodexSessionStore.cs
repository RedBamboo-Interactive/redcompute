namespace RedCompute.Plugin.Codex;

public interface ICodexSessionStore
{
    CodexSessionRecord? FindSession(string sessionId);
    CodexSessionRecord? FindSessionByJobId(Guid jobId);
    List<CodexSessionRecord> GetActiveSessions();
    List<CodexSessionRecord> GetRecentSessions(HashSet<string> excludeIds, int limit = 20, bool includeDismissed = false);
    void SaveSession(CodexSessionRecord record);
    void DismissSession(string sessionId);
    void AddMessage(CodexMessageRecord message);
    void AddMessages(List<CodexMessageRecord> messages);
    List<CodexMessageRecord> GetMessages(string sessionId, int limit = 50_000);
    Dictionary<Guid, string> GetSessionStatusesByJobIds(IEnumerable<Guid> jobIds);
}
