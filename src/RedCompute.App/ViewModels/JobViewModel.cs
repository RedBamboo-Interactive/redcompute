using CommunityToolkit.Mvvm.ComponentModel;
using RedCompute.Core.Jobs;

namespace RedCompute.App.ViewModels;

public partial class JobViewModel : ObservableObject
{
    public Guid Id { get; }
    public string CapabilitySlug { get; }
    public string ProviderName { get; }
    public JobStatus Status { get; }
    public DateTimeOffset QueuedAt { get; }
    public DateTimeOffset? StartedAt { get; }
    public DateTimeOffset? CompletedAt { get; }
    public long? DurationMs { get; }
    public string InputJson { get; }
    public string? ErrorMessage { get; }
    public string? OutputLocation { get; }
    public long? OutputSizeBytes { get; }
    public string? OutputContentType { get; }
    public string? CallerInfo { get; }

    public bool IsRunning => Status == JobStatus.Running;

    public string StatusColor => Status switch
    {
        JobStatus.Completed => "#43A25A",
        JobStatus.Running => "#FFB74D",
        JobStatus.Failed => "#FF5252",
        JobStatus.Cancelled => "#72767D",
        _ => "#ADAEB3"
    };

    public string Summary
    {
        get
        {
            var duration = DurationMs.HasValue ? $" ({DurationMs}ms)" : "";
            return $"{CapabilitySlug.ToUpper()}{duration}";
        }
    }

    public string TimeDisplay => QueuedAt.ToLocalTime().ToString("HH:mm:ss");

    public string DurationDisplay => DurationMs.HasValue ? $"{DurationMs}ms" : "—";

    public string QueuedAtDisplay => QueuedAt.ToLocalTime().ToString("HH:mm:ss.fff");

    public string StartedAtDisplay => StartedAt?.ToLocalTime().ToString("HH:mm:ss.fff") ?? "—";

    public string CompletedAtDisplay => CompletedAt?.ToLocalTime().ToString("HH:mm:ss.fff") ?? "—";

    public string OutputSizeDisplay => OutputSizeBytes.HasValue
        ? OutputSizeBytes.Value < 1024 ? $"{OutputSizeBytes}B"
        : OutputSizeBytes.Value < 1048576 ? $"{OutputSizeBytes.Value / 1024.0:F1}KB"
        : $"{OutputSizeBytes.Value / 1048576.0:F1}MB"
        : "—";

    public JobViewModel(JobRecord record)
    {
        Id = record.Id;
        CapabilitySlug = record.CapabilitySlug;
        ProviderName = record.ProviderName;
        Status = record.Status;
        QueuedAt = record.QueuedAt;
        StartedAt = record.StartedAt;
        CompletedAt = record.CompletedAt;
        DurationMs = record.DurationMs;
        InputJson = record.InputJson;
        ErrorMessage = record.ErrorMessage;
        OutputLocation = record.OutputLocation;
        OutputSizeBytes = record.OutputSizeBytes;
        OutputContentType = record.OutputContentType;
        CallerInfo = record.CallerInfo;
    }
}
