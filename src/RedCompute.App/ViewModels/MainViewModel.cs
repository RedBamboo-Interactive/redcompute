using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RedCompute.Core.Jobs;

namespace RedCompute.App.ViewModels;

public partial class MainViewModel : ObservableObject
{
    [ObservableProperty]
    private int _selectedTabIndex;

    [ObservableProperty]
    private string _statusText = "Starting...";

    public ObservableCollection<CapabilityCardViewModel> CapabilityCards { get; } = new();
    private readonly Dictionary<string, CapabilityCardViewModel> _cardBySlug = new();
    public JobsTabViewModel JobsTab { get; } = new();
    public ConsoleLogViewModel ConsoleLog { get; } = new();

    public string LogFilePath => App.FileLogger.LogFilePath;

    [RelayCommand]
    private void OpenLogFile()
    {
        try { Process.Start(new ProcessStartInfo(LogFilePath) { UseShellExecute = true }); }
        catch { }
    }

    // Settings
    [ObservableProperty]
    private int _apiPort;

    [ObservableProperty]
    private int _jobRetentionDays;

    public ObservableCollection<CapabilitySettingsViewModel> CapabilitySettings { get; } = new();

    public string ConfigPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RedCompute", "config.json");

    public string ApiBaseUrl => $"http://localhost:{App.ConfigManager.Config.ApiPort}";

    public void LoadSettingsFromConfig()
    {
        ApiPort = App.ConfigManager.Config.ApiPort;
        JobRetentionDays = App.ConfigManager.Config.JobRetentionDays;

        CapabilitySettings.Clear();
        foreach (var (slug, capConfig) in App.ConfigManager.Config.Capabilities)
        {
            var def = Services.CapabilityDefinitionFactory.Create(slug);
            var providerKey = capConfig.ActiveProvider ?? "none";
            capConfig.Providers.TryGetValue(providerKey, out var provConfig);

            CapabilitySettings.Add(new CapabilitySettingsViewModel
            {
                Slug = slug,
                DisplayName = def?.DisplayName ?? slug,
                ActiveProvider = providerKey,
                BackendPort = provConfig?.BackendPort,
                WslDistro = provConfig?.WslDistro,
                ServerPath = provConfig?.ServerPath,
                Model = provConfig?.Model,
                HealthEndpoint = provConfig?.HealthEndpoint ?? "/health"
            });
        }
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

    public void RefreshCapabilities()
    {
        Application.Current?.Dispatcher?.BeginInvoke(async () =>
        {
            var recentJobs = App.JobTracker.GetJobsSince(DateTimeOffset.UtcNow.AddMinutes(-5));
            var jobsBySlug = recentJobs.GroupBy(j => j.CapabilitySlug)
                .ToDictionary(g => g.Key, g => g.OrderBy(j => j.QueuedAt).ToList());

            var seenSlugs = new HashSet<string>();

            foreach (var (slug, entry) in App.Registry.Capabilities)
            {
                seenSlugs.Add(slug);

                var status = entry.ActiveProvider != null
                    ? await entry.ActiveProvider.GetStatusAsync()
                    : Core.Providers.BackendStatus.Stopped;

                if (!_cardBySlug.TryGetValue(slug, out var card))
                {
                    card = new CapabilityCardViewModel
                    {
                        Slug = slug,
                        DisplayName = entry.Definition.DisplayName,
                        Type = entry.Definition.Type,
                        ProviderName = entry.ActiveProvider?.Name ?? entry.Config.ActiveProvider ?? "none"
                    };
                    _cardBySlug[slug] = card;
                    CapabilityCards.Add(card);
                }

                card.Status = status;
                card.ProviderName = entry.ActiveProvider?.Name ?? entry.Config.ActiveProvider ?? "none";

                jobsBySlug.TryGetValue(slug, out var slugJobs);
                card.RecomputeMiniFrieze(slugJobs ?? new List<JobRecord>());
                card.RecomputeJobFrieze();
            }

            for (int i = CapabilityCards.Count - 1; i >= 0; i--)
            {
                if (!seenSlugs.Contains(CapabilityCards[i].Slug))
                {
                    _cardBySlug.Remove(CapabilityCards[i].Slug);
                    CapabilityCards.RemoveAt(i);
                }
            }

            StatusText = $"Running — {CapabilityCards.Count} capabilities";
        });
    }

}
