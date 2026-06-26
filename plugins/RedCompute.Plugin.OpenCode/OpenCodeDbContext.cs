using System.IO;
using Microsoft.EntityFrameworkCore;

namespace RedCompute.Plugin.OpenCode;

public class OpenCodeDbContext : DbContext
{
    private static readonly string DbPath;

    public DbSet<OpenCodeSessionRecord> Sessions => Set<OpenCodeSessionRecord>();
    public DbSet<OpenCodeMessageRecord> Messages => Set<OpenCodeMessageRecord>();

    static OpenCodeDbContext()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RedCompute", "plugins", "opencode");
        Directory.CreateDirectory(dir);
        DbPath = Path.Combine(dir, "opencode.db");
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
        try
        {
            using var cmd = conn.CreateCommand();
            foreach (var col in new[] { "OpenCodeSessionId", "Effort", "Source" })
            {
                cmd.CommandText = $"ALTER TABLE Sessions ADD COLUMN {col} TEXT";
                try { cmd.ExecuteNonQuery(); }
                catch { }
            }
            cmd.CommandText = "ALTER TABLE Messages ADD COLUMN AttachmentsJson TEXT";
            try { cmd.ExecuteNonQuery(); }
            catch { }
            foreach (var col in new[] { "ProcessId INTEGER", "LastActivity TEXT" })
            {
                cmd.CommandText = $"ALTER TABLE Sessions ADD COLUMN {col}";
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
        modelBuilder.Entity<OpenCodeMessageRecord>(entity =>
        {
            entity.HasKey(m => m.Id);
            entity.Property(m => m.Id).ValueGeneratedOnAdd();
            entity.HasIndex(m => m.SessionId);
            entity.HasIndex(m => m.Timestamp);
            entity.Property(m => m.Timestamp).HasConversion(
                v => v.ToString("O"),
                v => DateTimeOffset.Parse(v));
        });

        modelBuilder.Entity<OpenCodeSessionRecord>(entity =>
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
