namespace RedCompute.Core.Storage;

/// <summary>
/// The one place RedCompute decides where its persistent data lives: the host, Core, the plugin SDK
/// and the plugins all resolve their locations here (plugins through <see cref="Current"/>, or through
/// their execution host's <c>Platform.ComputeDataRoot</c> for host-scoped files).
/// <list type="bullet">
/// <item>The data root is <c>REDCOMPUTE_DATA_ROOT</c> when set (the Leaf kernel sets it), read once
/// per process. It must be a fully qualified path; anything else is a configuration error, never a
/// silent fallback.</item>
/// <item>Without the variable it is today's location, byte for byte:
/// <c>%LOCALAPPDATA%\RedCompute</c> on Windows, <c>$XDG_DATA_HOME/RedCompute</c> or
/// <c>~/.local/share/RedCompute</c> on Linux (what <see cref="Environment.SpecialFolder.LocalApplicationData"/>
/// answers there).</item>
/// <item><see cref="SuiteRoot"/> (<c>RedSuite</c>: keys, badge signing, Google OAuth) is shared with
/// RedLeaf and never follows the data root.</item>
/// </list>
/// Stored paths are still written absolute (a rolled-back build must read them); readers also accept a relative
/// value (see <see cref="StoredPaths"/>), and after a move a
/// <see cref="RebaseMap"/> (<c>REDCOMPUTE_REBASE_MAP</c>) rewrites the ones under a moved folder.
/// </summary>
public sealed class ComputeStorage
{
    /// <summary>The environment variable that relocates the data root.</summary>
    public const string DataRootVariable = "REDCOMPUTE_DATA_ROOT";

    /// <summary>The process exit code of a RedCompute that refused to start on an invalid data root (EX_CONFIG).</summary>
    public const int ConfigurationErrorExitCode = 78;

    private const string AppFolderName = "RedCompute";
    private const string SuiteFolderName = "RedSuite";

    private static readonly Lazy<ComputeStorage> LazyCurrent = new(
        () => FromEnvironment(Environment.GetEnvironmentVariable, Environment.GetFolderPath),
        LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// This process's storage, resolved from the environment on first use. Throws
    /// <see cref="ComputeStorageConfigurationException"/> (every time) when the variable is invalid.
    /// </summary>
    public static ComputeStorage Current => LazyCurrent.Value;

    /// <summary>
    /// Resolves <see cref="Current"/> now, so an invalid <see cref="DataRootVariable"/> stops startup
    /// with its own message before any static state captures a path.
    /// </summary>
    public static ComputeStorage EnsureConfigured() => Current;

    public ComputeStorage(string dataRoot, string suiteRoot, bool isRelocated)
    {
        DataRoot = dataRoot;
        SuiteRoot = suiteRoot;
        IsRelocated = isRelocated;
    }

    /// <summary>
    /// The storage a process with this environment uses. Separate from <see cref="Current"/> so tests
    /// can resolve without touching the process environment.
    /// </summary>
    public static ComputeStorage FromEnvironment(
        Func<string, string?> getVariable, Func<Environment.SpecialFolder, string> getFolderPath)
    {
        var localAppData = getFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var suiteRoot = Path.Combine(localAppData, SuiteFolderName);

        var configured = getVariable(DataRootVariable);
        if (configured is null)
            return new ComputeStorage(Path.Combine(localAppData, AppFolderName), suiteRoot, isRelocated: false);

        if (string.IsNullOrWhiteSpace(configured) || !Path.IsPathFullyQualified(configured))
            throw new ComputeStorageConfigurationException(
                $"{DataRootVariable} must be an absolute path, but it is '{configured}'. " +
                $"Set it to a fully qualified folder, or unset it to use the default location " +
                $"({Path.Combine(localAppData, AppFolderName)}).");

        return new ComputeStorage(Path.GetFullPath(configured), suiteRoot, isRelocated: true);
    }

    /// <summary>The root of everything RedCompute persists.</summary>
    public string DataRoot { get; }

    /// <summary>The suite key folder shared with RedLeaf (<c>%LOCALAPPDATA%\RedSuite</c>). Does not relocate.</summary>
    public string SuiteRoot { get; }

    /// <summary>True when <see cref="DataRootVariable"/> set the data root.</summary>
    public bool IsRelocated { get; }

    /// <summary><c>redcompute.db</c>: jobs, logs, transcript sequences, session titles, rollback ledger.</summary>
    public string Database => Path.Combine(DataRoot, "redcompute.db");

    /// <summary><c>outputs\</c>: job output files.</summary>
    public string Outputs => Path.Combine(DataRoot, "outputs");

    /// <summary><c>input-attachments\</c>: staged session input images.</summary>
    public string InputAttachments => Path.Combine(DataRoot, "input-attachments");

    /// <summary><c>input-attachments\attachments.db</c>: attachment and session input queue rows.</summary>
    public string AttachmentsDatabase => Path.Combine(InputAttachments, "attachments.db");

    /// <summary><c>conversation-sessions\</c>: empty workspaces for conversation-only sessions.</summary>
    public string ConversationSessions => Path.Combine(DataRoot, "conversation-sessions");

    /// <summary><c>config.json</c>.</summary>
    public string ConfigFile => Path.Combine(DataRoot, "config.json");

    /// <summary><c>entity-cache.json</c>: the last provider entity snapshot from RedLeaf.</summary>
    public string EntityCache => Path.Combine(DataRoot, "entity-cache.json");

    /// <summary><c>quality-mode-cache.json</c>: the last quality tier snapshot from RedLeaf.</summary>
    public string QualityModeCache => Path.Combine(DataRoot, "quality-mode-cache.json");

    /// <summary>The folder holding <c>node.json</c> (this node's identity).</summary>
    public string NodeIdentityDirectory => DataRoot;

    /// <summary>The folder holding <c>long-lived-processes.json</c> and <c>long-lived-processes\</c> (logs).</summary>
    public string LongLivedProcessesDirectory => DataRoot;

    /// <summary><c>session-processes\</c>: escapee ledgers that survive a restart.</summary>
    public string SessionProcesses => Path.Combine(DataRoot, "session-processes");

    /// <summary><c>planned-restart.json</c>: the drain-to-replacement handoff.</summary>
    public string PlannedRestartCheckpoint => Path.Combine(DataRoot, "planned-restart.json");

    /// <summary><c>capabilities\</c>: user overrides of capability display manifests.</summary>
    public string CapabilityOverrides => Path.Combine(DataRoot, "capabilities");

    /// <summary><c>.defender-exclusions</c>: marker that the one-time Defender exclusions ran.</summary>
    public string DefenderExclusionsMarker => Path.Combine(DataRoot, ".defender-exclusions");

    /// <summary><c>backups\</c>: files RedCompute hands to a backup (the job database snapshot). Never referenced by stored rows.</summary>
    public string Backups => Path.Combine(DataRoot, "backups");

    /// <summary><c>telemetry.db</c>: request telemetry (RedBamboo.AppHost).</summary>
    public string TelemetryDatabase => Path.Combine(DataRoot, "telemetry.db");

    /// <summary><c>Logs\</c>: the daily log files of the host (RedBamboo.AppHost's FileLogger).</summary>
    public string Logs => Path.Combine(DataRoot, "Logs");

    /// <summary><c>cloudflared-process.json</c>: the record of the cloudflared process a tunnel service started.</summary>
    public string TunnelState => Path.Combine(DataRoot, "cloudflared-process.json");

    /// <summary>
    /// Every location RedCompute owns, with the class a data move gives it. This is the one declaration
    /// (<c>GET /storage/manifest</c> serves it, the kernel's relocation reads it): a location added to this class
    /// is declared here or the coverage test fails. Paths are relative to <see cref="DataRoot"/>, '/'-separated.
    /// Anything under the data root that is not declared here is treated by the kernel as durable and mutable.
    /// </summary>
    public IReadOnlyList<ComputeStorageLocation> Locations() =>
    [
        Declare("database", Database, StorageClass.Durable, StorageMutability.Mutable),
        Declare("outputs", Outputs, StorageClass.Durable, StorageMutability.Immutable),
        Declare("input-attachments", InputAttachments, StorageClass.Durable, StorageMutability.Immutable),
        Declare("attachments-database", AttachmentsDatabase, StorageClass.Durable, StorageMutability.Mutable),
        Declare("conversation-sessions", ConversationSessions, StorageClass.Durable, StorageMutability.Mutable),
        Declare("config", ConfigFile, StorageClass.Durable, StorageMutability.Mutable),
        Declare("entity-cache", EntityCache, StorageClass.Rebuildable, StorageMutability.Mutable),
        Declare("quality-mode-cache", QualityModeCache, StorageClass.Rebuildable, StorageMutability.Mutable),
        Declare("node-identity", Path.Combine(NodeIdentityDirectory, NodeIdentityFileName), StorageClass.Durable, StorageMutability.Mutable),
        Declare("long-lived-processes-state", Path.Combine(LongLivedProcessesDirectory, LongLivedProcessesFileName), StorageClass.Durable, StorageMutability.Mutable),
        Declare("long-lived-processes-logs", Path.Combine(LongLivedProcessesDirectory, "long-lived-processes"), StorageClass.Durable, StorageMutability.Mutable),
        // Process ledgers and the drain checkpoint describe processes that are gone once RedCompute is stopped for a move.
        Declare("session-processes", SessionProcesses, StorageClass.Disposable, StorageMutability.Mutable),
        Declare("planned-restart", PlannedRestartCheckpoint, StorageClass.Disposable, StorageMutability.Mutable),
        Declare("capability-overrides", CapabilityOverrides, StorageClass.Durable, StorageMutability.Mutable),
        // The one-time Defender exclusions name this folder: the new location must run them again.
        Declare("defender-exclusions-marker", DefenderExclusionsMarker, StorageClass.Disposable, StorageMutability.Mutable),
        Declare("backups", Backups, StorageClass.Rebuildable, StorageMutability.Immutable),
        Declare("telemetry", TelemetryDatabase, StorageClass.Rebuildable, StorageMutability.Mutable),
        Declare("logs", Logs, StorageClass.Durable, StorageMutability.Mutable),
        Declare("tunnel-state", TunnelState, StorageClass.Disposable, StorageMutability.Mutable),
        Declare("plugins", Path.Combine(DataRoot, "plugins"), StorageClass.Durable, StorageMutability.Mutable),
        // Placement shadow evidence (RedCompute.App's PlacementShadowStore.FileName; this project cannot reference it).
        Declare("placement-shadow", Path.Combine(DataRoot, "placement-shadow.json"), StorageClass.Durable, StorageMutability.Mutable),
    ];

    /// <summary>The file name of the node identity record (<c>node.json</c>).</summary>
    public const string NodeIdentityFileName = "node.json";

    /// <summary>The file name of the long-lived process registry.</summary>
    public const string LongLivedProcessesFileName = "long-lived-processes.json";

    private ComputeStorageLocation Declare(string name, string absolute, string storageClass, string mutability)
        => new(name, Path.GetRelativePath(DataRoot, absolute).Replace(Path.DirectorySeparatorChar, '/'), storageClass, mutability);

    /// <summary><c>plugins\&lt;pluginId&gt;\</c>: a plugin's own data (for example <c>claude-code</c>, <c>codex</c>, <c>opencode</c>).</summary>
    public string PluginDirectory(string pluginId)
    {
        if (string.IsNullOrWhiteSpace(pluginId) || pluginId.IndexOfAny(['/', '\\']) >= 0 || pluginId is "." or "..")
            throw new ArgumentException($"Invalid plugin id '{pluginId}'.", nameof(pluginId));
        return Path.Combine(DataRoot, "plugins", pluginId);
    }
}

/// <summary>The class a data move gives a location (<c>GET /storage/manifest</c>).</summary>
public static class StorageClass
{
    /// <summary>Copied and verified.</summary>
    public const string Durable = "durable";
    /// <summary>Copied only when the person asks.</summary>
    public const string Rebuildable = "rebuildable";
    /// <summary>Never copied (temp, lock and process-state files).</summary>
    public const string Disposable = "disposable";
}

/// <summary>Whether a location's files are written once (safe to copy while running) or change in place.</summary>
public static class StorageMutability
{
    public const string Immutable = "immutable";
    public const string Mutable = "mutable";
}

/// <summary>One declared location: a path relative to the data root, '/'-separated.</summary>
public sealed record ComputeStorageLocation(string Name, string Path, string Class, string Mutability);

/// <summary><see cref="ComputeStorage.DataRootVariable"/> holds a value RedCompute must not start on.</summary>
public sealed class ComputeStorageConfigurationException(string message) : InvalidOperationException(message);
