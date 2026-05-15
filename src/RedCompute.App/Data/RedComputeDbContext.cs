using System.IO;
using Microsoft.EntityFrameworkCore;
using RedCompute.Core.Jobs;
using RedCompute.Core.Logging;

namespace RedCompute.App.Data;

public class RedComputeDbContext : DbContext
{
    private readonly string _dbPath;

    public DbSet<JobRecord> Jobs => Set<JobRecord>();
    public DbSet<LogEntry> LogEntries => Set<LogEntry>();

    public RedComputeDbContext()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RedCompute");
        Directory.CreateDirectory(dir);
        _dbPath = Path.Combine(dir, "redcompute.db");
    }

    public void MigrateSchema()
    {
        var conn = Database.GetDbConnection();
        conn.Open();
        using var cmd = conn.CreateCommand();
        foreach (var col in new[] { ("Progress", "REAL"), ("ResultJson", "TEXT"), ("Name", "TEXT"), ("Rationale", "TEXT"), ("CostUsd", "REAL") })
        {
            cmd.CommandText = $"ALTER TABLE Jobs ADD COLUMN {col.Item1} {col.Item2}";
            try { cmd.ExecuteNonQuery(); } catch { /* column already exists */ }
        }

        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS LogEntries (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Timestamp TEXT NOT NULL,
                Tag TEXT NOT NULL DEFAULT '',
                TagCategory TEXT NOT NULL DEFAULT 'debug',
                Message TEXT NOT NULL DEFAULT '',
                FullMessage TEXT NOT NULL DEFAULT '',
                TagColor TEXT NOT NULL DEFAULT '#72767D',
                IsMultiline INTEGER NOT NULL DEFAULT 0,
                IsError INTEGER NOT NULL DEFAULT 0,
                JobId TEXT NULL
            );
            CREATE INDEX IF NOT EXISTS IX_LogEntries_JobId ON LogEntries(JobId);
            CREATE INDEX IF NOT EXISTS IX_LogEntries_Timestamp ON LogEntries(Timestamp);
            CREATE INDEX IF NOT EXISTS IX_LogEntries_Tag ON LogEntries(Tag);
            """;
        cmd.ExecuteNonQuery();
    }

    protected override void OnConfiguring(DbContextOptionsBuilder options)
    {
        options.UseSqlite($"Data Source={_dbPath}");
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<JobRecord>(entity =>
        {
            entity.HasKey(j => j.Id);
            entity.Property(j => j.Status).HasConversion<string>();
            entity.Property(j => j.QueuedAt).HasConversion(
                v => v.ToString("O"),
                v => DateTimeOffset.Parse(v));
            entity.Property(j => j.StartedAt).HasConversion(
                v => v.HasValue ? v.Value.ToString("O") : null,
                v => v != null ? DateTimeOffset.Parse(v) : null);
            entity.Property(j => j.CompletedAt).HasConversion(
                v => v.HasValue ? v.Value.ToString("O") : null,
                v => v != null ? DateTimeOffset.Parse(v) : null);
            entity.HasIndex(j => j.CapabilitySlug);
            entity.HasIndex(j => j.Status);
            entity.HasIndex(j => j.QueuedAt);
            entity.HasIndex(j => j.IdempotencyKey).IsUnique(false);
            entity.Ignore(j => j.DurationMs);
        });

        modelBuilder.Entity<LogEntry>(entity =>
        {
            entity.HasKey(l => l.Id);
            entity.Property(l => l.Id).ValueGeneratedOnAdd();
            entity.HasIndex(l => l.JobId);
            entity.HasIndex(l => l.Timestamp);
            entity.HasIndex(l => l.Tag);
            entity.Ignore(l => l.TimestampText);
            entity.Ignore(l => l.PreviewMessage);
        });
    }
}
