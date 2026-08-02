using System.IO;
using Microsoft.EntityFrameworkCore;
using RedCompute.Core.Jobs;
using RedCompute.Core.Logging;

namespace RedCompute.App.Data;

public class RedComputeDbContext : DbContext
{
    private readonly string _dbPath;

    public DbSet<JobRecord> Jobs => Set<JobRecord>();
    public DbSet<JobLifecycleEvent> JobEvents => Set<JobLifecycleEvent>();
    public DbSet<JobOutboxMessage> JobOutbox => Set<JobOutboxMessage>();
    public DbSet<LogEntry> LogEntries => Set<LogEntry>();

    public RedComputeDbContext()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RedCompute");
        Directory.CreateDirectory(dir);
        _dbPath = Path.Combine(dir, "redcompute.db");
    }

    public RedComputeDbContext(string dbPath)
    {
        _dbPath = dbPath;
        var directory = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
    }

    public void MigrateSchema()
    {
        var conn = Database.GetDbConnection();
        conn.Open();
        using var cmd = conn.CreateCommand();
        foreach (var col in new[]
        {
            ("Progress", "REAL"), ("ResultJson", "TEXT"), ("Name", "TEXT"),
            ("Rationale", "TEXT"), ("CostUsd", "REAL"), ("CallerInfo", "TEXT"),
            ("IdempotencyKey", "TEXT"), ("IdempotencyScope", "TEXT"),
            ("IdempotencyFingerprint", "TEXT"), ("CreationProvenanceJson", "TEXT"),
            ("UserId", "TEXT"), ("UserName", "TEXT"), ("UserAvatarUrl", "TEXT"),
            ("ExternalExecution", "INTEGER NOT NULL DEFAULT 0"), ("ParentJobId", "TEXT"),
            ("LeaseOwner", "TEXT"), ("LeaseTokenHash", "TEXT"),
            ("LeaseExpiresAt", "TEXT"), ("AttemptCount", "INTEGER NOT NULL DEFAULT 0")
        })
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

            CREATE TABLE IF NOT EXISTS JobEvents (
                Id TEXT PRIMARY KEY,
                JobId TEXT NOT NULL,
                Kind TEXT NOT NULL,
                OccurredAt TEXT NOT NULL,
                ProvenanceJson TEXT NULL,
                DataJson TEXT NOT NULL DEFAULT '{}'
            );
            CREATE INDEX IF NOT EXISTS IX_JobEvents_JobId_OccurredAt ON JobEvents(JobId, OccurredAt);
            CREATE TRIGGER IF NOT EXISTS TR_JobEvents_Immutable
            BEFORE UPDATE ON JobEvents
            BEGIN
                SELECT RAISE(ABORT, 'JobEvents are immutable');
            END;

            CREATE TABLE IF NOT EXISTS JobOutbox (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                MessageId TEXT NOT NULL,
                Kind TEXT NOT NULL,
                JobId TEXT NOT NULL,
                JobEventId TEXT NULL,
                PayloadJson TEXT NOT NULL DEFAULT '{}',
                CreatedAt TEXT NOT NULL,
                NextAttemptAt TEXT NOT NULL,
                Attempts INTEGER NOT NULL DEFAULT 0,
                AcknowledgedAt TEXT NULL,
                LastError TEXT NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS IX_JobOutbox_MessageId ON JobOutbox(MessageId);
            CREATE INDEX IF NOT EXISTS IX_JobOutbox_Pending ON JobOutbox(AcknowledgedAt, NextAttemptAt);
            CREATE INDEX IF NOT EXISTS IX_Jobs_IdempotencyFingerprint ON Jobs(IdempotencyFingerprint);
            CREATE INDEX IF NOT EXISTS IX_Jobs_IdempotencyScope_Key ON Jobs(IdempotencyScope, IdempotencyKey);
            CREATE INDEX IF NOT EXISTS IX_Jobs_ParentJobId ON Jobs(ParentJobId);
            CREATE INDEX IF NOT EXISTS IX_Jobs_ExternalLease ON Jobs(ExternalExecution, Status, LeaseExpiresAt);
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
            entity.HasIndex(j => new { j.IdempotencyScope, j.IdempotencyKey }).IsUnique(false);
            entity.HasIndex(j => j.IdempotencyFingerprint).IsUnique(false);
            entity.HasIndex(j => j.ParentJobId).IsUnique(false);
            entity.HasIndex(j => new { j.ExternalExecution, j.Status, j.LeaseExpiresAt }).IsUnique(false);
            entity.Property(j => j.LeaseExpiresAt).HasConversion(
                v => v.HasValue ? v.Value.ToString("O") : null,
                v => v != null ? DateTimeOffset.Parse(v) : null);
            entity.Ignore(j => j.DurationMs);
            entity.Ignore(j => j.CreationProvenance);
            entity.Ignore(j => j.IsIdempotencyReuse);
        });

        modelBuilder.Entity<JobLifecycleEvent>(entity =>
        {
            entity.ToTable("JobEvents");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Kind).HasConversion<string>();
            entity.Property(e => e.OccurredAt).HasConversion(
                v => v.ToString("O"),
                v => DateTimeOffset.Parse(v));
            entity.HasIndex(e => new { e.JobId, e.OccurredAt });
            entity.Ignore(e => e.Provenance);
        });

        modelBuilder.Entity<JobOutboxMessage>(entity =>
        {
            entity.ToTable("JobOutbox");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedOnAdd();
            entity.Property(e => e.Kind).HasConversion<string>();
            entity.Property(e => e.CreatedAt).HasConversion(
                v => v.ToString("O"),
                v => DateTimeOffset.Parse(v));
            entity.Property(e => e.NextAttemptAt).HasConversion(
                v => v.ToString("O"),
                v => DateTimeOffset.Parse(v));
            entity.Property(e => e.AcknowledgedAt).HasConversion(
                v => v.HasValue ? v.Value.ToString("O") : null,
                v => v != null ? DateTimeOffset.Parse(v) : null);
            entity.HasIndex(e => e.MessageId).IsUnique();
            entity.HasIndex(e => new { e.AcknowledgedAt, e.NextAttemptAt });
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
