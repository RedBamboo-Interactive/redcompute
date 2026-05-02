using System.Collections.Specialized;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using RedCompute.App.TrayIcon;

namespace RedCompute.App.Views;

public partial class MainWindow : Window
{
    private readonly DispatcherTimer _statusTimer;
    private readonly TrayIconManager _trayIcon = new();

    public MainWindow()
    {
        InitializeComponent();
        DataContext = App.MainViewModel;
        Loaded += OnLoaded;

        RestoreWindowState();

        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _statusTimer.Tick += (_, _) => App.MainViewModel.RefreshCapabilities();
    }

    private void RestoreWindowState()
    {
        var wc = App.ConfigManager.Config.Window;
        Width = wc.Width;
        Height = wc.Height;
        if (wc.Left.HasValue && wc.Top.HasValue)
        {
            Left = wc.Left.Value;
            Top = wc.Top.Value;
            WindowStartupLocation = WindowStartupLocation.Manual;
        }
        if (wc.IsMaximized)
            WindowState = WindowState.Maximized;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        App.MainViewModel.RefreshCapabilities();

        App.JobTracker.JobCreated += job => App.MainViewModel.JobsTab.OnJobCreated(job);
        App.JobTracker.JobUpdated += job => App.MainViewModel.JobsTab.OnJobUpdated(job);
        App.MainViewModel.JobsTab.Initialize();

        App.MainViewModel.LogEntries.CollectionChanged += LogEntries_CollectionChanged;

        _trayIcon.Initialize(this);
        _statusTimer.Start();
    }

    private void LogEntries_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add && LogsList != null)
        {
            LogsList.ScrollIntoView(LogsList.Items[^1]);
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _statusTimer.Stop();
        _trayIcon.Dispose();
        base.OnClosed(e);
    }

    private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
            DragMove();
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Maximize_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Hide();
    }
}
