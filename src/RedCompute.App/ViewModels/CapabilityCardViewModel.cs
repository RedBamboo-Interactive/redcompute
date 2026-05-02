using System.Collections.ObjectModel;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MaterialDesignThemes.Wpf;
using RedCompute.Core.Capabilities;
using RedCompute.Core.Jobs;
using RedCompute.Core.Providers;

namespace RedCompute.App.ViewModels;

public partial class CapabilityCardViewModel : ObservableObject
{
    private const int MiniQuantaCount = 32;
    private static readonly TimeSpan MiniQuantumSize = TimeSpan.FromSeconds(5);

    [ObservableProperty]
    private string _slug = "";

    [ObservableProperty]
    private string _displayName = "";

    [ObservableProperty]
    private BackendStatus _status;

    [ObservableProperty]
    private string _providerName = "";

    [ObservableProperty]
    private int _completedJobs;

    [ObservableProperty]
    private int _failedJobs;

    [ObservableProperty]
    private int _activeJobs;

    [ObservableProperty]
    private string _utilizationDisplay = "0%";

    public CapabilityType Type { get; init; }

    public PackIconKind IconKind => FriezeLaneViewModel.MapIcon(Type);

    public bool IsRunning => Status == BackendStatus.Running;

    public string StatusColor => Status switch
    {
        BackendStatus.Running => "#43A25A",
        BackendStatus.Starting => "#FFB74D",
        BackendStatus.Error => "#FF5252",
        BackendStatus.Draining => "#26A69A",
        _ => "#72767D"
    };

    public ObservableCollection<FriezeSegment> MiniSegments { get; } = new();

    partial void OnStatusChanged(BackendStatus value)
    {
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(StatusColor));
        ToggleCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private async Task Toggle()
    {
        var entry = App.Registry.Get(Slug);
        if (entry?.ActiveProvider == null) return;

        if (IsRunning)
        {
            await entry.ActiveProvider.StopAsync();
            Status = BackendStatus.Stopped;
        }
        else
        {
            Status = BackendStatus.Starting;
            var success = await entry.ActiveProvider.StartAsync();
            Status = success ? BackendStatus.Running : BackendStatus.Error;
        }
    }

    [RelayCommand]
    private void OpenSettings()
    {
        App.MainViewModel.SelectedTabIndex = 2;
    }

    public void UpdateStats(List<JobRecord> jobs, int windowSeconds = 300)
    {
        ActiveJobs = jobs.Count(j => j.Status == JobStatus.Running);
        CompletedJobs = jobs.Count(j => j.Status == JobStatus.Completed);
        FailedJobs = jobs.Count(j => j.Status == JobStatus.Failed);

        var totalDurationMs = jobs
            .Where(j => j.CompletedAt.HasValue && j.StartedAt.HasValue)
            .Sum(j => (j.CompletedAt!.Value - j.StartedAt!.Value).TotalMilliseconds);

        var pct = windowSeconds > 0 ? totalDurationMs / (windowSeconds * 1000.0) * 100 : 0;
        pct = Math.Min(pct, 100);
        UtilizationDisplay = $"{pct:F0}%";
    }

    public void RecomputeMiniFrieze(List<JobRecord> jobs)
    {
        if (jobs.Count == 0)
        {
            FillEmpty();
            return;
        }

        var now = DateTimeOffset.Now;
        var windowSpan = TimeSpan.FromMilliseconds(MiniQuantaCount * MiniQuantumSize.TotalMilliseconds);
        var windowStart = now - windowSpan;

        var intervals = new List<(DateTimeOffset Start, DateTimeOffset End, SolidColorBrush Brush, string Tooltip)>();

        foreach (var job in jobs)
        {
            if (job.Status == JobStatus.Queued)
            {
                intervals.Add((job.QueuedAt, now, FriezeColors.Queued, $"Queued: {job.CapabilitySlug}"));
                continue;
            }

            if (job.StartedAt.HasValue && job.QueuedAt < job.StartedAt.Value)
                intervals.Add((job.QueuedAt, job.StartedAt.Value, FriezeColors.Queued, $"Queued: {job.CapabilitySlug}"));

            var execStart = job.StartedAt ?? job.QueuedAt;
            var execEnd = job.CompletedAt ?? now;

            var (brush, tooltip) = job.Status switch
            {
                JobStatus.Running => (FriezeColors.Running, $"Running: {job.CapabilitySlug}"),
                JobStatus.Completed => (FriezeColors.Completed, $"Completed ({job.DurationMs}ms)"),
                JobStatus.Failed => (FriezeColors.Failed, $"Failed: {job.ErrorMessage ?? "unknown"}"),
                JobStatus.Cancelled => (FriezeColors.Cancelled, "Cancelled"),
                _ => (FriezeColors.Running, $"Running: {job.CapabilitySlug}")
            };

            intervals.Add((execStart, execEnd, brush, tooltip));
        }

        var desired = new List<(SolidColorBrush Brush, string Tooltip)>(MiniQuantaCount);

        for (int q = 0; q < MiniQuantaCount; q++)
        {
            var quantumStart = windowStart + TimeSpan.FromMilliseconds(q * MiniQuantumSize.TotalMilliseconds);
            var quantumEnd = quantumStart + MiniQuantumSize;

            SolidColorBrush bestBrush = FriezeColors.Idle;
            string bestTooltip = "";
            int bestPriority = -1;

            foreach (var interval in intervals)
            {
                if (interval.Start < quantumEnd && interval.End > quantumStart)
                {
                    int p = GetPriority(interval.Brush);
                    if (p > bestPriority)
                    {
                        bestPriority = p;
                        bestBrush = interval.Brush;
                        bestTooltip = interval.Tooltip;
                    }
                }
            }

            desired.Add((bestBrush, bestTooltip));
        }

        ApplySegments(desired);
    }

    private static int GetPriority(SolidColorBrush brush)
    {
        if (ReferenceEquals(brush, FriezeColors.Failed)) return 6;
        if (ReferenceEquals(brush, FriezeColors.Running)) return 5;
        if (ReferenceEquals(brush, FriezeColors.Queued)) return 4;
        if (ReferenceEquals(brush, FriezeColors.Completed)) return 3;
        if (ReferenceEquals(brush, FriezeColors.Cancelled)) return 2;
        return 0;
    }

    private void FillEmpty()
    {
        ApplySegments(Enumerable.Repeat((FriezeColors.Empty, ""), MiniQuantaCount).ToList());
    }

    private void ApplySegments(List<(SolidColorBrush Brush, string Tooltip)> desired)
    {
        for (int i = 0; i < Math.Min(MiniSegments.Count, desired.Count); i++)
        {
            MiniSegments[i].Color = desired[i].Brush;
            MiniSegments[i].Tooltip = desired[i].Tooltip;
        }

        for (int i = MiniSegments.Count; i < desired.Count; i++)
            MiniSegments.Add(new FriezeSegment(desired[i].Brush, desired[i].Tooltip));

        while (MiniSegments.Count > desired.Count)
            MiniSegments.RemoveAt(MiniSegments.Count - 1);
    }
}
