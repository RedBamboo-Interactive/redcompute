using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using MaterialDesignThemes.Wpf;
using RedCompute.App.ViewModels;

namespace RedCompute.App.Views.Components;

public partial class JobsTabContent : UserControl
{
    private readonly DispatcherTimer _positionTimer;
    private readonly List<AudioWidgetState> _audioWidgets = [];
    private bool _videoSeeking;
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
        StopAllAudio();
        StopVideo();

        var job = (DataContext as MainViewModel)?.JobsTab.SelectedJob;
        if (job == null || !job.HasOutputFile) return;

        if (job.IsAudioOutput)
            BuildAudioPlayers(job);
        else if (job.IsVideoOutput)
        {
            VideoPlayer.Source = new Uri(job.OutputLocation!);
            VideoPlayer.Stop();
        }
    }

    // ── Audio widgets ──

    private sealed class AudioWidgetState
    {
        public required MediaElement Player;
        public required PackIcon PlayPauseIcon;
        public required Slider SeekSlider;
        public required TextBlock TimeText;
        public bool IsPlaying;
        public bool IsSeeking;
    }

    private void BuildAudioPlayers(JobViewModel job)
    {
        AudioPlayersPanel.Children.Clear();
        _audioWidgets.Clear();

        for (int i = 0; i < job.ClipPaths.Count; i++)
        {
            var clipPath = job.ClipPaths[i];
            var widget = CreateAudioWidget(clipPath, job.ClipCount > 1 ? $"Variation {i + 1}" : null);
            AudioPlayersPanel.Children.Add(widget.Container);
            _audioWidgets.Add(widget.State);
        }
    }

    private (Border Container, AudioWidgetState State) CreateAudioWidget(string filePath, string? label)
    {
        var player = new MediaElement
        {
            LoadedBehavior = MediaState.Manual,
            UnloadedBehavior = MediaState.Close,
            Volume = 1.0,
            Visibility = Visibility.Collapsed,
            Source = new Uri(filePath)
        };

        var playIcon = new PackIcon
        {
            Kind = PackIconKind.Play,
            Width = 20, Height = 20,
            Foreground = (Brush)FindResource("AccentGreen")
        };

        var playBtn = new Button
        {
            Content = playIcon,
            Style = (Style)FindResource("MaterialDesignFlatButton"),
            Padding = new Thickness(0),
            MinWidth = 0, Width = 32, Height = 32
        };

        var slider = new Slider
        {
            Minimum = 0, Maximum = 100, Value = 0,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 8, 0),
            Foreground = (Brush)FindResource("AccentGreen")
        };

        var timeText = new TextBlock
        {
            Text = "0:00 / 0:00",
            Foreground = Brushes.White, Opacity = 0.5,
            FontSize = 11, FontFamily = new FontFamily("Consolas"),
            VerticalAlignment = VerticalAlignment.Center
        };

        var state = new AudioWidgetState
        {
            Player = player,
            PlayPauseIcon = playIcon,
            SeekSlider = slider,
            TimeText = timeText
        };

        player.MediaOpened += (_, _) =>
        {
            if (player.NaturalDuration.HasTimeSpan)
                timeText.Text = $"0:00 / {FormatTime(player.NaturalDuration.TimeSpan)}";
        };

        player.MediaEnded += (_, _) =>
        {
            state.IsPlaying = false;
            playIcon.Kind = PackIconKind.Play;
            slider.Value = 0;
            player.Stop();
            StopTimerIfIdle();
        };

        playBtn.Click += (_, _) =>
        {
            if (state.IsPlaying)
            {
                player.Pause();
                state.IsPlaying = false;
                playIcon.Kind = PackIconKind.Play;
                StopTimerIfIdle();
            }
            else
            {
                player.Play();
                state.IsPlaying = true;
                playIcon.Kind = PackIconKind.Pause;
                _positionTimer.Start();
            }
        };

        slider.AddHandler(Thumb.DragStartedEvent, new DragStartedEventHandler((_, _) => state.IsSeeking = true));
        slider.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler((_, _) =>
        {
            state.IsSeeking = false;
            if (player.NaturalDuration.HasTimeSpan)
            {
                var total = player.NaturalDuration.TimeSpan.TotalSeconds;
                player.Position = TimeSpan.FromSeconds(total * slider.Value / 100.0);
            }
        }));

        player.Stop();

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        grid.Children.Add(player);
        Grid.SetColumn(playBtn, 0); grid.Children.Add(playBtn);
        Grid.SetColumn(slider, 1); grid.Children.Add(slider);
        Grid.SetColumn(timeText, 2); grid.Children.Add(timeText);

        var content = new StackPanel();

        if (label != null)
        {
            content.Children.Add(new TextBlock
            {
                Text = label,
                Foreground = Brushes.White, Opacity = 0.5,
                FontSize = 11, Margin = new Thickness(0, 0, 0, 6)
            });
        }

        content.Children.Add(grid);

        var border = new Border
        {
            CornerRadius = new CornerRadius(6),
            Margin = new Thickness(0, 0, 0, 8),
            Padding = new Thickness(12, 10, 12, 10),
            Background = new SolidColorBrush(Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF)),
            Child = content
        };

        return (border, state);
    }

    private void StopAllAudio()
    {
        foreach (var w in _audioWidgets)
        {
            w.IsPlaying = false;
            w.PlayPauseIcon.Kind = PackIconKind.Play;
            w.SeekSlider.Value = 0;
            w.TimeText.Text = "0:00 / 0:00";
            w.Player.Stop();
            w.Player.Source = null;
        }
        _audioWidgets.Clear();
        AudioPlayersPanel.Children.Clear();
        StopTimerIfIdle();
    }

    private void StopTimerIfIdle()
    {
        if (!_audioWidgets.Any(w => w.IsPlaying) && !_videoPlaying)
            _positionTimer.Stop();
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
            StopTimerIfIdle();
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
        foreach (var w in _audioWidgets)
        {
            if (w.IsPlaying && !w.IsSeeking && w.Player.NaturalDuration.HasTimeSpan)
            {
                var total = w.Player.NaturalDuration.TimeSpan;
                var pos = w.Player.Position;
                w.SeekSlider.Value = total.TotalSeconds > 0 ? pos.TotalSeconds / total.TotalSeconds * 100 : 0;
                w.TimeText.Text = $"{FormatTime(pos)} / {FormatTime(total)}";
            }
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
