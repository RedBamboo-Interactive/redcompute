namespace RedCompute.Node;

/// <summary>
/// Where the agent keeps everything (design 2.3). Never RedCompute's folders: the execution host's data
/// root (<see cref="Compute"/>) is the agent's own, set through <c>REDCOMPUTE_DATA_ROOT</c> before
/// anything reads <c>ComputeStorage</c>, so a node and a RedCompute on one machine never share files.
/// <list type="bullet">
/// <item>Windows: data <c>%LOCALAPPDATA%\LeafNode</c> (<c>node.json</c>, <c>logs</c>, <c>spool</c>,
/// <c>audit</c>, <c>compute</c>); programs <c>%LOCALAPPDATA%\Programs\LeafNode\&lt;version&gt;</c>.</item>
/// <item>Linux: data <c>$XDG_DATA_HOME/leaf-node/data</c>; logs, spool and audit under
/// <c>$XDG_STATE_HOME/leaf-node</c>; programs <c>$XDG_DATA_HOME/leaf-node/&lt;version&gt;</c> with the
/// shim <c>~/.local/bin/leaf-node</c>; the lock and the control socket in <c>$XDG_RUNTIME_DIR</c>.</item>
/// <item><c>LEAF_NODE_HOME</c> puts all of it under one folder (tests, a second agent for development).</item>
/// </list>
/// </summary>
public sealed record NodePaths
{
    public const string HomeVariable = "LEAF_NODE_HOME";

    public required string Data { get; init; }
    public required string Logs { get; init; }
    public required string Spool { get; init; }
    public required string Audit { get; init; }

    /// <summary>The execution host's data root on the node (its containment ledgers).</summary>
    public required string Compute { get; init; }

    /// <summary>Program files: one folder per version beside <c>current.json</c>.</summary>
    public required string Programs { get; init; }

    /// <summary>Linux only: the folder of the <c>leaf-node</c> shim on PATH. Null on Windows.</summary>
    public string? Bin { get; init; }

    /// <summary>Linux: the user's systemd unit folder. Null on Windows.</summary>
    public string? SystemdUnits { get; init; }

    /// <summary>The single-instance lock and the control socket (Linux); a per-folder suffix for the pipe and mutex (Windows).</summary>
    public required string Runtime { get; init; }

    /// <summary>Set when <see cref="HomeVariable"/> placed everything; the pipe and mutex names then carry its hash.</summary>
    public string? IsolatedHome { get; init; }

    public string StateFile => Path.Combine(Data, "node.json");
    public string KeyFile => Path.Combine(Data, "key.p8");
    public string LogFile => Path.Combine(Logs, "leaf-node.log");
    public string ControlSocket => Path.Combine(Runtime, "leaf-node.sock");
    public string LockFile => Path.Combine(Runtime, "leaf-node.lock");

    /// <summary>The folders install creates and uninstall removes, deepest data first.</summary>
    public IReadOnlyList<string> OwnedFolders()
    {
        var folders = new List<string> { Logs, Spool, Audit, Compute, Data, Programs };
        // Linux keeps logs, spool and audit under one state folder: remove it too once empty.
        var state = Path.GetDirectoryName(Logs);
        if (state is not null && Path.GetDirectoryName(Spool) == state && Path.GetDirectoryName(Audit) == state
            && !string.Equals(state, Data, StringComparison.Ordinal))
            folders.Add(state);
        if (IsolatedHome is not null) folders.Add(IsolatedHome);
        return folders.Distinct(StringComparer.Ordinal).ToList();
    }

    /// <summary>The paths for this user, from the environment and the OS folders the host reports.</summary>
    public static NodePaths Resolve(Func<string, string?> getVariable, Func<Environment.SpecialFolder, string> getFolderPath,
        bool windows)
    {
        var home = getVariable(HomeVariable);
        if (!string.IsNullOrWhiteSpace(home))
        {
            if (!Path.IsPathFullyQualified(home))
                throw new NodeCommandException("bad_home", $"{HomeVariable} must be an absolute path, but it is '{home}'.");
            home = Path.GetFullPath(home);
            var data = Path.Combine(home, "data");
            return new NodePaths
            {
                Data = data,
                Logs = Path.Combine(home, "state", "logs"),
                Spool = Path.Combine(home, "state", "spool"),
                Audit = Path.Combine(home, "state", "audit"),
                Compute = Path.Combine(data, "compute"),
                Programs = Path.Combine(home, "programs"),
                Bin = windows ? null : Path.Combine(home, "bin"),
                SystemdUnits = windows ? null : Path.Combine(home, "systemd"),
                Runtime = Path.Combine(home, "run"),
                IsolatedHome = home,
            };
        }

        if (windows)
        {
            var local = getFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var data = Path.Combine(local, "LeafNode");
            return new NodePaths
            {
                Data = data,
                Logs = Path.Combine(data, "logs"),
                Spool = Path.Combine(data, "spool"),
                Audit = Path.Combine(data, "audit"),
                Compute = Path.Combine(data, "compute"),
                Programs = Path.Combine(local, "Programs", "LeafNode"),
                Runtime = data,
            };
        }

        var userHome = getFolderPath(Environment.SpecialFolder.UserProfile);
        string Xdg(string variable, params string[] fallback)
        {
            var value = getVariable(variable);
            return !string.IsNullOrWhiteSpace(value) && Path.IsPathFullyQualified(value)
                ? value
                : Path.Combine([userHome, .. fallback]);
        }

        var dataHome = Xdg("XDG_DATA_HOME", ".local", "share");
        var stateHome = Path.Combine(Xdg("XDG_STATE_HOME", ".local", "state"), "leaf-node");
        var configHome = Xdg("XDG_CONFIG_HOME", ".config");
        var runtime = getVariable("XDG_RUNTIME_DIR");
        var linuxData = Path.Combine(dataHome, "leaf-node", "data");
        return new NodePaths
        {
            Data = linuxData,
            Logs = Path.Combine(stateHome, "logs"),
            Spool = Path.Combine(stateHome, "spool"),
            Audit = Path.Combine(stateHome, "audit"),
            Compute = Path.Combine(linuxData, "compute"),
            Programs = Path.Combine(dataHome, "leaf-node"),
            Bin = Path.Combine(userHome, ".local", "bin"),
            SystemdUnits = Path.Combine(configHome, "systemd", "user"),
            // Without a runtime folder (no login session), the state folder holds the lock and socket.
            Runtime = !string.IsNullOrWhiteSpace(runtime) && Path.IsPathFullyQualified(runtime) ? runtime : stateHome,
        };
    }
}
