using RedCompute.PluginSdk;

namespace RedCompute.Plugin.ClaudeCode;

public class ClaudeSessionStore : IClaudeSessionStore
{
    private static AiSessionSnapshot ToSnapshot(ClaudeSessionRecord r) => new()
    {
        Provider = "claude-code",
        Id = r.Id,
        Title = r.Title,
        ProjectName = r.ProjectName,
        ProjectPath = r.ProjectPath,
        Status = r.Status,
        StopReason = r.StopReason,
        StartedAt = r.StartedAt,
        Model = r.Model,
        ExternalSessionId = r.ClaudeSessionId,
        MessageCount = r.MessageCount,
        CostUsd = r.CostUsd,
        InputTokens = r.InputTokens,
        OutputTokens = r.OutputTokens,
        CacheReadInputTokens = r.CacheReadInputTokens,
        CacheCreationInputTokens = r.CacheCreationInputTokens,
        ContextTokens = r.ContextTokens,
        ContextWindow = r.ContextWindow,
        Effort = r.Effort,
        JobId = r.JobId,
        Dismissed = r.Dismissed,
        Source = r.Source,
    };

    private static AiMessageSnapshot ToSnapshot(ClaudeMessageRecord m) => new()
    {
        Provider = "claude-code",
        SessionId = m.SessionId,
        Role = m.Role,
        EventType = m.EventType,
        Content = m.Content,
        ToolName = m.ToolName,
        ToolInput = m.ToolInput,
        ToolResult = m.ToolResult,
        MessageId = m.MessageId,
        Timestamp = m.Timestamp,
    };

    public ClaudeSessionRecord? FindSession(string sessionId)
    {
        using var db = new ClaudeDbContext();
        return db.Sessions.Find(sessionId);
    }

    public ClaudeSessionRecord? FindSessionByJobId(Guid jobId)
    {
        using var db = new ClaudeDbContext();
        return db.Sessions.FirstOrDefault(s => s.JobId == jobId);
    }

    public List<ClaudeSessionRecord> GetActiveSessions()
    {
        using var db = new ClaudeDbContext();
        return db.Sessions
            .Where(s => s.Status == "Active" || s.Status == "Idle" || s.Status == "Starting")
            .ToList();
    }

    public List<ClaudeSessionRecord> GetRecentSessions(HashSet<string> excludeIds, int limit = 20, bool includeDismissed = false)
    {
        using var db = new ClaudeDbContext();
        var query = db.Sessions.Where(s => !excludeIds.Contains(s.Id));
        if (!includeDismissed) query = query.Where(s => !s.Dismissed);
        return query
            .OrderByDescending(s => s.StartedAt)
            .Take(limit)
            .ToList();
    }

    public void SaveSession(ClaudeSessionRecord record)
    {
        using var db = new ClaudeDbContext();
        var existing = db.Sessions.Find(record.Id);
        if (existing != null)
        {
            existing.ProjectName = record.ProjectName;
            existing.ProjectPath = record.ProjectPath;
            existing.Status = record.Status;
            existing.StopReason = record.StopReason;
            existing.StartedAt = record.StartedAt;
            existing.Model = record.Model;
            existing.ClaudeSessionId = record.ClaudeSessionId;
            existing.Title = record.Title;
            existing.MessageCount = record.MessageCount;
            existing.CostUsd = record.CostUsd;
            existing.InputTokens = record.InputTokens;
            existing.OutputTokens = record.OutputTokens;
            existing.CacheReadInputTokens = record.CacheReadInputTokens;
            existing.CacheCreationInputTokens = record.CacheCreationInputTokens;
            existing.ContextTokens = record.ContextTokens;
            existing.ContextWindow = record.ContextWindow;
            existing.Effort = record.Effort;
            existing.JobId = record.JobId;
            existing.Dismissed = record.Dismissed;
            existing.Source = record.Source;
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
        using var db = new ClaudeDbContext();
        var record = db.Sessions.Find(sessionId);
        if (record != null)
        {
            record.Dismissed = true;
            db.SaveChanges();
            SuiteMirror.PublishSession(ToSnapshot(record));
        }
    }

    public void AddMessage(ClaudeMessageRecord message)
    {
        using var db = new ClaudeDbContext();
        db.Messages.Add(message);
        db.SaveChanges();
        SuiteMirror.PublishMessages([ToSnapshot(message)]);
    }

    public void AddMessages(List<ClaudeMessageRecord> messages)
    {
        if (messages.Count == 0) return;
        using var db = new ClaudeDbContext();
        db.Messages.AddRange(messages);
        db.SaveChanges();
        SuiteMirror.PublishMessages(messages.Select(ToSnapshot).ToList());
    }

    public List<ClaudeMessageRecord> GetMessages(string sessionId, int limit = 50_000)
    {
        using var db = new ClaudeDbContext();
        return db.Messages
            .Where(m => m.SessionId == sessionId)
            .OrderByDescending(m => m.Id)
            .Take(limit)
            .ToList()
            .OrderBy(m => m.Id)
            .ToList();
    }

    public DateTimeOffset GetLastMessageTimestamp(string sessionId)
    {
        using var db = new ClaudeDbContext();
        return db.Messages
            .Where(m => m.SessionId == sessionId)
            .OrderByDescending(m => m.Timestamp)
            .Select(m => m.Timestamp)
            .FirstOrDefault();
    }

    public Dictionary<Guid, string> GetSessionStatusesByJobIds(IEnumerable<Guid> jobIds)
    {
        var jobIdSet = new HashSet<Guid>(jobIds);
        using var db = new ClaudeDbContext();
        return db.Sessions
            .Where(s => s.JobId != null && jobIdSet.Contains(s.JobId.Value))
            .Select(s => new { s.JobId, s.Status })
            .ToList()
            .ToDictionary(r => r.JobId!.Value, r => r.Status);
    }
}
