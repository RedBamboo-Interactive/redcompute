using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RedCompute.Core.Providers;
using RedCompute.Node.Runtimes;
using RedCompute.NodeLink;
using RedCompute.NodeLink.Forward;
using RedCompute.PluginSdk.Hosting;
using RedCompute.Tests;
using Xunit;

namespace RedCompute.Node.Tests;

/// <summary>
/// The agent's runtimes over a Phase 4 link (design 2.4): the owner's registrations, <c>runtime.v1</c> with exactly
/// its parameters, the forward to a running runtime on its allowlist (a stand-in ComfyUI on Kestrel), the managed
/// lifecycle under Kept containment with adoption, and every stop: kill-all, revoke, unpair.
/// </summary>
public sealed class RuntimeAgentTests : IAsyncLifetime
{
    private const string Attached = "comfy-a";
    private const string Managed = "comfy-m";

    private readonly NodeHome _home = new("rt");
    private readonly CancellationTokenSource _stop = new();
    private FakeComfy _comfy = null!;
    private LinkedAgent _linked = null!;

    public async Task InitializeAsync()
    {
        _comfy = await FakeComfy.StartAsync();
        var registry = new RuntimeRegistry(_home.Host, _home.Paths);
        registry.Add(new RuntimeRegistration
        {
            Id = Attached, Kind = RuntimeKinds.ComfyUi, Label = "Desktop ComfyUI", Managed = HostRuntimeManagement.Attach, Port = _comfy.Port,
            Accelerator = "cuda:0", AcceptedGraphsRunInstalledNodes = true,
        });
        registry.Add(new RuntimeRegistration
        {
            Id = Managed, Kind = RuntimeKinds.ComfyUi, Managed = HostRuntimeManagement.Spawn, Port = FreePort(),
            ServerPath = Path.Combine(_home.Root, "SECRET-COMFY-PATH"), ServerArgs = "--SECRET-ARGS", AcceptedGraphsRunInstalledNodes = true,
        });
        _linked = await LinkedAgent.StartAsync(_home, new ScriptedProbes(), _stop.Token, serverFeatures: LinkedAgent.Phase4Features,
            runtimeSpec: Sleeper, runtimeProbe: ProbeAsync);
        await Wait.UntilAsync(() => _linked.Agent.Runtimes.Status(Attached)?.Status == BackendStatus.Running, "the attached runtime up");
    }

    public async Task DisposeAsync()
    {
        await _linked.Agent.Runtimes.StopAllManagedAsync("test end");
        _stop.Cancel();
        await _linked.DisposeAsync();
        await _comfy.DisposeAsync();
        _home.Dispose();
    }

    /// <summary>A managed runtime here is a sleeping process; the attached one is probed for real over HTTP.</summary>
    internal static HostSpawnSpec Sleeper(RuntimeRegistration _)
    {
        var start = TestProcesses.Sleep(300);
        return new HostSpawnSpec { FileName = start.FileName, ArgumentList = start.ArgumentList.ToList(), Containment = ContainmentMode.None, OwnerLabel = "comfyui" };
    }

    private static readonly HttpClient Probe = new() { Timeout = TimeSpan.FromSeconds(2) };

    internal static async Task<bool> ProbeAsync(RuntimeRegistration runtime, CancellationToken ct)
    {
        if (runtime.Managed == HostRuntimeManagement.Spawn) return true;
        try
        {
            using var response = await Probe.GetAsync($"http://127.0.0.1:{runtime.Port}/system_stats", ct);
            return response.IsSuccessStatusCode;
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException)
        {
            return false;
        }
    }

    internal static int FreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    internal static int PidOf(NodeHome home, string id)
    {
        var state = JsonNode.Parse(File.ReadAllText(Path.Combine(home.Paths.RuntimeState, id + ".json")))!.AsObject();
        return state.First(p => p.Key.Equals("pid", StringComparison.OrdinalIgnoreCase)).Value!.GetValue<int>();
    }

    internal static bool Alive(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private ForwardClient Forward() => new(() => _linked.ServerSession);

    private HttpClient Http(string runtime = Attached)
        => new(Forward().CreateHandler(runtime)) { BaseAddress = new Uri($"http://{runtime}.node.leaf-node.invalid/") };

    // ---------------------------------------------------------------- runtime.v1

    [Fact]
    public async Task The_server_lists_the_runtimes_without_paths_or_arguments()
    {
        var list = await _linked.ServerSession.CallAsync("runtime.list");
        var raw = list.GetRawText();
        Assert.DoesNotContain("SECRET", raw);
        Assert.DoesNotContain(_home.Root.Replace("\\", "\\\\"), raw);
        var entries = list.EnumerateArray().ToDictionary(e => e.GetProperty("id").GetString()!);
        Assert.Equal(["comfy-a", "comfy-m"], entries.Keys.Order());
        var attached = entries[Attached];
        Assert.Equal("attach", attached.GetProperty("managed").GetString());
        Assert.Equal("running", attached.GetProperty("status").GetString());
        Assert.Equal("cuda:0", attached.GetProperty("accelerator").GetString());
        Assert.Equal("image-gen", attached.GetProperty("capabilities")[0].GetString());
        Assert.Equal(_comfy.Port, attached.GetProperty("ports")[0].GetInt32());
        Assert.Equal("Desktop ComfyUI", attached.GetProperty("label").GetString());
        Assert.Equal("stopped", entries[Managed].GetProperty("status").GetString());
        Assert.Equal(
            ["accelerator", "capabilities", "enabled", "id", "kind", "label", "managed", "ports", "startedAt", "status", "statusDetail", "version"],
            attached.EnumerateObject().Select(p => p.Name).Order());
    }

    [Theory]
    [InlineData("runtime.list", "{\"all\":true}")]
    [InlineData("runtime.start", "{\"id\":\"comfy-m\",\"argv\":[\"sh\",\"-c\",\"id\"]}")]
    [InlineData("runtime.start", "{\"id\":\"comfy-m\",\"fileName\":\"/bin/sh\"}")]
    [InlineData("runtime.start", "{\"id\":\"comfy-m\",\"env\":{\"PATH\":\"/tmp\"}}")]
    [InlineData("runtime.start", "{\"id\":\"comfy-m\",\"cwd\":\"/\"}")]
    [InlineData("runtime.start", "{\"id\":\"comfy-m\",\"extraArgs\":\"--listen 0.0.0.0\"}")]
    [InlineData("runtime.start", "{}")]
    [InlineData("runtime.start", "{\"id\":\"../x\"}")]
    [InlineData("runtime.start", "{\"id\":5}")]
    [InlineData("runtime.start", "[\"comfy-m\"]")]
    [InlineData("runtime.stop", "{\"id\":\"comfy-m\",\"graceSeconds\":61}")]
    [InlineData("runtime.stop", "{\"id\":\"comfy-m\",\"graceSeconds\":\"5\"}")]
    [InlineData("runtime.stop", "{\"id\":\"comfy-m\",\"graceSeconds\":1.5}")]
    [InlineData("runtime.logs", "{\"id\":\"comfy-m\",\"tailLines\":201}")]
    [InlineData("runtime.logs", "{\"id\":\"comfy-m\"}")]
    [InlineData("runtime.logs", "{\"id\":\"comfy-m\",\"tailLines\":10,\"path\":\"/etc/passwd\"}")]
    [InlineData("runtime.status", "{\"id\":\"comfy-m\",\"id2\":\"x\"}")]
    public async Task Parameters_are_exactly_the_listed_members(string method, string parameters)
    {
        var error = await Assert.ThrowsAsync<LinkRpcException>(() => _linked.ServerSession.CallAsync(method, JsonNode.Parse(parameters)));
        Assert.Equal(LinkErrorCodes.InvalidParams, error.Code);
        Assert.Null(_linked.Agent.Runtimes.Status(Managed)!.StartedAt);
    }

    [Fact]
    public async Task Start_and_stop_refuse_what_the_agent_does_not_manage()
    {
        Assert.Equal(HostRuntimeException.NotManaged, (await Assert.ThrowsAsync<LinkRpcException>(() =>
            _linked.ServerSession.CallAsync("runtime.start", new JsonObject { ["id"] = Attached }))).Code);
        Assert.Equal(HostRuntimeException.NotManaged, (await Assert.ThrowsAsync<LinkRpcException>(() =>
            _linked.ServerSession.CallAsync("runtime.stop", new JsonObject { ["id"] = Attached }))).Code);
        Assert.Equal(HostRuntimeException.Unknown, (await Assert.ThrowsAsync<LinkRpcException>(() =>
            _linked.ServerSession.CallAsync("runtime.start", new JsonObject { ["id"] = "nope" }))).Code);
        Assert.Equal(HostRuntimeException.Unknown, (await Assert.ThrowsAsync<LinkRpcException>(() =>
            _linked.ServerSession.CallAsync("runtime.status", new JsonObject { ["id"] = "nope" }))).Code);

        new RuntimeRegistry(_home.Host, _home.Paths).SetEnabled(Managed, false);
        await _linked.Agent.Runtimes.ReloadAsync();
        Assert.Equal(HostRuntimeException.Disabled, (await Assert.ThrowsAsync<LinkRpcException>(() =>
            _linked.ServerSession.CallAsync("runtime.start", new JsonObject { ["id"] = Managed }))).Code);
    }

    [Fact]
    public async Task A_managed_runtime_starts_reports_its_changes_and_stops_on_request()
    {
        var started = await _linked.ServerSession.CallAsync("runtime.start", new JsonObject { ["id"] = Managed });
        Assert.Equal("starting", started.GetProperty("status").GetString());
        var pid = PidOf(_home, Managed);
        Assert.True(Alive(pid));
        await Wait.UntilAsync(() => _linked.Agent.Runtimes.Status(Managed)?.Status == BackendStatus.Running, "running");
        await Wait.UntilAsync(() => _linked.Server.RuntimeChanges.Any(c => c["id"]!.GetValue<string>() == Managed
            && c["status"]!.GetValue<string>() == "running"), "the runtime.changed push");

        // A second start of a live runtime is the same runtime, not a second process.
        await _linked.ServerSession.CallAsync("runtime.start", new JsonObject { ["id"] = Managed });
        Assert.Equal(pid, PidOf(_home, Managed));

        var stopped = await _linked.ServerSession.CallAsync("runtime.stop", new JsonObject { ["id"] = Managed, ["graceSeconds"] = 5 });
        Assert.Equal("stopped", stopped.GetProperty("status").GetString());
        await Wait.UntilAsync(() => !Alive(pid), "the process gone");
        Assert.False(File.Exists(Path.Combine(_home.Paths.RuntimeState, Managed + ".json")));
        var audit = _home.Environment().Audit.Read().Where(l => l["kind"]!.GetValue<string>() == "runtime" && l["name"]!.GetValue<string>() == Managed)
            .Select(l => l["outcome"]!.GetValue<string>()).ToList();
        Assert.Contains("started", audit);
        Assert.Contains("stopped", audit);
        Assert.Contains(_home.Environment().Audit.Read(), l => l["name"]!.GetValue<string>() == "runtime.start" && l["kind"]!.GetValue<string>() == "request");
    }

    [Fact]
    public async Task A_runtime_that_exits_on_its_own_is_in_error_and_says_so()
    {
        var registry = new RuntimeRegistry(_home.Host, _home.Paths);
        await using var manager = new RuntimeManager(_home.Environment(), _ =>
        {
            var start = TestProcesses.Sleep(1);
            return new HostSpawnSpec { FileName = start.FileName, ArgumentList = start.ArgumentList.ToList(), Containment = ContainmentMode.None, OwnerLabel = "comfyui" };
        }, (_, _) => Task.FromResult(false));
        manager.Load();
        await manager.StartAsync(Managed);
        await Wait.UntilAsync(() => manager.Status(Managed)!.Status == BackendStatus.Error, "the error", timeoutMs: 20_000);
        Assert.StartsWith("It exited", manager.Status(Managed)!.StatusDetail);
        Assert.Contains(manager.Logs(Managed, 10), line => line.Contains("exited"));
        Assert.NotNull(registry.Load().Single(r => r.Id == Managed));
    }

    // ---------------------------------------------------------------- the forward

    [Fact]
    public async Task A_get_reaches_the_runtime_as_the_node_rebuilds_it()
    {
        using var http = Http();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/view?filename=ComfyUI_00001_.png&type=output&subfolder=");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "leaf-secret");
        request.Headers.Add("Cookie", "leaf=1");
        request.Headers.Add("X-Forwarded-For", "10.9.8.7");
        request.Headers.Add("X-Trace", "t1");
        using var response = await http.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("PNG:ComfyUI_00001_.png", await response.Content.ReadAsStringAsync());
        Assert.Equal("image/png", response.Content.Headers.ContentType?.MediaType);
        Assert.False(response.Headers.Contains("Set-Cookie"), "a runtime's cookie never reaches the server");

        var seen = _comfy.Requests.Last(r => r.Target.StartsWith("/view", StringComparison.Ordinal));
        Assert.Equal("GET", seen.Method);
        Assert.Equal("/view?filename=ComfyUI_00001_.png&type=output&subfolder=", seen.Target);
        Assert.Equal($"127.0.0.1:{_comfy.Port}", seen.Headers["host"]);
        Assert.Equal("close", seen.Headers["connection"]);
        Assert.Equal("t1", seen.Headers["x-trace"]);
        foreach (var header in new[] { "authorization", "cookie", "x-forwarded-for" }) Assert.False(seen.Headers.ContainsKey(header), header);
        Assert.Contains(_home.Environment().Audit.Read(), l => l["kind"]!.GetValue<string>() == "forward" && l["name"]!.GetValue<string>() == "/view");
    }

    [Fact]
    public async Task A_prompt_post_streams_its_body_to_the_runtime()
    {
        using var http = Http();
        var pad = new byte[3 * 1024 * 1024 + 17];
        new Random(5).NextBytes(pad);
        var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { prompt = new { pad = Convert.ToBase64String(pad) }, client_id = "c1" }));
        using var response = await http.PostAsync("/prompt", new ByteArrayContent(body) { Headers = { ContentType = new("application/json") } });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var seen = _comfy.Requests.Last(r => r.Target == "/prompt");
        Assert.Equal(body.Length, seen.BodyBytes);
        Assert.Equal(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(body)), seen.BodySha256);
    }

    [Fact]
    public async Task An_image_upload_passes_the_guard_and_a_bad_one_never_reaches_the_runtime()
    {
        using var http = Http();
        var image = new byte[200_000];
        new Random(9).NextBytes(image);
        using (var form = new MultipartFormDataContent("leafbound"))
        {
            form.Add(new ByteArrayContent(image), "image", "input.png");
            form.Add(new StringContent("true"), "overwrite");
            using var ok = await http.PostAsync("/upload/image", form);
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        }
        Assert.Single(_comfy.Requests, r => r.Target == "/upload/image");

        using (var bad = new MultipartFormDataContent("leafbound"))
        {
            bad.Add(new ByteArrayContent(image), "image", "../../custom_nodes/evil.py");
            var error = await Assert.ThrowsAnyAsync<HttpRequestException>(() => http.PostAsync("/upload/image", bad));
            Assert.Equal(ForwardErrorCodes.RouteNotAllowed, FindCode(error));
        }
        await Task.Delay(300);
        Assert.Equal(1, _comfy.Requests.Count(r => r.Target == "/upload/image" && r.BodyBytes > 0));
    }

    /// <summary>Posts a prompt through the forward and answers the id the runtime assigned.</summary>
    private static async Task<string> SubmitAsync(HttpClient http)
    {
        using var response = await http.PostAsync("/prompt", new StringContent("{\"prompt\":{},\"client_id\":\"c1\"}", Encoding.UTF8, "application/json"));
        response.EnsureSuccessStatusCode();
        return JsonNode.Parse(await response.Content.ReadAsStringAsync())!["prompt_id"]!.GetValue<string>();
    }

    private async Task<string?> PostAsync(HttpClient http, string target, string? json)
    {
        var before = _comfy.Requests.Count(r => r.Target == target);
        try
        {
            using var response = await http.PostAsync(target, json is null ? null : new StringContent(json, Encoding.UTF8, "application/json"));
            Assert.Equal(before + 1, _comfy.Requests.Count(r => r.Target == target));
            return null;
        }
        catch (HttpRequestException error)
        {
            await Task.Delay(100);
            Assert.Equal(before, _comfy.Requests.Count(r => r.Target == target && r.BodyBytes > 0 || r.Target == target && json is null));
            return FindCode(error);
        }
    }

    [Fact]
    public async Task Interrupt_cancel_and_dequeue_act_only_on_prompts_the_server_submitted_here()
    {
        using var http = Http();
        var mine = await SubmitAsync(http);

        // The id the runtime answered is the one the server may stop: targeted, never global.
        Assert.Null(await PostAsync(http, "/interrupt", $"{{\"prompt_id\":\"{mine}\"}}"));
        Assert.Null(await PostAsync(http, $"/api/jobs/{mine}/cancel", "{}"));
        Assert.Null(await PostAsync(http, "/queue", $"{{\"delete\":[\"{mine}\"]}}"));

        // The owner's prompts (any id this forward never answered), a global interrupt, and no body at all: refused.
        Assert.Equal(ForwardErrorCodes.RouteNotAllowed, await PostAsync(http, "/interrupt", "{\"prompt_id\":\"owners-prompt\"}"));
        Assert.Equal(ForwardErrorCodes.RouteNotAllowed, await PostAsync(http, "/interrupt", "{}"));
        Assert.Equal(ForwardErrorCodes.RouteNotAllowed, await PostAsync(http, "/interrupt", null));
        Assert.Equal(ForwardErrorCodes.RouteNotAllowed, await PostAsync(http, "/api/jobs/owners-prompt/cancel", "{}"));
        Assert.Equal(ForwardErrorCodes.RouteNotAllowed, await PostAsync(http, "/queue", "{\"delete\":[\"owners-prompt\"]}"));
        // Nor may a prompt take an id of its choosing (one it read from /queue, say).
        Assert.Equal(ForwardErrorCodes.RouteNotAllowed, await PostAsync(http, "/prompt", "{\"prompt\":{},\"prompt_id\":\"owners-prompt\"}"));
        // An id answered for another runtime is not this one's.
        Assert.False(_linked.Agent.ForwardTarget.Submitted.Contains(Managed, mine));
    }

    [Fact]
    public async Task An_upload_reaches_the_runtime_with_the_one_content_type_the_guard_parsed()
    {
        using var http = Http();
        using var form = new MultipartFormDataContent("leaf-bound.1");
        form.Add(new ByteArrayContent([1, 2, 3]), "image", "input.png");
        using var ok = await http.PostAsync("/upload/image", form);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var seen = _comfy.Requests.Last(r => r.Target == "/upload/image");
        Assert.Equal("multipart/form-data; boundary=\"leaf-bound.1\"", seen.Headers["content-type"]);

        // Two Content-Types are refused at OPEN: the runtime never sees which one it would have read.
        var target = new RuntimeForwardTarget(_linked.Agent.Runtimes);
        var twice = ForwardOpen.Http(Attached, "POST", "/upload/image", "",
            [new("content-type", "multipart/form-data; boundary=a"), new("content-type", "multipart/form-data; boundary=b")], 100);
        Assert.Equal(ForwardErrorCodes.RouteNotAllowed, target.Decide(twice).RefusalCode);
        var json = ForwardOpen.Http(Attached, "POST", "/prompt", "", [new("content-type", "application/json"), new("Content-Type", "text/plain")], 2);
        Assert.Equal(ForwardErrorCodes.RouteNotAllowed, target.Decide(json).RefusalCode);
    }

    [Theory]
    [InlineData("GET", "/manager/queue/start")]
    [InlineData("POST", "/api/manager/reboot")]
    [InlineData("GET", "/api/userdata/comfy.settings.json")]
    [InlineData("GET", "/view?filename=../../models/x.safetensors&type=output")]
    [InlineData("GET", "/view?filename=owners-photo.png&type=input")]
    [InlineData("GET", "/object_info")]
    [InlineData("POST", "/customnode/install")]
    [InlineData("GET", "/internal/logs")]
    public async Task Routes_off_the_allowlist_are_refused_and_never_reach_the_runtime(string method, string target)
    {
        using var http = Http();
        var before = _comfy.Requests.Count;
        var error = await Assert.ThrowsAnyAsync<HttpRequestException>(() => http.SendAsync(new HttpRequestMessage(new HttpMethod(method), target)));
        Assert.Equal(ForwardErrorCodes.RouteNotAllowed, FindCode(error));
        Assert.Equal(before, _comfy.Requests.Count);
        Assert.Contains(_home.Environment().Audit.Read(), l => l["kind"]!.GetValue<string>() == "forward"
            && l["outcome"]!.GetValue<string>() == ForwardErrorCodes.RouteNotAllowed);
    }

    [Fact]
    public async Task Only_a_registered_running_runtime_is_reached()
    {
        using var unknown = Http("comfy-x");
        Assert.Equal(ForwardErrorCodes.RuntimeUnknown, FindCode(await Assert.ThrowsAnyAsync<HttpRequestException>(() => unknown.GetAsync("/system_stats"))));
        using var stopped = Http(Managed);
        Assert.Equal(ForwardErrorCodes.RuntimeNotRunning, FindCode(await Assert.ThrowsAnyAsync<HttpRequestException>(() => stopped.GetAsync("/system_stats"))));
    }

    [Fact]
    public async Task The_websocket_reaches_the_runtime_with_its_client_id()
    {
        using var socket = await Forward().ConnectWebSocketAsync(Attached, "/ws", "clientId=leaf-1");
        await socket.SendAsync(Encoding.UTF8.GetBytes("{\"hello\":1}"), WebSocketMessageType.Text, true, CancellationToken.None);
        var buffer = new byte[1024];
        var result = await socket.ReceiveAsync(buffer, CancellationToken.None);
        Assert.Equal("{\"hello\":1}", Encoding.UTF8.GetString(buffer, 0, result.Count));
        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
        Assert.Contains(_comfy.Requests, r => r.Method == "WS" && r.Target == "/ws?clientId=leaf-1");
        Assert.Equal(ForwardErrorCodes.RouteNotAllowed,
            (await Assert.ThrowsAsync<ForwardException>(() => Forward().ConnectWebSocketAsync(Attached, "/ws", "clientId=a/b"))).Code);
    }

    // ---------------------------------------------------------------- every stop

    [Fact]
    public async Task Kill_all_ends_every_forward_and_stops_managed_runtimes_but_not_attached_ones()
    {
        await _linked.ServerSession.CallAsync("runtime.start", new JsonObject { ["id"] = Managed });
        var pid = PidOf(_home, Managed);
        using var http = Http();
        using var response = await http.GetAsync("/view?filename=slow.bin", HttpCompletionOption.ResponseHeadersRead);
        var body = await response.Content.ReadAsStreamAsync();
        await Wait.UntilAsync(() => _linked.Agent.Forward.OpenExchanges == 1, "the download");

        var (channels, runtimes) = await _linked.Agent.KillAllAsync();
        Assert.True(channels >= 1);
        Assert.Equal(1, runtimes);
        var error = await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            var buffer = new byte[64 * 1024];
            while (await body.ReadAsync(buffer) > 0) { }
        });
        Exception? stream = error;
        while (stream is not null and not ForwardStreamException) stream = stream.InnerException;
        Assert.True(stream is ForwardStreamException { Code: "killed_by_owner" }, error.ToString());
        await Wait.UntilAsync(() => !Alive(pid), "the managed runtime gone");
        Assert.Equal(BackendStatus.Stopped, _linked.Agent.Runtimes.Status(Managed)!.Status);
        Assert.Equal(BackendStatus.Running, _linked.Agent.Runtimes.Status(Attached)!.Status);
        await Wait.UntilAsync(() => _linked.Agent.Forward.OpenExchanges == 0, "the forward table empty");
    }

    [Fact]
    public async Task A_revoke_stops_every_managed_runtime()
    {
        await _linked.ServerSession.CallAsync("runtime.start", new JsonObject { ["id"] = Managed });
        var pid = PidOf(_home, Managed);
        await _linked.LinkServer.RevokeAsync(NodeAgentLinkTests.NodeId);
        await Wait.UntilAsync(() => !Alive(pid), "the managed runtime gone after the revoke");
        Assert.Contains(_home.Environment().Audit.Read(), l => l["kind"]!.GetValue<string>() == "runtime" && l["outcome"]!.GetValue<string>() == "stopped");
    }

    [Fact]
    public async Task The_owner_drives_the_running_agent_with_leaf_node_runtime()
    {
        // The running agent's control channel, as leaf-node run serves it.
        await using var control = new Control.ControlServer(_home.Paths,
            (request, token) => NodeCommands.HandleControlAsync(_linked.Agent, request, () => Task.CompletedTask, token), _home.Environment().Log);
        control.Start();
        var output = new StringWriter();
        var commands = new NodeCommands(_home.Environment(), output);

        Assert.Equal(0, await commands.RunAsync(["runtime", "list"], CancellationToken.None));
        Assert.True(output.ToString().Contains($"comfy-a  comfyui  attach  127.0.0.1:{_comfy.Port}  enabled  running"), output.ToString());
        Assert.True(output.ToString().Contains("comfy-m  comfyui  spawn"), output.ToString());

        Assert.Equal(0, await commands.RunAsync(["runtime", "start", Managed], CancellationToken.None));
        var pid = PidOf(_home, Managed);
        await Wait.UntilAsync(() => _linked.Agent.Runtimes.Status(Managed)?.Status == BackendStatus.Running, "running");
        Assert.NotEqual(0, await commands.RunAsync(["runtime", "start", Attached], CancellationToken.None));
        Assert.Contains("attach mode", output.ToString());

        // Disabling a running runtime stops it, and the server can no longer start or reach it.
        Assert.Equal(0, await commands.RunAsync(["runtime", "disable", Managed], CancellationToken.None));
        await Wait.UntilAsync(() => !Alive(pid), "the disabled runtime stopped");
        Assert.Equal(HostRuntimeException.Disabled, (await Assert.ThrowsAsync<LinkRpcException>(() =>
            _linked.ServerSession.CallAsync("runtime.start", new JsonObject { ["id"] = Managed }))).Code);

        // Removing the attached one: the forward refuses it at once.
        Assert.Equal(0, await commands.RunAsync(["runtime", "remove", Attached], CancellationToken.None));
        using var http = Http();
        Assert.Equal(ForwardErrorCodes.RuntimeUnknown, FindCode(await Assert.ThrowsAnyAsync<HttpRequestException>(() => http.GetAsync("/system_stats"))));
    }

    private static string? FindCode(Exception error)
    {
        for (Exception? e = error; e is not null; e = e.InnerException)
            if (e is ForwardException forward) return forward.Code;
        return null;
    }
}

/// <summary>The managed lifecycle without a link: adoption across an agent restart, and the owner's commands.</summary>
public sealed class RuntimeLifecycleTests : IDisposable
{
    private readonly NodeHome _home = new("rtl");

    public void Dispose() => _home.Dispose();

    private RuntimeRegistration Spawned(string id = "comfy-m") => new()
    {
        Id = id, Kind = RuntimeKinds.ComfyUi, Managed = HostRuntimeManagement.Spawn, Port = RuntimeAgentTests.FreePort(),
        ServerPath = _home.Root, AcceptedGraphsRunInstalledNodes = true,
    };

    [Fact]
    public async Task A_restarted_agent_adopts_what_it_started_and_never_starts_a_second_one()
    {
        new RuntimeRegistry(_home.Host, _home.Paths).Add(Spawned());
        var env = _home.Environment();
        int pid;
        await using (var first = new RuntimeManager(env, RuntimeAgentTests.Sleeper, (_, _) => Task.FromResult(true)))
        {
            first.Load();
            await first.StartAsync("comfy-m");
            pid = RuntimeAgentTests.PidOf(_home, "comfy-m");
        }
        // The agent stopped: the runtime is kept.
        Assert.True(RuntimeAgentTests.Alive(pid));

        await using var second = new RuntimeManager(env, _ => throw new InvalidOperationException("never spawn again"), (_, _) => Task.FromResult(true));
        try
        {
            second.Load();
            var adopted = second.Status("comfy-m")!;
            Assert.Equal(BackendStatus.Starting, adopted.Status);
            Assert.Equal("adopted after an agent restart", adopted.StatusDetail);
            await second.ProbeOnceAsync();
            Assert.Equal(BackendStatus.Running, second.Status("comfy-m")!.Status);
            await second.StartAsync("comfy-m"); // already up: no spawn
            Assert.Equal(pid, RuntimeAgentTests.PidOf(_home, "comfy-m"));
            Assert.Contains(_home.Environment().Audit.Read(), l => l["outcome"]!.GetValue<string>() == "adopted");
        }
        finally
        {
            Assert.Equal(1, await second.StopAllManagedAsync("test"));
        }
        await Wait.UntilAsync(() => !RuntimeAgentTests.Alive(pid), "the adopted runtime gone");
    }

    [Fact]
    public async Task A_state_file_whose_process_is_gone_is_forgotten()
    {
        new RuntimeRegistry(_home.Host, _home.Paths).Add(Spawned());
        Directory.CreateDirectory(_home.Paths.RuntimeState);
        File.WriteAllText(Path.Combine(_home.Paths.RuntimeState, "comfy-m.json"),
            JsonSerializer.Serialize(new { pid = 999_999, createdUtcTicks = 1L, startedAt = DateTimeOffset.UtcNow }));
        await using var manager = new RuntimeManager(_home.Environment(), RuntimeAgentTests.Sleeper, (_, _) => Task.FromResult(true));
        manager.Load();
        Assert.Equal(BackendStatus.Stopped, manager.Status("comfy-m")!.Status);
        Assert.False(File.Exists(Path.Combine(_home.Paths.RuntimeState, "comfy-m.json")));
    }

    private string ComfyFolder()
    {
        var folder = Path.Combine(_home.Root, "ComfyUI");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "main.py"), "# ComfyUI");
        return folder;
    }

    [Fact]
    public async Task The_owner_registers_lists_disables_and_removes_runtimes()
    {
        var env = _home.Environment();
        var output = new StringWriter();
        var commands = new NodeCommands(env, output);
        var comfy = ComfyFolder();
        var port = RuntimeAgentTests.FreePort();

        // A ComfyUI runs whatever node types are installed: the owner says so, or nothing is registered.
        Assert.NotEqual(0, await commands.RunAsync(["runtime", "add", "comfyui", "--attach", "http://127.0.0.1:8188"], CancellationToken.None));
        Assert.Contains("--accept-graphs-run-installed-nodes", output.ToString());
        Assert.Empty(new RuntimeRegistry(_home.Host, _home.Paths).Load());

        // Attach is to this machine only; a spawned ComfyUI needs its folder with main.py, and a free port.
        foreach (var args in new[]
        {
            new[] { "--attach", "http://192.168.1.20:8188" }, ["--attach", "https://127.0.0.1:8188"], ["--attach", "http://127.0.0.1:8188/manager"],
            ["--path", _home.Root], [], ["--path", comfy, "--port", "x"],
        })
        {
            output.GetStringBuilder().Clear();
            Assert.NotEqual(0, await commands.RunAsync(["runtime", "add", "comfyui", .. args, "--accept-graphs-run-installed-nodes"], CancellationToken.None));
        }
        var busy = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        busy.Start();
        try
        {
            var taken = ((System.Net.IPEndPoint)busy.LocalEndpoint).Port.ToString();
            output.GetStringBuilder().Clear();
            Assert.NotEqual(0, await commands.RunAsync(["runtime", "add", "comfyui", "--path", comfy, "--port", taken,
                "--accept-graphs-run-installed-nodes"], CancellationToken.None));
            Assert.Contains("already listens", output.ToString());
        }
        finally
        {
            busy.Stop();
        }

        output.GetStringBuilder().Clear();
        Assert.Equal(0, await commands.RunAsync(["runtime", "add", "comfyui", "--attach", "http://127.0.0.1:8188", "--label", "Desktop",
            "--gpu", "cuda:0", "--accept-graphs-run-installed-nodes"], CancellationToken.None));
        Assert.Contains("never start or stop it", output.ToString());
        Assert.Equal(0, await commands.RunAsync(["runtime", "add", "comfyui", "--path", comfy, "--port", port.ToString(), "--models-dir", comfy,
            "--extra-args=--lowvram --preview-method auto", "--accept-graphs-run-installed-nodes"], CancellationToken.None));
        // Inside WSL the folder is the distribution's: checked when it starts, not here.
        Assert.Equal(0, await commands.RunAsync(["runtime", "add", "comfyui", "--id", "wsl-comfy", "--wsl", "Ubuntu-24.04", "--path", "/home/me/ComfyUI",
            "--port", RuntimeAgentTests.FreePort().ToString(), "--accept-graphs-run-installed-nodes"], CancellationToken.None));
        var all = new RuntimeRegistry(_home.Host, _home.Paths).Load();
        Assert.Equal(["comfyui-1", "comfyui-2", "wsl-comfy"], all.Select(r => r.Id));
        Assert.Equal((HostRuntimeManagement.Attach, 8188, "cuda:0", "Desktop"), (all[0].Managed, all[0].Port, all[0].Accelerator, all[0].Label));
        Assert.Equal((HostRuntimeManagement.Spawn, "--lowvram --preview-method auto", comfy, comfy),
            (all[1].Managed, all[1].ServerArgs, all[1].ServerPath, all[1].ModelsDirectory));
        Assert.Equal("Ubuntu-24.04", all[2].WslDistro);

        // The same id or port twice, an unknown kind, a bad id.
        Assert.NotEqual(0, await commands.RunAsync(["runtime", "add", "comfyui", "--id", "comfyui-1", "--attach", "http://127.0.0.1:9000",
            "--accept-graphs-run-installed-nodes"], CancellationToken.None));
        Assert.NotEqual(0, await commands.RunAsync(["runtime", "add", "comfyui", "--attach", "http://127.0.0.1:8188",
            "--accept-graphs-run-installed-nodes"], CancellationToken.None));
        Assert.NotEqual(0, await commands.RunAsync(["runtime", "add", "shell", "--attach", "http://127.0.0.1:9001"], CancellationToken.None));
        Assert.NotEqual(0, await commands.RunAsync(["runtime", "add", "comfyui", "--id", "C/../x", "--attach", "http://127.0.0.1:9002",
            "--accept-graphs-run-installed-nodes"], CancellationToken.None));

        output.GetStringBuilder().Clear();
        Assert.Equal(0, await commands.RunAsync(["runtime", "list"], CancellationToken.None));
        Assert.Contains("comfyui-1  comfyui  attach  127.0.0.1:8188  enabled  agent not running", output.ToString());

        Assert.Equal(0, await commands.RunAsync(["runtime", "disable", "comfyui-2"], CancellationToken.None));
        Assert.False(new RuntimeRegistry(_home.Host, _home.Paths).Load().Single(r => r.Id == "comfyui-2").Enabled);
        Assert.Equal(0, await commands.RunAsync(["runtime", "remove", "comfyui-1"], CancellationToken.None));
        Assert.Equal(["comfyui-2", "wsl-comfy"], new RuntimeRegistry(_home.Host, _home.Paths).Load().Select(r => r.Id));
        Assert.NotEqual(0, await commands.RunAsync(["runtime", "remove", "comfyui-1"], CancellationToken.None));
        Assert.Equal(NodeExitCodes.NotRunning, await commands.RunAsync(["runtime", "start", "comfyui-2"], CancellationToken.None));

        var audit = env.Audit.Read().Where(l => l["kind"]!.GetValue<string>() == "local").Select(l => l["name"]!.GetValue<string>()).ToList();
        Assert.Contains("runtime-add", audit);
        Assert.Contains("runtime-disable", audit);
        Assert.Contains("runtime-remove", audit);
    }

    [Fact]
    public async Task Runtime_test_starts_probes_and_stops_again_without_an_agent()
    {
        new RuntimeRegistry(_home.Host, _home.Paths).Add(Spawned());
        var answers = false;
        var env = _home.Environment(runtimeSpec: RuntimeAgentTests.Sleeper, runtimeProbe: (_, _) => Task.FromResult(answers));
        var output = new StringWriter();

        Assert.Equal(NodeExitCodes.Failed, await new NodeCommands(env, output).RunAsync(["runtime", "test", "comfy-m", "--timeout", "2"],
            CancellationToken.None));
        Assert.Contains("did not answer", output.ToString());
        Assert.False(File.Exists(Path.Combine(_home.Paths.RuntimeState, "comfy-m.json")), "the test stopped what it started");

        answers = true;
        output.GetStringBuilder().Clear();
        Assert.Equal(0, await new NodeCommands(env, output).RunAsync(["runtime", "test", "comfy-m"], CancellationToken.None));
        Assert.Contains("answered GET /system_stats", output.ToString());
        Assert.False(File.Exists(Path.Combine(_home.Paths.RuntimeState, "comfy-m.json")));
        var outcomes = env.Audit.Read().Where(l => l["kind"]!.GetValue<string>() == "runtime").Select(l => l["outcome"]!.GetValue<string>()).ToList();
        Assert.Contains("test-failed", outcomes);
        Assert.Contains("test-passed", outcomes);
    }

    [Fact]
    public void A_registration_file_with_a_bad_entry_keeps_the_good_ones()
    {
        var registry = new RuntimeRegistry(_home.Host, _home.Paths);
        registry.Add(Spawned("good"));
        var file = File.ReadAllText(_home.Paths.RuntimesFile);
        var json = JsonNode.Parse(file)!.AsObject();
        var list = json.First(p => p.Key.Equals("runtimes", StringComparison.OrdinalIgnoreCase)).Value!.AsArray();
        var bad = list[0]!.DeepClone().AsObject();
        bad[bad.First(p => p.Key.Equals("id", StringComparison.OrdinalIgnoreCase)).Key] = "BAD ID";
        list.Add(bad);
        File.WriteAllText(_home.Paths.RuntimesFile, json.ToJsonString());
        Assert.Equal(["good"], registry.Load().Select(r => r.Id));
    }
}
