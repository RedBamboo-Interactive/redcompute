using System.Diagnostics;
using Microsoft.Data.Sqlite;
using RedCompute.App.Api.Endpoints;
using RedCompute.App.Services;
using RedCompute.Core.Configuration;
using RedCompute.Core.Capabilities;
using RedCompute.Core.Jobs;
using RedCompute.Core.Providers;
using RedCompute.Core.Sessions;
using RedCompute.Plugin.ClaudeCode;
using RedCompute.PluginSdk;
using Xunit;
using MemoryJobTracker = RedCompute.App.Tests.ClaudeBackgroundWorkTests.MemoryJobTracker;
using NullQualityModes = RedCompute.App.Tests.ClaudeBackgroundWorkTests.NullQualityModes;

namespace RedCompute.App.Tests;

/// <summary>
/// Two Claude Code providers on one machine: the shared <c>claude-code</c> instance and a dedicated
/// one with its own binary (a second account). Both share claude.db. Every session belongs to exactly
/// one instance, and only that instance claims, recovers, lists or resumes it, whichever instance the
/// registry happens to list first. Real stores on a scratch database, never the installed one.
/// </summary>
[Collection("Plugin database path")]
public sealed class DedicatedClaudeProviderRoutingTests : IDisposable
{
    private const string Work = "claude-work";
    private readonly string _root = Path.Combine(
        Environment.GetEnvironmentVariable("REDLEAF_SCRATCH_DIR") ?? Path.GetTempPath(),
        "redcompute-dedicated-claude", Guid.NewGuid().ToString("N"));
    private readonly string _claudePath = ClaudeDbContext.DbPath;
    private readonly List<Process> _processes = [];

    public DedicatedClaudeProviderRoutingTests()
    {
        Directory.CreateDirectory(_root);
        ClaudeDbContext.DbPath = Path.Combine(_root, "claude.db");
        using var db = new ClaudeDbContext();
        db.Initialize();
    }

    public void Dispose()
    {
        foreach (var process in _processes)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); process.WaitForExit(5_000); } catch { }
        }
        ClaudeDbContext.DbPath = _claudePath;
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void Each_store_sees_only_its_own_rows_and_never_writes_over_the_other()
    {
        var shared = new ClaudeSessionStore();
        var work = new ClaudeSessionStore(Work);
        var sharedJob = Guid.NewGuid();
        var workJob = Guid.NewGuid();
        Save(shared, "s-shared", "Idle", "anthropic-direct", sharedJob);
        Save(work, "s-work", "Idle", Work, workJob);

        Assert.NotNull(shared.FindSession("s-shared"));
        Assert.Null(shared.FindSession("s-work"));
        Assert.NotNull(work.FindSession("s-work"));
        Assert.Null(work.FindSession("s-shared"));
        Assert.Equal(["s-shared"], shared.GetActiveSessions().Select(s => s.Id));
        Assert.Equal(["s-work"], work.GetActiveSessions().Select(s => s.Id));
        Assert.Equal(["s-shared"], shared.GetRecentSessions([]).Select(s => s.Id));
        Assert.Equal(["s-work"], work.GetRecentSessions([]).Select(s => s.Id));
        Assert.Null(shared.FindSessionByJobId(workJob));
        Assert.Equal("s-work", work.FindSessionByJobId(workJob)?.Id);
        Assert.Equal([sharedJob], shared.GetSessionStatusesByJobIds([sharedJob, workJob]).Keys);
        Assert.Equal([workJob], work.GetSessionStatusesByJobIds([sharedJob, workJob]).Keys);

        // A write from the wrong instance changes nothing; the owner's own updates keep the owner.
        var stray = work.FindSession("s-work")!;
        stray.Status = "Stopped";
        shared.SaveSession(stray);
        shared.DismissSession("s-work");
        Assert.Equal("Idle", work.FindSession("s-work")!.Status);
        Assert.False(work.FindSession("s-work")!.Dismissed);
        stray.Title = "renamed";
        work.SaveSession(stray);
        Assert.Equal(Work, Scalar("SELECT RuntimeOwner FROM Sessions WHERE Id = 's-work'"));
        Assert.Equal(DBNull.Value, Scalar("SELECT RuntimeOwner FROM Sessions WHERE Id = 's-shared'"));
    }

    [Fact]
    public void A_dedicated_instance_adopts_only_its_entitys_legacy_rows()
    {
        var shared = new ClaudeSessionStore();
        Save(shared, "legacy-work", "Stopped", "Claude-Work", Guid.NewGuid());
        Save(shared, "legacy-work-by-id", "Stopped", "6f1c0a52-0000-0000-0000-000000000001", Guid.NewGuid());
        Save(shared, "legacy-shared", "Stopped", "anthropic-direct", Guid.NewGuid());
        Save(shared, "legacy-default", "Stopped", null, Guid.NewGuid());

        var adopted = ClaudeSessionStore.AdoptLegacySessions(Work, [Work, "6f1c0a52-0000-0000-0000-000000000001", null]);
        Assert.Equal(2, adopted);
        Assert.Equal(0, ClaudeSessionStore.AdoptLegacySessions(Work, [Work])); // Idempotent.

        var work = new ClaudeSessionStore(Work);
        Assert.Equal(["legacy-work", "legacy-work-by-id"], work.GetRecentSessions([]).Select(s => s.Id).Order());
        Assert.Equal(["legacy-default", "legacy-shared"], shared.GetRecentSessions([]).Select(s => s.Id).Order());
    }

    [Fact]
    public void The_column_arrives_on_upgrade_and_old_rows_stay_shared()
    {
        var path = ClaudeDbContext.DbPath;
        Execute("ALTER TABLE Sessions DROP COLUMN RuntimeOwner");
        Execute("INSERT INTO Sessions (Id, ProjectName, ProjectPath, Status, StartedAt, MessageCount, Dismissed, Unattended) "
            + "VALUES ('old', 'P', '.', 'Stopped', '2026-11-01T00:00:00.0000000+00:00', 0, 0, 0)");
        using (var db = new ClaudeDbContext()) db.Initialize();
        using (var db = new ClaudeDbContext()) db.Initialize(); // Idempotent.

        Assert.Equal(path, ClaudeDbContext.DbPath);
        Assert.NotNull(new ClaudeSessionStore().FindSession("old"));
        Assert.Null(new ClaudeSessionStore(Work).FindSession("old"));
    }

    [Fact]
    public void Only_a_dedicated_entity_gets_its_own_owner()
    {
        Assert.Equal(Work, ClaudeCodeProvider.RuntimeOwnerOf(new ProviderConfig
            { Type = "ClaudeCode", RuntimeBinding = "Dedicated", EntitySlug = $" {Work} " }));
        Assert.Null(ClaudeCodeProvider.RuntimeOwnerOf(new ProviderConfig
            { Type = "ClaudeCode", RuntimeBinding = "shared", EntitySlug = "anthropic-direct" }));
        Assert.Null(ClaudeCodeProvider.RuntimeOwnerOf(new ProviderConfig { Type = "ClaudeCode" }));
    }

    /// <summary>
    /// The reported failure: the shared instance listed first claimed the dedicated session and
    /// resumed it with its own binary, on the wrong account, while the dedicated process lived on.
    /// </summary>
    [Fact]
    public async Task A_dedicated_session_resumes_on_its_own_binary_whichever_instance_is_listed_first()
    {
        var sharedLaunches = new List<string>();
        var workLaunches = new List<string>();
        var (sharedProvider, _) = Instance(null, "claude-shared.exe", sharedLaunches);
        var (workProvider, workStore) = Instance(Work, "claude-work.exe", workLaunches);
        Save(workStore, "dedicated", "Stopped", Work, Guid.NewGuid(), SessionStopReasons.MaintenanceRestart);
        Save(new ClaudeSessionStore(), "everyday", "Stopped", "anthropic-direct", Guid.NewGuid(),
            SessionStopReasons.MaintenanceRestart);

        foreach (var order in new[] { new[] { sharedProvider, workProvider }, [workProvider, sharedProvider] })
        {
            var ordered = Registry(order);
            var (owner, info) = UnifiedSessionEndpoints.FindSessionAcrossProviders(ordered, "dedicated");
            Assert.Same(workProvider, owner);
            Assert.Equal(Work, info!.ProviderEntity);
            Assert.Same(sharedProvider, UnifiedSessionEndpoints.FindSessionAcrossProviders(ordered, "everyday").provider);
            Assert.Same(workProvider, UnifiedSessionEndpoints.FindSessionOwner(order, "claude-code", "dedicated"));
            Assert.Same(sharedProvider, UnifiedSessionEndpoints.FindSessionOwner(order, "claude-code", "everyday"));
        }

        Assert.Null(sharedProvider.GetSessionInfo("dedicated"));
        Assert.DoesNotContain(sharedProvider.GetSessions(50, includeDismissed: true), s => s.Id == "dedicated");
        Assert.Null(await sharedProvider.ResumeSessionAsync("dedicated", Provenance()));
        Assert.Empty(sharedLaunches);

        var registry = Registry([sharedProvider, workProvider]);
        var (resumer, _) = UnifiedSessionEndpoints.FindSessionAcrossProviders(registry, "dedicated");
        var resumed = await resumer!.ResumeSessionAsync("dedicated", Provenance());

        // Exactly one process, the dedicated binary's.
        Assert.True(resumed is not null, resumer.LastStartError);
        Assert.Equal([Path.Combine(_root, "claude-work.exe")], workLaunches);
        Assert.Empty(sharedLaunches);
        Assert.Null(sharedProvider.GetSessionInfo("dedicated"));
        var process = Assert.Single(_processes);

        // DELETE follows the same owner, so the process it reports killed is the one that dies.
        var (killer, _) = UnifiedSessionEndpoints.FindSessionAcrossProviders(registry, "dedicated");
        Assert.Same(workProvider, killer);
        await killer!.ForceKillAsync("dedicated");
        Assert.True(process.WaitForExit(10_000), "the dedicated process outlived its DELETE");
        Assert.NotEqual(RedCompute.Core.Sessions.SessionStatus.Active, workProvider.GetSessionInfo("dedicated")!.Status);
        Assert.NotEqual(RedCompute.Core.Sessions.SessionStatus.Idle, workProvider.GetSessionInfo("dedicated")!.Status);
    }

    private static JobProvenance Provenance()
        => JobProvenance.DirectRedCompute("POST", "/ai-session/sessions", new JobBeneficiary("user", "owner"), "request-1", null);

    [Fact]
    public void Recovery_at_startup_settles_only_the_instances_own_rows()
    {
        Save(new ClaudeSessionStore(Work), "work-active", "Active", Work, Guid.NewGuid());
        Save(new ClaudeSessionStore(), "shared-active", "Active", "anthropic-direct", Guid.NewGuid());

        // Recovery kills a recorded PID before it saves, so the shared instance must not even read the row.
        var sharedLog = new List<string>();
        Instance(null, "claude-shared.exe", [], sharedLog);
        Assert.DoesNotContain(sharedLog, line => line.Contains("work-active"));
        Assert.Contains(sharedLog, line => line.Contains("Recovered session shared-active"));
        Assert.Equal("Active", Scalar("SELECT Status FROM Sessions WHERE Id = 'work-active'"));
        Assert.Equal("Stopped", Scalar("SELECT Status FROM Sessions WHERE Id = 'shared-active'"));

        Instance(Work, "claude-work.exe", []);
        Assert.Equal("Stopped", Scalar("SELECT Status FROM Sessions WHERE Id = 'work-active'"));
    }

    [Fact]
    public void A_shared_entity_never_lands_on_a_dedicated_instance()
    {
        var capability = new CapabilityConfig
        {
            Providers = new Dictionary<string, ProviderConfig>
            {
                [Work] = new() { Type = "ClaudeCode", RuntimeBinding = "dedicated", EntitySlug = Work },
                ["claude-code"] = new() { Type = "ClaudeCode" },
            },
        };
        var shared = new ProviderEntityConfig("id", "anthropic-direct", "Claude", "claude-code",
            null, null, null, null, "active", null) { ProviderType = "ClaudeCode" };

        Assert.Equal("claude-code", ProviderConfigService.FindMatchingProviderName(capability, "ClaudeCode", shared));
        capability.Providers.Remove("claude-code");
        Assert.Null(ProviderConfigService.FindMatchingProviderName(capability, "ClaudeCode", shared));
    }

    // --- helpers ------------------------------------------------------------------------------

    private (ClaudeCodeProvider Provider, ClaudeSessionStore Store) Instance(
        string? owner, string binary, List<string> launches, List<string>? log = null)
    {
        var path = Path.Combine(_root, binary);
        if (!File.Exists(path)) File.WriteAllText(path, "");
        var store = new ClaudeSessionStore(owner);
        var jobs = new MemoryJobTracker();
        var service = new ClaudeSessionService(new ClaudeConfig { ClaudePath = path }, jobs,
            store, new NullQualityModes(), () => null, (line, _) => { lock (launches) log?.Add(line); });
        service.LaunchSessionProcess = start =>
        {
            launches.Add(start.FileName);
            var process = Process.Start(new ProcessStartInfo
            {
                FileName = TestProcesses.StdinSink, CreateNoWindow = true, UseShellExecute = false,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            })!;
            _processes.Add(process);
            return process;
        };
        return (new ClaudeCodeProvider("ai-session", service, store, jobs, (_, _) => { }), store);
    }

    private static CapabilityRegistry Registry(IEnumerable<ClaudeCodeProvider> providers)
    {
        var registry = new CapabilityRegistry();
        var map = new Dictionary<string, IBackendProvider>();
        var index = 0;
        foreach (var provider in providers) map[$"p{index++}"] = provider;
        registry.Register("ai-session", new CapabilityDefinition { Slug = "ai-session", DisplayName = "AI Session" },
            new CapabilityConfig(), map, "p0");
        return registry;
    }

    private void Save(ClaudeSessionStore store, string id, string status, string? providerEntity, Guid jobId,
        string? stopReason = null)
        => store.SaveSession(new ClaudeSessionRecord
        {
            Id = id, ProjectName = "P-" + id, ProjectPath = _root, Status = status, StopReason = stopReason,
            StartedAt = DateTimeOffset.UtcNow, StoppedAt = status == "Stopped" ? DateTimeOffset.UtcNow : null,
            Model = "opus", ClaudeSessionId = "native-" + id, MessageCount = 2, ProviderEntity = providerEntity,
            JobId = jobId, UserId = "owner", AccessLevel = SessionAccessLevels.Auto,
        });

    private static object? Scalar(string sql)
    {
        using var connection = new SqliteConnection($"Data Source={ClaudeDbContext.DbPath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    private static void Execute(string sql)
    {
        using var connection = new SqliteConnection($"Data Source={ClaudeDbContext.DbPath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
