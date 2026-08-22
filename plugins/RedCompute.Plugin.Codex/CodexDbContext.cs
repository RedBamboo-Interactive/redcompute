using System.IO;
using Microsoft.EntityFrameworkCore;

namespace RedCompute.Plugin.Codex;

public class CodexDbContext : DbContext
{
    private static readonly string DbPath;

    public DbSet<CodexSessionRecord> Sessions => Set<CodexSessionRecord>();
    public DbSet<CodexMessageRecord> Messages => Set<CodexMessageRecord>();

    static CodexDbContext()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RedCompute", "plugins", "codex");
        Directory.CreateDirectory(dir);
        DbPath = Path.Combine(dir, "codex.db");
    }

    public void Initialize()
    {
        Database.EnsureCreated();
        MigrateSchema();
    }

    /// <summary>
    /// Additive column adds for databases created before interactive sessions existed.
    /// EnsureCreated() only builds a schema from scratch — it will not touch an existing file — so
    /// without this an older codex.db keeps its original columns and every query throws.
    /// Each ALTER is tried and ignored, which is the cheap way to make this idempotent in SQLite.
    /// </summary>
    private void MigrateSchema()
    {
        var conn = Database.GetDbConnection();
        conn.Open();
        try
        {
            using var cmd = conn.CreateCommand();

            foreach (var col in new[]
                     {
                         "ThreadId TEXT", "Effort TEXT", "QualityTier TEXT", "ProviderEntity TEXT", "RepositoryId TEXT", "Source TEXT",
                         "ProcessId INTEGER", "LastActivity TEXT", "ContextTokens INTEGER",
                         "ContextWindow INTEGER",
                         "UserId TEXT", "UserName TEXT", "UserAvatarUrl TEXT",
                         "StopReason TEXT",
                     })
            {
                cmd.CommandText = $"ALTER TABLE Sessions ADD COLUMN {col}";
                try { cmd.ExecuteNonQuery(); }
                catch { }
            }

            foreach (var col in new[] { "MessageUid TEXT", "AttachmentsJson TEXT", "Phase TEXT" })
            {
                cmd.CommandText = $"ALTER TABLE Messages ADD COLUMN {col}";
                try { cmd.ExecuteNonQuery(); }
                catch { }
            }
        }
        finally
        {
            conn.Close();
        }
    }

    protected override void OnConfiguring(DbContextOptionsBuilder options)
    {
        options.UseSqlite($"Data Source={DbPath}");
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<CodexMessageRecord>(entity =>
        {
            entity.HasKey(m => m.Id);
            entity.Property(m => m.Id).ValueGeneratedOnAdd();
            entity.HasIndex(m => m.SessionId);
            entity.HasIndex(m => m.Timestamp);
            entity.Property(m => m.Timestamp).HasConversion(
                v => v.ToString("O"),
                v => DateTimeOffset.Parse(v));
        });

        modelBuilder.Entity<CodexSessionRecord>(entity =>
        {
            entity.HasKey(s => s.Id);
            entity.Property(s => s.StartedAt).HasConversion(
                v => v.ToString("O"),
                v => DateTimeOffset.Parse(v));
            entity.Property(s => s.LastActivity).HasConversion(
                v => v.HasValue ? v.Value.ToString("O") : null,
                v => v != null ? DateTimeOffset.Parse(v) : null);
        });
    }
}
