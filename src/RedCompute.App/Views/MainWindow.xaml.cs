using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace RedCompute.App.Views;

public partial class MainWindow : Window
{
    private readonly DispatcherTimer _statusTimer;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = App.MainViewModel;
        Loaded += OnLoaded;

        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _statusTimer.Tick += (_, _) => App.MainViewModel.RefreshCapabilities();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        App.MainViewModel.RefreshCapabilities();
        App.MainViewModel.RefreshJobs();

        App.JobTracker.JobCreated += _ => App.MainViewModel.RefreshJobs();
        App.JobTracker.JobUpdated += _ => App.MainViewModel.RefreshJobs();

        // Auto-scroll logs
        App.MainViewModel.LogEntries.CollectionChanged += LogEntries_CollectionChanged;

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

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Nav_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && int.TryParse(fe.Tag?.ToString(), out var index))
        {
            DashboardTab.Visibility = index == 0 ? Visibility.Visible : Visibility.Collapsed;
            JobsTab.Visibility = index == 1 ? Visibility.Visible : Visibility.Collapsed;
            SettingsTab.Visibility = index == 2 ? Visibility.Visible : Visibility.Collapsed;
            LogsTab.Visibility = index == 3 ? Visibility.Visible : Visibility.Collapsed;
        }
    }
}
