using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RedCompute.Core.Sessions;

namespace RedCompute.Plugin.Codex;

/// <summary>
/// The enforcement point for Codex tool-free generate. Codex exec has no switch that empties its
/// tool surface and reports it, so tool-free calls point a custom Codex model provider at this
/// loopback relay instead. Codex keeps doing what it is good at (login, token refresh, model
/// selection, prompt assembly); the relay owns the model boundary:
///
/// <list type="bullet">
/// <item>Requests: only <c>message</c> input items and an allowlist of top-level fields reach the
/// model; <c>tools</c> is forced empty, <c>tool_choice</c> to <c>none</c>. Every tool definition
/// Codex assembled is removed before it leaves the machine.</item>
/// <item>Responses: the whole upstream response is buffered and inspected before Codex sees any of
/// it. Any output item other than <c>message</c> or <c>reasoning</c>, or any tool-call event, is
/// withheld and replaced by a non-retryable error, so Codex never receives a call it could run.</item>
/// </list>
///
/// Each generate call gets its own unguessable path prefix and evidence record. The call is
/// reported as verified only when at least one model response passed through the relay and no
/// request or response was refused.
/// </summary>
public sealed class CodexToolFreeRelay : IAsyncDisposable
{
    public static readonly Uri ChatGptUpstream = new("https://chatgpt.com/backend-api/codex/");
    public static readonly Uri ApiKeyUpstream = new("https://api.openai.com/v1/");

    private static readonly HashSet<string> AllowedRequestFields =
    [
        "model", "stream", "input", "instructions", "tool_choice", "tools", "parallel_tool_calls",
        "reasoning", "store", "include", "prompt_cache_key", "text", "client_metadata",
        "max_output_tokens", "temperature", "top_p", "service_tier", "metadata", "truncation",
        "safety_identifier", "user",
    ];

    private static readonly HashSet<string> AllowedOutputItems = ["message", "reasoning"];

    private static readonly HashSet<string> DroppedRequestHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Host", "Content-Length", "Content-Type", "Content-Encoding", "Connection", "Keep-Alive",
        "Transfer-Encoding", "TE", "Upgrade", "Accept-Encoding", "Proxy-Authorization", "Proxy-Connection",
    };

    private readonly ConcurrentDictionary<string, RelayCall> _calls = new(StringComparer.Ordinal);

    /// <summary>One registered call: its evidence and the validated images the relay itself injects.</summary>
    private sealed record RelayCall(ToolFreeEvidence Evidence, IReadOnlyList<GenerateImage> Images);

    // The rebuilt body goes upstream as JSON and is never embedded in HTML, so the default encoder's
    // escaping of '+' and '/' (which would inflate base64 image data) is unnecessary.
    private static readonly JsonSerializerOptions UpstreamJson = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Message content part types that reach the model. Everything else is removed.</summary>
    private static readonly HashSet<string> AllowedContentParts = ["input_text", "output_text"];
    private readonly SemaphoreSlim _startGate = new(1, 1);
    private readonly HttpClient _upstream;
    private readonly Uri _chatGptUpstream;
    private readonly Uri _apiKeyUpstream;
    private readonly Action<string, Guid?> _log;
    private WebApplication? _app;
    private Uri? _origin;
    private string? _authority;

    public CodexToolFreeRelay(Action<string, Guid?> log, HttpMessageHandler? upstreamHandler = null,
        Uri? chatGptUpstream = null, Uri? apiKeyUpstream = null)
    {
        _log = log;
        _upstream = new HttpClient(upstreamHandler ?? CreateUpstreamHandler())
        {
            Timeout = TimeSpan.FromMinutes(11),
        };
        _chatGptUpstream = chatGptUpstream ?? ChatGptUpstream;
        _apiKeyUpstream = apiKeyUpstream ?? ApiKeyUpstream;
    }

    internal static SocketsHttpHandler CreateUpstreamHandler() => new()
    {
        AutomaticDecompression = DecompressionMethods.All,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        // Credentials go to the fixed upstream only; a redirect is refused, never followed.
        AllowAutoRedirect = false,
    };

    /// <summary>Registers one tool-free call and returns its private model-provider base URL.</summary>
    /// <summary>
    /// Registers one tool-free call. <paramref name="images"/> are the only images that can reach the
    /// model on this call: the relay removes every image part Codex sends and appends exactly these,
    /// as data URLs, to the final user message of each model request.
    /// </summary>
    public async Task<Lease> OpenAsync(CancellationToken ct, IReadOnlyList<GenerateImage>? images = null)
    {
        var origin = await EnsureStartedAsync(ct);
        var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        var evidence = new ToolFreeEvidence { ExpectedImages = images?.Count ?? 0 };
        _calls[nonce] = new RelayCall(evidence, images ?? []);
        return new Lease(this, nonce, new Uri(origin, $"{nonce}/v1"), evidence);
    }

    private async Task<Uri> EnsureStartedAsync(CancellationToken ct)
    {
        if (_origin != null) return _origin;
        await _startGate.WaitAsync(ct);
        try
        {
            if (_origin != null) return _origin;

            // An empty builder reads no appsettings or ASPNETCORE_* variables, so nothing ambient
            // can add an endpoint, and the no-op lifetime leaves process signals to the host app.
            var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions());
            builder.WebHost.UseKestrelCore().ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
            builder.Services.AddRoutingCore();
            builder.Services.AddSingleton<IHostLifetime, InertHostLifetime>();
            var app = builder.Build();
            app.Map("/{nonce}/v1/{**path}", HandleAsync);
            await app.StartAsync(ct);

            var address = app.Services.GetRequiredService<IServer>().Features
                .Get<IServerAddressesFeature>()!.Addresses.First();
            _app = app;
            _origin = new Uri(address.TrimEnd('/') + "/");
            _authority = _origin.Authority;
            _log($"[Codex] Tool-free relay listening on {_origin}", null);
            return _origin;
        }
        finally
        {
            _startGate.Release();
        }
    }

    private async Task HandleAsync(HttpContext context)
    {
        var nonce = context.Request.RouteValues["nonce"] as string ?? "";
        var path = context.Request.RouteValues["path"] as string ?? "";
        if (!_calls.TryGetValue(nonce, out var call))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var evidence = call.Evidence;

        // Codex dials 127.0.0.1:<port> directly. Any other Host (DNS rebinding) or any Origin (a
        // browser page) is not Codex, even when it somehow holds the nonce, and fails the call.
        if (RejectedCaller(context) is { } rejected)
        {
            evidence.Refuse(rejected);
            await WriteErrorAsync(context, StatusCodes.Status403Forbidden,
                "RedCompute tool-free relay only serves the Codex process it launched");
            return;
        }

        try
        {
            if (HttpMethods.IsGet(context.Request.Method) && path == "models")
            {
                // The model catalog carries no tool definitions and grants no authority.
                using var request = CreateUpstreamRequest(context, HttpMethod.Get, path, evidence);
                using var response = await _upstream.SendAsync(request, context.RequestAborted);
                if (await RefusedRedirectAsync(context, response, evidence)) return;
                await WriteAsync(context, (int)response.StatusCode,
                    response.Content.Headers.ContentType?.ToString(),
                    await response.Content.ReadAsByteArrayAsync(context.RequestAborted));
                return;
            }

            if (HttpMethods.IsPost(context.Request.Method) && path == "responses")
            {
                await RelayResponsesAsync(context, evidence, call.Images);
                return;
            }

            evidence.Refuse($"unexpected {context.Request.Method} /{path}");
            await WriteErrorAsync(context, StatusCodes.Status404NotFound,
                "RedCompute tool-free relay only serves the Responses API");
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            evidence.Refuse($"relay failure: {ex.Message}");
            if (!context.Response.HasStarted)
                await WriteErrorAsync(context, StatusCodes.Status502BadGateway,
                    $"RedCompute tool-free relay failed: {ex.Message}");
        }
    }

    private async Task RelayResponsesAsync(HttpContext context, ToolFreeEvidence evidence,
        IReadOnlyList<GenerateImage> images)
    {
        var encoding = context.Request.Headers.ContentEncoding.ToString();
        if (!string.IsNullOrEmpty(encoding) && !encoding.Equals("identity", StringComparison.OrdinalIgnoreCase))
        {
            evidence.Refuse($"request body uses content-encoding '{encoding}' and cannot be inspected");
            await WriteErrorAsync(context, StatusCodes.Status400BadRequest,
                "RedCompute tool-free relay cannot inspect an encoded request body");
            return;
        }

        using var buffer = new MemoryStream();
        await context.Request.Body.CopyToAsync(buffer, context.RequestAborted);
        var sanitized = SanitizeRequest(buffer.ToArray(), images);
        if (sanitized.Error != null)
        {
            evidence.Refuse(sanitized.Error);
            await WriteErrorAsync(context, StatusCodes.Status400BadRequest, sanitized.Error);
            return;
        }
        evidence.RecordRequest(sanitized.StrippedToolDefinitions, sanitized.DroppedInputItems, sanitized.DroppedFields,
            sanitized.DroppedContentParts, sanitized.InjectedImageHashes);

        using var request = CreateUpstreamRequest(context, HttpMethod.Post, "responses", evidence);
        request.Content = new ByteArrayContent(sanitized.Body!);
        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        using var response = await _upstream.SendAsync(request, context.RequestAborted);
        if (await RefusedRedirectAsync(context, response, evidence)) return;
        var body = await response.Content.ReadAsByteArrayAsync(context.RequestAborted);
        var contentType = response.Content.Headers.ContentType?.ToString();
        evidence.RecordUpstreamStatus((int)response.StatusCode);

        if (response.IsSuccessStatusCode)
        {
            var inspection = InspectResponse(body, contentType);
            if (inspection.Violation != null)
            {
                evidence.Refuse(inspection.Violation);
                _log($"[Codex] Tool-free relay withheld a model response: {inspection.Violation}", null);
                // 400 is terminal for Codex: it fails the turn instead of retrying the request.
                await WriteErrorAsync(context, StatusCodes.Status400BadRequest,
                    $"RedCompute tool-free relay withheld a model response: {inspection.Violation}");
                return;
            }
            evidence.RecordResponse(inspection.OutputItemTypes);
        }

        await WriteAsync(context, (int)response.StatusCode, contentType, body);
    }

    private string? RejectedCaller(HttpContext context)
    {
        var host = context.Request.Headers.Host.ToString();
        if (!string.Equals(host, _authority, StringComparison.OrdinalIgnoreCase))
            return $"request Host '{host}' is not {_authority}";
        if (context.Request.Headers.ContainsKey("Origin"))
            return "request carried an Origin header";
        return null;
    }

    private static async Task<bool> RefusedRedirectAsync(HttpContext context, HttpResponseMessage response,
        ToolFreeEvidence evidence)
    {
        var status = (int)response.StatusCode;
        if (status is < 300 or >= 400) return false;
        evidence.Refuse($"upstream answered with redirect {status}");
        await WriteErrorAsync(context, StatusCodes.Status502BadGateway,
            $"RedCompute tool-free relay refused an upstream redirect ({status})");
        return true;
    }

    private HttpRequestMessage CreateUpstreamRequest(HttpContext context, HttpMethod method, string path,
        ToolFreeEvidence evidence)
    {
        // ChatGPT sign-in sends its account header; an API key does not. The upstream is fixed by
        // the credential type and never taken from the request.
        var upstream = context.Request.Headers.ContainsKey("chatgpt-account-id") ? _chatGptUpstream : _apiKeyUpstream;
        var request = new HttpRequestMessage(method, new Uri(upstream, path + context.Request.QueryString));
        var forwarded = new List<string>();
        foreach (var (name, values) in context.Request.Headers)
        {
            if (DroppedRequestHeaders.Contains(name)) continue;
            if (request.Headers.TryAddWithoutValidation(name, values.ToArray()))
                forwarded.Add(name);
        }
        evidence.RecordUpstream(request.RequestUri!, forwarded);
        return request;
    }

    private static Task WriteAsync(HttpContext context, int status, string? contentType, byte[] body)
    {
        context.Response.StatusCode = status;
        if (contentType != null) context.Response.ContentType = contentType;
        context.Response.ContentLength = body.Length;
        return context.Response.Body.WriteAsync(body, context.RequestAborted).AsTask();
    }

    private static Task WriteErrorAsync(HttpContext context, int status, string message)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(new
        {
            error = new { message, type = "invalid_request_error", code = "redcompute_tool_free_relay" },
        });
        return WriteAsync(context, status, "application/json", body);
    }

    internal sealed record SanitizedRequest(byte[]? Body, int StrippedToolDefinitions,
        int DroppedInputItems, IReadOnlyList<string> DroppedFields, string? Error,
        int DroppedContentParts = 0, IReadOnlyList<string>? InjectedImageHashes = null);

    /// <summary>
    /// Rebuilds a Responses request so that no tool definition can reach the model. This is an
    /// allowlist: input items other than messages and unknown top-level fields are dropped, not
    /// forwarded, so a tool family Codex adds later is removed without a code change here.
    /// </summary>
    internal static SanitizedRequest SanitizeRequest(byte[] body, IReadOnlyList<GenerateImage>? images = null)
    {
        try
        {
            return SanitizeRequestCore(body, images ?? []);
        }
        catch (Exception ex) when (ex is InvalidOperationException or JsonException or ArgumentException)
        {
            return new SanitizedRequest(null, 0, 0, [], $"request body could not be sanitized: {ex.Message}");
        }
    }

    private static SanitizedRequest SanitizeRequestCore(byte[] body, IReadOnlyList<GenerateImage> images)
    {
        JsonObject root;
        try
        {
            if (JsonNode.Parse(body) is not JsonObject parsed)
                return new SanitizedRequest(null, 0, 0, [], "request body is not a JSON object");
            root = parsed;
        }
        catch (JsonException ex)
        {
            return new SanitizedRequest(null, 0, 0, [], $"request body is not JSON: {ex.Message}");
        }

        var stripped = 0;
        var droppedFields = new List<string>();
        foreach (var name in root.Select(property => property.Key).ToList())
        {
            if (AllowedRequestFields.Contains(name)) continue;
            stripped += CountToolDefinitions(root[name]);
            root.Remove(name);
            droppedFields.Add(name);
        }

        stripped += CountToolDefinitions(root["tools"]);
        root["tools"] = new JsonArray();
        root["tool_choice"] = "none";
        root["parallel_tool_calls"] = false;

        var droppedItems = 0;
        var droppedParts = 0;
        JsonObject? lastUser = null;
        if (root["input"] is JsonArray input)
        {
            var kept = new JsonArray();
            foreach (var item in input.ToList())
            {
                if (item is JsonObject message && IsMessage(message))
                {
                    input.Remove(item);
                    droppedParts += KeepTextParts(message);
                    kept.Add(item);
                    if (message["role"] is JsonValue role && role.TryGetValue<string>(out var name) && name == "user")
                        lastUser = message;
                }
                else
                {
                    stripped += CountToolDefinitions(item);
                    droppedItems++;
                }
            }
            root["input"] = kept;
        }
        else if (root["input"] is not (null or JsonValue))
        {
            return new SanitizedRequest(null, stripped, 0, droppedFields, "request input has an unexpected shape");
        }

        var injected = new List<string>();
        if (images.Count > 0)
        {
            if (lastUser == null)
                return new SanitizedRequest(null, stripped, droppedItems, droppedFields,
                    "request has no user message to carry the images", droppedParts);
            if (lastUser["content"] is not JsonArray content)
            {
                // A bare string becomes one text part so the images can follow it.
                var text = lastUser["content"] is JsonValue value && value.TryGetValue<string>(out var bare) ? bare : "";
                content = [new JsonObject { ["type"] = "input_text", ["text"] = text }];
                lastUser["content"] = content;
            }
            foreach (var image in images)
            {
                content.Add(new JsonObject { ["type"] = "input_image", ["image_url"] = image.DataUrl });
                injected.Add(image.Sha256);
            }
        }

        return new SanitizedRequest(JsonSerializer.SerializeToUtf8Bytes(root, UpstreamJson), stripped, droppedItems, droppedFields,
            null, droppedParts, injected);
    }

    /// <summary>
    /// Keeps only text parts in a message's content array and returns how many were removed. Images,
    /// files and anything unknown that Codex (or anything upstream of it) put in a message never reach
    /// the model; the only images are the ones the relay appends from the validated set.
    /// </summary>
    private static int KeepTextParts(JsonObject message)
    {
        if (message["content"] is not JsonArray content) return 0;
        var removed = 0;
        foreach (var part in content.ToList())
        {
            var type = part is JsonObject obj && obj["type"] is JsonValue value && value.TryGetValue<string>(out var name)
                ? name
                : null;
            if (type != null && AllowedContentParts.Contains(type)) continue;
            content.Remove(part);
            removed++;
        }
        return removed;
    }

    private static bool IsMessage(JsonObject item)
    {
        var type = item["type"] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
        return type == "message" || (type == null && item.ContainsKey("role"));
    }

    private static int CountToolDefinitions(JsonNode? node) => node switch
    {
        JsonObject obj when obj["tools"] is JsonArray nested => Math.Max(1, nested.Sum(CountToolDefinitions)),
        JsonObject obj when obj.ContainsKey("type") && obj.ContainsKey("name") => 1,
        JsonArray array => array.Sum(CountToolDefinitions),
        _ => 0,
    };

    internal sealed record ResponseInspection(IReadOnlyList<string> OutputItemTypes, string? Violation);

    /// <summary>
    /// Inspects a complete upstream Responses result, streamed (SSE) or not, and names the first
    /// tool call, hosted tool activity or unknown output item it contains.
    /// </summary>
    internal static ResponseInspection InspectResponse(byte[] body, string? contentType)
    {
        try
        {
            return InspectResponseCore(body, contentType);
        }
        catch (InvalidOperationException ex)
        {
            // Undecodable text or an unexpected value kind: withhold rather than guess.
            return new ResponseInspection([], $"upstream response could not be inspected: {ex.Message}");
        }
    }

    private static ResponseInspection InspectResponseCore(byte[] body, string? contentType)
    {
        var items = new List<string>();
        var text = Encoding.UTF8.GetString(body);
        var payloads = contentType?.StartsWith("text/event-stream", StringComparison.OrdinalIgnoreCase) == true
                       || text.TrimStart().StartsWith("event:", StringComparison.Ordinal)
                       || text.TrimStart().StartsWith("data:", StringComparison.Ordinal)
            ? SsePayloads(text)
            : [text];

        foreach (var payload in payloads)
        {
            if (payload.Trim() is "" or "[DONE]") continue;
            JsonElement root;
            try
            {
                using var document = JsonDocument.Parse(payload);
                root = document.RootElement.Clone();
            }
            catch (JsonException)
            {
                return new ResponseInspection(items, "upstream response contained an unparseable event");
            }
            if (root.ValueKind != JsonValueKind.Object) continue;

            if (root.TryGetProperty("type", out var typeValue) && typeValue.ValueKind == JsonValueKind.String
                && IsToolEvent(typeValue.GetString()!))
                return new ResponseInspection(items, $"tool event '{typeValue.GetString()}'");

            if (root.TryGetProperty("item", out var item) && CheckItem(item, items) is { } itemViolation)
                return new ResponseInspection(items, itemViolation);

            var response = root.TryGetProperty("response", out var nested) && nested.ValueKind == JsonValueKind.Object
                ? nested
                : root;
            if (response.TryGetProperty("output", out var output) && output.ValueKind == JsonValueKind.Array)
                foreach (var outputItem in output.EnumerateArray())
                    if (CheckItem(outputItem, items) is { } outputViolation)
                        return new ResponseInspection(items, outputViolation);
        }

        return new ResponseInspection(items.Distinct().ToList(), null);
    }

    private static string? CheckItem(JsonElement item, List<string> items)
    {
        if (item.ValueKind != JsonValueKind.Object) return "output item is not an object";
        var type = item.TryGetProperty("type", out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()!
            : "(untyped)";
        if (!AllowedOutputItems.Contains(type)) return $"output item '{type}'";
        items.Add(type);
        return null;
    }

    private static bool IsToolEvent(string type)
        => type.Contains("_call", StringComparison.Ordinal)
           || type.Contains("tool", StringComparison.OrdinalIgnoreCase)
           || type.StartsWith("response.mcp", StringComparison.Ordinal);

    private static List<string> SsePayloads(string text)
    {
        var payloads = new List<string>();
        var data = new StringBuilder();
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.Length == 0)
            {
                if (data.Length > 0) payloads.Add(data.ToString());
                data.Clear();
                continue;
            }
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
            if (data.Length > 0) data.Append('\n');
            data.Append(line.AsSpan(5).TrimStart(' '));
        }
        if (data.Length > 0) payloads.Add(data.ToString());
        return payloads;
    }

    public async ValueTask DisposeAsync()
    {
        if (_app != null) await _app.DisposeAsync();
        _upstream.Dispose();
        _startGate.Dispose();
    }

    private sealed class InertHostLifetime : IHostLifetime
    {
        public Task WaitForStartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    /// <summary>One tool-free call's private relay route. Disposing it revokes the route.</summary>
    public sealed class Lease : IDisposable
    {
        private readonly CodexToolFreeRelay _relay;
        private readonly string _nonce;

        internal Lease(CodexToolFreeRelay relay, string nonce, Uri baseUrl, ToolFreeEvidence evidence)
        {
            _relay = relay;
            _nonce = nonce;
            BaseUrl = baseUrl;
            Evidence = evidence;
        }

        public Uri BaseUrl { get; }
        public ToolFreeEvidence Evidence { get; }

        public void Dispose() => _relay._calls.TryRemove(_nonce, out _);
    }
}

/// <summary>What the relay saw for one tool-free call.</summary>
public sealed class ToolFreeEvidence
{
    private readonly object _gate = new();
    private readonly List<string> _refusals = [];
    private readonly HashSet<string> _outputItemTypes = [];
    private readonly HashSet<string> _droppedFields = new(StringComparer.Ordinal);
    private readonly HashSet<string> _forwardedHeaders = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _upstreams = new(StringComparer.Ordinal);
    private readonly List<int> _upstreamStatuses = [];
    private readonly List<IReadOnlyList<string>> _injectedImages = [];

    public int Requests { get; private set; }
    /// <summary>Validated images registered for this call; every model request must carry all of them.</summary>
    public int ExpectedImages { get; init; }
    /// <summary>Content parts other than text that the relay removed from messages.</summary>
    public int DroppedContentParts { get; private set; }
    public int Responses { get; private set; }
    public int StrippedToolDefinitions { get; private set; }
    public int DroppedInputItems { get; private set; }

    public IReadOnlyList<string> Refusals { get { lock (_gate) return _refusals.ToList(); } }
    public IReadOnlyCollection<string> OutputItemTypes { get { lock (_gate) return _outputItemTypes.ToList(); } }
    /// <summary>Top-level request fields removed by the allowlist.</summary>
    public IReadOnlyCollection<string> DroppedFields { get { lock (_gate) return _droppedFields.ToList(); } }
    /// <summary>Names (never values) of the headers forwarded upstream.</summary>
    public IReadOnlyCollection<string> ForwardedHeaders { get { lock (_gate) return _forwardedHeaders.ToList(); } }
    /// <summary>Upstream URLs contacted, without query strings.</summary>
    public IReadOnlyCollection<string> Upstreams { get { lock (_gate) return _upstreams.ToList(); } }
    public IReadOnlyList<int> UpstreamStatuses { get { lock (_gate) return _upstreamStatuses.ToList(); } }
    /// <summary>SHA-256 of each image the relay injected, per request, in order.</summary>
    public IReadOnlyList<IReadOnlyList<string>> InjectedImages { get { lock (_gate) return _injectedImages.ToList(); } }

    /// <summary>Why the call is not verified, or null when it is.</summary>
    public string? VerificationError
    {
        get
        {
            lock (_gate)
            {
                if (_refusals.Count > 0) return $"tool-free relay refused: {string.Join("; ", _refusals)}";
                if (Responses == 0) return "no model response passed through the tool-free relay";
                if (_injectedImages.Any(hashes => hashes.Count != ExpectedImages))
                    return $"a model request did not carry all {ExpectedImages} validated image(s)";
                return null;
            }
        }
    }

    internal void RecordRequest(int strippedToolDefinitions, int droppedInputItems, IEnumerable<string> droppedFields,
        int droppedContentParts = 0, IReadOnlyList<string>? injectedImages = null)
    {
        lock (_gate)
        {
            Requests++;
            StrippedToolDefinitions += strippedToolDefinitions;
            DroppedInputItems += droppedInputItems;
            DroppedContentParts += droppedContentParts;
            _droppedFields.UnionWith(droppedFields);
            _injectedImages.Add(injectedImages ?? []);
        }
    }

    internal void RecordUpstream(Uri uri, IEnumerable<string> forwardedHeaders)
    {
        lock (_gate)
        {
            _upstreams.Add(uri.GetLeftPart(UriPartial.Path));
            _forwardedHeaders.UnionWith(forwardedHeaders);
        }
    }

    internal void RecordUpstreamStatus(int status)
    {
        lock (_gate) _upstreamStatuses.Add(status);
    }

    internal void RecordResponse(IEnumerable<string> outputItemTypes)
    {
        lock (_gate)
        {
            Responses++;
            _outputItemTypes.UnionWith(outputItemTypes);
        }
    }

    internal void Refuse(string reason)
    {
        lock (_gate) _refusals.Add(reason);
    }
}
