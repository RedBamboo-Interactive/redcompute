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
    public string? CallerInfo { get; }

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
        CallerInfo = record.CallerInfo;
    }
}
