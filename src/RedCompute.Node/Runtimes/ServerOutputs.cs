using System.Text.Json;

namespace RedCompute.Node.Runtimes;

/// <summary>A file <c>/view</c> serves: its <c>type</c> (output or temp), <c>subfolder</c> and <c>filename</c>.</summary>
public readonly record struct OutputFile(string Type, string Subfolder, string Filename);

/// <summary>
/// The output files each runtime produced for the prompts the server submitted through this node (review item 5), read
/// from the runtime's <c>/history/{id}</c> answers to the server (that route takes only those prompts). <c>/view</c>
/// serves only these: never the owner's own files, which ComfyUI names in sequence (<c>ComfyUI_00001_.png</c>…), so a
/// name could be guessed. Bounded: at most <see cref="PerPrompt"/> files from one answer, the most recent
/// <see cref="PerRuntime"/> per runtime, each for <see cref="MaxAge"/>. In memory: after an agent restart the server's
/// earlier outputs are no longer served, as its earlier prompts can no longer be stopped.
/// </summary>
public sealed class ServerOutputs(TimeProvider time)
{
    public const int PerRuntime = 4096;
    public const int PerPrompt = 256;
    public static readonly TimeSpan MaxAge = TimeSpan.FromHours(24);

    private readonly object _gate = new();
    private readonly Dictionary<string, (Dictionary<OutputFile, DateTimeOffset> At, Queue<(OutputFile File, DateTimeOffset At)> Order)> _byRuntime
        = new(StringComparer.Ordinal);

    public void Add(string runtime, IEnumerable<OutputFile> files)
    {
        var now = time.GetUtcNow();
        lock (_gate)
        {
            if (!_byRuntime.TryGetValue(runtime, out var known))
                _byRuntime[runtime] = known = (new Dictionary<OutputFile, DateTimeOffset>(), new Queue<(OutputFile, DateTimeOffset)>());
            foreach (var file in files.Take(PerPrompt))
            {
                known.At[file] = now;
                known.Order.Enqueue((file, now));
            }
            // The oldest first: past the cap, or past the age. A file recorded again later stays under its later time.
            while (known.Order.Count > 0 && (known.Order.Count > PerRuntime || now - known.Order.Peek().At > MaxAge))
            {
                var (file, at) = known.Order.Dequeue();
                if (known.At.TryGetValue(file, out var latest) && latest == at) known.At.Remove(file);
            }
        }
    }

    public bool Contains(string runtime, OutputFile file)
    {
        lock (_gate)
            return _byRuntime.TryGetValue(runtime, out var known) && known.At.TryGetValue(file, out var at)
                   && time.GetUtcNow() - at <= MaxAge;
    }

    /// <summary>The files a <c>/history/{id}</c> answer lists for that prompt (<c>{id: {outputs: {node: {kind: [file…]}}}}</c>).</summary>
    internal static List<OutputFile> FilesOf(ReadOnlySpan<byte> answer, string promptId)
    {
        var files = new List<OutputFile>();
        try
        {
            using var json = JsonDocument.Parse(answer.ToArray());
            if (json.RootElement.ValueKind != JsonValueKind.Object
                || !json.RootElement.TryGetProperty(promptId, out var entry) || entry.ValueKind != JsonValueKind.Object
                || !entry.TryGetProperty("outputs", out var outputs) || outputs.ValueKind != JsonValueKind.Object)
                return files;
            foreach (var node in outputs.EnumerateObject())
            {
                if (node.Value.ValueKind != JsonValueKind.Object) continue;
                foreach (var kind in node.Value.EnumerateObject())
                {
                    if (kind.Value.ValueKind != JsonValueKind.Array) continue;
                    foreach (var item in kind.Value.EnumerateArray())
                    {
                        if (files.Count >= PerPrompt) return files;
                        if (File(item) is { } file) files.Add(file);
                    }
                }
            }
        }
        catch (JsonException)
        {
        }
        return files;
    }

    /// <summary>One listed file, as <c>/view</c> would ask for it (no subfolder is "", no type is ComfyUI's "output").</summary>
    private static OutputFile? File(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object || !Text(item, "filename", out var filename) || filename is null) return null;
        if (!Text(item, "subfolder", out var subfolder) || !Text(item, "type", out var type)) return null;
        var file = new OutputFile(type ?? "output", subfolder ?? "", filename);
        return file.Type is "output" or "temp" && RuntimeRoutes.IsFileName(file.Filename) && RuntimeRoutes.IsSubfolder(file.Subfolder)
            ? file
            : null;
    }

    /// <summary>A member that is absent (null value) or a string; false for anything else.</summary>
    private static bool Text(JsonElement item, string name, out string? value)
    {
        value = null;
        if (!item.TryGetProperty(name, out var member)) return true;
        if (member.ValueKind != JsonValueKind.String) return false;
        value = member.GetString();
        return true;
    }
}

/// <summary>
/// A <c>/history/{id}</c> answer on its way back to the server: passed through as it is read, its first
/// <see cref="Keep"/> bytes kept, and the prompt's output files recorded once the runtime ended it (before the node's
/// CLOSE, so before the server can ask <c>/view</c> for them). A longer answer records nothing.
/// </summary>
internal sealed class HistoryAnswerRecorder(Stream inner, Action<ReadOnlyMemory<byte>> record) : Stream
{
    public const int Keep = 16 * 1024 * 1024;
    private readonly MemoryStream _kept = new();
    private bool _over;
    private bool _recorded;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        var read = await inner.ReadAsync(buffer, ct);
        if (read > 0)
        {
            if (_kept.Length + read > Keep) _over = true;
            else if (!_over) _kept.Write(buffer.Span[..read]);
        }
        else if (!_recorded)
        {
            _recorded = true;
            if (!_over) record(_kept.GetBuffer().AsMemory(0, (int)_kept.Length));
        }
        return read;
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
        if (disposing) inner.Dispose();
        base.Dispose(disposing);
    }
}
