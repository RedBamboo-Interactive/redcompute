using System.Text.Json.Nodes;
using Xunit;

namespace RedCompute.Node.Tests;

/// <summary>The local audit log's per-day cap: a server that floods the node cannot fill its disk.</summary>
public sealed class AuditLogTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "leaf-node-audit-" + Guid.NewGuid().ToString("N")[..10]);
    private readonly ManualTime _time = new(new DateTimeOffset(2026, 11, 16, 9, 0, 0, TimeSpan.Zero));

    public void Dispose()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
    }

    [Fact]
    public void A_day_stops_at_its_cap_with_one_truncated_marker_even_across_a_restart()
    {
        // Review finding: the audit log had no size cap.
        const long cap = 4096;
        var audit = new AuditLog(_folder, _time, cap);
        var file = Path.Combine(_folder, "2026-11-16.jsonl");

        for (var i = 0; i < 500; i++) audit.Record("request", "node.hardware", "answered", $"call {i}", "session-1");

        var size = new FileInfo(file).Length;
        Assert.InRange(size, cap / 2, cap + 512);
        var records = audit.Read();
        Assert.InRange(records.Count, 10, 499);
        var marker = records[^1];
        Assert.Equal(("audit", AuditLog.TruncatedOutcome), (marker["kind"]!.GetValue<string>(), marker["outcome"]!.GetValue<string>()));
        Assert.Single(records, record => record["outcome"]!.GetValue<string>() == AuditLog.TruncatedOutcome);
        Assert.All(records.SkipLast(1), record => Assert.Equal("node.hardware", record["name"]!.GetValue<string>()));

        // A restarted agent finds the marker and writes nothing more that day.
        var restarted = new AuditLog(_folder, _time, cap);
        restarted.Record("local", "pair", "paired");
        Assert.Equal(size, new FileInfo(file).Length);

        // The next day starts a new file.
        _time.Now = _time.Now.AddDays(1);
        restarted.Record("local", "pair", "paired");
        var next = Assert.Single(JsonNode.Parse(File.ReadAllLines(Path.Combine(_folder, "2026-11-17.jsonl")).Single())!.AsObject(),
            pair => pair.Key == "name");
        Assert.Equal("pair", next.Value!.GetValue<string>());
    }

    [Fact]
    public void Below_the_cap_nothing_is_dropped()
    {
        var audit = new AuditLog(_folder, _time);

        for (var i = 0; i < 200; i++) audit.Record("request", "node.cli", "answered");

        Assert.Equal(AuditLog.DefaultMaxDayBytes, audit.MaxDayBytes);
        var records = audit.Read();
        Assert.Equal(200, records.Count);
        Assert.DoesNotContain(records, record => record["outcome"]!.GetValue<string>() == AuditLog.TruncatedOutcome);
    }

    private sealed class ManualTime(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
