using System.Collections.Concurrent;
using System.Net.Http;
using System.Net.WebSockets;
using RedCompute.PluginSdk.Hosting;
using RedCompute.PluginSdk.Hosting.Local;

namespace RedCompute.App.Tests;

/// <summary>
/// An execution host whose network behaves the way a remote node's will (Phase 4, design 2.2):
/// <see cref="IHostNetwork.ResolveServiceUri"/> returns a <c>.invalid</c> sentinel that never resolves,
/// and only the host's own handler and WebSocket reach the service (here a test server on loopback).
/// Code that builds its own <see cref="HttpClient"/> or <see cref="ClientWebSocket"/> from a resolved
/// Uri fails, visibly, on the sentinel name; everything else is node zero's.
/// </summary>
internal sealed class SentinelNetworkHost : IExecutionHost
{
    public const string SentinelHost = "svc.node-01jcz0000000000000000sent.leaf-node.invalid";

    private readonly LocalExecutionHost _local = new("node_sentinel");

    public SentinelNetworkHost(bool canLaunchLocalBackends = true)
    {
        Network = new SentinelNetwork(_local.Network);
        Platform = new LaunchingPlatform(_local.Platform, canLaunchLocalBackends);
    }

    public SentinelNetwork Recorder => (SentinelNetwork)Network;

    public string NodeId => _local.NodeId;
    public HostPath Path(string value) => _local.Path(value);
    public IHostPathRules PathRules => _local.PathRules;
    public IHostProcesses Processes => _local.Processes;
    public IHostContainment Containment => _local.Containment;
    public IHostFileSystem Files => _local.Files;
    public IHostNetwork Network { get; }
    public IHostPlatform Platform { get; }
    public IHostHardware Hardware => _local.Hardware;
    public IHostQueries Queries => _local.Queries;

    internal sealed class SentinelNetwork(IHostNetwork local) : IHostNetwork
    {
        /// <summary>The host-local Uris handlers were made for.</summary>
        public ConcurrentQueue<Uri> Handlers { get; } = new();

        /// <summary>The host-local Uris WebSockets were opened to.</summary>
        public ConcurrentQueue<Uri> Sockets { get; } = new();

        /// <summary>Each request a handler carried, as "METHOD host path".</summary>
        public ConcurrentQueue<string> Sent { get; } = new();

        public bool IsListening(int port) => local.IsListening(port);

        public Uri ResolveServiceUri(Uri hostLocalUri)
            => new UriBuilder(hostLocalUri) { Host = SentinelHost }.Uri;

        public Task<IHostPortForward> ReverseForwardAsync(Uri serverLocalUri, CancellationToken ct = default)
            => local.ReverseForwardAsync(serverLocalUri, ct);

        public HttpMessageHandler CreateServiceHandler(Uri hostLocalUri)
        {
            Handlers.Enqueue(hostLocalUri);
            return new ToLoopback(this, hostLocalUri.Port);
        }

        public async Task<WebSocket> ConnectWebSocketAsync(Uri hostLocalUri, CancellationToken ct = default)
        {
            Sockets.Enqueue(hostLocalUri);
            if (hostLocalUri.Host == SentinelHost)
                throw new InvalidOperationException("a WebSocket was asked for a resolved Uri, not the host's own");
            return await local.ConnectWebSocketAsync(hostLocalUri, ct);
        }

        /// <summary>Carries a request for the sentinel to the service's port on loopback.</summary>
        private sealed class ToLoopback(SentinelNetwork owner, int port) : DelegatingHandler(new HttpClientHandler())
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var uri = request.RequestUri!;
                owner.Sent.Enqueue($"{request.Method} {uri.Host} {uri.AbsolutePath}");
                if (uri.Host != SentinelHost)
                    throw new HttpRequestException($"the host's handler was given '{uri.Host}', not the resolved service");
                request.RequestUri = new UriBuilder(uri) { Host = "127.0.0.1", Port = port }.Uri;
                return base.SendAsync(request, cancellationToken);
            }
        }
    }

    private sealed class LaunchingPlatform(IHostPlatform inner, bool canLaunch) : IHostPlatform
    {
        public HostPlatformInfo Info => inner.Info;
        public bool IsWindows => inner.IsWindows;
        public bool IsLinux => inner.IsLinux;
        public bool CanLaunchLocalBackends => canLaunch;
        public string? GetEnvironmentVariable(string name) => inner.GetEnvironmentVariable(name);
        public string GetFolderPath(Environment.SpecialFolder folder) => inner.GetFolderPath(folder);
        public string ComputeDataRoot => inner.ComputeDataRoot;
    }
}
