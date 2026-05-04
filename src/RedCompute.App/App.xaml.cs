using System.Windows;
using RedCompute.App.Data;
using RedCompute.App.Services;
using RedCompute.App.Services.Jobs;
using RedCompute.App.Api;
using RedCompute.App.TrayIcon;
using RedCompute.Core.Providers;

namespace RedCompute.App;

public partial class App : Application
{
    private static Mutex? _mutex;
    private CancellationTokenSource? _relayCts;
    private RelayServer? _relayServer;
    private TrayIconManager? _trayIcon;

    public static FileLoggerService FileLogger { get; } = new();
    public static LoggingService Logger { get; private set; } = null!;
    public static ConfigManager ConfigManager { get; } = new();
    public static CapabilityRegistry Registry { get; } = new();
    public static JobTrackingService JobTracker { get; } = new();
    public static CloudflareTunnelService TunnelService { get; } = new();

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

        InitializeDatabase();

        Logger = new LoggingService(FileLogger);

        ConfigManager.Load();
        Log("[App] Configuration loaded");

        var recovered = JobTracker.RecoverOrphanedJobs();
        if (recovered > 0)
            Log($"[App] Marked {recovered} orphaned job(s) as failed (interrupted by restart)");

        InitializeCapabilities();
        await StartRelayServer();
        _ = ProbeRunningBackends();
        _ = StartTunnelIfEnabled();

        _trayIcon = new TrayIconManager();
        _trayIcon.Initialize();
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        await TunnelService.DisposeAsync();
        _relayCts?.Cancel();
        if (_relayServer != null)
            await _relayServer.StopAsync();
        await Registry.StopAll();
        Logger.Dispose();
        FileLogger.Dispose();
        _trayIcon?.Dispose();
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
            db.MigrateSchema();
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
                provider = ProviderFactory.Create(definition.Type, providerConfig, s => Log(s));
            }

            Registry.Register(slug, definition, capConfig, provider);
            Log($"[App] Registered capability: {slug} (provider: {provider?.Name ?? "none"})");
        }
    }

    private async Task ProbeRunningBackends()
    {
        foreach (var (slug, entry) in Registry.Capabilities)
        {
            if (entry.ActiveProvider == null) continue;
            try
            {
                var started = await entry.ActiveProvider.StartAsync();
                if (started)
                    Log($"[App] Auto-detected running backend for: {slug}");
            }
            catch (Exception ex)
            {
                Log($"[App] Probe failed for {slug}: {ex.Message}");
            }
        }
    }

    private async Task StartRelayServer()
    {
        _relayCts = new CancellationTokenSource();
        _relayServer = new RelayServer(ConfigManager.Config, Registry, JobTracker, Logger, ConfigManager, TunnelService, (msg, jobId) => Log(msg, jobId));

        try
        {
            await _relayServer.StartAsync(_relayCts.Token);
        }
        catch (Exception ex)
        {
            Log($"[App] Failed to start relay: {ex.Message}");
        }
    }

    private async Task StartTunnelIfEnabled()
    {
        var tunnel = ConfigManager.Config.Tunnel;
        if (!tunnel.Enabled) return;

        try
        {
            await TunnelService.StartAsync(ConfigManager.Config.ApiPort, tunnel);
            Log($"[Tunnel] Started (hostname: {tunnel.Hostname ?? "unknown"})");
        }
        catch (Exception ex)
        {
            Log($"[Tunnel] Failed to start: {ex.Message}");
        }
    }


    public static void Log(string message, Guid? jobId = null)
    {
        if (Logger != null)
        {
            Logger.Log(message, jobId);
        }
        else
        {
            var timestamped = $"[{DateTime.Now:HH:mm:ss.fff}] {message}";
            Console.WriteLine(timestamped);
            FileLogger.Write(timestamped);
        }
    }
}
