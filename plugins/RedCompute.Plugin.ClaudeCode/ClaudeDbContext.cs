using System.IO;
using Microsoft.EntityFrameworkCore;

namespace RedCompute.Plugin.ClaudeCode;

public class ClaudeDbContext : DbContext
{
    private static readonly string DbPath;

    public DbSet<ClaudeSessionRecord> Sessions => Set<ClaudeSessionRecord>();
    public DbSet<ClaudeMessageRecord> Messages => Set<ClaudeMessageRecord>();

    static ClaudeDbContext()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RedCompute", "plugins", "claude-code");
        Directory.CreateDirectory(dir);
        DbPath = Path.Combine(dir, "claude.db");
    }

    public void Initialize()
    {
        Database.EnsureCreated();
        MigrateSchema();
    }

    private void MigrateSchema()
    {
        var conn = Database.GetDbConnection();
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA table_info(Sessions)";
        var columns = new HashSet<string>();
        using (var reader = cmd.ExecuteReader())
        {
            while (reader.Read())
                columns.Add(reader.GetString(1));
        }

        if (!columns.Contains("Source"))
        {
            using var alter = conn.CreateCommand();
            alter.CommandText = "ALTER TABLE Sessions ADD COLUMN Source TEXT";
            alter.ExecuteNonQuery();
        }

        if (!columns.Contains("StopReason"))
        {
            using var alter = conn.CreateCommand();
            alter.CommandText = "ALTER TABLE Sessions ADD COLUMN StopReason TEXT";
            alter.ExecuteNonQuery();
        }

        if (!columns.Contains("ProcessId"))
        {
            using var alter = conn.CreateCommand();
            alter.CommandText = "ALTER TABLE Sessions ADD COLUMN ProcessId INTEGER";
            alter.ExecuteNonQuery();
        }

        if (!columns.Contains("LastActivity"))
        {
            using var alter = conn.CreateCommand();
            alter.CommandText = "ALTER TABLE Sessions ADD COLUMN LastActivity TEXT";
            alter.ExecuteNonQuery();
        }

        using var backfill = conn.CreateCommand();
        backfill.CommandText = "UPDATE Sessions SET Source = 'Nova' WHERE ProjectName = 'nova-workspace' AND Source IS NULL";
        backfill.ExecuteNonQuery();

        cmd.CommandText = "PRAGMA table_info(Messages)";
        var msgColumns = new HashSet<string>();
        using (var reader = cmd.ExecuteReader())
        {
            while (reader.Read())
                msgColumns.Add(reader.GetString(1));
        }

        if (!msgColumns.Contains("AttachmentsJson"))
        {
            using var alter = conn.CreateCommand();
            alter.CommandText = "ALTER TABLE Messages ADD COLUMN AttachmentsJson TEXT";
            alter.ExecuteNonQuery();
        }
    }

    protected override void OnConfiguring(DbContextOptionsBuilder options)
    {
        options.UseSqlite($"Data Source={DbPath}");
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ClaudeMessageRecord>(entity =>
        {
            entity.HasKey(m => m.Id);
            entity.Property(m => m.Id).ValueGeneratedOnAdd();
            entity.HasIndex(m => m.SessionId);
            entity.HasIndex(m => m.Timestamp);
            entity.Property(m => m.Timestamp).HasConversion(
                v => v.ToString("O"),
                v => DateTimeOffset.Parse(v));
        });

        modelBuilder.Entity<ClaudeSessionRecord>(entity =>
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
