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
        });
    }
}
