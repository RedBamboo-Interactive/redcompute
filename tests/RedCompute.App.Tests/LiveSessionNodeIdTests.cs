using System.Reflection;
using Microsoft.Data.Sqlite;
using RedCompute.Core.Sessions;
using RedCompute.Plugin.ClaudeCode;
using RedCompute.Plugin.Codex;
using RedCompute.Plugin.OpenCode;
using RedCompute.PluginSdk;
using Xunit;
using MemoryJobTracker = RedCompute.App.Tests.ClaudeBackgroundWorkTests.MemoryJobTracker;
using NullQualityModes = RedCompute.App.Tests.ClaudeBackgroundWorkTests.NullQualityModes;

namespace RedCompute.App.Tests;

/// <summary>
/// Node zero, carried from M3 (decision 2): the session info Claude, Codex and OpenCode report carries
/// the node its row records, uniformly. A loaded row reports its own node (NULL stays null, which the
/// API reads as the legacy node); persisting a session copies the store's node back, so a new session
/// reports the stamp its insert wrote and a later save never changes it. Real stores on scratch
/// databases, never the installed ones.
/// </summary>
[Collection("Plugin database path")]
public sealed class LiveSessionNodeIdTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "redcompute-live-nodeid", Guid.NewGuid().ToString("N"));
    private readonly string _claudePath = ClaudeDbContext.DbPath;
    private readonly string _codexPath = CodexDbContext.DbPath;
    private readonly string _openCodePath = OpenCodeDbContext.DbPath;

    public LiveSessionNodeIdTests()
    {
        Directory.CreateDirectory(_root);
        ClaudeDbContext.DbPath = Path.Combine(_root, "claude.db");
        CodexDbContext.DbPath = Path.Combine(_root, "codex.db");
        OpenCodeDbContext.DbPath = Path.Combine(_root, "opencode.db");
        using (var db = new ClaudeDbContext()) db.Initialize();
        using (var db = new CodexDbContext()) db.Initialize();
        using (var db = new OpenCodeDbContext()) db.Initialize();
    }

    public void Dispose()
    {
        ClaudeDbContext.DbPath = _claudePath;
        CodexDbContext.DbPath = _codexPath;
        OpenCodeDbContext.DbPath = _openCodePath;
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void Claude_reports_the_node_of_its_row()
    {
        new ClaudeSessionStore(null, "node_zero").SaveSession(ClaudeRow("stamped"));
        new ClaudeSessionStore(null, null).SaveSession(ClaudeRow("legacy"));
        var store = new ClaudeSessionStore(null, "node_zero");
        var service = new ClaudeSessionService(new ClaudeConfig(), new MemoryJobTracker(), store, new NullQualityModes(),
            () => null, (_, _) => { });
        ISessionProvider provider = new ClaudeCodeProvider("ai-session", service, store, new MemoryJobTracker(), (_, _) => { });

        Assert.Equal("node_zero", provider.GetSessionInfo("stamped")!.NodeId);
        Assert.Null(provider.GetSessionInfo("legacy")!.NodeId);
        Assert.Equal("node_zero", provider.GetSession("stamped").Info!.NodeId);
        Assert.Contains(provider.GetSessions(20, true), s => s.Id == "stamped" && s.NodeId == "node_zero");

        // A new session reports the stamp its first save wrote; a store stamping another node keeps it.
        var info = new ClaudeSessionInfo { Id = "new", ProjectName = "P", ProjectPath = _root };
        Persist(service, info);
        Assert.Equal("node_zero", info.NodeId);
        var other = new ClaudeSessionService(new ClaudeConfig(), new MemoryJobTracker(), new ClaudeSessionStore(null, "node_other"),
            new NullQualityModes(), () => null, (_, _) => { });
        var later = new ClaudeSessionInfo { Id = "new", ProjectName = "P", ProjectPath = _root };
        Persist(other, later);
        Assert.Equal("node_zero", later.NodeId);
    }

    [Fact]
    public void Codex_reports_the_node_of_its_row()
    {
        new CodexSessionStore("node_zero").SaveSession(CodexRow("stamped"));
        new CodexSessionStore(null).SaveSession(CodexRow("legacy"));
        var store = new CodexSessionStore("node_zero");
        var service = new CodexInteractiveService(new CodexConfig(), store, null!, null!, null!, null!, null!, () => null, (_, _) => { });
        ISessionProvider provider = new CodexProvider("ai-session", service, new MemoryJobTracker(), (_, _) => { });

        Assert.Equal("node_zero", provider.GetSessionInfo("stamped")!.NodeId);
        Assert.Null(provider.GetSessionInfo("legacy")!.NodeId);
        Assert.Equal("node_zero", provider.GetSession("stamped").Info!.NodeId);

        var info = new CodexSessionInfo { Id = "new", ProjectName = "P", ProjectPath = _root, Status = "Idle" };
        Persist(service, info);
        Assert.Equal("node_zero", info.NodeId);
        var other = new CodexInteractiveService(new CodexConfig(), new CodexSessionStore("node_other"), null!, null!, null!, null!,
            null!, () => null, (_, _) => { });
        var later = new CodexSessionInfo { Id = "new", ProjectName = "P", ProjectPath = _root, Status = "Stopped" };
        Persist(other, later);
        Assert.Equal("node_zero", later.NodeId);
    }

    [Fact]
    public void OpenCode_reports_the_node_of_its_row()
    {
        new OpenCodeSessionStore("node_zero").SaveSession(OpenCodeRow("stamped"));
        new OpenCodeSessionStore(null).SaveSession(OpenCodeRow("legacy"));
        var store = new OpenCodeSessionStore("node_zero");
        var service = OpenCode(store);
        ISessionProvider provider = new OpenCodeProvider("ai-session", service, store, new MemoryJobTracker(), (_, _) => { });

        Assert.Equal("node_zero", provider.GetSessionInfo("stamped")!.NodeId);
        Assert.Null(provider.GetSessionInfo("legacy")!.NodeId);
        Assert.Equal("node_zero", provider.GetSession("stamped").Info!.NodeId);

        var info = new OpenCodeSessionInfo { Id = "new", ProjectName = "P", ProjectPath = _root, Status = "Idle" };
        Persist(service, info);
        Assert.Equal("node_zero", info.NodeId);
        var later = new OpenCodeSessionInfo { Id = "new", ProjectName = "P", ProjectPath = _root, Status = "Stopped" };
        Persist(OpenCode(new OpenCodeSessionStore("node_other")), later);
        Assert.Equal("node_zero", later.NodeId);
    }

    private OpenCodeSessionService OpenCode(IOpenCodeSessionStore store)
        => new(new OpenCodeConfig(), new MemoryJobTracker(), store, (_, _) => { },
            new OpenCodeNativeSessionReader(Path.Combine(_root, "native-none.db")));

    /// <summary>The service's own persist of a session's info (the path every live save takes).</summary>
    private static void Persist(object service, object info)
    {
        var name = service switch
        {
            CodexInteractiveService => "Persist",
            _ => "PersistSessionRecord",
        };
        service.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic, [info.GetType()])!
            .Invoke(service, [info]);
    }

    private ClaudeSessionRecord ClaudeRow(string id) => new()
    {
        Id = id, ProjectName = "P", ProjectPath = _root, Status = "Stopped", StartedAt = DateTimeOffset.UtcNow,
    };

    private CodexSessionRecord CodexRow(string id) => new()
    {
        Id = id, ProjectName = "P", ProjectPath = _root, Status = "Stopped", StartedAt = DateTimeOffset.UtcNow,
    };

    private OpenCodeSessionRecord OpenCodeRow(string id) => new()
    {
        Id = id, ProjectName = "P", ProjectPath = _root, Status = "Stopped", StartedAt = DateTimeOffset.UtcNow,
    };
}
