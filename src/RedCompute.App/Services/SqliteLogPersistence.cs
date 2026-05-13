using System.Threading.Channels;
using RedBamboo.AppHost.Logging;
using RedCompute.App.Data;
using CoreLogEntry = RedCompute.Core.Logging.LogEntry;

namespace RedCompute.App.Services;

public class SqliteLogPersistence : ILogPersistence, IDisposable
{
    private readonly Channel<CoreLogEntry> _channel;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _writerTask;

    public SqliteLogPersistence()
    {
        _channel = Channel.CreateUnbounded<CoreLogEntry>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });
        _writerTask = Task.Run(WriteLoopAsync);
    }

    public Task PersistAsync(IReadOnlyList<LogEntry> entries)
    {
        foreach (var entry in entries)
        {
            var core = ToCoreEntry(entry);
            _channel.Writer.TryWrite(core);
        }
        return Task.CompletedTask;
    }

    public Task<(IReadOnlyList<LogEntry> Entries, int Total)> QueryAsync(LogQueryParams query)
    {
        using var db = new RedComputeDbContext();
        IQueryable<CoreLogEntry> q = db.LogEntries;

        if (query.MinLevel is { } minLevel)
        {
            if (minLevel >= RedBamboo.AppHost.Logging.LogLevel.Error)
                q = q.Where(e => e.IsError);
        }
        if (query.Category is { } cat)
            q = q.Where(e => e.TagCategory.StartsWith(cat));
        if (query.Search is { } search)
            q = q.Where(e => e.Message.Contains(search) || e.FullMessage.Contains(search));
        if (query.JobId is { } jobIdStr && Guid.TryParse(jobIdStr, out var jobId))
            q = q.Where(e => e.JobId == jobId);
        if (query.Since is { } since)
            q = q.Where(e => e.Timestamp >= since.LocalDateTime);
        if (query.Until is { } until)
            q = q.Where(e => e.Timestamp <= until.LocalDateTime);

        var total = q.Count();
        var entries = q
            .OrderByDescending(e => e.Timestamp)
            .Take(query.Limit)
            .ToList()
            .Select(ToAppHostEntry)
            .ToList();

        return Task.FromResult<(IReadOnlyList<LogEntry> Entries, int Total)>((entries, total));
    }

    public Task CleanupAsync(int retentionDays)
    {
        using var db = new RedComputeDbContext();
        var cutoff = DateTime.Now.AddDays(-retentionDays);
        var old = db.LogEntries.Where(l => l.Timestamp < cutoff);
        if (old.Any())
        {
            db.LogEntries.RemoveRange(old);
            db.SaveChanges();
        }
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _cts.Cancel();
        _channel.Writer.Complete();
        try { _writerTask.Wait(TimeSpan.FromSeconds(3)); } catch { }
        _cts.Dispose();
    }

    private async Task WriteLoopAsync()
    {
        var batch = new List<CoreLogEntry>(50);
        var token = _cts.Token;

        while (!token.IsCancellationRequested)
        {
            try
            {
                batch.Clear();
                if (await _channel.Reader.WaitToReadAsync(token))
                {
                    while (_channel.Reader.TryRead(out var entry) && batch.Count < 50)
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

    private static CoreLogEntry ToCoreEntry(LogEntry entry) => new()
    {
        Timestamp = entry.Timestamp.LocalDateTime,
        Tag = entry.Tag ?? "",
        TagCategory = entry.Category,
        Message = entry.Message,
        FullMessage = entry.FullMessage ?? entry.Message,
        TagColor = entry.TagColor ?? "#72767D",
        IsMultiline = entry.IsMultiline,
        IsError = entry.IsError,
        JobId = entry.JobId is { } jid && Guid.TryParse(jid, out var g) ? g : null,
    };

    private static LogEntry ToAppHostEntry(CoreLogEntry e) => new()
    {
        Id = e.Id.ToString(),
        Timestamp = new DateTimeOffset(e.Timestamp),
        Level = e.IsError ? RedBamboo.AppHost.Logging.LogLevel.Error : RedBamboo.AppHost.Logging.LogLevel.Info,
        Category = e.TagCategory,
        Source = "redcompute",
        Message = e.Message,
        FullMessage = e.FullMessage,
        Tag = e.Tag != "" ? e.Tag : null,
        TagColor = e.TagColor,
        JobId = e.JobId?.ToString(),
    };
}
