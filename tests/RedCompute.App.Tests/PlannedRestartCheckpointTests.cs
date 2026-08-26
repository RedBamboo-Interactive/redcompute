using RedCompute.Core.Sessions;
using Xunit;

namespace RedCompute.App.Tests;

public sealed class PlannedRestartCheckpointTests
{
    [Fact]
    public void Fresh_checkpoint_round_trips_and_is_consumed()
    {
        var directory = Path.Combine(Path.GetTempPath(), "redcompute-restart-tests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "checkpoint.json");
        var jobId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        try
        {
            PlannedRestartCheckpoint.Write(new PlannedRestartData(
                "run-1", now,
                [new PlannedRestartSession("codex", "session-1", jobId)]), path);

            var loaded = PlannedRestartCheckpoint.LoadForStartup(path, now.AddSeconds(5));

            Assert.NotNull(loaded);
            Assert.True(PlannedRestartCheckpoint.ContainsSession("CODEX", "session-1"));
            Assert.Contains(jobId, PlannedRestartCheckpoint.JobIds());
            PlannedRestartCheckpoint.Consume(path);
            Assert.False(File.Exists(path));
            Assert.Null(PlannedRestartCheckpoint.Current);
        }
        finally
        {
            PlannedRestartCheckpoint.Consume(path);
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Stale_checkpoint_is_ignored()
    {
        var directory = Path.Combine(Path.GetTempPath(), "redcompute-restart-tests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "checkpoint.json");
        var now = DateTimeOffset.UtcNow;
        try
        {
            PlannedRestartCheckpoint.Write(new PlannedRestartData(
                "old-run", now.AddHours(-1), []), path);

            Assert.Null(PlannedRestartCheckpoint.LoadForStartup(path, now));
        }
        finally
        {
            PlannedRestartCheckpoint.Consume(path);
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }
}
