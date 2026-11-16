using System.Diagnostics;
using System.IO;
using Microsoft.EntityFrameworkCore;
using RedCompute.Core.Storage;

namespace RedCompute.Plugin.OpenCode;

public class OpenCodeDbContext : DbContext
{
    // Settable so a test can never reach the real database.
    internal static string DbPath { get; set; }

    public DbSet<OpenCodeSessionRecord> Sessions => Set<OpenCodeSessionRecord>();
    public DbSet<OpenCodeMessageRecord> Messages => Set<OpenCodeMessageRecord>();

    static OpenCodeDbContext()
    {
        var dir = ComputeStorage.Current.PluginDirectory("opencode");
        Directory.CreateDirectory(dir);
        DbPath = Path.Combine(dir, "opencode.db");
    }

    /// <summary>
    /// How long adding <c>Sessions.NodeId</c> took, when this context's <see cref="Initialize"/> added it;
    /// null when the column already existed. The provider logs it once at boot, so the cost of the ALTER
    /// on a large live database is recorded (as RedCompute's own <c>Jobs.NodeId</c>).
    /// </summary>
    public TimeSpan? NodeIdColumnAddedIn { get; private set; }

    /// <summary>How long adding <c>Sessions.Placement</c> took, when <see cref="Initialize"/> added it; null otherwise.</summary>
    public TimeSpan? PlacementColumnAddedIn { get; private set; }

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
            foreach (var col in new[] { "OpenCodeSessionId", "Effort", "QualityTier", "ProviderEntity", "RepositoryId", "Source", "UserId", "UserName", "UserAvatarUrl", "StopReason", "StoppedAt", "DeveloperInstructions", "ScratchDirectory" })
            {
                cmd.CommandText = $"ALTER TABLE Sessions ADD COLUMN {col} TEXT";
                try { cmd.ExecuteNonQuery(); }
                catch { }
            }
            cmd.CommandText = "ALTER TABLE Messages ADD COLUMN AttachmentsJson TEXT";
            try { cmd.ExecuteNonQuery(); }
            catch { }
            cmd.CommandText = "ALTER TABLE Messages ADD COLUMN MessageUid TEXT";
            try { cmd.ExecuteNonQuery(); }
            catch { }
            cmd.CommandText = "ALTER TABLE Messages ADD COLUMN ProviderPartId TEXT";
            try { cmd.ExecuteNonQuery(); }
            catch { }
            cmd.CommandText = "ALTER TABLE Messages ADD COLUMN ParentToolUseId TEXT";
            try { cmd.ExecuteNonQuery(); }
            catch { }
            cmd.CommandText = """
                CREATE UNIQUE INDEX IF NOT EXISTS IX_Messages_SessionId_ProviderPartId_EventType
                ON Messages (SessionId, ProviderPartId, EventType)
                WHERE ProviderPartId IS NOT NULL
                """;
            cmd.ExecuteNonQuery();
            foreach (var col in new[] { "ProcessId INTEGER", "LastActivity TEXT", "ContextWindow INTEGER",
                         "AccessLevel TEXT", "MaxAccessLevel TEXT", "AccessLevelBeforePlan TEXT", "AccessLevelSource TEXT",
                         "Unattended INTEGER NOT NULL DEFAULT 0" })
            {
                cmd.CommandText = $"ALTER TABLE Sessions ADD COLUMN {col}";
                try { cmd.ExecuteNonQuery(); }
                catch { }
            }

            // Nullable, no default, CHECK or index: SQLite rewrites only the schema text, never a row,
            // whatever the table holds. Existing rows read NULL, which is the legacy node.
            cmd.CommandText = "ALTER TABLE Sessions ADD COLUMN NodeId TEXT";
            var timer = Stopwatch.StartNew();
            try
            {
                cmd.ExecuteNonQuery();
                NodeIdColumnAddedIn = timer.Elapsed;
            }
            catch { /* column already exists */ }

            // The same schema-only add: existing rows read NULL, a session started with path fields only.
            cmd.CommandText = "ALTER TABLE Sessions ADD COLUMN Placement TEXT";
            timer.Restart();
            try
            {
                cmd.ExecuteNonQuery();
                PlacementColumnAddedIn = timer.Elapsed;
            }
            catch { /* column already exists */ }
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
            entity.HasIndex(m => new { m.SessionId, m.ProviderPartId, m.EventType })
                .IsUnique()
                .HasFilter("ProviderPartId IS NOT NULL");
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
            entity.Property(s => s.StoppedAt).HasConversion(
                v => v.HasValue ? v.Value.ToString("O") : null,
                v => v != null ? DateTimeOffset.Parse(v) : null);
        });
    }
}
