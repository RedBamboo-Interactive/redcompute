using RedBamboo.AppHost.Logging;
using RedCompute.App.Data;
using RedCompute.App.Helpers;
using CoreLogEntry = RedCompute.Core.Logging.LogEntry;

namespace RedCompute.App.Services;

public class LoggingService : IDisposable
{
    private readonly LogService _logService;
    private readonly Func<string?> _configuredMinimum;

    public LoggingService(LogService logService, Func<string?>? configuredMinimum = null)
    {
        _logService = logService;
        _configuredMinimum = configuredMinimum ?? (() => "Info");
    }

    public CoreLogEntry Log(string rawMessage, Guid? jobId = null)
    {
        var parsed = LogEntryParser.Parse(rawMessage);
        parsed.JobId = jobId;

        var level = parsed.IsError
            ? RedBamboo.AppHost.Logging.LogLevel.Error
            : RedBamboo.AppHost.Logging.LogLevel.Info;

        if (RedComputeLoggingPolicy.IsEnabled(_configuredMinimum(), level))
            ManagedProcessLogProtocol.Write(new ManagedProcessLogRecord(
                level,
                parsed.TagCategory,
                parsed.Message,
                FullMessage: parsed.FullMessage,
                JobId: jobId?.ToString(),
                Tag: parsed.Tag != "" ? parsed.Tag : null,
                TagColor: parsed.TagColor));

        _logService.Log(
            level,
            parsed.TagCategory,
            parsed.Message,
            fullMessage: parsed.FullMessage,
            tag: parsed.Tag != "" ? parsed.Tag : null,
            tagColor: parsed.TagColor,
            jobId: jobId?.ToString());

        return parsed;
    }

    public List<CoreLogEntry> GetLogsForJob(Guid jobId, string? tag = null, int limit = 200, int offset = 0)
    {
        using var db = new RedComputeDbContext();
        var query = db.LogEntries.Where(l => l.JobId == jobId);
        if (!string.IsNullOrEmpty(tag))
            query = query.Where(l => l.Tag == tag);
        return query
            .OrderBy(l => l.Timestamp)
            .Skip(offset)
            .Take(limit)
            .ToList();
    }

    public int GetLogCountForJob(Guid jobId)
    {
        using var db = new RedComputeDbContext();
        return db.LogEntries.Count(l => l.JobId == jobId);
    }

    public int CleanupOldLogs(int retentionDays)
    {
        using var db = new RedComputeDbContext();
        var cutoff = DateTime.Now.AddDays(-retentionDays);
        var old = db.LogEntries.Where(l => l.Timestamp < cutoff);
        var count = old.Count();
        if (count > 0)
        {
            db.LogEntries.RemoveRange(old);
            db.SaveChanges();
        }
        return count;
    }

    public void Dispose() { }
}
