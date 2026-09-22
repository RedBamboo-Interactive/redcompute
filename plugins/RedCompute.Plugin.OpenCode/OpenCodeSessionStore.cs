using RedCompute.PluginSdk;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace RedCompute.Plugin.OpenCode;

public class OpenCodeSessionStore : IOpenCodeSessionStore
{
    private static AiSessionSnapshot ToSnapshot(OpenCodeSessionRecord r) => new()
    {
        Provider = "opencode",
        ProviderEntity = r.ProviderEntity,
        Id = r.Id,
        Title = r.Title,
        ProjectName = r.ProjectName,
        ProjectPath = r.ProjectPath,
        RepositoryId = r.RepositoryId,
        Status = r.Status,
        StopReason = r.StopReason,
        StartedAt = r.StartedAt,
        Model = r.Model,
        ExternalSessionId = r.OpenCodeSessionId,
        MessageCount = r.MessageCount,
        CostUsd = r.CostUsd,
        InputTokens = r.InputTokens,
        OutputTokens = r.OutputTokens,
        ContextWindow = r.ContextWindow,
        Effort = r.Effort,
        QualityTier = r.QualityTier,
        JobId = r.JobId,
        Dismissed = r.Dismissed,
        Source = r.Source,
        UserId = r.UserId,
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
        ProviderPartId = m.ProviderPartId,
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
            existing.RepositoryId = record.RepositoryId;
            existing.Status = record.Status;
            existing.StopReason = record.StopReason;
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
            existing.QualityTier = record.QualityTier;
            existing.ProviderEntity = record.ProviderEntity;
            existing.Source = record.Source;
            existing.UserId = record.UserId;
            existing.UserName = record.UserName;
            existing.UserAvatarUrl = record.UserAvatarUrl;
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
        try
        {
            db.SaveChanges();
        }
        catch (DbUpdateException ex) when (IsDuplicateProviderPart(ex, message.ProviderPartId))
        {
            return;
        }
        SuiteMirror.PublishMessages([ToSnapshot(message)]);
    }

    public void AddMessages(List<OpenCodeMessageRecord> messages)
    {
        if (messages.Count == 0) return;
        // Persist locally first, then mirror the whole semantic prefix. Publish
        // duplicates too: a prior mirror may have failed after the local commit.
        foreach (var message in messages)
        {
            using var db = new OpenCodeDbContext();
            if (message.ProviderPartId is { } partId && db.Messages.Any(existing =>
                    existing.SessionId == message.SessionId && existing.ProviderPartId == partId
                    && existing.EventType == message.EventType)) continue;
            db.Messages.Add(message);
            try { db.SaveChanges(); }
            catch (DbUpdateException ex) when (IsDuplicateProviderPart(ex, message.ProviderPartId)) { }
        }
        PublishCompletedMessagesWithParent(messages, FindSession(messages[0].SessionId) is not null);
    }

    /// <summary>
    /// Completed transcript records are children of an ai-session entity in RedLeaf. Persistent
    /// sessions publish that parent from SaveSession, while stateless executions intentionally
    /// have no local session row. Publish a minimal one-shot parent before the checkpoint so the
    /// transcript pipeline can flush the parent upsert before appending its first message.
    /// </summary>
    internal static void PublishCompletedMessagesWithParent(
        IReadOnlyList<OpenCodeMessageRecord> messages,
        bool sessionExists)
    {
        if (messages.Count == 0) return;

        if (!sessionExists)
        {
            var first = messages[0];
            _ = Guid.TryParse(first.SessionId, out var jobId);
            SuiteMirror.PublishSession(new AiSessionSnapshot
            {
                Provider = "opencode",
                Id = first.SessionId,
                Title = "OpenCode one-shot execution",
                ProjectName = "One-shot execution",
                Status = "Stopped",
                StartedAt = messages.Min(message => message.Timestamp),
                MessageCount = messages.Count,
                JobId = jobId == Guid.Empty ? null : jobId,
            });
        }

        SuiteMirror.PublishCompletedMessages(messages.Select(ToSnapshot).ToList());
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

    private static bool IsDuplicateProviderPart(DbUpdateException exception, string? providerPartId) =>
        providerPartId != null
        && exception.InnerException is SqliteException { SqliteExtendedErrorCode: 2067 };
}
