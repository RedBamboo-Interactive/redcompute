using System.IO;
using System.Text;
using System.Windows;
using RedBamboo.AppHost.Logging;
using RedBamboo.AppHost.Tray;
using RedBamboo.AppHost.Tunnel;
using RedCompute.App.Data;
using RedCompute.App.Services;
using RedCompute.App.Services.Hardware;
using RedCompute.App.Services.Jobs;
using RedCompute.App.Api;
using RedCompute.Core.Providers;
using RedCompute.PluginSdk;

namespace RedCompute.App;

public partial class App : Application
{
    private static Mutex? _mutex;
    private CancellationTokenSource? _relayCts;
    private RelayServer? _relayServer;
    private RedBamboo.AppHost.Tray.TrayIconManager? _trayIcon;

    public static SqliteLogPersistence LogPersistence { get; } = new();
    public static LogService LogService { get; } = new(new LogServiceOptions
    {
        Source = "redcompute",
        BufferCapacity = 4096,
        Persistence = LogPersistence,
    });
    public static FileLogger FileLogger { get; } = new("RedCompute");
    public static LoggingService Logger { get; private set; } = null!;
    public static ConfigManager ConfigManager { get; } = new();
    public static CapabilityRegistry Registry { get; } = new();
    public static ProviderDiscovery ProviderDiscovery { get; private set; } = null!;
    public static CapabilityManifestLoader ManifestLoader { get; } = new();
    public static JobTrackingService JobTracker { get; } = new();
    public static CloudflareTunnelService TunnelService { get; } = new(logger: null);
    public static HardwareMonitorService HardwareMonitor { get; } = new();

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

        Console.InputEncoding = Encoding.UTF8;
        Console.OutputEncoding = Encoding.UTF8;

        InitializeDatabase();

        FileLogger.AttachTo(LogService);
        Logger = new LoggingService(LogService);

        ConfigManager.Load();
        Log("[App] Configuration loaded");

        if (ConfigManager.Config.AutoStartDocker)
            _ = DockerDesktopService.EnsureRunningAsync(s => Log(s));

        var recovered = JobTracker.RecoverOrphanedJobs();
        if (recovered > 0)
            Log($"[App] Marked {recovered} orphaned job(s) as failed (interrupted by restart)");

        ProviderDiscovery = new ProviderDiscovery(s => Log(s));
        ProviderDiscovery.ScanAssemblies();
        InitializeCapabilities();
        HardwareMonitor.Start(Registry);
        await StartRelayServer();
        _ = ProbeRunningBackends();
        _ = StartTunnelIfEnabled();

        _trayIcon = new RedBamboo.AppHost.Tray.TrayIconManager(new TrayIconConfig
        {
            AppName = "RedCompute",
            Port = ConfigManager.Config.ApiPort,
            EnableAutoStartToggle = true,
            RebuildScript = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "rebuild.ps1")),
            LoadIcon = () => IconHelper.CreateTrayIcon(StatusColors.Teal, TrayIcons.Microchip),
            GetStatusLines = async () =>
            {
                var lines = new List<string>();
                foreach (var (slug, entry) in Registry.Capabilities)
                {
                    var status = entry.ActiveProvider != null
                        ? await entry.ActiveProvider.GetStatusAsync()
                        : BackendStatus.Stopped;
                    lines.Add($"{entry.Definition.DisplayName}: {status}");
                }
                return lines;
            },
        });
        _trayIcon.Initialize();
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        HardwareMonitor.Dispose();
        await TunnelService.DisposeAsync();
        _relayCts?.Cancel();
        if (_relayServer != null)
            await _relayServer.StopAsync();
        await Registry.StopAll();
        Logger.Dispose();
        LogPersistence.Dispose();
        await FileLogger.DisposeAsync();
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

        var extraServices = new object?[]
        {
            (IJobTracker)JobTracker,
            (Action<string, Guid?>)((msg, jobId) => Log(msg, jobId)),
        };

        foreach (var (slug, capConfig) in config.Capabilities)
        {
            var definition = ManifestLoader.Load(slug, capConfig);

            var providers = new Dictionary<string, IBackendProvider>();
            foreach (var (providerName, providerConfig) in capConfig.Providers)
            {
                var provider = ProviderDiscovery.Create(providerConfig.Type, providerConfig, slug, s => Log(s), extraServices);
                if (provider != null)
                    providers[providerName] = provider;
                else
                    Log($"[App] Warning: provider type '{providerConfig.Type}' not found for {slug}/{providerName}");
            }

            Registry.Register(slug, definition, capConfig, providers, capConfig.ActiveProvider);
            var names = providers.Count > 0 ? string.Join(", ", providers.Keys) : "none";
            Log($"[App] Registered capability: {slug} (providers: {names}, default: {capConfig.ActiveProvider ?? "none"})");
        }
    }

    private async Task ProbeRunningBackends()
    {
        var tasks = new List<Task>();
        foreach (var (slug, entry) in Registry.Capabilities)
        {
            if (entry.ActiveProvider == null) continue;
            tasks.Add(StartCapability(slug, entry));
        }
        await Task.WhenAll(tasks);

        _ = RetryFailedBackends();
    }

    private async Task StartCapability(string slug, CapabilityEntry entry)
    {
        try
        {
            var started = await entry.ActiveProvider!.StartAsync();
            if (started)
                Log($"[App] Backend started for: {slug}");
        }
        catch (Exception ex)
        {
            Log($"[App] Start failed for {slug}: {ex.Message}");
        }
    }

    private async Task RetryFailedBackends()
    {
        var retryInterval = TimeSpan.FromSeconds(60);
        while (true)
        {
            await Task.Delay(retryInterval);

            foreach (var (slug, entry) in Registry.Capabilities)
            {
                if (entry.ActiveProvider == null) continue;
                if (entry.IsManuallyDisabled) continue;

                var status = await entry.ActiveProvider.GetStatusAsync();
                if (status is not (BackendStatus.Error or BackendStatus.Stopped)) continue;

                Log($"[App] Retrying failed backend: {slug}");
                await StartCapability(slug, entry);
            }
        }
    }

    private async Task StartRelayServer()
    {
        _relayCts = new CancellationTokenSource();
        _relayServer = new RelayServer(ConfigManager.Config, Registry, JobTracker, Logger, ConfigManager, TunnelService, HardwareMonitor, (msg, jobId) => Log(msg, jobId));

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
            await TunnelService.StartAsync(new TunnelConfig
            {
                Enabled = true,
                TunnelToken = tunnel.TunnelToken,
                Hostname = tunnel.Hostname,
                CloudflaredPath = tunnel.CloudflaredPath,
                AccessToken = tunnel.AccessToken,
            });
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
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {message}");
        }
    }
}
