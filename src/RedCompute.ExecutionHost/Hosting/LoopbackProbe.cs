using System.Net;
using System.Net.Http;
using System.Net.Sockets;

namespace RedCompute.PluginSdk.Hosting;

/// <summary>
/// The HTTP handler for a status or health probe of a service on this machine. On Windows a connect to a
/// loopback port nobody listens on is refused only after about two seconds (the stack retries the SYN), and
/// <c>localhost</c> tries <c>::1</c> first while the suite listens on IPv4 only; a status poll that waits for that
/// waits two seconds per stopped provider. Here each connect attempt to a loopback address gives up after
/// <see cref="ConnectTimeout"/>, and <c>localhost</c> tries 127.0.0.1 before ::1 (each with that budget), so a
/// service listening on either still answers. Any other host (a WSL or LAN address, a container name) connects
/// exactly as a default handler does. Only for probes: job traffic keeps the default handler.
/// </summary>
public static class LoopbackProbe
{
    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromMilliseconds(250);

    public static HttpMessageHandler CreateHandler() => CreateHandler(ConnectTimeout);

    internal static HttpMessageHandler CreateHandler(TimeSpan connectTimeout) => new SocketsHttpHandler
    {
        ConnectCallback = (context, ct) => ConnectAsync(context.DnsEndPoint, connectTimeout, ct),
    };

    /// <summary>The loopback addresses to try for <paramref name="host"/>, in order; null when it is not a loopback host.</summary>
    internal static IPAddress[]? LoopbackAddresses(string host)
    {
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            return [IPAddress.Loopback, IPAddress.IPv6Loopback];
        var literal = host.StartsWith('[') && host.EndsWith(']') ? host[1..^1] : host;
        return IPAddress.TryParse(literal, out var address) && IPAddress.IsLoopback(address) ? [address] : null;
    }

    private static async ValueTask<Stream> ConnectAsync(DnsEndPoint endpoint, TimeSpan connectTimeout, CancellationToken ct)
    {
        if (LoopbackAddresses(endpoint.Host) is not { } addresses)
            return await ConnectOnceAsync(endpoint, null, ct);

        Exception? last = null;
        foreach (var address in addresses)
        {
            try { return await ConnectOnceAsync(new IPEndPoint(address, endpoint.Port), connectTimeout, ct); }
            catch (Exception ex) when (!ct.IsCancellationRequested) { last = ex; }
        }
        throw last ?? new HttpRequestException($"No loopback address for {endpoint.Host}");
    }

    private static async ValueTask<Stream> ConnectOnceAsync(EndPoint endpoint, TimeSpan? timeout, CancellationToken ct)
    {
        var socket = endpoint is IPEndPoint ip
            ? new Socket(ip.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
            : new Socket(SocketType.Stream, ProtocolType.Tcp);
        socket.NoDelay = true;
        using var deadline = timeout is { } budget ? CancellationTokenSource.CreateLinkedTokenSource(ct) : null;
        deadline?.CancelAfter(timeout!.Value);
        try
        {
            await socket.ConnectAsync(endpoint, deadline?.Token ?? ct);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch (OperationCanceledException) when (deadline is not null && !ct.IsCancellationRequested)
        {
            socket.Dispose();
            throw new HttpRequestException($"Connecting to {endpoint} took longer than {timeout!.Value.TotalMilliseconds:0} ms");
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
