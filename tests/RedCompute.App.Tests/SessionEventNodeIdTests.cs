using System.Reflection;
using Microsoft.Data.Sqlite;
using RedCompute.App.Api.Endpoints;
using RedCompute.App.Services;
using RedCompute.App.Services.Nodes;
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
/// One node per row for every consumer: a session.created or session.updated event, projected as
/// RelayServer projects it (<see cref="SessionEventProjection"/>), reports the node a GET of that
/// session reports. A row with NodeId NULL is the legacy node on both, never null on one and the
/// legacy id on the other. The registry's legacy id differs from its local id, so the two cannot be
/// confused. Real provider stores on scratch databases.
/// </summary>
[Collection("Plugin database path")]
public sealed class SessionEventNodeIdTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "redcompute-event-nodeid", Guid.NewGuid().ToString("N"));
    private readonly string _claudePath = ClaudeDbContext.DbPath;
    private readonly string _codexPath = CodexDbContext.DbPath;
    private readonly string _openCodePath = OpenCodeDbContext.DbPath;
    private readonly INodeRegistry _nodes = new NodeRegistry(
        new NodeIdentityResult(new NodeIdentity("node_now", DateTimeOffset.UtcNow, "node_legacy"), true, false, null),
        NodeRegistryTests.Platform(), "0.2.0", DateTimeOffset.UtcNow);

    public SessionEventNodeIdTests()
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
    public void Claude_events_and_reads_report_one_node_per_row()
    {
        var legacyStore = new ClaudeSessionStore(null, null);
        legacyStore.SaveSession(new ClaudeSessionRecord
        {
            Id = "legacy", ProjectName = "P", ProjectPath = _root, Status = "Stopped", StartedAt = DateTimeOffset.UtcNow,
        });
        new ClaudeSessionStore(null, "node_now").SaveSession(new ClaudeSessionRecord
        {
            Id = "stamped", ProjectName = "P", ProjectPath = _root, Status = "Stopped", StartedAt = DateTimeOffset.UtcNow,
        });
        var service = new ClaudeSessionService(new ClaudeConfig(), new MemoryJobTracker(), legacyStore, new NullQualityModes(),
            () => null, (_, _) => { });
        var provider = new ClaudeCodeProvider("ai-session", service, legacyStore, new MemoryJobTracker(), (_, _) => { });

        Verify(provider, service, legacyStore.FindSession, ClaudeSessionStore.ToSnapshot,
            new ClaudeSessionInfo { Id = "fresh", ProjectName = "P", ProjectPath = _root });
    }

    [Fact]
    public void Codex_events_and_reads_report_one_node_per_row()
    {
        var legacyStore = new CodexSessionStore(null);
        legacyStore.SaveSession(new CodexSessionRecord
        {
            Id = "legacy", ProjectName = "P", ProjectPath = _root, Status = "Stopped", StartedAt = DateTimeOffset.UtcNow,
        });
        new CodexSessionStore("node_now").SaveSession(new CodexSessionRecord
        {
            Id = "stamped", ProjectName = "P", ProjectPath = _root, Status = "Stopped", StartedAt = DateTimeOffset.UtcNow,
        });
        var service = new CodexInteractiveService(new CodexConfig(), legacyStore, null!, null!, null!, null!, null!, () => null,
            (_, _) => { });
        var provider = new CodexProvider("ai-session", service, new MemoryJobTracker(), (_, _) => { });

        Verify(provider, service, legacyStore.FindSession, CodexSessionStore.ToSnapshot,
            new CodexSessionInfo { Id = "fresh", ProjectName = "P", ProjectPath = _root, Status = "Idle" });
    }

    [Fact]
    public void OpenCode_events_and_reads_report_one_node_per_row()
    {
        var legacyStore = new OpenCodeSessionStore(null);
        legacyStore.SaveSession(new OpenCodeSessionRecord
        {
            Id = "legacy", ProjectName = "P", ProjectPath = _root, Status = "Stopped", StartedAt = DateTimeOffset.UtcNow,
        });
        new OpenCodeSessionStore("node_now").SaveSession(new OpenCodeSessionRecord
        {
            Id = "stamped", ProjectName = "P", ProjectPath = _root, Status = "Stopped", StartedAt = DateTimeOffset.UtcNow,
        });
        var service = new OpenCodeSessionService(new OpenCodeConfig(), new MemoryJobTracker(), legacyStore, (_, _) => { },
            new OpenCodeNativeSessionReader(Path.Combine(_root, "native-none.db")));
        var provider = new OpenCodeProvider("ai-session", service, legacyStore, new MemoryJobTracker(), (_, _) => { });

        Verify(provider, service, legacyStore.FindSession, OpenCodeSessionStore.ToSnapshot,
            new OpenCodeSessionInfo { Id = "fresh", ProjectName = "P", ProjectPath = _root, Status = "Idle" });
    }

    /// <summary>
    /// session.updated for the legacy and the stamped row (the info a resume builds from the row), and
    /// session.created for a new session whose store has no persisted identity to stamp (so its row is
    /// NULL too): each event's node, projected as RelayServer broadcasts it, equals the node of a GET,
    /// both the provider's read and the mirror read (the row's snapshot), resolved as the API resolves it.
    /// </summary>
    private void Verify<TRecord, TInfo>(ISessionProvider provider, object service, Func<string, TRecord?> find,
        Func<TRecord, AiSessionSnapshot> snapshot, TInfo fresh)
        where TRecord : class where TInfo : class
    {
        var events = new List<(string Type, UnifiedSessionInfo Session)>();
        ((IPluginEventSource)provider).PluginEvent += (type, data) =>
        {
            if (data is UnifiedSessionInfo session) events.Add((type, session));
        };

        // A new session, persisted by the service's own save through a store without a stamp.
        Persist(service, fresh);
        Raise(service, "SessionCreated", fresh);
        Raise(service, "SessionUpdated", FromRow(service, find("legacy")!));
        Raise(service, "SessionUpdated", FromRow(service, find("stamped")!));

        Assert.Equal(["session.created", "session.updated", "session.updated"], events.Select(e => e.Type));
        Assert.Equal(["node_legacy", "node_legacy", "node_now"], events.Select(e =>
        {
            SessionEventProjection.ApplyNode(e.Session, _nodes);
            return e.Session.NodeId;
        }));
        foreach (var (_, session) in events)
        {
            var read = UnifiedSessionEndpoints.WithNode(provider.GetSessionInfo(session.Id)!, _nodes).NodeId;
            var mirrored = UnifiedSessionEndpoints.WithNode(new UnifiedSessionInfo
            {
                Id = session.Id, Provider = session.Provider, ProjectName = "P", ProjectPath = _root,
                NodeId = snapshot(find(session.Id)!).NodeId,
            }, _nodes).NodeId;
            Assert.Equal(read, session.NodeId);
            Assert.Equal(mirrored, session.NodeId);
        }
    }

    private static void Persist(object service, object info)
    {
        var name = service is CodexInteractiveService ? "Persist" : "PersistSessionRecord";
        service.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic, [info.GetType()])!
            .Invoke(service, [info]);
    }

    /// <summary>The service's own mapping from a stored row to the info its resume and reads report.</summary>
    private static object FromRow(object service, object record)
    {
        var name = service is CodexInteractiveService ? "ToInfo" : "ToSessionInfo";
        return service.GetType().GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic, [record.GetType()])!
            .Invoke(null, [record])!;
    }

    private static void Raise(object service, string name, object info)
        => ((Delegate)service.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(service)!)
            .DynamicInvoke(info);
}
