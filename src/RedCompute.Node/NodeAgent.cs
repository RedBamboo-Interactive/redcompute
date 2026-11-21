using System.Security.Cryptography;
using System.Text.Json.Nodes;
using RedCompute.Node.Credentials;
using RedCompute.Node.Link;
using RedCompute.Node.Pairing;
using RedCompute.Node.Reports;
using RedCompute.Node.Runtimes;
using RedCompute.NodeLink;
using RedCompute.NodeLink.Forward;
using RedCompute.NodeLink.Noise;
using RedCompute.PluginSdk.Hosting;

namespace RedCompute.Node;

/// <summary>Everything a verb or the agent needs, injectable for tests.</summary>
public sealed class NodeEnvironment
{
    public required NodePaths Paths { get; init; }
    public required IExecutionHost Host { get; init; }
    public required INodeKeyStores KeyStores { get; init; }
    public required NodeLog Log { get; init; }
    public TimeProvider Time { get; init; } = TimeProvider.System;
    public Func<NodeServerClient> ServerClient { get; init; } = () => new NodeServerClient();
    public IProbeRunner? ProbeRunner { get; init; }
    public LinkOptions Link { get; init; } = LinkOptions.Default;
    public ReconnectBackoff? Backoff { get; init; }

    /// <summary>Tests: dial something other than the real WebSocket (an in-process server).</summary>
    public Func<NodePairing, Func<ECDsa>, Func<CancellationToken, Task<ILinkTransport>>>? Dial { get; init; }

    /// <summary>How often the agent pushes a hardware report, and re-probes the CLIs (design 2.4).</summary>
    public TimeSpan HardwareReportInterval { get; init; } = TimeSpan.FromSeconds(60);
    public TimeSpan CliReprobeInterval { get; init; } = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Reap the probe trees an earlier run left (the containment ledger boot sweep, design 2.4). Tests
    /// turn it off: their process has no ledgers of its own.
    /// </summary>
    public bool SweepContainmentAtStart { get; init; } = true;

    /// <summary>How often the agent probes its runtimes' health (Phase 4 design 2.4).</summary>
    public TimeSpan RuntimeProbeInterval { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Tests: what a managed runtime launches, instead of the shared recipe.</summary>
    public Func<RuntimeRegistration, PluginSdk.Hosting.HostSpawnSpec>? RuntimeSpec { get; init; }

    /// <summary>Tests: the runtimes' health probe, instead of GET /system_stats.</summary>
    public Func<RuntimeRegistration, CancellationToken, Task<bool>>? RuntimeProbe { get; init; }

    public NodeStateStore StateStore => _state ??= new NodeStateStore(Host, Paths);
    private NodeStateStore? _state;

    public AuditLog Audit => _audit ??= new AuditLog(Paths.Audit, Time);
    private AuditLog? _audit;
}

/// <summary>
/// The background agent (<c>leaf-node run</c>, design 2.2): reads <c>node.json</c>, keeps the link while
/// paired, pushes reports, answers the control channel, and on revocation runs the kill switch, deletes its
/// key and spool, records why, and stops dialling (design 3.5). It keeps running unpaired, so a later
/// <c>leaf-node pair</c> reloads it.
/// <para>
/// Which revocation it acts on depends on the pin. A node that pinned the server's link key (E2E) acts only on the
/// server's sealed, in-band verdict (<c>link.verdict</c>, or the welcome's refusal). A close 4403 or an HTTP 403/404
/// could come from anything on the path, so for that node they mean "revoked, unverified": nothing is deleted, the
/// link retries with backoff, and its owner can run <c>leaf-node unpair</c>. A node that never pinned still treats
/// them as before (design 1b.10, the migration).
/// </para>
/// </summary>
public sealed class NodeAgent : IAsyncDisposable
{
    private readonly NodeEnvironment _env;
    private readonly NodeReporter _reporter;
    private readonly RuntimeManager _runtimes;
    private readonly ForwardHost _forward;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private CancellationTokenSource? _linkRun;
    private LinkClient? _client;
    private NodeLinkHandler? _handler;
    private NodePairing? _pairing;
    private TaskCompletionSource _reload = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private DateTimeOffset? _statusSince;
    private string _linkStatus = "not-paired";
    private string? _linkReason;

    public NodeAgent(NodeEnvironment env)
    {
        _env = env;
        _reporter = new NodeReporter(env.Host, env.ProbeRunner ?? new HostProbeRunner(env.Host), env.Time);
        _runtimes = new RuntimeManager(env, env.RuntimeSpec, env.RuntimeProbe);
        ForwardTarget = new RuntimeForwardTarget(_runtimes);
        _forward = new ForwardHost(ForwardTarget, audit: entry => env.Audit.Record("forward", entry.Path, entry.Outcome,
            $"{entry.Runtime} {entry.Mode}{(entry.Method is null ? "" : " " + entry.Method)} status {entry.Status?.ToString() ?? "-"}"
            + $" request {entry.RequestBytes} B response {entry.ResponseBytes} B"));
        _runtimes.Changed += OnRuntimeChanged;
    }

    /// <summary>The runtimes the owner registered here, and what the agent started from them.</summary>
    public RuntimeManager Runtimes => _runtimes;

    public ForwardHost Forward => _forward;

    /// <summary>What the forward reaches: the runtimes' allowlists, and the prompts the server submitted through it.</summary>
    public RuntimeForwardTarget ForwardTarget { get; }

    /// <summary>Every runtime change goes to the server as <c>runtime.changed</c>, so it never polls (design 2.4).</summary>
    private void OnRuntimeChanged(PluginSdk.Hosting.HostRuntime runtime)
    {
        if (Client?.Session is not { IsAttached: true } session || !session.HasFeature(LinkFeatures.RuntimeV1)) return;
        _ = Task.Run(async () =>
        {
            try
            {
                await session.NotifyAsync(RuntimeRpc.Changed, RuntimeManager.Json(runtime));
            }
            catch (Exception error) when (error is LinkRpcException or LinkClosedException or ObjectDisposedException
                                              or InvalidOperationException or LinkChannelResetException)
            {
                // the server lists the runtimes again when the link comes back
            }
        });
    }

    public LinkClient? Client { get { lock (_gate) return _client; } }
    public NodeLinkHandler? Handler { get { lock (_gate) return _handler; } }

    /// <summary>Completes each time the link status changes (tests wait on it).</summary>
    public event Action<string, string?>? LinkStatusChanged;

    /// <summary>Runs until <paramref name="ct"/>: one link per pairing, re-read on <see cref="Reload"/>.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        _env.Log.Info($"leaf-node {AgentIdentity.Version} started ({AgentIdentity.Os} {AgentIdentity.Arch}).");
        if (_env.SweepContainmentAtStart)
        {
            try
            {
                var swept = _env.Host.Containment.SweepLedgers();
                if (swept > 0) _env.Log.Info($"Reaped {swept} probe tree(s) an earlier run left.");
            }
            catch (Exception error)
            {
                _env.Log.Warn($"Containment sweep: {error.Message}");
            }
        }
        try
        {
            // What an earlier agent started keeps running across a restart: adopt it, never start a second one.
            _runtimes.Load();
        }
        catch (NodeCommandException error)
        {
            _env.Log.Error(error.Message);
        }
        _runtimes.StartHealthLoop(_env.RuntimeProbeInterval);

        while (!ct.IsCancellationRequested)
        {
            Task reload;
            lock (_gate) reload = _reload.Task;
            // An unpair in progress finishes before node.json is read again.
            await _lifecycle.WaitAsync(ct);
            _lifecycle.Release();
            var state = _env.StateStore.Load();
            if (state.Pairing is null)
            {
                SetLinkStatus(state.Ended is null ? "not-paired" : state.Ended.Reason, null);
                await Task.WhenAny(reload, Task.Delay(Timeout.Infinite, ct));
                continue;
            }

            try
            {
                await RunLinkAsync(state.Pairing, reload, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
            }
            catch (NodeCommandException error)
            {
                _env.Log.Error(error.Message);
                SetLinkStatus("error", error.Message);
                await Task.WhenAny(reload, Task.Delay(TimeSpan.FromMinutes(1), _env.Time, ct).ContinueWith(_ => { }));
            }
        }
    }

    /// <summary>Re-reads <c>node.json</c>: after a pairing, or after an unpair the CLI did.</summary>
    public void Reload()
    {
        TaskCompletionSource previous;
        lock (_gate)
        {
            previous = _reload;
            _reload = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _linkRun?.Cancel();
        }
        previous.TrySetResult();
    }

    private async Task RunLinkAsync(NodePairing pairing, Task reload, CancellationToken ct)
    {
        var store = _env.KeyStores.ForKeyRef(pairing.KeyRef);
        ECDsa OpenKey() => store.Open(pairing.KeyRef, pairing.ServerOrigin)
                           ?? throw new NodeCommandException("key_missing",
                               $"The node key ({pairing.KeyRef}) is gone. Pair again: leaf-node unpair, then leaf-node pair.");
        using (OpenKey()) { } // fail now, not at the first dial

        // E2E (P0.5c): a pinned node always encrypts to its pin and never sends plaintext; the identity key signs
        // the binding of its link key into each handshake.
        using var linkKey = pairing.LinkPin is null ? null
            : store.OpenLinkKey(pairing.KeyRef, pairing.ServerOrigin)
              ?? throw new NodeCommandException("link_key_missing",
                  "This node's link key is gone. Ask its owner for a re-pin code, then run: leaf-node repin --code <code>.");
        if (pairing.LinkPin is { Protocol: var pinned } && pinned != NoiseProtocol.Name)
            throw new NodeCommandException("link_protocol_unsupported",
                $"This node's link is pinned to {pinned}, which leaf-node {AgentIdentity.Version} does not speak. Update leaf-node; it never falls back.");
        var secure = pairing.LinkPin is { } pin ? Secure(pairing, pin.PublicKeyBytes(), linkKey!, OpenKey) : null;

        var dial = _env.Dial?.Invoke(pairing, OpenKey) ?? new NodeDialer(pairing, OpenKey, _env.Time).DialAsync;
        var handler = new NodeLinkHandler(pairing.NodeId, _reporter, _env.Audit, _env.Log, () => pairing.CredentialStore,
            (session, clis) => PushAsync(session, new JsonObject { ["clis"] = clis }), _runtimes, _forward);
        var client = new LinkClient(new LinkClientOptions
        {
            NodeId = pairing.NodeId,
            Agent = AgentIdentity.Info,
            Handler = handler,
            Dial = dial,
            Features = AgentIdentity.Features,
            Link = _env.Link,
            Backoff = _env.Backoff,
            Secure = secure,
        });
        client.StatusChanged += (status, reason) => OnClientStatus(client, status, reason);

        using var run = CancellationTokenSource.CreateLinkedTokenSource(ct);
        lock (_gate)
        {
            _client = client;
            _handler = handler;
            _pairing = pairing;
            _linkRun = run;
        }
        if (reload.IsCompleted) run.Cancel();
        _env.Log.Info($"Linking {pairing.NodeId} to {pairing.ServerOrigin}.");

        var reports = ReportLoopAsync(client, pairing, run.Token);
        try
        {
            await client.RunAsync(run.Token);
        }
        finally
        {
            run.Cancel();
            try
            {
                await reports;
            }
            catch (OperationCanceledException)
            {
            }
            await client.DisposeAsync();
            lock (_gate)
            {
                _client = null;
                _handler = null;
                _linkRun = null;
            }
        }

        if (client.Status == LinkClientStatus.Refused)
        {
            // Revoked or forgotten by the server (design 3.5 step 4).
            var reason = client.StatusReason is "node_unknown" ? "unknown" : "revoked";
            handler.KillAll();
            await _runtimes.StopAllManagedAsync(reason);
            await ForgetAsync(pairing, reason, callServer: false, CancellationToken.None);
            _env.Log.Warn(reason == "revoked"
                ? $"The server revoked this node ({client.StatusReason}). Its key and spool are deleted; pair again to reconnect."
                : $"The server does not know this node ({client.StatusReason}). Its key is deleted; pair again to reconnect.");
        }
    }

    /// <summary>The Noise initiator for a pinned node (design 1b.2, 1b.4).</summary>
    internal static Func<ILinkTransport, CancellationToken, Task<ILinkTransport>> Secure(NodePairing pairing, byte[] serverKey,
        NoiseKeyPair linkKey, Func<ECDsa> identity)
        => async (transport, ct) => await NoiseLink.InitiateAsync(transport, new NoiseInitiatorOptions
        {
            NodeId = pairing.NodeId,
            LocalStatic = linkKey,
            ServerStatic = serverKey,
            Binding = binding => NoiseBinding.Payload(binding, bytes =>
            {
                using var key = identity();
                return key.SignData(bytes, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            }),
        }, ct);

    /// <summary>What the owner reads when the encrypted link cannot be set up (design 1b.7).</summary>
    internal static string RepinAdvice(string? reason) => reason switch
    {
        _ when reason?.Contains(NoiseLink.KeyUnknown) == true || reason?.Contains(NoiseLink.KeyMismatch) == true =>
            "The server's link key is not the one this node pinned. It will not connect until its owner gives it a re-pin code: leaf-node repin --code <code>.",
        _ when reason?.Contains(NoiseLink.E2eRequired) == true =>
            "The server requires an encrypted link, and this node has no pinned link key. Ask its owner for a re-pin code: leaf-node repin --code <code>.",
        _ when reason?.Contains(NoiseLink.IdentityInvalid) == true =>
            "The server could not tie this node's link key to its identity. Ask its owner for a re-pin code: leaf-node repin --code <code>.",
        _ => "The encrypted link could not be set up. Ask the node's owner for a re-pin code: leaf-node repin --code <code>.",
    };

    /// <summary>What the owner reads when a pinned node was told "revoked" by something it cannot authenticate.</summary>
    internal static string UnverifiedAdvice(string? reason)
        => (reason?.Contains(LinkVerdicts.Unknown) == true
               ? "The server says it does not know this node"
               : "The server says this node was revoked")
           + ", but not on the encrypted link, so the answer could have come from anything on the path. Nothing is "
           + "deleted, and it keeps trying with backoff. If its owner did revoke or remove it, run: leaf-node unpair.";

    private void OnClientStatus(LinkClient client, LinkClientStatus status, string? reason)
    {
        var text = status switch
        {
            LinkClientStatus.Connecting => "connecting",
            LinkClientStatus.Online => "online",
            LinkClientStatus.Offline => "offline",
            LinkClientStatus.Refused => "refused",
            LinkClientStatus.NeedsUpdate => "needs-update",
            LinkClientStatus.NeedsRepin => "needs-repin",
            LinkClientStatus.RevokedUnverified => "revoked-unverified",
            _ => "stopped",
        };
        if (status == LinkClientStatus.Online) _env.Log.Info($"Link online ({reason}).");
        else if (status == LinkClientStatus.NeedsRepin) _env.Log.Warn($"{RepinAdvice(reason)} ({reason})");
        else if (status == LinkClientStatus.RevokedUnverified) _env.Log.Warn($"{UnverifiedAdvice(reason)} ({reason})");
        else if (status is LinkClientStatus.Offline or LinkClientStatus.Refused or LinkClientStatus.NeedsUpdate or LinkClientStatus.NeedsRepin)
            _env.Log.Info($"Link {text}{(reason is null ? "" : $": {reason}")}.");
        SetLinkStatus(text, reason);
    }

    private void SetLinkStatus(string status, string? reason)
    {
        bool changed;
        lock (_gate)
        {
            changed = _linkStatus != status || _linkReason != reason;
            if (_linkStatus != status) _statusSince = _env.Time.GetUtcNow();
            _linkStatus = status;
            _linkReason = reason;
        }
        if (changed) LinkStatusChanged?.Invoke(status, reason);
    }

    /// <summary>Hardware every minute, the CLIs at start and every 30 minutes when they changed (design 2.4).</summary>
    private async Task ReportLoopAsync(LinkClient client, NodePairing pairing, CancellationToken ct)
    {
        var nextCli = _env.Time.GetUtcNow();
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(_env.HardwareReportInterval, _env.Time, ct);
            if (client.Session is not { IsAttached: true } session) continue;
            try
            {
                var report = new JsonObject();
                if (session.HasFeature(LinkFeatures.HardwareV1))
                    report["hardware"] = await _reporter.HardwareReportAsync(pairing.NodeId, ct);
                if (session.HasFeature(LinkFeatures.CliV1) && _env.Time.GetUtcNow() >= nextCli)
                {
                    nextCli = _env.Time.GetUtcNow() + _env.CliReprobeInterval;
                    var (clis, changed) = await _reporter.ReprobeAsync(ct);
                    if (changed) report["clis"] = clis;
                }
                if (report.Count > 0) await PushAsync(session, report);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                _env.Log.Warn($"Report: {error.Message}");
            }
        }
    }

    private static async Task PushAsync(LinkSession session, JsonObject report)
    {
        try
        {
            await session.NotifyAsync(NodeLinkHandler.Report, report);
        }
        catch (Exception error) when (error is LinkRpcException or LinkClosedException or ObjectDisposedException
                                          or InvalidOperationException)
        {
            // the next report carries it
        }
    }

    /// <summary>
    /// Ends this pairing locally: optionally tells the server (the signed unpair), then deletes the key,
    /// empties the spool and writes how it ended into <c>node.json</c>.
    /// </summary>
    public async Task<bool> ForgetAsync(NodePairing pairing, string reason, bool callServer, CancellationToken ct)
    {
        await _lifecycle.WaitAsync(ct);
        try
        {
            return await NodeLifecycle.ForgetAsync(_env, pairing, reason, callServer, ct);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    /// <summary>The <c>unpair</c> verb when the agent runs: say goodbye on the link, stop it, then forget.</summary>
    public async Task<JsonObject> UnpairAsync(CancellationToken ct)
    {
        await _lifecycle.WaitAsync(ct);
        bool confirmed;
        NodePairing pairing;
        try
        {
            var state = _env.StateStore.Load();
            pairing = state.Pairing ?? throw new NodeCommandException("not_paired", "This node is not paired.");
            await SayByeAsync("unpair");
            lock (_gate) _linkRun?.Cancel();
            await _runtimes.StopAllManagedAsync("unpaired");
            confirmed = await NodeLifecycle.ForgetAsync(_env, pairing, "unpaired", callServer: true, ct);
        }
        finally
        {
            _lifecycle.Release();
        }
        Reload();
        return new JsonObject { ["ok"] = true, ["nodeId"] = pairing.NodeId, ["serverConfirmed"] = confirmed };
    }

    /// <summary>A graceful close tells the server why (<c>link.bye {reason}</c>, design 4.9).</summary>
    public async Task SayByeAsync(string reason)
    {
        if (Client?.Session is not { IsAttached: true } session) return;
        try
        {
            // A call, not a notification: its answer (or error) proves the server read it before the link closes.
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await session.CallAsync(NodeLinkHandler.Bye, new JsonObject { ["reason"] = reason }, timeout.Token);
        }
        catch (Exception error) when (error is LinkRpcException or LinkClosedException or OperationCanceledException
                                          or InvalidOperationException)
        {
        }
    }

    public int KillAll() => Handler?.KillAll() ?? _forward.ResetAll("killed_by_owner", "The node's owner ran kill-all.");

    /// <summary>The kill switch: every channel and forward ends, and every runtime the agent started stops.</summary>
    public async Task<(int Channels, int Runtimes)> KillAllAsync()
    {
        var channels = KillAll();
        var runtimes = await _runtimes.StopAllManagedAsync("kill-all");
        _env.Audit.Record("local", "kill-all-runtimes", "done", $"{runtimes} runtime(s)");
        return (channels, runtimes);
    }

    /// <summary>The <c>status</c> verb's answer.</summary>
    public JsonObject Status()
    {
        var state = _env.StateStore.Load();
        LinkClient? client;
        string status;
        string? reason;
        DateTimeOffset? since;
        lock (_gate)
        {
            client = _client;
            status = _linkStatus;
            reason = _linkReason;
            since = _statusSince;
        }
        var answer = NodeLifecycle.StateJson(state);
        answer["ok"] = true;
        answer["running"] = true;
        answer["pid"] = Environment.ProcessId;
        answer["runtimes"] = new JsonArray(_runtimes.List().Select(r => (JsonNode)RuntimeManager.Json(r)).ToArray());
        answer["link"] = new JsonObject
        {
            ["status"] = status,
            ["reason"] = reason,
            ["since"] = since?.ToString("O"),
            ["connections"] = client?.Connections,
            ["resumedConnections"] = client?.ResumedConnections,
            ["serverEpoch"] = client?.Welcome?.ServerEpoch,
            ["updateAvailable"] = client?.Welcome?.AgentPolicy is { } policy
                                  && AgentVersions.Compare(AgentIdentity.Version, policy.Recommended) < 0,
        };
        return answer;
    }

    public async ValueTask DisposeAsync()
    {
        lock (_gate) _linkRun?.Cancel();
        if (Client is { } client) await client.DisposeAsync();
        await _runtimes.DisposeAsync();
        _reporter.Dispose();
    }
}

/// <summary>What the CLI and the agent both do to a pairing.</summary>
public static class NodeLifecycle
{
    public static async Task<bool> ForgetAsync(NodeEnvironment env, NodePairing pairing, string reason, bool callServer,
        CancellationToken ct)
    {
        var confirmed = false;
        INodeKeyStore? store = null;
        try
        {
            store = env.KeyStores.ForKeyRef(pairing.KeyRef);
        }
        catch (NodeCommandException error)
        {
            env.Log.Warn(error.Message);
        }

        if (callServer && store is not null)
        {
            using var key = SafeOpen(env, store, pairing);
            if (key is not null)
            {
                using var server = env.ServerClient();
                confirmed = await server.UnpairAsync(pairing.ServerOrigin, pairing.NodeId, key, env.Time.GetUtcNow(), ct);
                if (!confirmed)
                    env.Log.Warn($"{pairing.ServerOrigin} did not confirm the unpair; the node shows offline there until someone revokes it.");
            }
        }

        try
        {
            store?.DeleteLinkKey(pairing.KeyRef, pairing.ServerOrigin);
            store?.Delete(pairing.KeyRef, pairing.ServerOrigin);
        }
        catch (NodeCommandException error)
        {
            env.Log.Warn($"The key could not be deleted: {error.Message}");
        }
        ClearSpool(env);
        env.StateStore.Save(new NodeState
        {
            Ended = new NodeEnding(pairing.NodeId, pairing.ServerOrigin, reason, env.Time.GetUtcNow()),
        });
        env.Audit.Record("local", "forget", reason, pairing.NodeId);
        return confirmed;
    }

    private static ECDsa? SafeOpen(NodeEnvironment env, INodeKeyStore store, NodePairing pairing)
    {
        try
        {
            return store.Open(pairing.KeyRef, pairing.ServerOrigin);
        }
        catch (NodeCommandException error)
        {
            env.Log.Warn(error.Message);
            return null;
        }
    }

    private static void ClearSpool(NodeEnvironment env)
    {
        var spool = env.Host.Path(env.Paths.Spool);
        if (env.Host.Files.DirectoryExists(spool)) env.Host.Files.DeleteDirectory(spool, recursive: true);
    }

    /// <summary><c>node.json</c> as the status verb shows it (never the key reference's secret parts: there are none).</summary>
    public static JsonObject StateJson(NodeState state)
    {
        var answer = new JsonObject
        {
            ["agent"] = new JsonObject { ["version"] = AgentIdentity.Version, ["os"] = AgentIdentity.Os, ["arch"] = AgentIdentity.Arch },
            ["paired"] = state.Pairing is not null,
        };
        if (state.Pairing is { } pairing)
        {
            answer["nodeId"] = pairing.NodeId;
            answer["serverOrigin"] = pairing.ServerOrigin;
            answer["fingerprint"] = pairing.Fingerprint;
            answer["credentialStore"] = pairing.CredentialStore;
            answer["linkEncryption"] = pairing.LinkPin is null ? "off" : "on";
            answer["serverLinkFingerprint"] = pairing.LinkPin?.Fingerprint;
            answer["pairedAt"] = pairing.PairedAt.ToString("O");
        }
        if (state.Ended is { } ended)
            answer["ended"] = new JsonObject
            {
                ["nodeId"] = ended.NodeId,
                ["serverOrigin"] = ended.ServerOrigin,
                ["reason"] = ended.Reason,
                ["at"] = ended.At.ToString("O"),
            };
        return answer;
    }
}
