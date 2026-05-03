using System.Collections.ObjectModel;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MaterialDesignThemes.Wpf;
using RedCompute.App.Views.Dialogs;
using RedCompute.Core.Capabilities;
using RedCompute.Core.Jobs;
using RedCompute.Core.Providers;

namespace RedCompute.App.ViewModels;

public partial class CapabilityCardViewModel : ObservableObject
{
    private const int MiniQuantaCount = 32;
    private const int JobFriezeCount = 32;
    private static readonly TimeSpan MiniQuantumSize = TimeSpan.FromSeconds(5);

    [ObservableProperty]
    private string _slug = "";

    [ObservableProperty]
    private string _displayName = "";

    [ObservableProperty]
    private BackendStatus _status;

    [ObservableProperty]
    private string _providerName = "";

    public CapabilityType Type { get; init; }

    public PackIconKind IconKind => MapIcon(Type);

    private static PackIconKind MapIcon(CapabilityType type) => type switch
    {
        CapabilityType.Tts => PackIconKind.VolumeHigh,
        CapabilityType.Stt => PackIconKind.Microphone,
        CapabilityType.ImageGen => PackIconKind.Image,
        CapabilityType.MusicGen => PackIconKind.MusicNote,
        CapabilityType.Llm => PackIconKind.Brain,
        CapabilityType.VideoGen => PackIconKind.Video,
        _ => PackIconKind.Cog
    };

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
    public ObservableCollection<FriezeSegment> JobSegments { get; } = new();

    partial void OnStatusChanged(BackendStatus value)
    {
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(StatusColor));
        ToggleCommand.NotifyCanExecuteChanged();
        QueueJobCommand.NotifyCanExecuteChanged();
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

    [RelayCommand(CanExecute = nameof(IsRunning))]
    private async Task QueueJob()
    {
        var vm = new QueueJobDialogViewModel(Slug, DisplayName);
        var view = new QueueJobDialog { DataContext = vm };
        _ = vm.LoadManifestAsync();
        await DialogHost.Show(view, "RootDialog");
    }

    [RelayCommand]
    private void OpenSettings()
    {
        App.MainViewModel.SelectedTabIndex = 2;
    }

    public void RecomputeJobFrieze()
    {
        var recent = App.JobTracker.GetJobs(capabilitySlug: Slug, limit: JobFriezeCount)
            .AsEnumerable()
            .Reverse()
            .ToList();

        var desired = new List<(SolidColorBrush Brush, string Tooltip)>(JobFriezeCount);

        for (int i = 0; i < JobFriezeCount - recent.Count; i++)
            desired.Add((FriezeColors.Empty, ""));

        foreach (var job in recent)
        {
            var (brush, tooltip) = job.Status switch
            {
                JobStatus.Completed => (FriezeColors.Completed, $"Completed ({job.DurationMs}ms)"),
                JobStatus.Failed => (FriezeColors.Failed, $"Failed: {job.ErrorMessage ?? "unknown"}"),
                JobStatus.Cancelled => (FriezeColors.Cancelled, "Cancelled"),
                JobStatus.Running => (FriezeColors.Running, "Running…"),
                JobStatus.Queued => (FriezeColors.Queued, "Queued"),
                _ => (FriezeColors.Idle, "")
            };
            desired.Add((brush, tooltip));
        }

        ApplySegments(JobSegments, desired);
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

        ApplySegments(MiniSegments, desired);
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
        ApplySegments(MiniSegments, Enumerable.Repeat((FriezeColors.Empty, ""), MiniQuantaCount).ToList());
    }

    private static void ApplySegments(ObservableCollection<FriezeSegment> segments, List<(SolidColorBrush Brush, string Tooltip)> desired)
    {
        for (int i = 0; i < Math.Min(segments.Count, desired.Count); i++)
        {
            segments[i].Color = desired[i].Brush;
            segments[i].Tooltip = desired[i].Tooltip;
        }

        for (int i = segments.Count; i < desired.Count; i++)
            segments.Add(new FriezeSegment(desired[i].Brush, desired[i].Tooltip));

        while (segments.Count > desired.Count)
            segments.RemoveAt(segments.Count - 1);
    }
}
