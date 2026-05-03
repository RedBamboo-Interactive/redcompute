using System.Threading.Channels;
using RedCompute.App.Data;
using RedCompute.App.Helpers;
using RedCompute.Core.Logging;

namespace RedCompute.App.Services;

public class LoggingService : IDisposable
{
    private readonly FileLoggerService _fileLogger;
    private readonly Channel<LogEntry> _persistChannel;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _persistTask;

    public event Action<LogEntry>? LogEntryCreated;

    public LoggingService(FileLoggerService fileLogger)
    {
        _fileLogger = fileLogger;
        _persistChannel = Channel.CreateUnbounded<LogEntry>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });
        _persistTask = Task.Run(PersistLoopAsync);
    }

    public LogEntry Log(string rawMessage, Guid? jobId = null)
    {
        var entry = LogEntryParser.Parse(rawMessage);
        entry.JobId = jobId;

        var timestamped = $"[{entry.Timestamp:HH:mm:ss.fff}] {rawMessage}";
        Console.WriteLine(timestamped);
        _fileLogger.Write(timestamped);

        _persistChannel.Writer.TryWrite(entry);
        LogEntryCreated?.Invoke(entry);

        return entry;
    }

    public List<LogEntry> GetLogsForJob(Guid jobId, string? tag = null, int limit = 200, int offset = 0)
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

    public (List<LogEntry> Entries, int TotalCount) QueryLogs(
        string? tag = null, string? search = null, DateTime? since = null, DateTime? until = null,
        Guid? jobId = null, bool? errorsOnly = null, int limit = 100, int offset = 0)
    {
        using var db = new RedComputeDbContext();
        IQueryable<LogEntry> query = db.LogEntries;

        if (!string.IsNullOrEmpty(tag))
            query = query.Where(l => l.Tag == tag || l.TagCategory == tag);
        if (!string.IsNullOrEmpty(search))
            query = query.Where(l => l.Message.Contains(search) || l.FullMessage.Contains(search));
        if (since.HasValue)
            query = query.Where(l => l.Timestamp >= since.Value);
        if (until.HasValue)
            query = query.Where(l => l.Timestamp <= until.Value);
        if (jobId.HasValue)
            query = query.Where(l => l.JobId == jobId.Value);
        if (errorsOnly == true)
            query = query.Where(l => l.IsError);

        var totalCount = query.Count();
        var entries = query
            .OrderByDescending(l => l.Timestamp)
            .Skip(offset)
            .Take(limit)
            .ToList();

        return (entries, totalCount);
    }

    public Dictionary<string, int> GetTagCounts(DateTime? since = null)
    {
        using var db = new RedComputeDbContext();
        IQueryable<LogEntry> query = db.LogEntries;
        if (since.HasValue)
            query = query.Where(l => l.Timestamp >= since.Value);
        return query
            .GroupBy(l => l.Tag)
            .Where(g => g.Key != "")
            .Select(g => new { Tag = g.Key, Count = g.Count() })
            .ToDictionary(x => x.Tag, x => x.Count);
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

    private async Task PersistLoopAsync()
    {
        var batch = new List<LogEntry>(50);
        var token = _cts.Token;

        while (!token.IsCancellationRequested)
        {
            try
            {
                batch.Clear();
                if (await _persistChannel.Reader.WaitToReadAsync(token))
                {
                    while (_persistChannel.Reader.TryRead(out var entry) && batch.Count < 50)
                        batch.Add(entry);

                    if (batch.Count > 0)
                    {
                        using var db = new RedComputeDbContext();
                        db.LogEntries.AddRange(batch);
                        await db.SaveChangesAsync(token);
                    }
                }
            }
            catch (OperationCanceledException) { break; }
            catch { await Task.Delay(500, token); }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _persistChannel.Writer.Complete();
        try { _persistTask.Wait(TimeSpan.FromSeconds(3)); } catch { }
        _cts.Dispose();
    }
}
