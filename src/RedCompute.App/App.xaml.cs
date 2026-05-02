using System.Threading;
using System.Windows;
using RedCompute.App.Data;
using RedCompute.App.Services;
using RedCompute.App.Services.Jobs;
using RedCompute.App.Api;
using RedCompute.App.ViewModels;
using RedCompute.Core.Providers;
using Microsoft.EntityFrameworkCore;

namespace RedCompute.App;

public partial class App : Application
{
    private static Mutex? _mutex;
    private CancellationTokenSource? _relayCts;
    private RelayServer? _relayServer;

    public static ConfigManager ConfigManager { get; } = new();
    public static CapabilityRegistry Registry { get; } = new();
    public static JobTrackingService JobTracker { get; } = new();
    public static MainViewModel MainViewModel { get; } = new();

    protected override async void OnStartup(StartupEventArgs e)
    {
        _mutex = new Mutex(true, @"Global\RedCompute_SingleInstance", out bool createdNew);
        if (!createdNew)
        {
            MessageBox.Show("RedCompute is already running.", "RedCompute", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        base.OnStartup(e);

        ConfigManager.Load();
        Log("[App] Configuration loaded");

        InitializeDatabase();
        InitializeCapabilities();
        await StartRelayServer();
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        _relayCts?.Cancel();
        if (_relayServer != null)
            await _relayServer.StopAsync();
        await Registry.StopAll();
        _mutex?.ReleaseMutex();
        _mutex?.Dispose();
        base.OnExit(e);
    }

    private void InitializeDatabase()
    {
        try
        {
            using var db = new RedComputeDbContext();
            db.Database.EnsureCreated();
            Log("[App] Database initialized");
        }
        catch (Exception ex)
        {
            Log($"[App] Database error: {ex.Message}");
        }
    }

    private void InitializeCapabilities()
    {
        var config = ConfigManager.Config;
        foreach (var (slug, capConfig) in config.Capabilities)
        {
            var definition = CapabilityDefinitionFactory.Create(slug);
            if (definition == null) continue;
            definition.Enabled = capConfig.Enabled;

            IBackendProvider? provider = null;
            if (capConfig.ActiveProvider != null && capConfig.Providers.TryGetValue(capConfig.ActiveProvider, out var providerConfig))
            {
                provider = ProviderFactory.Create(definition.Type, providerConfig, Log);
            }

            Registry.Register(slug, definition, capConfig, provider);
            Log($"[App] Registered capability: {slug} (provider: {provider?.Name ?? "none"})");
        }
    }

    private async Task StartRelayServer()
    {
        _relayCts = new CancellationTokenSource();
        _relayServer = new RelayServer(ConfigManager.Config, Registry, JobTracker, Log);

        try
        {
            await _relayServer.StartAsync(_relayCts.Token);
        }
        catch (Exception ex)
        {
            Log($"[App] Failed to start relay: {ex.Message}");
        }
    }

    public static void Log(string message)
    {
        var timestamped = $"[{DateTime.Now:HH:mm:ss.fff}] {message}";
        Console.WriteLine(timestamped);
        MainViewModel.AddLog(timestamped);
    }
}
