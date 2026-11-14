using System.Text.Json;
using RedCompute.App.Services.Nodes;
using RedCompute.PluginSdk;
using Xunit;

namespace RedCompute.App.Tests;

/// <summary>Node zero's identity (design 2.1): created once, stable across restarts, never rewritten when unreadable.</summary>
public sealed class NodeRegistryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "redcompute-node-registry-tests", Guid.NewGuid().ToString("N"));
    private readonly List<string> _log = [];

    private string NodeJson => Path.Combine(_root, NodeIdentityStore.FileName);

    private NodeIdentityResult Load(DateTimeOffset? now = null)
        => NodeIdentityStore.LoadOrCreate(_root, _log.Add, now is { } at ? () => at : null);

    [Fact]
    public void First_boot_creates_node_json_with_a_ulid_id_whose_legacy_rows_id_is_itself()
    {
        var at = new DateTimeOffset(2026, 11, 14, 12, 0, 0, TimeSpan.Zero);
        var result = Load(at);

        Assert.True(result.Persisted);
        Assert.True(result.Created);
        Assert.Null(result.Problem);
        Assert.Matches("^node_[0-9A-HJKMNP-TV-Z]{26}$", result.Identity.Id);
        Assert.Equal(result.Identity.Id, result.Identity.LegacyRowsNodeId);

        using var file = JsonDocument.Parse(File.ReadAllText(NodeJson));
        Assert.Equal(result.Identity.Id, file.RootElement.GetProperty("id").GetString());
        Assert.Equal(result.Identity.Id, file.RootElement.GetProperty("legacyRowsNodeId").GetString());
        Assert.Equal(at, DateTimeOffset.Parse(file.RootElement.GetProperty("createdAt").GetString()!));
        // Atomic write: nothing but node.json is left behind.
        Assert.Equal([NodeIdentityStore.FileName], Directory.GetFiles(_root).Select(Path.GetFileName));
    }

    [Fact]
    public void The_id_is_stable_across_restarts_and_the_file_is_not_rewritten()
    {
        var first = Load();
        var bytes = File.ReadAllBytes(NodeJson);
        var written = File.GetLastWriteTimeUtc(NodeJson);

        for (var restart = 0; restart < 3; restart++)
        {
            var again = Load(DateTimeOffset.UtcNow.AddDays(restart + 1));
            Assert.True(again.Persisted);
            Assert.False(again.Created);
            Assert.Equal(first.Identity, again.Identity);
        }
        Assert.Equal(bytes, File.ReadAllBytes(NodeJson));
        Assert.Equal(written, File.GetLastWriteTimeUtc(NodeJson));
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("[]")]
    [InlineData("{\"createdAt\":\"2026-01-01T00:00:00Z\"}")]
    [InlineData("{\"id\":\"\"}")]
    [InlineData("{\"id\":\"has spaces in it\"}")]
    [InlineData("")]
    public void An_unreadable_node_json_is_left_as_it_is_and_the_run_uses_an_unpersisted_id(string content)
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(NodeJson, content);
        var bytes = File.ReadAllBytes(NodeJson);

        var first = Load();
        var second = Load();

        Assert.False(first.Persisted);
        Assert.False(first.Created);
        Assert.Contains("unreadable", first.Problem);
        Assert.Matches("^node_", first.Identity.Id);
        // Each run's stand-in id is its own: nothing may come to depend on it.
        Assert.NotEqual(first.Identity.Id, second.Identity.Id);
        Assert.Equal(bytes, File.ReadAllBytes(NodeJson));
        Assert.Equal([NodeIdentityStore.FileName], Directory.GetFiles(_root).Select(Path.GetFileName));
        Assert.Contains(_log, line => line.Contains("unreadable") && line.Contains("unpersisted"));
    }

    [Fact]
    public void A_node_json_that_is_a_directory_is_unreadable_and_untouched()
    {
        Directory.CreateDirectory(NodeJson);

        var result = Load();

        Assert.False(result.Persisted);
        Assert.NotNull(result.Problem);
        Assert.True(Directory.Exists(NodeJson));
        Assert.Empty(Directory.GetFiles(_root));
    }

    [Fact]
    public void A_missing_directory_is_created_on_first_boot()
    {
        Assert.False(Directory.Exists(_root));
        Assert.True(Load().Persisted);
        Assert.True(File.Exists(NodeJson));
    }

    [Fact]
    public void The_legacy_rows_id_is_kept_when_it_differs_from_the_id()
    {
        // After node zero moves (Phase 7), NULL rows still mean the machine that wrote them.
        Directory.CreateDirectory(_root);
        File.WriteAllText(NodeJson,
            "{\"id\":\"node_NEWSERVER\",\"createdAt\":\"2027-01-01T00:00:00Z\",\"legacyRowsNodeId\":\"node_LAURENTPC\"}");

        var registry = new NodeRegistry(Load(), Platform(), "0.2.0", DateTimeOffset.UtcNow);

        Assert.Equal("node_NEWSERVER", registry.LocalNodeId);
        Assert.Equal("node_LAURENTPC", registry.LegacyNodeId);
        INodeRegistry nodes = registry;
        Assert.Equal("node_LAURENTPC", nodes.Resolve(null));
        Assert.Equal("node_OTHER", nodes.Resolve("node_OTHER"));
    }

    [Fact]
    public void A_file_without_a_legacy_rows_id_reads_it_as_the_id()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(NodeJson, "{\"id\":\"node_ONLY\"}");
        var bytes = File.ReadAllBytes(NodeJson);

        var result = Load();

        Assert.True(result.Persisted);
        Assert.Equal("node_ONLY", result.Identity.LegacyRowsNodeId);
        Assert.Equal(bytes, File.ReadAllBytes(NodeJson));
    }

    [Fact]
    public void The_registry_has_exactly_the_local_node_and_accepts_the_local_alias()
    {
        var seen = new DateTimeOffset(2026, 11, 14, 13, 0, 0, TimeSpan.Zero);
        var started = seen.AddHours(-1);
        var registry = new NodeRegistry(Load(), Platform(), "0.2.0", started, () => seen);

        var local = Assert.Single(registry.Nodes);
        Assert.Equal(registry.LocalNodeId, local.Id);
        Assert.Equal("GOLDEN-PC", local.Name);
        Assert.Equal(RedCompute.Core.Nodes.NodeKind.Local, local.Kind);
        Assert.Equal(RedCompute.Core.Nodes.NodeStatus.Online, local.Status);
        Assert.Equal(started, local.StartedAt);
        Assert.Equal(seen, local.LastSeenAt);
        Assert.Equal("0.2.0", local.AgentVersion);

        Assert.Equal(local, registry.Find("local"));
        Assert.Equal(local, registry.Find("LOCAL"));
        Assert.Equal(local, registry.Find(registry.LocalNodeId));
        Assert.Null(registry.Find(registry.LocalNodeId.ToLowerInvariant()));
        Assert.Null(registry.Find("node_UNKNOWN"));
    }

    [Fact]
    public void Ids_are_time_ordered()
    {
        var earlier = NodeIdentityStore.NewId(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var later = NodeIdentityStore.NewId(new DateTimeOffset(2026, 1, 1, 0, 0, 1, TimeSpan.Zero));
        Assert.True(string.CompareOrdinal(earlier, later) < 0);
        Assert.NotEqual(NodeIdentityStore.NewId(DateTimeOffset.UtcNow), NodeIdentityStore.NewId(DateTimeOffset.UtcNow));
    }

    [Fact]
    public void The_platform_is_detected_from_this_machine()
    {
        var platform = NodePlatform.Detect();
        Assert.Equal(Environment.MachineName, platform.MachineName);
        Assert.Equal(OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsLinux() ? "linux" : platform.Os, platform.Os);
        Assert.False(string.IsNullOrWhiteSpace(platform.Architecture));
        Assert.False(string.IsNullOrWhiteSpace(platform.OsDescription));
    }

    internal static NodePlatform Platform()
        => new("windows", "Microsoft Windows 10.0.26200", "x64", "GOLDEN-PC", "Golden CPU 9000");

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { }
    }
}
