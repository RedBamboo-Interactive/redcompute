using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using RedCompute.Core.Configuration;
using RedCompute.Core.Discovery;
using RedCompute.Core.Jobs;
using RedCompute.Core.Providers;
using RedCompute.PluginSdk;

namespace RedCompute.Plugin.OpenAILive;

public sealed class OpenAILiveProvider : IPluginProvider, IVoiceSessionProvider
{
    public static string ProviderTypeName => "OpenAILive";

    private sealed record LiveConnection(ClientWebSocket Socket, CancellationTokenSource Lifetime, Task Reader);

    private readonly ProviderConfig _config;
    private readonly Action<string> _log;
    private readonly HttpClient _http;
    private readonly Func<ClientWebSocket> _socketFactory;
    private readonly ConcurrentDictionary<string, LiveConnection> _connections = new(StringComparer.Ordinal);
    private volatile bool _started;

    public OpenAILiveProvider(ProviderConfig config, string capabilitySlug, Action<string> log)
        : this(config, log, new HttpClient { BaseAddress = new Uri("https://api.openai.com"), Timeout = TimeSpan.FromSeconds(30) },
            () => new ClientWebSocket())
    {
        CapabilitySlug = capabilitySlug;
    }

    internal OpenAILiveProvider(ProviderConfig config, Action<string> log, HttpClient http,
        Func<ClientWebSocket> socketFactory)
    {
        _config = config;
        _log = log;
        _http = http;
        _socketFactory = socketFactory;
    }

    public string Name => _config.DisplayName ?? "OpenAI GPT-Live";
    public string DisplayName => Name;
    public string ProviderType => ProviderTypeName;
    public string VoiceProviderId => _config.EntitySlug ?? "openai-live";
    public string DefaultModel => string.IsNullOrWhiteSpace(_config.Model) ? "gpt-live-1" : _config.Model;
    public string DefaultVoice => "marin";
    public string CapabilitySlug { get; private set; } = "voice-session";
    public Dictionary<string, ParameterSchema> InputParameters => new();
    public ReturnSchema OutputSchema => new() { ContentType = "application/json" };
    public bool IsProxy => false;
    public bool SupportsProgress => false;
    public bool SupportsRerun => false;
    public TimeSpan HealthCheckInterval => TimeSpan.FromMinutes(1);

    public Task<bool> StartAsync(CancellationToken ct = default)
    {
        _started = true;
        return Task.FromResult(true);
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        _started = false;
        foreach (var providerSessionId in _connections.Keys.ToArray())
        {
            try { await CloseVoiceSessionAsync(providerSessionId, ct); }
            catch { }
        }
    }

    public Task<BackendStatus> GetStatusAsync(CancellationToken ct = default)
        => Task.FromResult(!_started ? BackendStatus.Stopped
            : string.IsNullOrWhiteSpace(_config.ApiKey) ? BackendStatus.Error
            : BackendStatus.Running);

    public string? GetProxyTargetUrl() => null;
    public Task<JobResult?> ExecuteAsync(JobRequest request, CancellationToken ct = default)
        => Task.FromResult<JobResult?>(null);

    public async Task<VoiceSessionCreateResult> CreateVoiceSessionAsync(
        VoiceSessionCreateRequest request,
        Func<VoiceProviderEvent, Task> onEvent,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_config.ApiKey))
            throw new InvalidOperationException("OpenAI Live provider is not configured with a project API key");
        if (string.IsNullOrWhiteSpace(request.OfferSdp) || request.OfferSdp.Length > 256_000)
            throw new ArgumentException("A valid SDP offer is required", nameof(request));

        var payload = BuildCreatePayload(request);

        using var message = new HttpRequestMessage(HttpMethod.Post, "/v1/live/sessions")
        {
            Content = JsonContent.Create(payload),
        };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _config.ApiKey);
        using var response = await _http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct);
        var responseBody = await response.Content.ReadAsStringAsync(ct);
        if (response.StatusCode != HttpStatusCode.Created)
            throw new OpenAILiveException((int)response.StatusCode,
                $"OpenAI Live session creation failed with HTTP {(int)response.StatusCode}");

        using var document = JsonDocument.Parse(responseBody);
        var providerSessionId = document.RootElement.GetProperty("session").GetProperty("id").GetString();
        var answerSdp = document.RootElement.GetProperty("transport").GetProperty("sdp").GetString();
        if (string.IsNullOrWhiteSpace(providerSessionId) || string.IsNullOrWhiteSpace(answerSdp))
            throw new OpenAILiveException(502, "OpenAI Live returned an invalid session response");

        var socket = _socketFactory();
        socket.Options.SetRequestHeader("Authorization", $"Bearer {_config.ApiKey}");
        var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var uri = new Uri($"wss://api.openai.com/v1/live/sessions/{Uri.EscapeDataString(providerSessionId)}/attach");
        try
        {
            await socket.ConnectAsync(uri, lifetime.Token);
        }
        catch
        {
            socket.Dispose();
            lifetime.Dispose();
            throw;
        }

        var reader = ReadSidebandAsync(providerSessionId, socket, onEvent, lifetime.Token);
        if (!_connections.TryAdd(providerSessionId, new LiveConnection(socket, lifetime, reader)))
        {
            lifetime.Cancel();
            socket.Dispose();
            lifetime.Dispose();
            throw new InvalidOperationException("Duplicate OpenAI Live session id");
        }
        _ = reader.ContinueWith(_ => RemoveConnection(providerSessionId), CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return new(providerSessionId, answerSdp);
    }

    internal static object BuildCreatePayload(VoiceSessionCreateRequest request)
    {
        var input = request.InitialHistory?.Select(history => new
        {
            type = "message",
            role = history.Role,
            status = "completed",
            content = new[]
            {
                new
                {
                    type = history.Role == "assistant" ? "output_text" : "input_text",
                    text = history.Content,
                },
            },
        }).ToArray() ?? [];
        return new
        {
            session = new
            {
                model = request.Model,
                instructions = request.Instructions,
                input,
                audio = new { output = new { voice = request.Voice } },
                delegation = new { type = "client" },
                client = new
                {
                    data_channel = new
                    {
                        allowed_client_events = Array.Empty<string>(),
                        allowed_server_events = "all",
                    },
                },
                store = false,
            },
            transport = new { type = "webrtc", sdp = request.OfferSdp },
        };
    }

    public async Task SendDelegationUpdateAsync(string providerSessionId, string delegationId,
        VoiceDelegationUpdate update, CancellationToken ct)
    {
        if (!_connections.TryGetValue(providerSessionId, out var connection))
            throw new KeyNotFoundException("Voice session is not connected");
        var type = update.Kind switch
        {
            "thinking" => "session.thinking.append",
            "commentary" => "session.commentary.append",
            "failed" => "session.commentary.append",
            _ => throw new ArgumentException("Unknown delegation update kind", nameof(update)),
        };
        await SendJsonAsync(connection.Socket, new
        {
            type,
            event_id = update.EventId,
            delegation_id = delegationId,
            content = update.Content,
        }, ct);
    }

    public async Task CloseVoiceSessionAsync(string providerSessionId, CancellationToken ct)
    {
        if (!_connections.TryGetValue(providerSessionId, out var connection)) return;
        try
        {
            if (connection.Socket.State == WebSocketState.Open)
            {
                await SendJsonAsync(connection.Socket, new { type = "session.close", event_id = $"close_{Guid.NewGuid():N}" }, ct);
                try { await connection.Reader.WaitAsync(TimeSpan.FromSeconds(5), ct); }
                catch (TimeoutException) { }
            }
        }
        finally
        {
            // The reader continuation may already have removed and disposed this exact
            // connection after receiving session.closed. Only the winner of TryRemove
            // owns cancellation and disposal; touching the captured CTS afterwards was
            // the source of spurious close failures on an already-closed paid session.
            if (_connections.TryRemove(providerSessionId, out var owned))
            {
                CancelSafely(owned.Lifetime);
                try
                {
                    if (owned.Socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                        await owned.Socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Leaf voice session ended", CancellationToken.None);
                }
                catch { }
                owned.Socket.Dispose();
                owned.Lifetime.Dispose();
            }
        }
    }

    private async Task ReadSidebandAsync(string providerSessionId, ClientWebSocket socket,
        Func<VoiceProviderEvent, Task> onEvent, CancellationToken ct)
    {
        var buffer = new byte[32 * 1024];
        using var payload = new MemoryStream();
        try
        {
            while (!ct.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                var result = await socket.ReceiveAsync(buffer, ct);
                if (result.MessageType == WebSocketMessageType.Close) break;
                if (result.MessageType != WebSocketMessageType.Text) continue;
                payload.Write(buffer, 0, result.Count);
                if (!result.EndOfMessage) continue;
                var json = Encoding.UTF8.GetString(payload.GetBuffer(), 0, checked((int)payload.Length));
                payload.SetLength(0);
                var normalized = NormalizeEvent(json);
                if (normalized is not null) await onEvent(normalized);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _log($"[OpenAI Live] Sideband failed for {providerSessionId}: {ex.Message}");
            await onEvent(new("error", ErrorCode: "sideband_failed", ErrorMessage: "The voice control channel disconnected"));
        }
    }

    internal static VoiceProviderEvent? NormalizeEvent(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var type = String(root, "type");
        var eventId = String(root, "event_id");
        return type switch
        {
            "session.input_transcript.delta" => new(type, eventId, String(root, "delta"), Long(root, "start_ms"), Long(root, "end_ms")),
            "session.output_transcript.delta" => new(type, eventId, String(root, "delta"), Long(root, "start_ms"), Long(root, "end_ms")),
            "session.delegation.created" => root.TryGetProperty("delegation", out var delegation)
                ? new(type, eventId, DelegationId: String(delegation, "id"), OffsetMs: Long(root, "offset_ms")) : null,
            "session.commentary.appended" or "session.thinking.appended" or "session.instructions.appended"
                => new("session.delegation.update.accepted", eventId,
                    StartMs: Long(root, "start_ms"), EndMs: Long(root, "end_ms"),
                    ClientEventId: String(root, "client_event_id")),
            "session.started" or "session.closed" => new(type, eventId),
            "error" => new(type, eventId, ErrorCode: root.TryGetProperty("error", out var error) ? String(error, "code") : null,
                ErrorMessage: "OpenAI Live reported a session error",
                ClientEventId: root.TryGetProperty("error", out error) ? String(error, "client_event_id") : null),
            _ => null,
        };
    }

    private static string? String(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static long? Long(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.TryGetInt64(out var number) ? number : null;

    private static async Task SendJsonAsync(ClientWebSocket socket, object value, CancellationToken ct)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value);
        await socket.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
    }

    private void RemoveConnection(string providerSessionId)
    {
        if (!_connections.TryRemove(providerSessionId, out var connection)) return;
        CancelSafely(connection.Lifetime);
        connection.Socket.Dispose();
        connection.Lifetime.Dispose();
    }

    internal static void CancelSafely(CancellationTokenSource lifetime)
    {
        try { lifetime.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    public async ValueTask DisposeAsync() => await StopAsync();
}

public sealed class OpenAILiveException(int statusCode, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}
