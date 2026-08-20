using System.IO;
using System.Text;
using System.Windows;
using RedBamboo.AppHost.Logging;
using RedCompute.App.Data;
using RedCompute.App.Services;
using RedCompute.App.Services.Hardware;
using RedCompute.App.Services.Jobs;
using RedCompute.App.Api;
using RedCompute.Core.Capabilities;
using RedCompute.Core.Configuration;
using RedCompute.Core.Providers;
using RedCompute.PluginSdk;

namespace RedCompute.App;

// RedCompute is a headless child service of the Leaf kernel: the kernel spawns it,
// health-checks /ping, proxies its API at /compute/*, and owns the one tray icon, the
// one tunnel, and the one autostart entry. This process just runs GPU/AI/media work.
public partial class App : Application
{
    private static Mutex? _mutex;
    private static readonly SemaphoreSlim ProviderRefreshGate = new(1, 1);
    private CancellationTokenSource? _relayCts;
    private RelayServer? _relayServer;

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
    public static HardwareMonitorService HardwareMonitor { get; } = new();
    public static ProviderConfigService ProviderConfig { get; private set; } = null!;
    public static QualityModeService QualityModes { get; private set; } = null!;

    protected override async void OnStartup(StartupEventArgs e)
    {
        _mutex = new Mutex(true, @"Global\RedCompute_SingleInstance", out bool createdNew);
        if (!createdNew)
        {
            // Headless: no message box. A distinctive exit code lets the kernel's process
            // monitor tell "second instance" apart from a crash.
            Console.Error.WriteLine("RedCompute is already running — exiting.");
            Shutdown(64);
            return;
        }

        base.OnStartup(e);

        Console.InputEncoding = Encoding.UTF8;
        Console.OutputEncoding = Encoding.UTF8;

        InitializeDatabase();

        FileLogger.AttachTo(LogService);
        Logger = new LoggingService(LogService);

        ConfigManager.Load();
        ApplyCliArgs(e.Args);
        Log("[App] Configuration loaded");

        ProviderConfig = new ProviderConfigService(ConfigManager.Config, (msg, _) => Log(msg));
        await ProviderConfig.ImportLegacyApiKeysAsync();
        await ProviderConfig.InitialSyncAsync();
        ProviderConfig.ApplyToConfig(ConfigManager.Config);
        var vaultedProviderKeys = ProviderConfig.GetVaultedConfigProviderCoordinates(ConfigManager.Config);
        foreach (var coordinate in vaultedProviderKeys)
            ConfigManager.MarkApiKeyVaulted(coordinate.CapabilitySlug, coordinate.ProviderName);
        if (vaultedProviderKeys.Count > 0)
            ConfigManager.Save();
        Log("[App] Provider entities synced");

        // Construct the shared resolver before provider plugins so provider-owned internal
        // inference can resolve entity-backed tiers. RelayServer loads its authoritative
        // RedLeaf/cache snapshot before accepting requests.
        QualityModes = new QualityModeService(
            ConfigManager.Config, (msg, jobId) => Log(msg, jobId), ProviderConfig);

        DefenderExclusionService.EnsureExclusions(s => Log(s));

        // Docker is started on demand by the session endpoints when a request actually asks
        // for a container. This flag only forces the daemon up front, to trade a slower boot
        // for a faster first containerised session.
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
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        HardwareMonitor.Dispose();
        _relayCts?.Cancel();
        if (_relayServer != null)
            await _relayServer.StopAsync();
        await Registry.StopAll();
        Logger.Dispose();
        LogPersistence.Dispose();
        await FileLogger.DisposeAsync();
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
            (IProviderQualityModeResolver)QualityModes,
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

        // Workflow execution is admitted and audited by RedCompute but performed by
        // RedLeaf's trusted visual-graph worker. It is a capability without a provider.
        if (Registry.Get("workflow") == null)
        {
            var workflowConfig = new CapabilityConfig();
            var workflow = ManifestLoader.Load("workflow", workflowConfig);
            workflow.ExecutionMode = CapabilityExecutionMode.External;
            workflow.WorkerDisplayName ??= "RedLeaf Workflow Engine";
            Registry.Register("workflow", workflow, workflowConfig, [], null);
            Log("[App] Registered external capability: workflow (worker: RedLeaf Workflow Engine)");
        }
    }

    public static async Task<int> RefreshProviderSecretsAsync(CancellationToken ct = default)
    {
        await ProviderRefreshGate.WaitAsync(ct);
        try
        {
            // RedLeaf starts Compute before extension seeds are materialized. Retry the
            // one-way import here so providers introduced during plugin startup (for
            // example Suno) cannot leave a legacy config key stranded indefinitely.
            await ProviderConfig.ImportLegacyApiKeysAsync(ct);

            var before = ConfigManager.Config.Capabilities
                .SelectMany(capability => capability.Value.Providers.Select(provider => new
                {
                    Coordinate = ConfigManager.ApiKeyCoordinate(capability.Key, provider.Key),
                    provider.Value.ApiKey,
                }))
                .ToDictionary(item => item.Coordinate, item => item.ApiKey,
                    StringComparer.OrdinalIgnoreCase);

            await ProviderConfig.RefreshAsync(ct);
            ProviderConfig.ApplyToConfig(ConfigManager.Config);
            foreach (var coordinate in ProviderConfig.GetVaultedConfigProviderCoordinates(ConfigManager.Config))
                ConfigManager.MarkApiKeyVaulted(coordinate.CapabilitySlug, coordinate.ProviderName);

            // Persist immediately after an authenticated vault refresh. The runtime keeps
            // hydrated keys in memory, while serialization removes every coordinate now
            // owned by RedLeaf. Without this save, a successful late startup refresh leaves
            // legacy plaintext stranded on disk until an unrelated settings write.
            ConfigManager.Save();

            var changed = new List<(string Capability, string Provider)>();
            foreach (var (capabilitySlug, capability) in ConfigManager.Config.Capabilities)
            foreach (var (providerName, providerConfig) in capability.Providers)
            {
                var coordinate = ConfigManager.ApiKeyCoordinate(capabilitySlug, providerName);
                before.TryGetValue(coordinate, out var previous);
                if (!string.Equals(previous, providerConfig.ApiKey, StringComparison.Ordinal))
                    changed.Add((capabilitySlug, providerName));
            }

            var extraServices = new object?[]
            {
                (IJobTracker)JobTracker,
                (IProviderQualityModeResolver)QualityModes,
                (Action<string, Guid?>)((message, jobId) => Log(message, jobId)),
            };
            foreach (var (capabilitySlug, providerName) in changed)
            {
                var entry = Registry.Get(capabilitySlug);
                if (entry == null
                    || !entry.Config.Providers.TryGetValue(providerName, out var providerConfig))
                    continue;

                var replacement = ProviderDiscovery.Create(
                    providerConfig.Type, providerConfig, capabilitySlug, message => Log(message), extraServices);
                if (replacement == null) continue;
                if (entry.Providers.TryGetValue(providerName, out var previousProvider))
                {
                    try
                    {
                        await previousProvider.StopAsync();
                        await previousProvider.DisposeAsync();
                    }
                    catch (Exception ex)
                    {
                        Log($"[ProviderConfig] Could not cleanly stop {capabilitySlug}/{providerName}: {ex.Message}");
                    }
                }
                entry.Providers[providerName] = replacement;
                if (entry.DefaultProviderName == providerName && !entry.IsManuallyDisabled)
                {
                    try { await replacement.StartAsync(); }
                    catch (Exception ex)
                    {
                        Log($"[ProviderConfig] Could not start refreshed {capabilitySlug}/{providerName}: {ex.Message}");
                    }
                }
            }

            return changed.Count;
        }
        finally
        {
            ProviderRefreshGate.Release();
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
        _relayServer = new RelayServer(ConfigManager.Config, Registry, JobTracker, Logger,
            ConfigManager, HardwareMonitor, ProviderConfig, QualityModes,
            (msg, jobId) => Log(msg, jobId));

        try
        {
            await _relayServer.StartAsync(_relayCts.Token);
        }
        catch (Exception ex)
        {
            Log($"[App] Failed to start relay: {ex.Message}");
        }
    }

    private static void ApplyCliArgs(string[] args)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "--port":
                    if (int.TryParse(args[i + 1], out var port))
                        ConfigManager.Config.ApiPort = port;
                    i++;
                    break;
                case "--redleaf-url":
                    ConfigManager.Config.RedLeafUrl = args[i + 1];
                    i++;
                    break;
            }
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
