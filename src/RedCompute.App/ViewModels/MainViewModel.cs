using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace RedCompute.App.ViewModels;

public partial class MainViewModel : ObservableObject
{
    [ObservableProperty]
    private int _selectedTabIndex;

    [ObservableProperty]
    private string _statusText = "Starting...";

    public ObservableCollection<string> LogEntries { get; } = new();
    public ObservableCollection<CapabilityCardViewModel> CapabilityCards { get; } = new();
    public ObservableCollection<JobViewModel> RecentJobs { get; } = new();

    [ObservableProperty]
    private JobViewModel? _selectedJob;

    // Settings
    [ObservableProperty]
    private int _apiPort;

    [ObservableProperty]
    private int _jobRetentionDays;

    public string ConfigPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RedCompute", "config.json");

    public string ApiBaseUrl => $"http://localhost:{App.ConfigManager.Config.ApiPort}";

    public void LoadSettingsFromConfig()
    {
        ApiPort = App.ConfigManager.Config.ApiPort;
        JobRetentionDays = App.ConfigManager.Config.JobRetentionDays;
    }

    [RelayCommand]
    private void OpenConfig()
    {
        try { Process.Start(new ProcessStartInfo(ConfigPath) { UseShellExecute = true }); }
        catch { }
    }

    [RelayCommand]
    private void SaveConfig()
    {
        App.ConfigManager.Config.ApiPort = ApiPort;
        App.ConfigManager.Config.JobRetentionDays = JobRetentionDays;
        App.ConfigManager.Save();
        App.Log("[Settings] Configuration saved");
    }

    public void AddLog(string entry)
    {
        Application.Current?.Dispatcher?.BeginInvoke(() =>
        {
            LogEntries.Add(entry);
            if (LogEntries.Count > 5000)
                LogEntries.RemoveAt(0);
        });
    }

    public void RefreshCapabilities()
    {
        Application.Current?.Dispatcher?.BeginInvoke(async () =>
        {
            CapabilityCards.Clear();
            foreach (var (slug, entry) in App.Registry.Capabilities)
            {
                var status = entry.ActiveProvider != null
                    ? await entry.ActiveProvider.GetStatusAsync()
                    : Core.Providers.BackendStatus.Stopped;

                CapabilityCards.Add(new CapabilityCardViewModel
                {
                    Slug = slug,
                    DisplayName = entry.Definition.DisplayName,
                    Status = status,
                    ProviderName = entry.ActiveProvider?.Name ?? entry.Config.ActiveProvider ?? "none"
                });
            }
            StatusText = $"Running — {CapabilityCards.Count} capabilities";
        });
    }

    public void RefreshJobs()
    {
        Application.Current?.Dispatcher?.BeginInvoke(() =>
        {
            var jobs = App.JobTracker.GetJobs(limit: 100);
            RecentJobs.Clear();
            foreach (var job in jobs)
            {
                RecentJobs.Add(new JobViewModel(job));
            }
        });
    }
}
