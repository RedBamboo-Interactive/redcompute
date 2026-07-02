using RedCompute.PluginSdk;

namespace RedCompute.Plugin.OpenCode;

public class OpenCodeSessionStore : IOpenCodeSessionStore
{
    private static AiSessionSnapshot ToSnapshot(OpenCodeSessionRecord r) => new()
    {
        Provider = "opencode",
        Id = r.Id,
        Title = r.Title,
        ProjectName = r.ProjectName,
        ProjectPath = r.ProjectPath,
        Status = r.Status,
        StartedAt = r.StartedAt,
        Model = r.Model,
        ExternalSessionId = r.OpenCodeSessionId,
        MessageCount = r.MessageCount,
        CostUsd = r.CostUsd,
        InputTokens = r.InputTokens,
        OutputTokens = r.OutputTokens,
        ContextWindow = r.ContextWindow,
        Effort = r.Effort,
        JobId = r.JobId,
        Dismissed = r.Dismissed,
        Source = r.Source,
    };

    private static AiMessageSnapshot ToSnapshot(OpenCodeMessageRecord m) => new()
    {
        Provider = "opencode",
        SessionId = m.SessionId,
        Role = m.Role,
        EventType = m.EventType,
        Content = m.Content,
        ToolName = m.ToolName,
        ToolInput = m.ToolInput,
        ToolResult = m.ToolResult,
        MessageId = m.MessageId,
        MessageUid = m.MessageUid,
        Timestamp = m.Timestamp,
        AttachmentsJson = m.AttachmentsJson,
    };

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
            .Where(s => s.Status == "Active" || s.Status == "Starting" || s.Status == "Idle")
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
            existing.ContextWindow = record.ContextWindow;
            existing.JobId = record.JobId;
            existing.Dismissed = record.Dismissed;
            existing.ProcessId = record.ProcessId;
            if (record.LastActivity.HasValue)
                existing.LastActivity = record.LastActivity;
        }
        else
        {
            db.Sessions.Add(record);
        }
        db.SaveChanges();
        SuiteMirror.PublishSession(ToSnapshot(record));
    }

    public void DismissSession(string sessionId)
    {
        using var db = new OpenCodeDbContext();
        var record = db.Sessions.Find(sessionId);
        if (record != null)
        {
            record.Dismissed = true;
            db.SaveChanges();
            SuiteMirror.PublishSession(ToSnapshot(record));
        }
    }

    public void AddMessage(OpenCodeMessageRecord message)
    {
        using var db = new OpenCodeDbContext();
        db.Messages.Add(message);
        db.SaveChanges();
        SuiteMirror.PublishMessages([ToSnapshot(message)]);
    }

    public void AddMessages(List<OpenCodeMessageRecord> messages)
    {
        if (messages.Count == 0) return;
        using var db = new OpenCodeDbContext();
        db.Messages.AddRange(messages);
        db.SaveChanges();
        SuiteMirror.PublishMessages(messages.Select(ToSnapshot).ToList());
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
