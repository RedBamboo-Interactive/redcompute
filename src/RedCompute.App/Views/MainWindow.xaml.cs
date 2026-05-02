using System.Windows;
using System.Windows.Input;

namespace RedCompute.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContext = App.MainViewModel;
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        App.MainViewModel.RefreshCapabilities();
        App.MainViewModel.RefreshJobs();

        App.JobTracker.JobCreated += _ => App.MainViewModel.RefreshJobs();
        App.JobTracker.JobUpdated += _ => App.MainViewModel.RefreshJobs();
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
