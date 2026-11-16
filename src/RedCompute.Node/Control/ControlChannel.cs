using System.IO.Pipes;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace RedCompute.Node.Control;

/// <summary>
/// The local control channel between the CLI verbs and the running agent (design 2.2), for the same
/// OS user only: on Windows the named pipe <c>leaf-node-&lt;SID&gt;</c> created with
/// <see cref="PipeOptions.CurrentUserOnly"/> (its ACL admits only this user, and the client checks the
/// server is this user's); on Linux the Unix socket <c>$XDG_RUNTIME_DIR/leaf-node.sock</c>, mode 0600,
/// inside the user's own 0700 runtime folder. One JSON request line, one JSON answer line.
/// </summary>
public static class ControlChannel
{
    public const int MaxMessageBytes = 64 * 1024;

    /// <summary>The pipe name (Windows), with a suffix when <c>LEAF_NODE_HOME</c> isolates an agent.</summary>
    public static string PipeName(NodePaths paths) => "leaf-node-" + UserSuffix(paths);

    /// <summary>The single-instance mutex name (Windows).</summary>
    public static string MutexName(NodePaths paths) => @"Local\LeafNode-" + UserSuffix(paths);

    private static string UserSuffix(NodePaths paths)
    {
        var user = OperatingSystem.IsWindows()
            ? System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName
            : Environment.UserName;
        return paths.IsolatedHome is null
            ? user
            : user + "-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(paths.IsolatedHome)))[..12].ToLowerInvariant();
    }

    internal static async Task<JsonObject?> ReadMessageAsync(Stream stream, CancellationToken ct)
    {
        var buffer = new MemoryStream();
        var one = new byte[1];
        while (true)
        {
            var read = await stream.ReadAsync(one, ct);
            if (read == 0) break;
            if (one[0] == (byte)'\n') break;
            buffer.WriteByte(one[0]);
            if (buffer.Length > MaxMessageBytes) throw new InvalidDataException("control message too large");
        }
        if (buffer.Length == 0) return null;
        return JsonNode.Parse(buffer.ToArray()) as JsonObject;
    }

    internal static async Task WriteMessageAsync(Stream stream, JsonObject message, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(message.ToJsonString() + "\n");
        await stream.WriteAsync(bytes, ct);
        await stream.FlushAsync(ct);
    }
}

/// <summary>The agent's end: accepts connections and answers each request through <c>handle</c>.</summary>
public sealed class ControlServer : IAsyncDisposable
{
    private readonly NodePaths _paths;
    private readonly Func<JsonObject, CancellationToken, Task<JsonObject>> _handle;
    private readonly NodeLog _log;
    private readonly CancellationTokenSource _stop = new();
    private Socket? _socket;
    private Task? _loop;

    public ControlServer(NodePaths paths, Func<JsonObject, CancellationToken, Task<JsonObject>> handle, NodeLog log)
    {
        _paths = paths;
        _handle = handle;
        _log = log;
    }

    public void Start()
    {
        if (!OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(_paths.Runtime);
            // A stale socket from an agent that died: the instance lock is held, so nobody listens on it.
            if (File.Exists(_paths.ControlSocket)) File.Delete(_paths.ControlSocket);
            var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            socket.Bind(new UnixDomainSocketEndPoint(_paths.ControlSocket));
            File.SetUnixFileMode(_paths.ControlSocket, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            socket.Listen(8);
            _socket = socket;
        }
        _loop = Task.Run(() => AcceptLoopAsync(_stop.Token));
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                Stream stream;
                if (OperatingSystem.IsWindows())
                {
                    var pipe = new NamedPipeServerStream(ControlChannel.PipeName(_paths), PipeDirection.InOut,
                        NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    try
                    {
                        await pipe.WaitForConnectionAsync(ct);
                    }
                    catch
                    {
                        await pipe.DisposeAsync();
                        throw;
                    }
                    stream = pipe;
                }
                else
                {
                    var client = await _socket!.AcceptAsync(ct);
                    stream = new NetworkStream(client, ownsSocket: true);
                }
                _ = Task.Run(() => ServeAsync(stream, ct), CancellationToken.None);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception error)
            {
                _log.Warn($"Control channel: {error.Message}");
                await Task.Delay(TimeSpan.FromMilliseconds(200), CancellationToken.None);
            }
        }
    }

    private async Task ServeAsync(Stream stream, CancellationToken ct)
    {
        await using (stream)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(10));
                var request = await ControlChannel.ReadMessageAsync(stream, timeout.Token);
                if (request is null) return;
                JsonObject answer;
                try
                {
                    answer = await _handle(request, ct);
                }
                catch (NodeCommandException error)
                {
                    answer = new JsonObject { ["ok"] = false, ["error"] = error.Code, ["message"] = error.Message };
                }
                await ControlChannel.WriteMessageAsync(stream, answer, ct);
            }
            catch (Exception error) when (error is IOException or InvalidDataException or OperationCanceledException
                                              or System.Text.Json.JsonException)
            {
                // a client that went away or spoke garbage
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _socket?.Dispose();
        if (_loop is not null)
        {
            try
            {
                await _loop;
            }
            catch
            {
            }
        }
        if (!OperatingSystem.IsWindows() && File.Exists(_paths.ControlSocket)) File.Delete(_paths.ControlSocket);
        _stop.Dispose();
    }
}

/// <summary>The CLI's end: one request to the running agent, or null when no agent runs for this user.</summary>
public static class ControlClient
{
    public static async Task<JsonObject?> SendAsync(NodePaths paths, JsonObject request, TimeSpan? timeout = null,
        CancellationToken ct = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout ?? TimeSpan.FromSeconds(30));
        Stream stream;
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var pipe = new NamedPipeClientStream(".", ControlChannel.PipeName(paths), PipeDirection.InOut,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                try
                {
                    await pipe.ConnectAsync(TimeSpan.FromSeconds(2), deadline.Token);
                }
                catch
                {
                    await pipe.DisposeAsync();
                    throw;
                }
                stream = pipe;
            }
            else
            {
                if (!File.Exists(paths.ControlSocket)) return null;
                var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                try
                {
                    await socket.ConnectAsync(new UnixDomainSocketEndPoint(paths.ControlSocket), deadline.Token);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
                stream = new NetworkStream(socket, ownsSocket: true);
            }
        }
        catch (Exception error) when (error is TimeoutException or SocketException or IOException
                                          || (error is OperationCanceledException && !ct.IsCancellationRequested))
        {
            return null;
        }

        await using (stream)
        {
            await ControlChannel.WriteMessageAsync(stream, request, deadline.Token);
            return await ControlChannel.ReadMessageAsync(stream, deadline.Token)
                   ?? throw new NodeCommandException("no_answer", "The agent closed the control channel without answering.");
        }
    }
}
