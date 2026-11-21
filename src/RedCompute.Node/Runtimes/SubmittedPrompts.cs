using System.Text.Json;

namespace RedCompute.Node.Runtimes;

/// <summary>
/// The prompt ids each runtime gave the server through this node's forward (review P4-P7, item 4): what ComfyUI
/// answered to a <c>POST /prompt</c> that passed here. The routes that stop or dequeue a prompt (<c>/interrupt</c>,
/// <c>/queue</c> delete, <c>/api/jobs/{id}/cancel</c>) act only on these, never on the owner's own prompts. The ids
/// come from the runtime's answer, never from the server (a <c>/prompt</c> body may not choose its <c>prompt_id</c>).
/// In memory, the most recent <see cref="PerRuntime"/> per runtime: after an agent restart the server's earlier prompts
/// can no longer be stopped through the node, and their jobs fail as the link drops.
/// </summary>
public sealed class SubmittedPrompts
{
    public const int PerRuntime = 1024;

    private readonly object _gate = new();
    private readonly Dictionary<string, (HashSet<string> Ids, Queue<string> Order)> _byRuntime = new(StringComparer.Ordinal);

    public void Add(string runtime, string promptId)
    {
        lock (_gate)
        {
            if (!_byRuntime.TryGetValue(runtime, out var known))
                _byRuntime[runtime] = known = (new HashSet<string>(StringComparer.Ordinal), new Queue<string>());
            if (!known.Ids.Add(promptId)) return;
            known.Order.Enqueue(promptId);
            while (known.Order.Count > PerRuntime) known.Ids.Remove(known.Order.Dequeue());
        }
    }

    public bool Contains(string runtime, string promptId)
    {
        lock (_gate) return _byRuntime.TryGetValue(runtime, out var known) && known.Ids.Contains(promptId);
    }

    /// <summary>The <c>prompt_id</c> of a <c>/prompt</c> answer, or null.</summary>
    internal static string? PromptIdOf(ReadOnlySpan<byte> answer)
    {
        try
        {
            using var json = JsonDocument.Parse(answer.ToArray());
            return json.RootElement.ValueKind == JsonValueKind.Object
                   && json.RootElement.TryGetProperty("prompt_id", out var id) && id.ValueKind == JsonValueKind.String
                   && id.GetString() is { } value && RuntimeRoutes.IsId(value)
                ? value
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>
/// A <c>/prompt</c> answer on its way back to the server: passed through as it is read, its first 64 KiB kept, and the
/// <c>prompt_id</c> recorded once the runtime ended it (before the node's CLOSE, so before the server can act on it).
/// </summary>
internal sealed class PromptAnswerRecorder(Stream inner, Action<string> record) : Stream
{
    private const int Keep = 64 * 1024;
    private readonly MemoryStream _kept = new();
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
            if (_kept.Length < Keep) _kept.Write(buffer.Span[..(int)Math.Min(read, Keep - _kept.Length)]);
        }
        else if (!_recorded)
        {
            _recorded = true;
            if (SubmittedPrompts.PromptIdOf(_kept.GetBuffer().AsSpan(0, (int)_kept.Length)) is { } id) record(id);
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
