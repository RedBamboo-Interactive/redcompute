using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using RedCompute.Core.Providers;
using RedCompute.PluginSdk.Hosting;

namespace RedCompute.Node.Runtimes;

/// <summary>
/// Who asked for a runtime's start or stop, as its audit entry and log line say: the node's owner (<c>leaf-node runtime
/// start|stop</c>, over the control IPC) or the server (<c>runtime.start|stop</c> over the link).
/// </summary>
public enum RuntimeRequester
{
    Owner,
    Server,
}

/// <summary>
/// The runtimes on this node (Phase 4 design 2.4): the owner's registrations, and what the agent started from them.
/// It is the node's <see cref="IHostRuntimes"/>, which the server reaches over <c>runtime.v1</c>.
/// <list type="bullet">
/// <item>A spawn-mode runtime starts from the shared recipe (<see cref="RuntimeRecipes.ComfyUi"/>) and the owner's
/// local settings, as the node's user. A start takes an id and nothing else.</item>
/// <item>It runs <c>Kept</c>: a containment ledger per runtime (Windows) and a state file with its root's identity
/// (pid and creation time), so a restarted agent adopts it again instead of starting a second one.</item>
/// <item>An attach-mode runtime is the owner's own (ComfyUI Desktop, a service): only probed and forwarded to;
/// start and stop answer <c>runtime_not_managed</c>.</item>
/// <item>Kill-all, a revoke, an unpair and an uninstall stop every managed runtime; attach-mode ones stay up.</item>
/// </list>
/// </summary>
public sealed class RuntimeManager : IHostRuntimes, IAsyncDisposable
{
    public static readonly TimeSpan MaxGrace = TimeSpan.FromSeconds(60);

    private readonly NodeEnvironment _env;
    private readonly RuntimeRegistry _registry;
    private readonly Func<RuntimeRegistration, HostSpawnSpec> _spec;
    private readonly Func<RuntimeRegistration, CancellationToken, Task<bool>> _probe;
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _life = new();
    private Task? _healthLoop;

    private sealed class Entry(RuntimeRegistration registration)
    {
        public RuntimeRegistration Registration { get; set; } = registration;
        public BackendStatus Status { get; set; } = BackendStatus.Stopped;
        public string? Detail { get; set; }
        public DateTimeOffset? StartedAt { get; set; }
        public DateTimeOffset? StartRequestedAt { get; set; }
        public IHostProcess? Process { get; set; }
        public IContainedTree? Tree { get; set; }
        public int Pid { get; set; }
        public long Created { get; set; }
        public bool Stopping { get; set; }
        /// <summary>A stop killed it but it outlived the grace: its exit, whenever it comes, is the stop's, not a crash.</summary>
        public bool StopOverdue { get; set; }
        public bool Adopted { get; set; }
        /// <summary>The runtime's output file, rolled as the agent's own log is (5 MB, five files).</summary>
        public NodeLog? Log { get; set; }
        public bool Managed => Registration.Managed == HostRuntimeManagement.Spawn;
        public bool Alive => Pid != 0;
    }

    private sealed record ProcessState(int Pid, long CreatedUtcTicks, DateTimeOffset StartedAt);

    public RuntimeManager(NodeEnvironment env, Func<RuntimeRegistration, HostSpawnSpec>? spec = null,
        Func<RuntimeRegistration, CancellationToken, Task<bool>>? probe = null)
    {
        _env = env;
        _registry = new RuntimeRegistry(env.Host, env.Paths);
        _spec = spec ?? DefaultSpec;
        _probe = probe ?? DefaultProbeAsync;
    }

    public event Action<HostRuntime>? Changed;

    /// <summary>How long a started runtime may take to answer its health probe before it is in error.</summary>
    public TimeSpan StartTimeout { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>The spawn spec of a registration: the shared recipe with the owner's settings.</summary>
    public HostSpawnSpec DefaultSpec(RuntimeRegistration runtime) => runtime.Kind switch
    {
        RuntimeKinds.ComfyUi => RuntimeRecipes.ComfyUi(new ComfyUiRecipe
        {
            Host = "127.0.0.1",
            Port = runtime.Port,
            WslDistro = string.IsNullOrWhiteSpace(runtime.WslDistro) ? null : runtime.WslDistro,
            ServerPath = runtime.ServerPath,
            VenvPath = runtime.VenvPath,
            ServerArgs = runtime.ServerArgs ?? "",
            ModelsDirectory = runtime.ModelsDirectory ?? "",
        }, _env.Host),
        _ => throw new HostRuntimeException(HostRuntimeException.Unknown, $"No recipe for kind '{runtime.Kind}'."),
    };

    private static readonly HttpClient ProbeClient = new(new SocketsHttpHandler
    {
        UseProxy = false,
        UseCookies = false,
        AllowAutoRedirect = false,
        ConnectTimeout = TimeSpan.FromSeconds(2),
    }) { Timeout = TimeSpan.FromSeconds(3) };

    private static async Task<bool> DefaultProbeAsync(RuntimeRegistration runtime, CancellationToken ct)
    {
        try
        {
            using var response = await ProbeClient.GetAsync($"http://127.0.0.1:{runtime.Port}/system_stats", ct);
            return response.IsSuccessStatusCode;
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or IOException)
        {
            return false;
        }
    }

    // ================================================================= loading and adoption

    /// <summary>Reads the registrations and adopts what an earlier run of the agent left running.</summary>
    public void Load()
    {
        var registrations = _registry.Load();
        List<HostRuntime> changed = [];
        lock (_gate)
        {
            foreach (var registration in registrations)
            {
                if (_entries.TryGetValue(registration.Id, out var existing))
                {
                    existing.Registration = registration;
                    continue;
                }
                var entry = new Entry(registration);
                _entries[registration.Id] = entry;
                if (entry.Managed) Adopt(entry);
                changed.Add(View(entry));
            }
        }
        foreach (var view in changed) Raise(view);
    }

    /// <summary>The owner changed the registrations (<c>leaf-node runtime add|remove|enable|disable</c>).</summary>
    public async Task ReloadAsync()
    {
        var registrations = _registry.Load().ToDictionary(r => r.Id, StringComparer.Ordinal);
        List<string> gone;
        List<string> disabled;
        lock (_gate)
        {
            gone = _entries.Keys.Where(id => !registrations.ContainsKey(id)).ToList();
            disabled = _entries.Values.Where(e => registrations.TryGetValue(e.Registration.Id, out var r) && !r.Enabled && e.Managed && e.Alive)
                .Select(e => e.Registration.Id).ToList();
        }
        foreach (var id in gone.Concat(disabled)) await StopCoreAsync(id, "the owner removed or disabled it");
        lock (_gate) foreach (var id in gone) _entries.Remove(id);
        Load();
        foreach (var id in disabled) RaiseOf(id);
    }

    private void Adopt(Entry entry)
    {
        var id = entry.Registration.Id;
        var statePath = _env.Host.Path(StatePath(id));
        if (!_env.Host.Files.FileExists(statePath)) return;
        ProcessState? state = null;
        try
        {
            state = JsonSerializer.Deserialize<ProcessState>(_env.Host.Files.ReadAllText(statePath), NodeStateStore.Json);
        }
        catch (JsonException)
        {
        }
        IContainedTree? tree = null;
        var alive = false;
        if (state is not null)
        {
            tree = _env.Host.Files.FileExists(_env.Host.Path(LedgerPath(id)))
                ? _env.Host.Containment.Resume(Owner(id), LedgerPath(id))
                : _env.Host.Containment.Resume(Owner(id), LedgerPath(id), [(state.Pid, state.CreatedUtcTicks)]);
            alive = tree is not null || IdentityAlive(state.Pid, state.CreatedUtcTicks);
        }
        if (!alive || state is null)
        {
            DeleteState(id);
            return;
        }
        entry.Tree = tree;
        entry.Pid = state.Pid;
        entry.Created = state.CreatedUtcTicks;
        entry.StartedAt = state.StartedAt;
        entry.StartRequestedAt = _env.Time.GetUtcNow();
        entry.Adopted = true;
        entry.Status = BackendStatus.Starting;
        entry.Detail = "adopted after an agent restart";
        Remember(entry, $"[leaf-node] adopted PID {state.Pid} after an agent restart (earlier output is in the log file)");
        _env.Audit.Record("runtime", id, "adopted", $"pid {state.Pid}");
    }

    private bool IdentityAlive(int pid, long created)
    {
        try
        {
            return _env.Host.Processes.Find(pid) is { } info && info.CreatedUtcTicks == created;
        }
        catch
        {
            return false;
        }
    }

    // ================================================================= IHostRuntimes

    public IReadOnlyList<HostRuntime> List()
    {
        lock (_gate) return _entries.Values.OrderBy(e => e.Registration.Id, StringComparer.Ordinal).Select(View).ToList();
    }

    public HostRuntime? Status(string id)
    {
        lock (_gate) return _entries.TryGetValue(id, out var entry) ? View(entry) : null;
    }

    public RuntimeRegistration? Find(string id)
    {
        lock (_gate) return _entries.TryGetValue(id, out var entry) ? entry.Registration : null;
    }

    /// <summary>The reason a start or stop by <paramref name="by"/> is audited and logged with.</summary>
    public static string Reason(RuntimeRequester by) => by == RuntimeRequester.Owner ? "asked by the owner" : "asked by the server";

    // Through the shared interface only the server's runtime.v1 reaches this manager; the owner's commands name themselves.
    Task<HostRuntime> IHostRuntimes.StartAsync(string id, CancellationToken ct) => StartAsync(id, RuntimeRequester.Server, ct);

    Task<HostRuntime> IHostRuntimes.StopAsync(string id, TimeSpan? grace, CancellationToken ct) => StopAsync(id, RuntimeRequester.Server, grace, ct);

    /// <summary>Starts a managed runtime for <paramref name="by"/>, who its audit entry names.</summary>
    public Task<HostRuntime> StartAsync(string id, RuntimeRequester by, CancellationToken ct = default) => StartCore(id, Reason(by));

    private Task<HostRuntime> StartCore(string id, string why)
    {
        HostRuntime view;
        lock (_gate)
        {
            if (!_entries.TryGetValue(id, out var entry))
                throw new HostRuntimeException(HostRuntimeException.Unknown, $"No runtime '{id}' is registered on this node.");
            if (!entry.Managed)
                throw new HostRuntimeException(HostRuntimeException.NotManaged, $"'{id}' is run by the node's owner (attach mode): it is only reached, never started.");
            if (!entry.Registration.Enabled)
                throw new HostRuntimeException(HostRuntimeException.Disabled, $"The node's owner disabled '{id}'.");
            // A start during a stop is refused (review P4-P7, item 3): spawning now would race the stop's reap, and the
            // server retries once the runtime reports stopped. The flag is cleared as soon as the stopped process is gone.
            if (entry.Stopping)
                throw new HostRuntimeException(HostRuntimeException.Stopping, $"'{id}' is stopping; start it again once it has stopped.");
            if (entry.Alive) return Task.FromResult(View(entry));
            Spawn(entry, why);
            view = View(entry);
        }
        Raise(view);
        return Task.FromResult(view);
    }

    private void Spawn(Entry entry, string why)
    {
        var id = entry.Registration.Id;
        HostSpawnSpec spec;
        try
        {
            spec = _spec(entry.Registration);
        }
        catch (Exception error) when (error is not HostRuntimeException)
        {
            entry.Status = BackendStatus.Error;
            entry.Detail = "Its launch could not be built: " + error.Message;
            _env.Audit.Record("runtime", id, "start-failed", error.GetType().Name);
            return;
        }
        IHostProcess? process;
        try
        {
            process = _env.Host.Processes.Start(spec);
        }
        catch (Exception error)
        {
            process = null;
            entry.Detail = error.Message;
        }
        if (process is null)
        {
            entry.Status = BackendStatus.Error;
            entry.Detail = "It did not start" + (entry.Detail is null ? "." : $": {entry.Detail}");
            _env.Audit.Record("runtime", id, "start-failed", null);
            return;
        }
        entry.Process = process;
        entry.Pid = process.Id;
        entry.Created = process.CreatedUtcTicks;
        entry.Stopping = false;
        entry.StopOverdue = false;
        entry.Adopted = false;
        entry.Status = BackendStatus.Starting;
        entry.Detail = null;
        entry.StartedAt = _env.Time.GetUtcNow();
        entry.StartRequestedAt = entry.StartedAt;
        _env.Host.Files.CreateDirectory(_env.Host.Path(_env.Paths.RuntimeState));
        try
        {
            entry.Tree = _env.Host.Containment.Keep(process.Id, process.CreatedUtcTicks, Owner(id), LedgerPath(id));
        }
        catch (Exception error)
        {
            _env.Log.Warn($"Runtime {id}: containment failed ({error.Message}); it runs, tracked by its identity.");
        }
        _env.Host.Files.ReplaceAtomically(_env.Host.Path(StatePath(id)),
            JsonSerializer.Serialize(new ProcessState(process.Id, process.CreatedUtcTicks, entry.StartedAt.Value), NodeStateStore.Json));
        OpenLog(entry);
        if (spec.StandardOutput == StdioMode.Pipe)
        {
            process.OutputLine += line => { if (line is not null) Remember(entry, line); };
            process.BeginOutputReadLine();
        }
        if (spec.StandardError == StdioMode.Pipe)
        {
            process.ErrorLine += line => { if (line is not null) Remember(entry, line); };
            process.BeginErrorReadLine();
        }
        process.EnableRaisingEvents = true;
        process.Exited += (_, _) => OnExited(entry, process);
        if (process.HasExited) OnExited(entry, process);
        _env.Audit.Record("runtime", id, "started", $"pid {process.Id}; {why}");
        _env.Log.Info($"Runtime {id} started (PID {process.Id}, {why}).");
    }

    private void OnExited(Entry entry, IHostProcess process)
    {
        HostRuntime view;
        lock (_gate)
        {
            if (entry.Process != process) return;
            int? code = null;
            try
            {
                code = process.ExitCode;
            }
            catch
            {
            }
            var expected = entry.Stopping || entry.StopOverdue;
            // Written before the reap closes the runtime's log file.
            Remember(entry, $"[leaf-node] exited{(code is null ? "" : $" with code {code}")}");
            ReapAndForget(entry);
            entry.Status = expected ? BackendStatus.Stopped : BackendStatus.Error;
            entry.Detail = expected ? null : $"It exited{(code is null ? "" : $" with code {code}")}.";
            view = View(entry);
        }
        _env.Audit.Record("runtime", entry.Registration.Id, entry.Status == BackendStatus.Stopped ? "stopped" : "exited", null);
        Raise(view);
    }

    /// <summary>Stops a managed runtime for <paramref name="by"/>, who its audit entry names.</summary>
    public async Task<HostRuntime> StopAsync(string id, RuntimeRequester by, TimeSpan? grace = null, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(id, out var entry))
                throw new HostRuntimeException(HostRuntimeException.Unknown, $"No runtime '{id}' is registered on this node.");
            if (!entry.Managed)
                throw new HostRuntimeException(HostRuntimeException.NotManaged, $"'{id}' is run by the node's owner (attach mode): the agent never stops it.");
        }
        await StopCoreAsync(id, Reason(by), grace);
        return Status(id)!;
    }

    /// <summary>
    /// Stops one managed runtime: its whole kept tree (Windows) or its root's tree while the identity still matches.
    /// ComfyUI has no graceful stop, so it is killed at once; <paramref name="grace"/> bounds the wait for its exit.
    /// </summary>
    private async Task StopCoreAsync(string id, string why, TimeSpan? grace = null)
    {
        Entry? entry;
        IHostProcess? process;
        int pid;
        long created;
        lock (_gate)
        {
            if (!_entries.TryGetValue(id, out entry) || !entry.Managed || !entry.Alive) return;
            entry.Stopping = true;
            // What this stop stops: only that process (or, adopted, that pid and creation time) is reaped below.
            process = entry.Process;
            pid = entry.Pid;
            created = entry.Created;
            Kill(entry);
        }
        var wait = grace is { } g && g > TimeSpan.Zero ? (g > MaxGrace ? MaxGrace : g) : TimeSpan.FromSeconds(10);
        var deadline = DateTime.UtcNow + wait;
        while (DateTime.UtcNow < deadline)
        {
            if (process is null ? !IdentityAlive(pid, created) : Exited(process)) break;
            await Task.Delay(50);
        }
        HostRuntime view;
        var survived = false;
        lock (_gate)
        {
            var same = entry.Alive && ReferenceEquals(entry.Process, process) && entry.Pid == pid && entry.Created == created;
            if (same && (process is null ? !IdentityAlive(pid, created) : Exited(process)))
                ReapAndForget(entry);
            else if (same)
            {
                // The kill did not take within the grace (review follow-up 2): the stop is over, so Stopping is cleared
                // (a later start is no longer refused as runtime_stopping) and the runtime is in error, still tracked,
                // so a later stop tries again and an exit is still seen.
                survived = true;
                entry.Stopping = false;
                entry.StopOverdue = true;
                entry.Status = BackendStatus.Error;
                entry.Detail = $"It did not stop within {wait.TotalSeconds:0} s.";
            }
            if (!entry.Alive)
            {
                entry.Status = BackendStatus.Stopped;
                entry.Detail = null;
            }
            view = View(entry);
        }
        _env.Audit.Record("runtime", id, survived ? "stop-failed" : "stopped", why);
        if (survived) _env.Log.Warn($"Runtime {id} did not stop within {wait.TotalSeconds:0} s ({why}).");
        else _env.Log.Info($"Runtime {id} stopped ({why}).");
        Raise(view);
    }

    /// <summary>
    /// Whether the process ended. Its exit handler may already have reaped and disposed it, so a process with no
    /// handle left counts as ended.
    /// </summary>
    private static bool Exited(IHostProcess process)
    {
        try
        {
            return process.HasExited;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }

    private void Kill(Entry entry)
    {
        try
        {
            if (entry.Tree is { } tree && OperatingSystem.IsWindows())
            {
                tree.Reap();
                entry.Tree = null;
            }
            else if (entry.Process is { } process && !Exited(process))
            {
                process.Kill(entireProcessTree: true);
            }
            else if (IdentityAlive(entry.Pid, entry.Created))
            {
                using var root = System.Diagnostics.Process.GetProcessById(entry.Pid);
                root.Kill(entireProcessTree: true);
            }
        }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException or System.ComponentModel.Win32Exception)
        {
            // already gone
        }
    }

    /// <summary>The root is gone: reap what is left of its tree, drop the ledger and the state file.</summary>
    private void ReapAndForget(Entry entry)
    {
        try
        {
            if (entry.Tree is { } tree && OperatingSystem.IsWindows()) tree.Reap();
        }
        catch
        {
        }
        entry.Tree = null;
        // Disposed off the lock: Process.Dispose waits for an Exited callback in flight, and that callback (OnExited)
        // waits for this lock. Disposing here would deadlock a stop whose poll saw the exit before the callback ran.
        if (entry.Process is { } process) ThreadPool.QueueUserWorkItem(static p => p.Dispose(), process, preferLocal: false);
        entry.Process = null;
        entry.Pid = 0;
        entry.Created = 0;
        entry.StartedAt = null;
        entry.Adopted = false;
        entry.Stopping = false;
        entry.StopOverdue = false;
        entry.Log?.Dispose();
        entry.Log = null;
        DeleteState(entry.Registration.Id);
    }

    /// <summary>Kill-all, revoke, unpair, uninstall: every managed runtime stops; attach-mode ones stay up.</summary>
    public async Task<int> StopAllManagedAsync(string why)
    {
        List<string> ids;
        lock (_gate) ids = _entries.Values.Where(e => e.Managed && e.Alive).Select(e => e.Registration.Id).ToList();
        foreach (var id in ids) await StopCoreAsync(id, why);
        return ids.Count;
    }

    /// <summary>
    /// <c>leaf-node runtime test</c> (design 4): an attached runtime is probed once; a managed one is started if it is
    /// not running, probed until it answers or <paramref name="timeout"/> passes, and stopped again if the test
    /// started it. True when it answered.
    /// </summary>
    public async Task<(bool Ok, HostRuntime Runtime)> TestAsync(string id, TimeSpan timeout, CancellationToken ct = default)
    {
        var registration = Find(id) ?? throw new HostRuntimeException(HostRuntimeException.Unknown, $"No runtime '{id}' is registered on this node.");
        if (registration.Managed == HostRuntimeManagement.Attach)
        {
            var answered = await _probe(registration, ct);
            await ProbeOnceAsync(ct);
            return (answered, Status(id)!);
        }
        var wasRunning = Status(id) is { } before && before.StartedAt is not null;
        await StartCore(id, "the owner's test");
        var deadline = _env.Time.GetUtcNow() + timeout;
        var ok = false;
        while (_env.Time.GetUtcNow() < deadline && !ct.IsCancellationRequested)
        {
            if (Status(id) is { StartedAt: null }) break; // it exited
            if (await _probe(registration, ct))
            {
                ok = true;
                break;
            }
            await Task.Delay(TimeSpan.FromMilliseconds(500), _env.Time, ct);
        }
        await ProbeOnceAsync(ct);
        var result = Status(id)!;
        if (!wasRunning) await StopCoreAsync(id, "the owner's test ended");
        _env.Audit.Record("runtime", id, ok ? "test-passed" : "test-failed", null);
        return (ok, result);
    }


    // ================================================================= health

    public void StartHealthLoop(TimeSpan interval)
    {
        lock (_gate) _healthLoop ??= Task.Run(() => HealthLoopAsync(interval, _life.Token));
    }

    private async Task HealthLoopAsync(TimeSpan interval, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await ProbeOnceAsync(ct);
                await Task.Delay(interval, _env.Time, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception error)
            {
                _env.Log.Warn($"Runtime probe: {error.Message}");
            }
        }
    }

    /// <summary>Probes every runtime once and records what changed (tests call it directly).</summary>
    public async Task ProbeOnceAsync(CancellationToken ct = default)
    {
        List<Entry> entries;
        lock (_gate) entries = _entries.Values.ToList();
        foreach (var entry in entries)
        {
            var registration = entry.Registration;
            var adoptedGone = false;
            int gonePid = 0;
            long goneCreated = 0;
            lock (_gate)
                if (entry.Managed && entry.Alive && entry.Process is null && !IdentityAlive(entry.Pid, entry.Created) && entry.Tree is not { LiveProcesses.Count: > 0 })
                    (adoptedGone, gonePid, goneCreated) = (true, entry.Pid, entry.Created);
            var healthy = (!entry.Managed || entry.Alive) && !adoptedGone && await _probe(registration, ct);
            HostRuntime? changed = null;
            lock (_gate)
            {
                var before = (entry.Status, entry.Detail);
                // Reaped only if it is still the adopted process seen gone: a stop and a start during the probe leave a
                // new process, which the next probe looks at.
                var stillGone = adoptedGone && entry.Process is null && entry.Pid == gonePid && entry.Created == goneCreated;
                if (stillGone)
                {
                    ReapAndForget(entry);
                    entry.Status = BackendStatus.Error;
                    entry.Detail = "It exited while no agent watched it.";
                }
                else if (!entry.Managed)
                {
                    entry.Status = healthy ? BackendStatus.Running : BackendStatus.Stopped;
                    entry.Detail = healthy ? null : $"Nothing answers at 127.0.0.1:{registration.Port}.";
                }
                // A process a stop could not end stays in error ("did not stop") even while it still answers.
                else if (entry.Alive && !entry.Stopping && !entry.StopOverdue && !adoptedGone)
                {
                    if (healthy)
                    {
                        entry.Status = BackendStatus.Running;
                        entry.Detail = null;
                    }
                    else if (entry.Status == BackendStatus.Running
                             || _env.Time.GetUtcNow() - (entry.StartRequestedAt ?? _env.Time.GetUtcNow()) > StartTimeout)
                    {
                        entry.Status = BackendStatus.Error;
                        entry.Detail = $"It runs but does not answer at 127.0.0.1:{registration.Port}.";
                    }
                }
                if ((entry.Status, entry.Detail) != before) changed = View(entry);
            }
            if (changed is not null) Raise(changed);
        }
    }

    // ================================================================= views

    private HostRuntime View(Entry entry)
    {
        var registration = entry.Registration;
        return new HostRuntime(registration.Id, registration.Kind, registration.DisplayLabel, registration.Managed, registration.Enabled,
            entry.Status, entry.Detail, registration.Accelerator, RuntimeKinds.Capabilities(registration.Kind), [registration.Port],
            entry.Alive || !entry.Managed ? entry.StartedAt : null);
    }

    /// <summary>A <c>runtime.list</c> entry (design 2.4).</summary>
    public static JsonObject Json(HostRuntime runtime) => new()
    {
        ["id"] = runtime.Id,
        ["kind"] = runtime.Kind,
        ["label"] = runtime.Label,
        ["managed"] = runtime.Managed == HostRuntimeManagement.Spawn ? "spawn" : "attach",
        ["enabled"] = runtime.Enabled,
        ["status"] = runtime.Status.ToString().ToLowerInvariant(),
        ["statusDetail"] = runtime.StatusDetail,
        ["version"] = runtime.Version,
        ["accelerator"] = runtime.Accelerator,
        ["capabilities"] = new JsonArray(runtime.Capabilities.Select(c => (JsonNode)JsonValue.Create(c)!).ToArray()),
        ["ports"] = new JsonArray(runtime.Ports.Select(p => (JsonNode)JsonValue.Create(p)!).ToArray()),
        ["startedAt"] = runtime.StartedAt?.ToString("O"),
    };

    private void Raise(HostRuntime view)
    {
        try
        {
            Changed?.Invoke(view);
        }
        catch (Exception error)
        {
            _env.Log.Warn($"Runtime change: {error.Message}");
        }
    }

    private void RaiseOf(string id)
    {
        if (Status(id) is { } view) Raise(view);
    }

    /// <summary>One line of a runtime's output, to its log file on the node (NodeLog serializes its writers).</summary>
    private static void Remember(Entry entry, string line) => entry.Log?.Info(line.Length > 4096 ? line[..4096] : line);

    private void OpenLog(Entry entry)
    {
        // NodeLog opens its file on the first line and never throws: a full disk loses lines, not the runtime.
        entry.Log = new NodeLog(_env.Paths.RuntimeLog(entry.Registration.Id));
    }

    private static string Owner(string id) => $"leaf-node runtime {id}";
    private string StatePath(string id) => Path.Combine(_env.Paths.RuntimeState, id + ".json");
    private string LedgerPath(string id) => Path.Combine(_env.Paths.RuntimeState, id + ".ledger.json");

    private void DeleteState(string id)
    {
        try
        {
            var path = _env.Host.Path(StatePath(id));
            if (_env.Host.Files.FileExists(path)) _env.Host.Files.DeleteFile(path);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>The agent stops: what runs keeps running (kept), for the next agent to adopt.</summary>
    public async ValueTask DisposeAsync()
    {
        _life.Cancel();
        if (_healthLoop is { } loop)
        {
            try
            {
                await loop;
            }
            catch
            {
            }
        }
        lock (_gate)
        {
            foreach (var entry in _entries.Values)
            {
                try
                {
                    entry.Tree?.Release(deleteLedger: false);
                }
                catch
                {
                }
                entry.Log?.Dispose();
                entry.Process?.Dispose();
            }
        }
    }
}
