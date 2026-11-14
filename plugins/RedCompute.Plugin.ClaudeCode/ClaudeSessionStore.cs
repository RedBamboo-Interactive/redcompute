using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using RedCompute.Core.Sessions;
using RedCompute.PluginSdk;

namespace RedCompute.Plugin.ClaudeCode;

public class ClaudeSessionStore : IClaudeSessionStore
{
    private readonly string? _owner;

    /// <summary>The store the shared Claude instance uses: rows with no runtime owner.</summary>
    public ClaudeSessionStore() : this(null) { }

    /// <summary>
    /// A view of claude.db that holds only <paramref name="runtimeOwner"/>'s sessions (null = the
    /// shared instance). Every session-row read is filtered and every inserted row is stamped, so two
    /// provider instances never claim, recover, list or resume each other's sessions. Message reads go
    /// by a session id the caller already owns.
    /// </summary>
    public ClaudeSessionStore(string? runtimeOwner)
        => _owner = string.IsNullOrWhiteSpace(runtimeOwner) ? null : runtimeOwner.Trim();

    public string? RuntimeOwner => _owner;

    private IQueryable<ClaudeSessionRecord> Owned(ClaudeDbContext db)
    {
        var owner = _owner;
        return owner is null
            ? db.Sessions.Where(s => s.RuntimeOwner == null)
            : db.Sessions.Where(s => s.RuntimeOwner == owner);
    }

    /// <summary>
    /// Gives a dedicated instance the rows written for its provider entity before rows carried an
    /// owner. Without it those sessions stay with the shared instance and resume on its account.
    /// Returns the number of rows adopted.
    /// </summary>
    public static int AdoptLegacySessions(string runtimeOwner, IEnumerable<string?> entityReferences)
    {
        var references = entityReferences
            .Where(reference => !string.IsNullOrWhiteSpace(reference))
            .Select(reference => reference!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (string.IsNullOrWhiteSpace(runtimeOwner) || references.Count == 0) return 0;

        using var db = new ClaudeDbContext();
        var conn = db.Database.GetDbConnection();
        conn.Open();
        using var cmd = conn.CreateCommand();
        var names = new List<string>();
        for (var i = 0; i < references.Count; i++)
        {
            var parameter = cmd.CreateParameter();
            parameter.ParameterName = $"$r{i}";
            parameter.Value = references[i];
            cmd.Parameters.Add(parameter);
            names.Add(parameter.ParameterName);
        }
        var owner = cmd.CreateParameter();
        owner.ParameterName = "$owner";
        owner.Value = runtimeOwner.Trim();
        cmd.Parameters.Add(owner);
        cmd.CommandText = "UPDATE Sessions SET RuntimeOwner = $owner WHERE RuntimeOwner IS NULL "
            + $"AND ProviderEntity COLLATE NOCASE IN ({string.Join(", ", names)})";
        return cmd.ExecuteNonQuery();
    }

    private static AiSessionSnapshot ToSnapshot(ClaudeSessionRecord r) => new()
    {
        Provider = "claude-code",
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
        QualityTier = r.QualityTier,
        JobId = r.JobId,
        Dismissed = r.Dismissed,
        Source = r.Source,
        UserId = r.UserId,
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
        Phase = m.Phase,
        MessageUid = m.MessageUid,
        ParentToolUseId = m.ParentToolUseId,
        TurnOrigin = ReadTurnOrigin(m.TurnOriginJson),
        Timestamp = m.Timestamp,
        AttachmentsJson = m.AttachmentsJson,
    };

    internal static TurnOrigin? ReadTurnOrigin(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<TurnOrigin>(json); }
        catch (JsonException) { return null; }
    }

    public ClaudeSessionRecord? FindSession(string sessionId)
    {
        using var db = new ClaudeDbContext();
        // Read-only: the context is disposed on return, so tracking the row would buy nothing.
        return Owned(db).AsNoTracking().FirstOrDefault(s => s.Id == sessionId);
    }

    public ClaudeSessionRecord? FindSessionByJobId(Guid jobId)
    {
        using var db = new ClaudeDbContext();
        return Owned(db).FirstOrDefault(s => s.JobId == jobId);
    }

    public List<ClaudeSessionRecord> GetActiveSessions()
    {
        using var db = new ClaudeDbContext();
        return Owned(db)
            .Where(s => s.Status == "Active" || s.Status == "Idle" || s.Status == "Starting")
            .ToList();
    }

    public List<ClaudeSessionRecord> GetStrandedIdleExitSessions()
    {
        using var db = new ClaudeDbContext();
        return Owned(db)
            .Where(s => s.Status == "Error" && s.StopReason == "completed")
            .ToList();
    }

    public List<ClaudeSessionRecord> GetSessionsWithBackgroundTasks()
    {
        using var db = new ClaudeDbContext();
        return Owned(db)
            .Where(s => s.BackgroundTasksJson != null && s.BackgroundTasksJson != "" && s.BackgroundTasksJson != "[]")
            .ToList();
    }

    public string? FindToolUseMessageUid(string sessionId, string toolUseId)
    {
        using var db = new ClaudeDbContext();
        return db.Messages
            .Where(m => m.SessionId == sessionId && m.EventType == "tool_use" && m.MessageId == toolUseId)
            .OrderByDescending(m => m.Id)
            .Select(m => m.MessageUid)
            .FirstOrDefault();
    }

    public List<ClaudeSessionRecord> GetRecentSessions(HashSet<string> excludeIds, int limit = 20, bool includeDismissed = false)
    {
        using var db = new ClaudeDbContext();
        var query = Owned(db).Where(s => !excludeIds.Contains(s.Id));
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
            // Another instance's row: never write over it, and never move it.
            if (!string.Equals(existing.RuntimeOwner, _owner, StringComparison.Ordinal)) return;

            existing.ProjectName = record.ProjectName;
            existing.ProjectPath = record.ProjectPath;
            existing.ScratchDirectory = record.ScratchDirectory;
            existing.RepositoryId = record.RepositoryId;
            existing.Status = record.Status;
            existing.StopReason = record.StopReason;
            existing.StoppedAt = record.StoppedAt;
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
            existing.QualityTier = record.QualityTier;
            existing.ProviderEntity = record.ProviderEntity;
            existing.DeveloperInstructions = record.DeveloperInstructions;
            existing.JobId = record.JobId;
            existing.Dismissed = record.Dismissed;
            existing.Source = record.Source;
            existing.UserId = record.UserId;
            existing.UserName = record.UserName;
            existing.UserAvatarUrl = record.UserAvatarUrl;
            existing.ProcessId = record.ProcessId;
            existing.BackgroundTasksJson = record.BackgroundTasksJson;
            existing.AccessLevel = record.AccessLevel;
            existing.MaxAccessLevel = record.MaxAccessLevel;
            existing.AccessLevelBeforePlan = record.AccessLevelBeforePlan;
            existing.Unattended = record.Unattended;
            existing.AccessLevelSource = record.AccessLevelSource;
            if (record.LastActivity.HasValue)
                existing.LastActivity = record.LastActivity;
        }
        else
        {
            record.RuntimeOwner = _owner;
            db.Sessions.Add(record);
        }
        db.SaveChanges();
        SuiteMirror.PublishSession(ToSnapshot(record));
    }

    public void DismissSession(string sessionId)
    {
        using var db = new ClaudeDbContext();
        var record = Owned(db).FirstOrDefault(s => s.Id == sessionId);
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

    public string? GetForkTarget(string sessionId)
    {
        using var db = new ClaudeDbContext();
        return db.Sessions.Where(s => s.Id == sessionId).Select(s => s.ForkTargetJson).FirstOrDefault();
    }

    public void SetForkTarget(string sessionId, string? targetJson)
    {
        using var db = new ClaudeDbContext();
        var record = db.Sessions.Find(sessionId);
        if (record is null) return;
        record.ForkTargetJson = targetJson;
        db.SaveChanges();
    }

    public void MarkMessagesRolledBack(string sessionId, string messageUid)
    {
        using var db = new ClaudeDbContext();
        var from = db.Messages
            .Where(m => m.SessionId == sessionId && m.Role == "user" && m.MessageUid == messageUid)
            .Select(m => (long?)m.Id)
            .Min();
        if (from is null) return;
        foreach (var message in db.Messages.Where(m => m.SessionId == sessionId && m.Id >= from && !m.RolledBack))
            message.RolledBack = true;
        db.SaveChanges();
    }

    public List<ClaudeMessageRecord> GetMessages(string sessionId, int limit = 50_000)
    {
        using var db = new ClaudeDbContext();
        return db.Messages
            .AsNoTracking()
            .Where(m => m.SessionId == sessionId && !m.RolledBack)
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
            .Where(m => m.SessionId == sessionId && !m.RolledBack)
            .OrderByDescending(m => m.Timestamp)
            .Select(m => m.Timestamp)
            .FirstOrDefault();
    }

    public Dictionary<Guid, string> GetSessionStatusesByJobIds(IEnumerable<Guid> jobIds)
    {
        var jobIdSet = new HashSet<Guid>(jobIds);
        using var db = new ClaudeDbContext();
        return Owned(db)
            .Where(s => s.JobId != null && jobIdSet.Contains(s.JobId.Value))
            .Select(s => new { s.JobId, s.Status })
            .ToList()
            .ToDictionary(r => r.JobId!.Value, r => r.Status);
    }
}
