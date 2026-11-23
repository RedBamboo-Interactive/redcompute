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

    [Fact]
    public void A_read_and_a_write_by_another_log_on_the_folder_do_not_lock_each_other_out()
    {
        // The agent and a reader (leaf-node status, a test) each have their own AuditLog on one folder, so the in-process
        // gate does not order them: the reader used to open the file FileShare.Read, which fails against a writer that
        // holds it, and made the writer's append fail too (Record drops a line on an IOException).
        var writer = new AuditLog(_folder, _time);
        var reader = new AuditLog(_folder, _time);
        writer.Record("request", "first", "answered");
        var file = Path.Combine(_folder, "2026-11-16.jsonl");

        // Another day's file is held open by a writer, with a line half written.
        using (var appending = new FileStream(Path.Combine(_folder, "2026-11-15.jsonl"), FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
        {
            appending.Write("{\"at\":\"2026-11-15T09:00:00+00:00\",\"kind\":\"request\",\"name\":\"half"u8);
            appending.Flush();
            Assert.Equal("first", Assert.Single(reader.Read())["name"]!.GetValue<string>());
        }

        using (var reading = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            writer.Record("request", "second", "answered");
        }
        Assert.Equal(2, reader.Read().Count(record => record["name"]!.GetValue<string>() is "first" or "second"));
    }

    private sealed class ManualTime(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
