using System.Net.Http;
using System.Net.WebSockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using RedCompute.App.Services.Nodes;
using RedCompute.Core.Nodes;
using RedCompute.Core.Providers;
using RedCompute.NodeLink;
using RedCompute.NodeLink.Forward;
using RedCompute.PluginSdk.Hosting;
using Xunit;
using static RedCompute.App.Tests.NodeTestKit;

namespace RedCompute.App.Tests;

/// <summary>
/// Phase 4 P6 (design 2.4, 3.1): NodeHub's <see cref="RemoteExecutionHost"/>. An eligible node's host offers only its
/// owner's runtimes (over <c>runtime.v1</c>) and the forward to them; every other member fails closed; runtime reports
/// feed the registry; and eligibility needs a confirmed, online node whose agent speaks the Phase 4 features.
/// </summary>
public sealed class RemoteExecutionHostTests : IAsyncLifetime
{
    private readonly string _node = RemoteId(1);
    private readonly NodeRegistry _registry = Registry();
    private NodeHub _hub = null!;
    private PipedNode? _piped;
    private readonly RuntimeNodeHandler _agent = new();

    public Task InitializeAsync()
    {
        _registry.ApplyKernelNodes([Kernel(_node)]);
        _hub = new NodeHub(_registry, _ => { });
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (_piped is not null) await _piped.DisposeAsync();
        await _hub.DisposeAsync();
    }

    private async Task<RemoteExecutionHost> LinkAsync()
    {
        _piped = new PipedNode(_hub, _node, nodeHandler: _agent, features: RuntimeNodeHandler.Phase4Features);
        await WaitAsync(() => _registry.RuntimesOf(_node).Count > 0, "the runtime list");
        return Assert.IsType<RemoteExecutionHost>(_registry.HostFor(_node));
    }

    // ================================================================= eligibility

    [Fact]
    public async Task An_eligible_node_gets_its_remote_host_one_per_node()
    {
        var host = await LinkAsync();

        Assert.Equal(_node, host.NodeId);
        Assert.Same(host, _registry.HostFor(_node));
        var node = _registry.Find(_node)!;
        Assert.Equal(new NodePlacement(true, null), NodeRegistry.PlacementOf(node));
        Assert.Equal(new NodePlacement(true, null), _registry.Placement(node));
        Assert.Contains(LinkFeatures.RuntimeV1, node.Link!.Features);
    }

    [Fact]
    public async Task An_agent_without_the_phase_4_features_is_needs_update_and_gets_no_host()
    {
        await using var phase3 = new PipedNode(_hub, _node);
        await WaitAsync(() => _registry.Find(_node)?.Report?.Hardware is not null, "the Phase 3 report");

        var node = _registry.Find(_node)!;
        Assert.Equal(NodeStatus.Online, node.Status);
        Assert.Equal(new NodePlacement(false, NodeUnavailableReasons.NeedsUpdate), NodeRegistry.PlacementOf(node));
        Assert.Equal(NodeUnavailableReasons.NeedsUpdate, Assert.Throws<NodeUnavailableException>(() => _registry.HostFor(_node)).Reason);
        Assert.Empty(_registry.RuntimesOf(_node));
    }

    [Fact]
    public void Without_NodeHub_an_otherwise_eligible_node_is_remote_execution_not_available()
    {
        var registry = Registry();
        registry.ApplyKernelNodes([Kernel(_node)]);
        registry.LinkUp(_node, new NodeLinkState("s1", Now, Now, null, null, 1, RuntimeNodeHandler.Phase4Features, "0.2.0", false, null));

        var node = registry.Find(_node)!;
        Assert.True(NodeRegistry.PlacementOf(node).Eligible);
        Assert.Equal(new NodePlacement(false, NodeUnavailableReasons.RemoteExecutionNotAvailable), registry.Placement(node));
        Assert.Equal(NodeUnavailableReasons.RemoteExecutionNotAvailable,
            Assert.Throws<NodeUnavailableException>(() => registry.HostFor(_node)).Reason);
    }

    [Theory]
    [InlineData("revoked")]
    [InlineData("unconfirmed")]
    [InlineData("offline")]
    public void The_first_failing_check_is_the_reason(string state)
    {
        var features = RuntimeNodeHandler.Phase4Features;
        var registry = Registry();
        registry.ApplyKernelNodes([Kernel(_node, confirmed: state != "unconfirmed",
            status: state == "revoked" ? "revoked" : "offline")]);
        if (state != "offline") registry.LinkUp(_node, new NodeLinkState("s1", Now, Now, null, null, 1, features, "0.2.0", false, null));
        registry.AttachRemoteHosts(_ => throw new InvalidOperationException("no host may be asked for"));

        var reason = Assert.Throws<NodeUnavailableException>(() => registry.HostFor(_node)).Reason;
        Assert.Equal(state switch
        {
            "revoked" => NodeUnavailableReasons.Revoked,
            "unconfirmed" => NodeUnavailableReasons.NotConfirmed,
            _ => NodeUnavailableReasons.Offline,
        }, reason);
    }

    // ================================================================= fail closed, by construction

    /// <summary>Every member of every host surface a remote node does not offer, called with default arguments.</summary>
    public static TheoryData<string> FailClosedMembers()
    {
        var data = new TheoryData<string>();
        foreach (var surface in new[] { typeof(IHostProcesses), typeof(IHostContainment), typeof(IHostFileSystem), typeof(IHostQueries) })
            foreach (var method in surface.GetMethods())
                data.Add($"{surface.Name}.{method.Name}({string.Join(",", method.GetParameters().Select(p => p.ParameterType.Name))})");
        data.Add("IHostPlatform.GetEnvironmentVariable(String)");
        data.Add("IHostPlatform.GetFolderPath(SpecialFolder)");
        data.Add("IHostPlatform.get_ComputeDataRoot()");
        data.Add("IHostHardware.OpenSampler()");
        data.Add("IHostHardware.ProcessParentage()");
        data.Add("IHostNetwork.ReverseForwardAsync(Uri,CancellationToken)");
        data.Add("IHostPathRules.GetFullPath(String)");
        return data;
    }

    [Theory]
    [MemberData(nameof(FailClosedMembers))]
    public async Task Every_member_a_remote_node_does_not_offer_throws_and_sends_nothing(string member)
    {
        var host = await LinkAsync();
        var callsBefore = _agent.Calls.Count;
        var opensBefore = _agent.Runtime.Opens.Count;

        var (surfaceName, rest) = (member[..member.IndexOf('.')], member[(member.IndexOf('.') + 1)..]);
        var surface = typeof(IExecutionHost).Assembly.GetType("RedCompute.PluginSdk.Hosting." + surfaceName)!;
        var target = typeof(IExecutionHost).GetProperties().Single(p => p.PropertyType == surface).GetValue(host)!;
        var method = surface.GetMethods().Single(m =>
            $"{m.Name}({string.Join(",", m.GetParameters().Select(p => p.ParameterType.Name))})" == rest);
        var arguments = method.GetParameters().Select(p => Default(p.ParameterType)).ToArray();

        var error = Unwrap(() => method.Invoke(target, arguments));
        var refused = Assert.IsType<RemoteOperationNotAllowedException>(error);
        Assert.Equal(_node, refused.NodeId);
        Assert.StartsWith(surfaceName + ".", refused.Operation);
        Assert.Equal(callsBefore, _agent.Calls.Count);
        Assert.Equal(opensBefore, _agent.Runtime.Opens.Count);

        static Exception? Unwrap(Func<object?> call)
        {
            try
            {
                var result = call();
                if (result is Task task) task.GetAwaiter().GetResult();
                if (result is System.Collections.IEnumerable sequence and not string) foreach (var _ in sequence) { }
                return null;
            }
            catch (TargetInvocationException error) { return error.InnerException; }
            catch (Exception error) { return error; }
        }
    }

    private static object? Default(Type type)
    {
        if (type == typeof(string)) return "x";
        if (type == typeof(HostPath)) return new HostPath(RemoteId(1), "/tmp/x");
        if (type == typeof(Uri)) return new Uri("http://127.0.0.1:1/");
        if (type == typeof(HostSpawnSpec)) return new HostSpawnSpec { FileName = "sh", Containment = ContainmentMode.None, OwnerLabel = "t" };
        if (type == typeof(DetachedSpawnSpec)) return new DetachedSpawnSpec("x", new HostPath(RemoteId(1), "/"), null, "t", "l");
        if (type == typeof(byte[])) return Array.Empty<byte>();
        if (type == typeof(string[])) return Array.Empty<string>();
        return type.IsValueType ? Activator.CreateInstance(type) : null;
    }

    [Fact]
    public async Task The_platform_is_what_the_node_reported_and_it_launches_nothing()
    {
        var host = await LinkAsync();

        Assert.Equal("linux", host.Platform.Info.Os);
        Assert.Equal("leaflinux", host.Platform.Info.MachineName);
        Assert.True(host.Platform.IsLinux);
        Assert.False(host.Platform.IsWindows);
        Assert.False(host.Platform.CanLaunchLocalBackends);
        Assert.Equal("Virtual CPU", host.Hardware.ProcessorName());
        Assert.Equal('/', host.PathRules.DirectorySeparator);
        Assert.True(host.PathRules.IsPathFullyQualified("/home/lab"));
        Assert.Equal("/home/lab/ComfyUI", host.PathRules.Combine("/home/lab", "ComfyUI"));
        Assert.Equal(new HostPath(_node, "/srv"), host.Path("/srv"));
    }

    // ================================================================= the network: the forward only

    [Fact]
    public async Task ResolveServiceUri_is_a_sentinel_that_never_resolves()
    {
        var host = await LinkAsync();

        var resolved = host.Network.ResolveServiceUri(new Uri("http://127.0.0.1:8188/view?filename=a.png"));

        Assert.Equal($"comfyui-1.{_node.ToLowerInvariant().Replace('_', '-')}.leaf-node.invalid", resolved.Host);
        Assert.EndsWith(RemoteExecutionHost.SentinelSuffix, resolved.Host);
        Assert.Equal((8188, "/view", "?filename=a.png"), (resolved.Port, resolved.AbsolutePath, resolved.Query));
        Assert.EndsWith(RemoteExecutionHost.SentinelSuffix, host.Network.ResolveServiceUri(new Uri("http://127.0.0.1:9999/")).Host);
        // Code that builds its own client from a resolved Uri fails closed (the sentinel is .invalid).
        await Assert.ThrowsAnyAsync<HttpRequestException>(async () =>
        {
            using var own = new HttpClient();
            await own.GetAsync(resolved);
        });
        Assert.Empty(_agent.Runtime.Opens);
    }

    [Fact]
    public async Task The_service_handler_carries_requests_to_the_runtime_on_that_port_byte_for_byte()
    {
        var host = await LinkAsync();
        using var client = host.Network.CreateServiceClient("http://127.0.0.1:8188", TimeSpan.FromSeconds(30));
        var baseUrl = host.Network.ResolveServiceBaseUrl("http://127.0.0.1:8188");

        var blob = await client.GetByteArrayAsync(baseUrl + "/blob/3000001");
        var upload = MemoryRuntime.Blob(1_234_567);
        using var echoed = await client.PostAsync(baseUrl + "/echo-hash", new ByteArrayContent(upload));

        Assert.Equal(MemoryRuntime.Blob(3_000_001), blob);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(upload)), await echoed.Content.ReadAsStringAsync());
        Assert.All(_agent.Runtime.Opens, open => Assert.Equal(MemoryRuntime.Id, open.Runtime));
        Assert.Equal(["/blob/3000001", "/echo-hash"], _agent.Runtime.Opens.Select(open => open.Path));
    }

    [Fact]
    public async Task A_port_no_runtime_serves_reaches_nothing_and_a_bound_host_ignores_the_port()
    {
        var host = await LinkAsync();
        using var stray = host.Network.CreateServiceClient("http://127.0.0.1:9999", TimeSpan.FromSeconds(10));

        var refused = await Assert.ThrowsAsync<ForwardException>(() => stray.GetAsync("http://127.0.0.1:9999/blob/1"));
        Assert.Equal(ForwardErrorCodes.RuntimeUnknown, refused.Code);
        await Assert.ThrowsAsync<ForwardException>(() => host.Network.ConnectWebSocketAsync(new Uri("ws://127.0.0.1:9999/ws")));
        Assert.Empty(_agent.Runtime.Opens);

        var bound = host.ForRuntime(MemoryRuntime.Id);
        using var client = bound.Network.CreateServiceClient("http://127.0.0.1:9999", TimeSpan.FromSeconds(10));
        Assert.Equal(MemoryRuntime.Blob(10), await client.GetByteArrayAsync("http://127.0.0.1:9999/blob/10"));
        Assert.Throws<ArgumentException>(() => host.ForRuntime("not a runtime"));
    }

    [Fact]
    public async Task The_websocket_is_carried_over_the_link_to_the_runtime()
    {
        var host = await LinkAsync();

        using var socket = await host.Network.ConnectWebSocketAsync(new Uri("ws://127.0.0.1:8188/ws?clientId=abc"));
        var message = Encoding.UTF8.GetBytes("""{"type":"status"}""");
        await socket.SendAsync(message, WebSocketMessageType.Text, true, CancellationToken.None);
        var buffer = new byte[1024];
        var received = await socket.ReceiveAsync(buffer, CancellationToken.None);

        Assert.Equal(WebSocketMessageType.Text, received.MessageType);
        Assert.Equal(message, buffer[..received.Count]);
        var open = _agent.Runtime.Opens.Single();
        Assert.Equal((MemoryRuntime.Id, ForwardModes.Ws, "/ws", "clientId=abc"), (open.Runtime, open.Mode, open.Path, open.Query));
    }

    [Fact]
    public async Task IsListening_is_a_running_runtimes_port()
    {
        var host = await LinkAsync();
        Assert.False(host.Network.IsListening(8188));

        await _piped!.Client.Session!.NotifyAsync("runtime.changed", RuntimeNodeHandler.ComfyUi(status: "running"));
        await WaitAsync(() => host.Network.IsListening(8188), "the running report");
        Assert.False(host.Network.IsListening(9999));
    }

    // ================================================================= runtimes over runtime.v1

    [Fact]
    public async Task The_runtime_list_is_parsed_strictly_and_capabilities_come_from_the_servers_table()
    {
        _agent.Runtimes.Add(RuntimeNodeHandler.ComfyUi("comfyui-attach", managed: "attach", status: "running", port: 8190));
        _agent.Runtimes.Add(new JsonObject { ["id"] = "mystery-1", ["kind"] = "mystery", ["managed"] = "spawn", ["enabled"] = true,
            ["status"] = "running", ["capabilities"] = new JsonArray("image-gen"), ["ports"] = new JsonArray(7000) });
        _agent.Runtimes.Add(new JsonObject { ["id"] = "../bad", ["kind"] = "comfyui", ["managed"] = "spawn", ["enabled"] = true, ["status"] = "running" });
        _agent.Runtimes.Add(new JsonObject { ["id"] = "no-status", ["kind"] = "comfyui", ["managed"] = "spawn", ["enabled"] = true });
        _agent.Runtimes.Add(new JsonObject { ["id"] = "odd-accel", ["kind"] = "comfyui", ["managed"] = "spawn", ["enabled"] = true,
            ["status"] = "stopped", ["accelerator"] = "gpu-of-doom", ["capabilities"] = new JsonArray("image-gen") });
        var host = await LinkAsync();

        var runtimes = host.Runtimes.List();
        Assert.Equal(["comfyui-1", "comfyui-attach", "mystery-1", "odd-accel"], runtimes.Select(r => r.Id));
        var comfy = runtimes[0];
        Assert.Equal(("comfyui", HostRuntimeManagement.Spawn, BackendStatus.Stopped, "cuda:0", "0.3.40"),
            (comfy.Kind, comfy.Managed, comfy.Status, comfy.Accelerator, comfy.Version));
        Assert.Equal(["image-gen"], comfy.Capabilities);
        Assert.Equal([8188], comfy.Ports);
        Assert.Equal(HostRuntimeManagement.Attach, runtimes[1].Managed);
        Assert.Empty(runtimes[2].Capabilities);
        Assert.Null(runtimes[3].Accelerator);
    }

    [Fact]
    public async Task Start_sends_exactly_the_id_and_the_nodes_push_updates_the_registry()
    {
        var host = await LinkAsync();
        var changed = new List<HostRuntime>();
        host.Runtimes.Changed += runtime => { lock (changed) changed.Add(runtime); };
        var updated = new List<string>();
        _registry.Updated += id => { lock (changed) updated.Add(id); };

        var started = await host.Runtimes.StartAsync("comfyui-1");

        Assert.Equal(BackendStatus.Starting, started.Status);
        var (method, parameters) = _agent.Calls.Last();
        Assert.Equal(("runtime.start", """{"id":"comfyui-1"}"""), (method, parameters));

        await _piped!.Client.Session!.NotifyAsync("runtime.changed", RuntimeNodeHandler.ComfyUi(status: "running"));
        // The observers hear it after the registry has it (and after NodeHub's gathering window): wait for both.
        await WaitAsync(() => host.Runtimes.Status("comfyui-1")?.Status == BackendStatus.Running, "the push");
        await WaitAsync(() => { lock (changed) return changed.Count > 0 && updated.Contains(_node); }, "the observers");
        lock (changed)
        {
            Assert.Equal(BackendStatus.Running, Assert.Single(changed).Status);
            Assert.Contains(_node, updated);
        }
    }

    [Fact]
    public async Task Stop_sends_the_id_and_the_grace_and_refuses_a_grace_over_a_minute_before_sending()
    {
        var host = await LinkAsync();

        var stopped = await host.Runtimes.StopAsync("comfyui-1", TimeSpan.FromSeconds(5));
        Assert.Equal(BackendStatus.Stopped, stopped.Status);
        Assert.Equal(("runtime.stop", """{"id":"comfyui-1","graceSeconds":5}"""), _agent.Calls.Last());

        await host.Runtimes.StopAsync("comfyui-1");
        Assert.Equal(("runtime.stop", """{"id":"comfyui-1"}"""), _agent.Calls.Last());

        var calls = _agent.Calls.Count;
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => host.Runtimes.StopAsync("comfyui-1", TimeSpan.FromSeconds(61)));
        Assert.Equal(calls, _agent.Calls.Count);
    }

    [Fact]
    public async Task The_nodes_refusals_are_typed_and_an_id_it_never_reported_is_not_sent()
    {
        _agent.Runtimes.Add(RuntimeNodeHandler.ComfyUi("comfyui-attach", managed: "attach", port: 8190));
        _agent.Runtimes.Add(RuntimeNodeHandler.ComfyUi("comfyui-off", enabled: false, port: 8191));
        var host = await LinkAsync();

        Assert.Equal(HostRuntimeException.NotManaged,
            (await Assert.ThrowsAsync<HostRuntimeException>(() => host.Runtimes.StartAsync("comfyui-attach"))).Code);
        Assert.Equal(HostRuntimeException.Disabled,
            (await Assert.ThrowsAsync<HostRuntimeException>(() => host.Runtimes.StartAsync("comfyui-off"))).Code);
        // A start while the node is still stopping it (review P4-P7, item 3) is typed too, not a bare RPC failure.
        _agent.Stopping["comfyui-1"] = true;
        Assert.Equal(HostRuntimeException.Stopping,
            (await Assert.ThrowsAsync<HostRuntimeException>(() => host.Runtimes.StartAsync("comfyui-1"))).Code);
        var calls = _agent.Calls.Count;
        Assert.Equal(HostRuntimeException.Unknown,
            (await Assert.ThrowsAsync<HostRuntimeException>(() => host.Runtimes.StartAsync("never-reported"))).Code);
        Assert.Equal(HostRuntimeException.Unknown,
            (await Assert.ThrowsAsync<HostRuntimeException>(() => host.Runtimes.StartAsync("bad id"))).Code);
        Assert.Equal(calls, _agent.Calls.Count);
    }

    [Fact]
    public void Only_the_known_attached_session_runtime_reports_the_versioned_capability()
    {
        var report = RuntimeNodeHandler.ComfyUi("sessions", managed: "attach", status: "running");
        report["kind"] = "ai-session.v1";
        report["capabilities"] = new JsonArray("ai-session.v1", "image-gen");

        var runtime = RemoteRuntimeReport.Parse(System.Text.Json.JsonSerializer.SerializeToElement(report));
        Assert.NotNull(runtime);
        Assert.Equal(["ai-session.v1"], runtime.Capabilities);

        report["managed"] = "spawn";
        Assert.Null(RemoteRuntimeReport.Parse(System.Text.Json.JsonSerializer.SerializeToElement(report)));

        report["managed"] = "attach";
        report["kind"] = "comfyui";
        Assert.Equal(["image-gen"],
            RemoteRuntimeReport.Parse(System.Text.Json.JsonSerializer.SerializeToElement(report))!.Capabilities);
    }

    [Fact]
    public async Task Malformed_or_oversized_pushes_are_dropped()
    {
        var host = await LinkAsync();
        var session = _piped!.Client.Session!;

        await session.NotifyAsync("runtime.changed", new JsonObject { ["id"] = "comfyui-1", ["status"] = "running" });
        var huge = RuntimeNodeHandler.ComfyUi(status: "running");
        huge["statusDetail"] = new string('x', RemoteRuntimeReport.MaxEntryBytes);
        await session.NotifyAsync("runtime.changed", huge);
        await session.NotifyAsync("runtime.changed", RuntimeNodeHandler.ComfyUi("comfyui-2", port: 8189));
        await WaitAsync(() => host.Runtimes.Status("comfyui-2") is not null, "the well-formed push");

        Assert.Equal(BackendStatus.Stopped, host.Runtimes.Status("comfyui-1")!.Status);
    }

    [Fact]
    public async Task Offline_the_runtimes_stay_known_and_calls_fail_as_node_offline()
    {
        var host = await LinkAsync();
        await _piped!.DisposeAsync();
        _piped = null;
        await WaitAsync(() => _registry.Find(_node)?.Status == NodeStatus.Offline, "the link to close");

        Assert.Equal(NodeUnavailableReasons.Offline, Assert.Throws<NodeUnavailableException>(() => _registry.HostFor(_node)).Reason);
        Assert.Equal(NodeUnavailableReasons.Offline,
            (await Assert.ThrowsAsync<NodeUnavailableException>(() => host.Runtimes.StartAsync("comfyui-1"))).Reason);
        var offline = await Assert.ThrowsAsync<ForwardException>(() =>
            host.Network.CreateServiceClient("http://127.0.0.1:8188", TimeSpan.FromSeconds(5)).GetAsync("http://127.0.0.1:8188/blob/1"));
        Assert.Equal(ForwardErrorCodes.NodeOffline, offline.Code);
        Assert.Equal("comfyui-1", Assert.Single(_registry.RuntimesOf(_node)).Id);
    }

    [Fact]
    public void A_nodes_runtimes_survive_a_restart_through_the_cache_and_are_parsed_again()
    {
        var directory = Path.Combine(Path.GetTempPath(), "redcompute-remote-host-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var first = Registry(directory);
            first.ApplyKernelNodes([Kernel(_node)]);
            first.LinkUp(_node, new NodeLinkState("s1", Now, Now, null, null, 1, RuntimeNodeHandler.Phase4Features, "0.2.0", false, null));
            Assert.True(first.ApplyRuntimes(_node, RemoteRuntimeReport.ParseList(
                System.Text.Json.JsonSerializer.SerializeToElement(new JsonArray(RuntimeNodeHandler.ComfyUi(status: "running"))))));
            Assert.False(first.ApplyRuntimes(_node, first.RuntimesOf(_node)));

            var second = Registry(directory);

            var runtime = Assert.Single(second.RuntimesOf(_node));
            Assert.Equal(("comfyui-1", BackendStatus.Running, "cuda:0"), (runtime.Id, runtime.Status, runtime.Accelerator));
            Assert.Equal(["image-gen"], runtime.Capabilities);
            Assert.Equal(NodeStatus.Offline, second.Find(_node)!.Status);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }
}
