namespace RedCompute.Plugin.Codex;

public class CodexSessionStore : ICodexSessionStore
{
    public CodexSessionRecord? FindSession(string sessionId)
    {
        using var db = new CodexDbContext();
        return db.Sessions.Find(sessionId);
    }

    public CodexSessionRecord? FindSessionByJobId(Guid jobId)
    {
        using var db = new CodexDbContext();
        return db.Sessions.FirstOrDefault(s => s.JobId == jobId);
    }

    public List<CodexSessionRecord> GetActiveSessions()
    {
        using var db = new CodexDbContext();
        return db.Sessions
            .Where(s => s.Status == "Active" || s.Status == "Starting")
            .ToList();
    }

    public List<CodexSessionRecord> GetRecentSessions(HashSet<string> excludeIds, int limit = 20, bool includeDismissed = false)
    {
        using var db = new CodexDbContext();
        var query = db.Sessions.Where(s => !excludeIds.Contains(s.Id));
        if (!includeDismissed) query = query.Where(s => !s.Dismissed);
        return query
            .OrderByDescending(s => s.StartedAt)
            .Take(limit)
            .ToList();
    }

    public void SaveSession(CodexSessionRecord record)
    {
        using var db = new CodexDbContext();
        var existing = db.Sessions.Find(record.Id);
        if (existing != null)
        {
            existing.ProjectName = record.ProjectName;
            existing.ProjectPath = record.ProjectPath;
            existing.Status = record.Status;
            existing.StartedAt = record.StartedAt;
            existing.Model = record.Model;
            existing.Title = record.Title;
            existing.MessageCount = record.MessageCount;
            existing.CostUsd = record.CostUsd;
            existing.InputTokens = record.InputTokens;
            existing.OutputTokens = record.OutputTokens;
            existing.CachedInputTokens = record.CachedInputTokens;
            existing.JobId = record.JobId;
            existing.Dismissed = record.Dismissed;
        }
        else
        {
            db.Sessions.Add(record);
        }
        db.SaveChanges();
    }

    public void DismissSession(string sessionId)
    {
        using var db = new CodexDbContext();
        var record = db.Sessions.Find(sessionId);
        if (record != null)
        {
            record.Dismissed = true;
            db.SaveChanges();
        }
    }

    public void AddMessage(CodexMessageRecord message)
    {
        using var db = new CodexDbContext();
        db.Messages.Add(message);
        db.SaveChanges();
    }

    public void AddMessages(List<CodexMessageRecord> messages)
    {
        if (messages.Count == 0) return;
        using var db = new CodexDbContext();
        db.Messages.AddRange(messages);
        db.SaveChanges();
    }

    public List<CodexMessageRecord> GetMessages(string sessionId, int limit = 50_000)
    {
        using var db = new CodexDbContext();
        return db.Messages
            .Where(m => m.SessionId == sessionId)
            .OrderByDescending(m => m.Id)
            .Take(limit)
            .ToList()
            .OrderBy(m => m.Id)
            .ToList();
    }

    public Dictionary<Guid, string> GetSessionStatusesByJobIds(IEnumerable<Guid> jobIds)
    {
        var jobIdSet = new HashSet<Guid>(jobIds);
        using var db = new CodexDbContext();
        return db.Sessions
            .Where(s => s.JobId != null && jobIdSet.Contains(s.JobId.Value))
            .Select(s => new { s.JobId, s.Status })
            .ToList()
            .ToDictionary(r => r.JobId!.Value, r => r.Status);
    }
}
