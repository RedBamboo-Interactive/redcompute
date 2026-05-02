using System.IO;
using Microsoft.EntityFrameworkCore;
using RedCompute.Core.Jobs;

namespace RedCompute.App.Data;

public class RedComputeDbContext : DbContext
{
    private readonly string _dbPath;

    public DbSet<JobRecord> Jobs => Set<JobRecord>();

    public RedComputeDbContext()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RedCompute");
        Directory.CreateDirectory(dir);
        _dbPath = Path.Combine(dir, "redcompute.db");
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
            entity.Ignore(j => j.DurationMs);
        });
    }
}
