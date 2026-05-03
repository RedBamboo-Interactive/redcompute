using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RedCompute.Core.Jobs;
using RedCompute.Core.Logging;

namespace RedCompute.App.ViewModels;

public partial class JobsTabViewModel : ObservableObject
{
    private static readonly TimeSpan QuantumSize = TimeSpan.FromMilliseconds(500);
    private const int SquareSize = 10;
    private const int MaxRows = 5;

    private static readonly SolidColorBrush s_queued = FriezeColors.Queued;
    private static readonly SolidColorBrush s_running = FriezeColors.Running;
    private static readonly SolidColorBrush s_completed = FriezeColors.Completed;
    private static readonly SolidColorBrush s_failed = FriezeColors.Failed;
    private static readonly SolidColorBrush s_cancelled = FriezeColors.Cancelled;
    private static readonly SolidColorBrush s_idle = FriezeColors.Idle;
    private static readonly SolidColorBrush s_empty = FriezeColors.Empty;

    private static readonly List<ColorSlice> s_idleSlices = [new(s_idle, 1.0)];
    private static readonly List<ColorSlice> s_emptySlices = [new(s_empty, 1.0)];

    [ObservableProperty]
    private ObservableCollection<UnifiedFriezeSegment> _friezeSegments = new();

    [ObservableProperty]
    private string _friezeDurationText = "";

    [ObservableProperty]
    private ObservableCollection<JobViewModel> _recentJobs = new();

    [ObservableProperty]
    private JobViewModel? _selectedJob;

    partial void OnSelectedJobChanged(JobViewModel? value)
    {
        if (value == null) return;
        value.LogEntries.Clear();
        try
        {
            var logs = App.Logger.GetLogsForJob(value.Id);
            foreach (var entry in logs)
                value.LogEntries.Add(entry);
        }
        catch { }
        value.NotifyLogsChanged();
    }

    private readonly List<JobRecord> _allJobs = new();

    private double _friezeAvailableWidth;
    public double FriezeAvailableWidth
    {
        get => _friezeAvailableWidth;
        set
        {
            if (Math.Abs(_friezeAvailableWidth - value) < 1) return;
            _friezeAvailableWidth = value;
            OnPropertyChanged();
            RecomputeUnifiedFrieze();
        }
    }

    private int MaxQuanta
    {
        get
        {
            if (_friezeAvailableWidth <= 0) return 100;
            var perRow = (int)(_friezeAvailableWidth / SquareSize);
            return Math.Max(perRow, 1) * MaxRows;
        }
    }

    private readonly DispatcherTimer _elapsedTimer;

    public JobsTabViewModel()
    {
        _elapsedTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _elapsedTimer.Tick += (_, _) => RecomputeUnifiedFrieze();
    }

    public void Initialize()
    {
        var jobs = App.JobTracker.GetJobs(limit: 100);
        foreach (var job in jobs)
        {
            _allJobs.Add(job);
            RecentJobs.Add(new JobViewModel(job));
        }

        _allJobs.Sort((a, b) => a.QueuedAt.CompareTo(b.QueuedAt));
        RecomputeUnifiedFrieze();
        _elapsedTimer.Start();

        App.Logger.LogEntryCreated += OnLogEntryCreated;
    }

    private void OnLogEntryCreated(LogEntry entry)
    {
        if (entry.JobId == null) return;
        Application.Current?.Dispatcher?.BeginInvoke(() =>
        {
            if (SelectedJob != null && SelectedJob.Id == entry.JobId)
            {
                SelectedJob.LogEntries.Add(entry);
                SelectedJob.NotifyLogsChanged();
            }
        });
    }

    public void OnJobCreated(JobRecord job)
    {
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            _allJobs.Add(job);

            var vm = new JobViewModel(job);
            RecentJobs.Insert(0, vm);

            while (RecentJobs.Count > 100)
                RecentJobs.RemoveAt(RecentJobs.Count - 1);

            SelectedJob = vm;
            RecomputeUnifiedFrieze();
        });
    }

    public void OnJobUpdated(JobRecord job)
    {
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            var idx = _allJobs.FindIndex(j => j.Id == job.Id);
            if (idx >= 0) _allJobs[idx] = job;

            for (int i = 0; i < RecentJobs.Count; i++)
            {
                if (RecentJobs[i].Id == job.Id)
                {
                    var wasSelected = SelectedJob?.Id == job.Id;
                    RecentJobs[i] = new JobViewModel(job);
                    if (wasSelected)
                        SelectedJob = RecentJobs[i];
                    break;
                }
            }

            RecomputeUnifiedFrieze();
        });
    }

    [RelayCommand]
    private void ClearJobs()
    {
        RecentJobs.Clear();
        SelectedJob = null;
        _allJobs.Clear();
        FriezeDurationText = "";
        FillEmptyFrieze();
    }

    private void RecomputeUnifiedFrieze()
    {
        if (_allJobs.Count == 0)
        {
            FriezeDurationText = "";
            FillEmptyFrieze();
            return;
        }

        var timelineStart = _allJobs[0].QueuedAt;
        var now = DateTimeOffset.Now;
        var totalSpan = now - timelineStart;

        FriezeDurationText = totalSpan.TotalSeconds < 60
            ? $"{totalSpan.TotalSeconds:F0}s"
            : $"{(int)totalSpan.TotalMinutes}m {totalSpan.Seconds}s";

        if (totalSpan <= TimeSpan.Zero)
        {
            FillEmptyFrieze();
            return;
        }

        var intervals = new List<(DateTimeOffset Start, DateTimeOffset End, SolidColorBrush Brush, string Tooltip)>();

        foreach (var job in _allJobs)
        {
            if (job.Status == JobStatus.Queued)
            {
                intervals.Add((job.QueuedAt, now, s_queued, $"Queued: {job.CapabilitySlug}"));
                continue;
            }

            if (job.StartedAt.HasValue && job.QueuedAt < job.StartedAt.Value)
                intervals.Add((job.QueuedAt, job.StartedAt.Value, s_queued, $"Queued: {job.CapabilitySlug}"));

            var execStart = job.StartedAt ?? job.QueuedAt;
            var execEnd = job.CompletedAt ?? now;

            var (brush, tooltip) = job.Status switch
            {
                JobStatus.Running => (s_running, $"Running: {job.CapabilitySlug}"),
                JobStatus.Completed => (s_completed, $"Completed: {job.CapabilitySlug} ({job.DurationMs}ms)"),
                JobStatus.Failed => (s_failed, $"Failed: {job.CapabilitySlug} — {job.ErrorMessage ?? "unknown"}"),
                JobStatus.Cancelled => (s_cancelled, $"Cancelled: {job.CapabilitySlug}"),
                _ => (s_running, $"Running: {job.CapabilitySlug}")
            };

            intervals.Add((execStart, execEnd, brush, tooltip));
        }

        var totalQuanta = (int)Math.Ceiling(totalSpan / QuantumSize);
        var perRow = Math.Max((int)(_friezeAvailableWidth / SquareSize), 1);
        var maxQ = perRow * MaxRows;
        var excess = Math.Max(totalQuanta - maxQ, 0);
        var skipQuanta = (int)Math.Ceiling((double)excess / perRow) * perRow;
        var quantumCount = totalQuanta - skipQuanta;

        var desired = new List<UnifiedFriezeSegment>(quantumCount);

        for (int q = 0; q < quantumCount; q++)
        {
            var actualIndex = skipQuanta + q;
            var quantumStart = timelineStart + TimeSpan.FromMilliseconds(actualIndex * QuantumSize.TotalMilliseconds);
            var quantumEnd = quantumStart + QuantumSize;

            var overlapping = new List<(SolidColorBrush Brush, string Tooltip)>();

            foreach (var interval in intervals)
            {
                if (interval.Start < quantumEnd && interval.End > quantumStart)
                    overlapping.Add((interval.Brush, interval.Tooltip));
            }

            if (overlapping.Count == 0)
            {
                desired.Add(new UnifiedFriezeSegment(s_idleSlices, "Idle"));
                continue;
            }

            var distinct = overlapping
                .GroupBy(o => o.Brush, ReferenceEqualityComparer.Instance)
                .Select(g => (
                    Brush: (SolidColorBrush)g.Key!,
                    Tooltip: string.Join("\n", g.Select(x => x.Tooltip).Distinct())))
                .OrderByDescending(x => GetIntervalPriority(x.Brush))
                .ToList();

            if (distinct.Count == 1)
            {
                desired.Add(new UnifiedFriezeSegment(
                    [new ColorSlice(distinct[0].Brush, 1.0)],
                    distinct[0].Tooltip));
            }
            else
            {
                var proportion = 1.0 / distinct.Count;
                var slices = distinct.Select((d, i) =>
                    new ColorSlice(d.Brush, i == distinct.Count - 1 ? 1.0 - proportion * (distinct.Count - 1) : proportion))
                    .ToList();
                var combinedTooltip = string.Join("\n", distinct.Select(d => d.Tooltip));
                desired.Add(new UnifiedFriezeSegment(slices, combinedTooltip));
            }
        }

        while (desired.Count < maxQ)
            desired.Add(new UnifiedFriezeSegment(s_emptySlices, ""));

        ApplySegments(FriezeSegments, desired);
    }

    private static int GetIntervalPriority(SolidColorBrush brush)
    {
        if (ReferenceEquals(brush, s_failed)) return 6;
        if (ReferenceEquals(brush, s_running)) return 5;
        if (ReferenceEquals(brush, s_queued)) return 4;
        if (ReferenceEquals(brush, s_completed)) return 3;
        if (ReferenceEquals(brush, s_cancelled)) return 2;
        if (ReferenceEquals(brush, s_idle)) return 1;
        return 0;
    }

    private void FillEmptyFrieze()
    {
        var maxQ = MaxQuanta;
        if (maxQ <= 0) return;
        ApplySegments(FriezeSegments,
            Enumerable.Range(0, maxQ).Select(_ => new UnifiedFriezeSegment(s_emptySlices, "")).ToList());
    }

    private static void ApplySegments(ObservableCollection<UnifiedFriezeSegment> segments, List<UnifiedFriezeSegment> desired)
    {
        for (int i = 0; i < Math.Min(segments.Count, desired.Count); i++)
        {
            if (!SlicesEqual(segments[i].Slices, desired[i].Slices))
                segments[i].Slices = desired[i].Slices;
            if (segments[i].Tooltip != desired[i].Tooltip)
                segments[i].Tooltip = desired[i].Tooltip;
        }

        for (int i = segments.Count; i < desired.Count; i++)
            segments.Add(desired[i]);

        while (segments.Count > desired.Count)
            segments.RemoveAt(segments.Count - 1);
    }

    private static bool SlicesEqual(List<ColorSlice> a, List<ColorSlice> b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
            if (!ReferenceEquals(a[i].Brush, b[i].Brush) || Math.Abs(a[i].Proportion - b[i].Proportion) > 0.001)
                return false;
        return true;
    }
}
