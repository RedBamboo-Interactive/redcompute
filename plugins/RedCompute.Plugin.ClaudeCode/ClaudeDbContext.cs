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
        });
    }
}
