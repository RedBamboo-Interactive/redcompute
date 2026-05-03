using System.IO;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using RedCompute.Core.Jobs;

namespace RedCompute.App.ViewModels;

public partial class JobViewModel : ObservableObject
{
    private static readonly string OutputDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RedCompute", "outputs");
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

    public string? OutputMediaCategory => OutputContentType switch
    {
        string ct when ct.StartsWith("image/") => "image",
        string ct when ct.StartsWith("audio/") => "audio",
        string ct when ct.StartsWith("video/") => "video",
        _ => null
    };

    public bool IsImageOutput => OutputMediaCategory == "image";
    public bool IsAudioOutput => OutputMediaCategory == "audio";
    public bool IsVideoOutput => OutputMediaCategory == "video";
    public bool HasOutputFile => OutputLocation != null && File.Exists(OutputLocation);

    public List<string> ClipPaths { get; }
    public int ClipCount => ClipPaths.Count;
    public bool HasMultipleClips => ClipCount > 1;

    [ObservableProperty]
    private int _selectedClipIndex;

    public string? SelectedClipPath => SelectedClipIndex >= 0 && SelectedClipIndex < ClipPaths.Count
        ? ClipPaths[SelectedClipIndex] : null;

    partial void OnSelectedClipIndexChanged(int value) => OnPropertyChanged(nameof(SelectedClipPath));

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

    private BitmapImage? _outputImageSource;
    private bool _imageLoaded;

    public BitmapImage? OutputImageSource
    {
        get
        {
            if (!_imageLoaded)
            {
                _imageLoaded = true;
                if (IsImageOutput && HasOutputFile)
                {
                    try
                    {
                        var bmp = new BitmapImage();
                        bmp.BeginInit();
                        bmp.CacheOption = BitmapCacheOption.OnLoad;
                        bmp.UriSource = new Uri(OutputLocation!);
                        bmp.EndInit();
                        bmp.Freeze();
                        _outputImageSource = bmp;
                    }
                    catch { }
                }
            }
            return _outputImageSource;
        }
    }

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

        ClipPaths = BuildClipPaths(record);
    }

    private static List<string> BuildClipPaths(JobRecord record)
    {
        var paths = new List<string>();
        if (record.OutputLocation != null && File.Exists(record.OutputLocation))
            paths.Add(record.OutputLocation);

        for (int i = 1; i <= 4; i++)
        {
            var clipPath = Path.Combine(OutputDir, $"{record.Id}_clip{i}.mp3");
            if (File.Exists(clipPath))
                paths.Add(clipPath);
            else
                break;
        }
        return paths;
    }
}
