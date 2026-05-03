using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using MaterialDesignThemes.Wpf;
using RedCompute.App.ViewModels;

namespace RedCompute.App.Views.Components;

public partial class JobsTabContent : UserControl
{
    private readonly DispatcherTimer _positionTimer;
    private bool _audioSeeking;
    private bool _videoSeeking;
    private bool _audioPlaying;
    private bool _videoPlaying;

    public JobsTabContent()
    {
        InitializeComponent();

        _positionTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _positionTimer.Tick += PositionTimer_Tick;

        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is MainViewModel oldVm)
            oldVm.JobsTab.PropertyChanged -= JobsTab_PropertyChanged;
        if (e.NewValue is MainViewModel newVm)
            newVm.JobsTab.PropertyChanged += JobsTab_PropertyChanged;
    }

    private void JobsTab_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(JobsTabViewModel.SelectedJob))
            OnSelectedJobChanged();
    }

    private void OnSelectedJobChanged()
    {
        StopAudio();
        StopVideo();

        var job = (DataContext as MainViewModel)?.JobsTab.SelectedJob;
        if (job == null || !job.HasOutputFile) return;

        var uri = new Uri(job.OutputLocation!);

        if (job.IsAudioOutput)
        {
            AudioPlayer.Source = uri;
            AudioPlayer.Stop();
        }
        else if (job.IsVideoOutput)
        {
            VideoPlayer.Source = uri;
            VideoPlayer.Stop();
        }
    }

    // ── Audio ──

    private void PlayPause_Click(object sender, RoutedEventArgs e)
    {
        var job = (DataContext as MainViewModel)?.JobsTab.SelectedJob;
        if (job == null || !job.IsAudioOutput || !job.HasOutputFile) return;

        if (AudioPlayer.Source == null)
            AudioPlayer.Source = new Uri(job.OutputLocation!);

        if (_audioPlaying)
        {
            AudioPlayer.Pause();
            _audioPlaying = false;
            AudioPlayPauseIcon.Kind = PackIconKind.Play;
            _positionTimer.Stop();
        }
        else
        {
            AudioPlayer.Play();
            _audioPlaying = true;
            AudioPlayPauseIcon.Kind = PackIconKind.Pause;
            _positionTimer.Start();
        }
    }

    private void AudioSeek_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _audioSeeking = true;
    }

    private void AudioSeek_MouseUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _audioSeeking = false;
        if (AudioPlayer.NaturalDuration.HasTimeSpan)
        {
            var total = AudioPlayer.NaturalDuration.TimeSpan.TotalSeconds;
            AudioPlayer.Position = TimeSpan.FromSeconds(total * AudioSeekSlider.Value / 100.0);
        }
    }

    private void Media_MediaOpened(object sender, RoutedEventArgs e)
    {
        if (AudioPlayer.NaturalDuration.HasTimeSpan)
        {
            var total = AudioPlayer.NaturalDuration.TimeSpan;
            AudioTimeText.Text = $"0:00 / {FormatTime(total)}";
        }
    }

    private void Media_MediaEnded(object sender, RoutedEventArgs e)
    {
        _audioPlaying = false;
        AudioPlayPauseIcon.Kind = PackIconKind.Play;
        _positionTimer.Stop();
        AudioSeekSlider.Value = 0;
        AudioPlayer.Stop();
    }

    private void StopAudio()
    {
        _audioPlaying = false;
        AudioPlayPauseIcon.Kind = PackIconKind.Play;
        _positionTimer.Stop();
        AudioSeekSlider.Value = 0;
        AudioTimeText.Text = "0:00 / 0:00";
        AudioPlayer.Stop();
        AudioPlayer.Source = null;
    }

    // ── Video ──

    private void VideoPlayPause_Click(object sender, RoutedEventArgs e)
    {
        var job = (DataContext as MainViewModel)?.JobsTab.SelectedJob;
        if (job == null || !job.IsVideoOutput || !job.HasOutputFile) return;

        if (VideoPlayer.Source == null)
            VideoPlayer.Source = new Uri(job.OutputLocation!);

        if (_videoPlaying)
        {
            VideoPlayer.Pause();
            _videoPlaying = false;
            VideoPlayPauseIcon.Kind = PackIconKind.Play;
            _positionTimer.Stop();
        }
        else
        {
            VideoPlayer.Play();
            _videoPlaying = true;
            VideoPlayPauseIcon.Kind = PackIconKind.Pause;
            _positionTimer.Start();
        }
    }

    private void VideoSeek_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _videoSeeking = true;
    }

    private void VideoSeek_MouseUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _videoSeeking = false;
        if (VideoPlayer.NaturalDuration.HasTimeSpan)
        {
            var total = VideoPlayer.NaturalDuration.TimeSpan.TotalSeconds;
            VideoPlayer.Position = TimeSpan.FromSeconds(total * VideoSeekSlider.Value / 100.0);
        }
    }

    private void VideoMedia_Opened(object sender, RoutedEventArgs e)
    {
        if (VideoPlayer.NaturalDuration.HasTimeSpan)
        {
            var total = VideoPlayer.NaturalDuration.TimeSpan;
            VideoTimeText.Text = $"0:00 / {FormatTime(total)}";
        }
    }

    private void VideoMedia_Ended(object sender, RoutedEventArgs e)
    {
        _videoPlaying = false;
        VideoPlayPauseIcon.Kind = PackIconKind.Play;
        _positionTimer.Stop();
        VideoSeekSlider.Value = 0;
        VideoPlayer.Stop();
    }

    private void StopVideo()
    {
        _videoPlaying = false;
        VideoPlayPauseIcon.Kind = PackIconKind.Play;
        VideoSeekSlider.Value = 0;
        VideoTimeText.Text = "0:00 / 0:00";
        VideoPlayer.Stop();
        VideoPlayer.Source = null;
    }

    // ── Shared ──

    private void PositionTimer_Tick(object? sender, EventArgs e)
    {
        if (_audioPlaying && !_audioSeeking && AudioPlayer.NaturalDuration.HasTimeSpan)
        {
            var total = AudioPlayer.NaturalDuration.TimeSpan;
            var pos = AudioPlayer.Position;
            AudioSeekSlider.Value = total.TotalSeconds > 0 ? pos.TotalSeconds / total.TotalSeconds * 100 : 0;
            AudioTimeText.Text = $"{FormatTime(pos)} / {FormatTime(total)}";
        }

        if (_videoPlaying && !_videoSeeking && VideoPlayer.NaturalDuration.HasTimeSpan)
        {
            var total = VideoPlayer.NaturalDuration.TimeSpan;
            var pos = VideoPlayer.Position;
            VideoSeekSlider.Value = total.TotalSeconds > 0 ? pos.TotalSeconds / total.TotalSeconds * 100 : 0;
            VideoTimeText.Text = $"{FormatTime(pos)} / {FormatTime(total)}";
        }
    }

    private void OpenOutputFile_Click(object sender, RoutedEventArgs e)
    {
        var job = (DataContext as MainViewModel)?.JobsTab.SelectedJob;
        if (job?.OutputLocation != null && File.Exists(job.OutputLocation))
            Process.Start(new ProcessStartInfo(job.OutputLocation) { UseShellExecute = true });
    }

    private void OpenOutputFolder_Click(object sender, RoutedEventArgs e)
    {
        var job = (DataContext as MainViewModel)?.JobsTab.SelectedJob;
        if (job?.OutputLocation != null && File.Exists(job.OutputLocation))
            Process.Start("explorer.exe", $"/select, \"{job.OutputLocation}\"");
    }

    private void FriezeItemsControl_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.WidthChanged && DataContext is MainViewModel vm)
            vm.JobsTab.FriezeAvailableWidth = e.NewSize.Width;
    }

    private static string FormatTime(TimeSpan ts) =>
        ts.TotalHours >= 1 ? ts.ToString(@"h\:mm\:ss") : ts.ToString(@"m\:ss");
}
