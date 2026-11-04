namespace RedCompute.Plugin.Codex;

public interface ICodexSessionStore
{
    CodexSessionRecord? FindSession(string sessionId);
    CodexSessionRecord? FindSessionByJobId(Guid jobId);
    List<CodexSessionRecord> GetActiveSessions();
    List<CodexSessionRecord> GetSessionsWithStopReason(string status, string stopReason);
    List<CodexSessionRecord> GetSessionsWithoutJobs();
    List<CodexSessionRecord> GetRecentSessions(HashSet<string> excludeIds, int limit = 20, bool includeDismissed = false);
    void SaveSession(CodexSessionRecord record);
    void DismissSession(string sessionId);
    void AddMessage(CodexMessageRecord message);
    void AddMessages(List<CodexMessageRecord> messages);
    List<CodexMessageRecord> GetMessages(string sessionId, int limit = 50_000);
    void SetUserTurnId(string sessionId, string messageUid, string turnId) { }
    /// <summary>Reads through rolled-back rows too: a repeated rollback must still find its turn.</summary>
    string? GetUserTurnId(string sessionId, string messageUid) => null;
    /// <summary>Marks this user message and everything after it. They leave every read and stay on disk.</summary>
    void MarkMessagesRolledBack(string sessionId, string messageUid) { }
    Dictionary<Guid, string> GetSessionStatusesByJobIds(IEnumerable<Guid> jobIds);
}
