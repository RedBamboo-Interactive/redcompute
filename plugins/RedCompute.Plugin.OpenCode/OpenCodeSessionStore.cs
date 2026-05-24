namespace RedCompute.Plugin.OpenCode;

public class OpenCodeSessionStore : IOpenCodeSessionStore
{
    public OpenCodeSessionRecord? FindSession(string sessionId)
    {
        using var db = new OpenCodeDbContext();
        return db.Sessions.Find(sessionId);
    }

    public OpenCodeSessionRecord? FindSessionByJobId(Guid jobId)
    {
        using var db = new OpenCodeDbContext();
        return db.Sessions.FirstOrDefault(s => s.JobId == jobId);
    }

    public List<OpenCodeSessionRecord> GetActiveSessions()
    {
        using var db = new OpenCodeDbContext();
        return db.Sessions
            .Where(s => s.Status == "Active" || s.Status == "Starting")
            .ToList();
    }

    public List<OpenCodeSessionRecord> GetRecentSessions(HashSet<string> excludeIds, int limit = 20, bool includeDismissed = false)
    {
        using var db = new OpenCodeDbContext();
        var query = db.Sessions.Where(s => !excludeIds.Contains(s.Id));
        if (!includeDismissed) query = query.Where(s => !s.Dismissed);
        return query
            .OrderByDescending(s => s.StartedAt)
            .Take(limit)
            .ToList();
    }

    public void SaveSession(OpenCodeSessionRecord record)
    {
        using var db = new OpenCodeDbContext();
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
        using var db = new OpenCodeDbContext();
        var record = db.Sessions.Find(sessionId);
        if (record != null)
        {
            record.Dismissed = true;
            db.SaveChanges();
        }
    }

    public void AddMessage(OpenCodeMessageRecord message)
    {
        using var db = new OpenCodeDbContext();
        db.Messages.Add(message);
        db.SaveChanges();
    }

    public void AddMessages(List<OpenCodeMessageRecord> messages)
    {
        if (messages.Count == 0) return;
        using var db = new OpenCodeDbContext();
        db.Messages.AddRange(messages);
        db.SaveChanges();
    }

    public List<OpenCodeMessageRecord> GetMessages(string sessionId, int limit = 50_000)
    {
        using var db = new OpenCodeDbContext();
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
        using var db = new OpenCodeDbContext();
        return db.Sessions
            .Where(s => s.JobId != null && jobIdSet.Contains(s.JobId.Value))
            .Select(s => new { s.JobId, s.Status })
            .ToList()
            .ToDictionary(r => r.JobId!.Value, r => r.Status);
    }
}
