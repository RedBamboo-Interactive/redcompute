using System.Text.Json;
using RedCompute.App.Services;
using Xunit;

namespace RedCompute.App.Tests;

public sealed class MaintenanceStatusTests
{
    [Fact]
    public void Progress_retains_start_time_and_failure_releases_pause_without_success_claim()
    {
        var status = new MaintenanceStatus();
        var events = new List<MaintenanceStatusSnapshot>();
        status.Changed += events.Add;
        Assert.False(status.Current.Paused);
        status.Set("draining", "run-a");
        var start = status.Current.StartedAt;
        status.Set("draining", "run-a", 2);
        status.Set("draining", "run-a", 2);
        status.Set("draining", "run-a", 1);
        status.Set("launching", "run-a", 0);
        Assert.True(status.Current.Paused);
        Assert.Equal(start, status.Current.StartedAt);
        status.Set("failed", "run-a");
        Assert.False(status.Current.Paused);
        Assert.Equal("failed", status.Current.State);
        Assert.Equal(5, events.Count);
        status.Set("draining", "run-b");
        Assert.Null(status.Current.ActiveTurnCount);
        Assert.Equal("run-b", status.Current.RunId);
    }

    [Fact]
    public void A_failed_visibility_observer_cannot_strand_the_pause_or_block_other_observers()
    {
        var failures = new List<Exception>();
        var events = new List<MaintenanceStatusSnapshot>();
        var status = new MaintenanceStatus(failures.Add);
        status.Changed += _ => throw new InvalidOperationException("Disconnected observer");
        status.Changed += events.Add;
        status.Set("draining", "run-a");
        status.Set("failed", "run-a");
        Assert.False(status.Current.Paused);
        Assert.Equal(2, events.Count);
        Assert.Equal(2, failures.Count);
    }

    [Fact]
    public void Public_snapshot_exposes_only_progress_without_private_session_details()
    {
        var status = new MaintenanceStatus();
        status.Set("draining", "run-a", 3);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(status.Current,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        Assert.Equal(new[] { "activeTurnCount", "changedAt", "paused", "runId", "startedAt", "state" },
            json.RootElement.EnumerateObject().Select(property => property.Name).Order().ToArray());
    }
}
