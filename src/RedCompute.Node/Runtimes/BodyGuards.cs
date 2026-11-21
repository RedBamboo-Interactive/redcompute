using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RedCompute.Node.Runtimes;

/// <summary>
/// A request body checked as it streams into the runtime (Phase 4 design 2.4, the parameter rules). Bytes reach the
/// runtime only once what they belong to was checked: a multipart part's headers, a small field's value. A body that
/// breaks the rule fails its read with <c>route_not_allowed</c>, so the runtime sees a cut request, never the part.
/// </summary>
internal abstract class GuardStream(Stream inner) : Stream
{
    private readonly byte[] _read = new byte[64 * 1024];
    private readonly MemoryStream _released = new();
    private int _releasedAt;
    private bool _ended;

    protected Stream Inner { get; } = inner;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    /// <summary>Takes new bytes; releases what may go on with <see cref="Release"/>.</summary>
    protected abstract void Take(ReadOnlySpan<byte> data);

    /// <summary>The body ended: release the rest, or throw when it may not end here.</summary>
    protected abstract void End();

    protected void Release(ReadOnlySpan<byte> bytes) => _released.Write(bytes);

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        while (_releasedAt >= _released.Length)
        {
            _released.SetLength(0);
            _releasedAt = 0;
            if (_ended) return 0;
            var read = await Inner.ReadAsync(_read, ct);
            if (read == 0)
            {
                _ended = true;
                End();
            }
            else
            {
                Take(_read.AsSpan(0, read));
            }
        }
        var take = (int)Math.Min(buffer.Length, _released.Length - _releasedAt);
        _released.GetBuffer().AsSpan(_releasedAt, take).CopyTo(buffer.Span);
        _releasedAt += take;
        return take;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        => ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer, offset, count).GetAwaiter().GetResult();
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) Inner.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>
/// ComfyUI's <c>/upload/image</c>: <c>multipart/form-data</c> with exactly one file part named <c>image</c> whose file
/// name has no separator or <c>..</c>, and only the fields <c>overwrite</c> (true/false), <c>type</c> (input or temp)
/// and <c>subfolder</c> (relative, no <c>..</c>). Headers are at most 16 KiB per part, a field at most 1 KiB. The body
/// starts with its first boundary (no preamble) and ends with the closing one and at most a CRLF (no epilogue): no bytes
/// the guard did not read as a part reach the runtime's parser (review P4-P7, item 5).
/// </summary>
internal sealed class ImageUploadGuard : GuardStream
{
    private const int MaxHeaders = 16 * 1024;
    private const int MaxField = 1024;

    private enum State { Preamble, AfterDelimiter, Headers, File, Field, Epilogue }

    private readonly byte[] _first;
    private readonly byte[] _delimiter;
    private readonly List<byte> _hold = [];
    private State _state = State.Preamble;
    private string? _field;
    private int _images;

    public ImageUploadGuard(Stream inner, string boundary) : base(inner)
    {
        _first = Encoding.ASCII.GetBytes("--" + boundary);
        _delimiter = Encoding.ASCII.GetBytes("\r\n--" + boundary);
    }

    protected override void Take(ReadOnlySpan<byte> data)
    {
        _hold.AddRange(data.ToArray());
        Advance();
    }

    private static readonly byte[] Closing = "--\r\n"u8.ToArray();

    protected override void End()
    {
        Advance();
        if (_state != State.Epilogue || _hold.Count is not (2 or 4))
            throw RuntimeRoutes.NotAllowed("The upload's multipart body ended before its closing boundary.");
        if (_images != 1) throw RuntimeRoutes.NotAllowed("An upload carries exactly one image.");
        Flush(_hold.Count);
    }

    private void Advance()
    {
        while (true)
        {
            var span = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_hold);
            switch (_state)
            {
                case State.Preamble:
                {
                    var have = Math.Min(span.Length, _first.Length);
                    if (!span[..have].SequenceEqual(_first.AsSpan(0, have)))
                        throw RuntimeRoutes.NotAllowed("The upload does not start with its boundary.");
                    if (span.Length < _first.Length) return;
                    Flush(_first.Length);
                    _state = State.AfterDelimiter;
                    continue;
                }
                case State.AfterDelimiter:
                    if (span.Length < 2) return;
                    if (span[0] == '-' && span[1] == '-')
                    {
                        _state = State.Epilogue;
                        continue;
                    }
                    if (span[0] != '\r' || span[1] != '\n') throw RuntimeRoutes.NotAllowed("A malformed multipart boundary.");
                    Flush(2);
                    _state = State.Headers;
                    continue;
                case State.Headers:
                {
                    var end = span.IndexOf("\r\n\r\n"u8);
                    if (end < 0)
                    {
                        if (span.Length > MaxHeaders) throw RuntimeRoutes.NotAllowed("A part's headers are over 16 KiB.");
                        return;
                    }
                    CheckHeaders(RuntimeRoutes.Latin1(span[..end]));
                    Flush(end + 4);
                    continue;
                }
                case State.File:
                {
                    var at = span.IndexOf(_delimiter);
                    if (at < 0)
                    {
                        // Everything but a possible start of the delimiter goes on.
                        Flush(Math.Max(0, span.Length - (_delimiter.Length - 1)));
                        return;
                    }
                    Flush(at + _delimiter.Length);
                    _state = State.AfterDelimiter;
                    continue;
                }
                case State.Field:
                {
                    var at = span.IndexOf(_delimiter);
                    if (at < 0)
                    {
                        if (span.Length > MaxField + _delimiter.Length) throw RuntimeRoutes.NotAllowed($"The field '{_field}' is over 1 KiB.");
                        return;
                    }
                    CheckField(_field!, Encoding.UTF8.GetString(span[..at]));
                    Flush(at + _delimiter.Length);
                    _state = State.AfterDelimiter;
                    continue;
                }
                case State.Epilogue:
                    // "--" and at most a CRLF, held until the body ends.
                    if (span.Length > Closing.Length || !span.SequenceEqual(Closing.AsSpan(0, span.Length)))
                        throw RuntimeRoutes.NotAllowed("Nothing may follow an upload's closing boundary.");
                    return;
            }
        }
    }

    private void Flush(int count)
    {
        if (count <= 0) return;
        var span = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_hold);
        Release(span[..count]);
        _hold.RemoveRange(0, count);
    }

    private void CheckHeaders(string headers)
    {
        string? name = null, filename = null, extended = null;
        var disposition = false;
        foreach (var line in headers.Split("\r\n"))
        {
            var colon = line.IndexOf(':');
            if (colon <= 0) throw RuntimeRoutes.NotAllowed("A malformed part header.");
            var header = line[..colon].Trim();
            if (!header.Equals("Content-Disposition", StringComparison.OrdinalIgnoreCase)) continue;
            if (disposition) throw RuntimeRoutes.NotAllowed("A part has two Content-Disposition headers.");
            disposition = true;
            var parts = line[(colon + 1)..].Split(';').Select(p => p.Trim()).ToList();
            if (parts.Count == 0 || !parts[0].Equals("form-data", StringComparison.OrdinalIgnoreCase))
                throw RuntimeRoutes.NotAllowed("A part is not form-data.");
            foreach (var parameter in parts.Skip(1))
            {
                var equals = parameter.IndexOf('=');
                if (equals <= 0) throw RuntimeRoutes.NotAllowed("A malformed Content-Disposition parameter.");
                var key = parameter[..equals].Trim().ToLowerInvariant();
                var value = parameter[(equals + 1)..].Trim().Trim('"');
                switch (key)
                {
                    case "name" when name is null: name = value; break;
                    case "filename" when filename is null: filename = value; break;
                    // RFC 6266 (what .NET's multipart writer adds, and what the runtime reads first): utf-8''<escaped>.
                    case "filename*" when extended is null:
                        extended = ExtendedValue(value) ?? throw RuntimeRoutes.NotAllowed("A filename* is utf-8'' and an escaped name.");
                        break;
                    default: throw RuntimeRoutes.NotAllowed($"A Content-Disposition '{key}' is not allowed on an upload.");
                }
            }
        }
        if (name is null) throw RuntimeRoutes.NotAllowed("A part has no name.");
        if (filename is not null || extended is not null)
        {
            if (name != "image") throw RuntimeRoutes.NotAllowed($"Only the 'image' part may be a file (got '{name}').");
            // Both forms, when both are there, must be safe: the runtime may read either.
            foreach (var candidate in new[] { filename, extended })
                if (candidate is not null && !RuntimeRoutes.IsFileName(candidate))
                    throw RuntimeRoutes.NotAllowed($"The file name '{candidate}' is not allowed.");
            if (++_images > 1) throw RuntimeRoutes.NotAllowed("An upload carries exactly one image.");
            _state = State.File;
            return;
        }
        if (name is not ("overwrite" or "type" or "subfolder")) throw RuntimeRoutes.NotAllowed($"The upload field '{name}' is not allowed.");
        _field = name;
        _state = State.Field;
    }

    /// <summary>An RFC 5987 value in UTF-8 (<c>utf-8'lang'escaped</c>), decoded strictly; null for anything else.</summary>
    private static string? ExtendedValue(string value)
    {
        var first = value.IndexOf('\'');
        var second = first < 0 ? -1 : value.IndexOf('\'', first + 1);
        if (second < 0 || !value[..first].Equals("utf-8", StringComparison.OrdinalIgnoreCase)) return null;
        return RuntimeRoutes.Decode(value[(second + 1)..], plusIsSpace: false);
    }

    private static void CheckField(string name, string value)
    {
        var ok = name switch
        {
            "overwrite" => value is "true" or "false" or "1" or "0",
            "type" => value is "input" or "temp",
            "subfolder" => RuntimeRoutes.IsSubfolder(value),
            _ => false,
        };
        if (!ok) throw RuntimeRoutes.NotAllowed($"The upload field '{name}' may not be '{value}'.");
    }
}

/// <summary>A JSON body, held whole (up to its cap) and checked before a byte of it reaches the runtime.</summary>
internal abstract class JsonBodyGuard(Stream inner, long cap, string route) : GuardStream(inner)
{
    private readonly MemoryStream _body = new();

    protected override void Take(ReadOnlySpan<byte> data)
    {
        if (_body.Length + data.Length > cap) throw RuntimeRoutes.NotAllowed($"A {route} body is at most {cap} bytes.");
        _body.Write(data);
    }

    /// <summary>Null when the body may go on, else why not.</summary>
    protected abstract string? Check(JsonNode? body);

    protected override void End()
    {
        string? refusal;
        try
        {
            refusal = Check(JsonNode.Parse(_body.ToArray()));
        }
        catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException)
        {
            // Not JSON, or a member twice (which the runtime would read as the last one): refused either way.
            refusal = $"A {route} body is JSON with each member once.";
        }
        if (refusal is not null) throw RuntimeRoutes.NotAllowed(refusal);
        Release(_body.ToArray());
    }

    protected static bool IsString(JsonNode? node, out string value)
    {
        var isString = node is JsonValue v && v.GetValueKind() == JsonValueKind.String;
        value = isString ? node!.GetValue<string>() : "";
        return isString;
    }
}

/// <summary>
/// ComfyUI's <c>POST /queue</c> deletes only: <c>{"delete": ["prompt id", …]}</c> and nothing else (no <c>clear</c>), and
/// only prompts the server submitted through this node (<paramref name="submitted"/>).
/// </summary>
internal sealed class QueueDeleteGuard(Stream inner, long cap, Func<string, bool> submitted) : JsonBodyGuard(inner, cap, "/queue")
{
    protected override string? Check(JsonNode? body)
    {
        if (body is not JsonObject { Count: 1 } only || only["delete"] is not JsonArray ids)
            return "POST /queue may only delete: {\"delete\": [prompt ids]}.";
        foreach (var id in ids)
        {
            if (!IsString(id, out var value)) return "POST /queue may only delete: {\"delete\": [prompt ids]}.";
            if (!submitted(value)) return $"POST /queue may delete only prompts the server submitted through this node ('{value}' is not one).";
        }
        return null;
    }
}

/// <summary>
/// ComfyUI's <c>POST /interrupt</c>: exactly <c>{"prompt_id": id}</c>, an id the server submitted through this node.
/// ComfyUI interrupts only when that prompt is the one running.
/// </summary>
internal sealed class InterruptGuard(Stream inner, long cap, Func<string, bool> submitted) : JsonBodyGuard(inner, cap, "/interrupt")
{
    protected override string? Check(JsonNode? body)
    {
        if (body is not JsonObject { Count: 1 } only || !IsString(only["prompt_id"], out var id))
            return "POST /interrupt is exactly {\"prompt_id\": id}: a targeted interrupt, never a global one.";
        return submitted(id) ? null : $"POST /interrupt stops only a prompt the server submitted through this node ('{id}' is not one).";
    }
}

/// <summary>
/// ComfyUI's <c>POST /prompt</c>: a JSON object that does not choose its <c>prompt_id</c>. The id is ComfyUI's, recorded
/// from its answer (<see cref="SubmittedPrompts"/>): a chosen one could be an id the owner's own prompt has.
/// </summary>
internal sealed class PromptSubmitGuard(Stream inner, long cap) : JsonBodyGuard(inner, cap, "/prompt")
{
    protected override string? Check(JsonNode? body)
        => body is not JsonObject prompt ? "A /prompt body is a JSON object."
            : prompt.ContainsKey("prompt_id") ? "A /prompt body may not choose its prompt_id: ComfyUI assigns it."
            : null;
}
