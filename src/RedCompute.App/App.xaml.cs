using System.Threading;
using System.Windows;
using System.Windows.Threading;
using RedCompute.App.Data;
using RedCompute.App.Services;
using RedCompute.App.Services.Jobs;
using RedCompute.App.Api;
using RedCompute.App.ViewModels;
using RedCompute.Core.Providers;

namespace RedCompute.App;

public partial class App : Application
{
    private static Mutex? _mutex;
    private CancellationTokenSource? _relayCts;
    private RelayServer? _relayServer;
    private DispatcherTimer? _cleanupTimer;

    public static FileLoggerService FileLogger { get; } = new();
    public static LoggingService Logger { get; private set; } = null!;
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

        InitializeDatabase();

        Logger = new LoggingService(FileLogger);
        Logger.LogEntryCreated += MainViewModel.ConsoleLog.AddEntry;

        ConfigManager.Load();
        MainViewModel.LoadSettingsFromConfig();
        Log("[App] Configuration loaded");
        InitializeCapabilities();
        await StartRelayServer();
        _ = ProbeRunningBackends();
        StartJobCleanupTimer();
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        _cleanupTimer?.Stop();
        _relayCts?.Cancel();
        if (_relayServer != null)
            await _relayServer.StopAsync();
        await Registry.StopAll();
        SaveWindowState();
        Logger.Dispose();
        FileLogger.Dispose();
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
        MainViewModel.RefreshCapabilities();
    }

    private async Task StartRelayServer()
    {
        _relayCts = new CancellationTokenSource();
        _relayServer = new RelayServer(ConfigManager.Config, Registry, JobTracker, (msg, jobId) => Log(msg, jobId));

        try
        {
            await _relayServer.StartAsync(_relayCts.Token);
        }
        catch (Exception ex)
        {
            Log($"[App] Failed to start relay: {ex.Message}");
        }
    }

    private void StartJobCleanupTimer()
    {
        _cleanupTimer = new DispatcherTimer { Interval = TimeSpan.FromHours(1) };
        _cleanupTimer.Tick += (_, _) =>
        {
            var deletedJobs = JobTracker.CleanupOldJobs(ConfigManager.Config.JobRetentionDays);
            var deletedLogs = Logger.CleanupOldLogs(ConfigManager.Config.JobRetentionDays);
            if (deletedJobs > 0 || deletedLogs > 0)
                Log($"[App] Cleaned up {deletedJobs} jobs, {deletedLogs} log entries (>{ConfigManager.Config.JobRetentionDays} days)");
        };
        _cleanupTimer.Start();
    }

    private void SaveWindowState()
    {
        var mainWindow = MainWindow;
        if (mainWindow == null) return;

        var wc = ConfigManager.Config.Window;
        wc.IsMaximized = mainWindow.WindowState == WindowState.Maximized;
        if (mainWindow.WindowState == WindowState.Normal)
        {
            wc.Width = mainWindow.Width;
            wc.Height = mainWindow.Height;
            wc.Left = mainWindow.Left;
            wc.Top = mainWindow.Top;
        }
        else if (mainWindow.WindowState == WindowState.Maximized)
        {
            var bounds = mainWindow.RestoreBounds;
            wc.Width = bounds.Width;
            wc.Height = bounds.Height;
            wc.Left = bounds.Left;
            wc.Top = bounds.Top;
        }
        ConfigManager.Save();
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
