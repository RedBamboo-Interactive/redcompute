using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RedCompute.Core.Capabilities;
using RedCompute.Core.Jobs;

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

    [ObservableProperty]
    private ObservableCollection<FriezeLaneViewModel> _friezeLanes = new();

    [ObservableProperty]
    private ObservableCollection<JobViewModel> _recentJobs = new();

    [ObservableProperty]
    private JobViewModel? _selectedJob;

    private readonly Dictionary<string, List<JobRecord>> _jobsByCapability = new();
    private readonly Dictionary<string, FriezeLaneViewModel> _laneBySlug = new();

    private double _friezeAvailableWidth;
    public double FriezeAvailableWidth
    {
        get => _friezeAvailableWidth;
        set
        {
            if (Math.Abs(_friezeAvailableWidth - value) < 1) return;
            _friezeAvailableWidth = value;
            OnPropertyChanged();
            RecomputeAllFriezes();
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
        _elapsedTimer.Tick += (_, _) =>
        {
            RecomputeAllFriezes();
        };
    }

    public void Initialize()
    {
        foreach (var (slug, entry) in App.Registry.Capabilities)
        {
            EnsureLaneExists(slug, entry.Definition);
        }

        var jobs = App.JobTracker.GetJobs(limit: 100);
        foreach (var job in jobs)
        {
            EnsureLaneExists(job.CapabilitySlug);
            if (!_jobsByCapability.ContainsKey(job.CapabilitySlug))
                _jobsByCapability[job.CapabilitySlug] = new();
            _jobsByCapability[job.CapabilitySlug].Add(job);
            RecentJobs.Add(new JobViewModel(job));
        }

        foreach (var slug in _jobsByCapability.Keys)
            _jobsByCapability[slug] = _jobsByCapability[slug].OrderBy(j => j.QueuedAt).ToList();

        RecomputeAllFriezes();
        _elapsedTimer.Start();
    }

    public void OnJobCreated(JobRecord job)
    {
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            EnsureLaneExists(job.CapabilitySlug);

            if (!_jobsByCapability.ContainsKey(job.CapabilitySlug))
                _jobsByCapability[job.CapabilitySlug] = new();
            _jobsByCapability[job.CapabilitySlug].Add(job);

            var vm = new JobViewModel(job);
            RecentJobs.Insert(0, vm);

            while (RecentJobs.Count > 100)
                RecentJobs.RemoveAt(RecentJobs.Count - 1);

            SelectedJob = vm;
            RecomputeFriezeForLane(job.CapabilitySlug);
        });
    }

    public void OnJobUpdated(JobRecord job)
    {
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            if (_jobsByCapability.TryGetValue(job.CapabilitySlug, out var list))
            {
                var idx = list.FindIndex(j => j.Id == job.Id);
                if (idx >= 0) list[idx] = job;
            }

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

            RecomputeFriezeForLane(job.CapabilitySlug);
        });
    }

    private void EnsureLaneExists(string slug, CapabilityDefinition? definition = null)
    {
        if (_laneBySlug.ContainsKey(slug)) return;

        definition ??= Services.CapabilityDefinitionFactory.Create(slug);
        var type = definition?.Type ?? CapabilityType.Tts;
        var displayName = definition?.DisplayName ?? slug;

        var lane = new FriezeLaneViewModel(slug, displayName, type);
        _laneBySlug[slug] = lane;
        FriezeLanes.Add(lane);
        FillEmptyFrieze(lane.Segments);
    }

    [RelayCommand]
    private void ClearJobs()
    {
        RecentJobs.Clear();
        SelectedJob = null;
        _jobsByCapability.Clear();
        foreach (var lane in FriezeLanes)
        {
            lane.DurationText = "";
            FillEmptyFrieze(lane.Segments);
        }
    }

    private void RecomputeAllFriezes()
    {
        foreach (var lane in FriezeLanes)
            RecomputeFriezeForLane(lane.Slug);
    }

    private void RecomputeFriezeForLane(string slug)
    {
        if (!_laneBySlug.TryGetValue(slug, out var lane)) return;
        if (!_jobsByCapability.TryGetValue(slug, out var jobs) || jobs.Count == 0)
        {
            lane.DurationText = "";
            FillEmptyFrieze(lane.Segments);
            return;
        }

        var timelineStart = jobs[0].QueuedAt;
        var now = DateTimeOffset.Now;
        var totalSpan = now - timelineStart;

        lane.DurationText = totalSpan.TotalSeconds < 60
            ? $"{totalSpan.TotalSeconds:F0}s"
            : $"{(int)totalSpan.TotalMinutes}m {totalSpan.Seconds}s";

        if (totalSpan <= TimeSpan.Zero)
        {
            FillEmptyFrieze(lane.Segments);
            return;
        }

        var intervals = new List<(DateTimeOffset Start, DateTimeOffset End, SolidColorBrush Brush, string Tooltip)>();

        for (int i = 0; i < jobs.Count; i++)
        {
            var job = jobs[i];
            var jobStart = job.QueuedAt;

            var gapStart = i == 0 ? timelineStart : (jobs[i - 1].CompletedAt ?? jobs[i - 1].QueuedAt);
            if (gapStart < jobStart)
                intervals.Add((gapStart, jobStart, s_idle, "Idle"));

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
                JobStatus.Completed => (s_completed, $"Completed ({job.DurationMs}ms)"),
                JobStatus.Failed => (s_failed, $"Failed: {job.ErrorMessage ?? "unknown"}"),
                JobStatus.Cancelled => (s_cancelled, "Cancelled"),
                _ => (s_running, $"Running: {job.CapabilitySlug}")
            };

            intervals.Add((execStart, execEnd, brush, tooltip));
        }

        var lastJob = jobs[^1];
        var lastEnd = lastJob.CompletedAt;
        if (lastEnd.HasValue && lastEnd.Value < now)
            intervals.Add((lastEnd.Value, now, s_idle, "Idle"));

        var totalQuanta = (int)Math.Ceiling(totalSpan / QuantumSize);
        var perRow = Math.Max((int)(_friezeAvailableWidth / SquareSize), 1);
        var maxQ = perRow * MaxRows;
        var excess = Math.Max(totalQuanta - maxQ, 0);
        var skipQuanta = (int)Math.Ceiling((double)excess / perRow) * perRow;
        var quantumCount = totalQuanta - skipQuanta;

        var desired = new List<(SolidColorBrush Brush, string Tooltip)>(quantumCount);

        for (int q = 0; q < quantumCount; q++)
        {
            var actualIndex = skipQuanta + q;
            var quantumStart = timelineStart + TimeSpan.FromMilliseconds(actualIndex * QuantumSize.TotalMilliseconds);
            var quantumEnd = quantumStart + QuantumSize;

            SolidColorBrush bestBrush = s_idle;
            string bestTooltip = "Idle";
            int bestPriority = -1;

            foreach (var interval in intervals)
            {
                if (interval.Start < quantumEnd && interval.End > quantumStart)
                {
                    int p = GetIntervalPriority(interval.Brush);
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

        while (desired.Count < maxQ)
            desired.Add((s_empty, ""));

        ApplySegments(lane.Segments, desired);
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

    private void FillEmptyFrieze(ObservableCollection<FriezeSegment> segments)
    {
        var maxQ = MaxQuanta;
        if (maxQ <= 0) return;
        ApplySegments(segments, Enumerable.Repeat((s_empty, ""), maxQ).ToList());
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
