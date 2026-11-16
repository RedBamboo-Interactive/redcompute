using System.Text;
using System.Text.Json.Nodes;

namespace RedCompute.Node;

/// <summary>
/// The agent's log (design 2.4): <c>leaf-node.log</c> rolled at 5 MB, five files kept. Lines are UTC
/// timestamped. A verb run from a terminal also writes to the console.
/// </summary>
public sealed class NodeLog : IDisposable
{
    public const long MaxBytes = 5 * 1024 * 1024;
    public const int Files = 5;

    private readonly object _gate = new();
    private readonly string _path;
    private readonly TextWriter? _console;
    private FileStream? _stream;

    public NodeLog(string path, TextWriter? console = null)
    {
        _path = path;
        _console = console;
    }

    /// <summary>A log that writes nowhere (tests that do not look at it).</summary>
    public static NodeLog None { get; } = new("", null);

    public void Info(string message) => Write("INF", message);
    public void Warn(string message) => Write("WRN", message);
    public void Error(string message) => Write("ERR", message);

    private void Write(string level, string message)
    {
        var line = $"{DateTimeOffset.UtcNow:yyyy-MM-ddTHH:mm:ss.fffZ} {level} {message}";
        lock (_gate)
        {
            _console?.WriteLine(message);
            if (_path.Length == 0) return;
            try
            {
                if (_stream is null)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                    _stream = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                }
                var bytes = Encoding.UTF8.GetBytes(line + "\n");
                if (_stream.Length + bytes.Length > MaxBytes) Roll();
                _stream!.Write(bytes);
                _stream.Flush();
            }
            catch (IOException)
            {
                // A full disk must not stop the agent; the line is lost.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private void Roll()
    {
        _stream?.Dispose();
        _stream = null;
        for (var i = Files - 1; i >= 1; i--)
        {
            var from = i == 1 ? _path : $"{_path}.{i - 1}";
            if (File.Exists(from)) File.Move(from, $"{_path}.{i}", overwrite: true);
        }
        _stream = new FileStream(_path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
    }

    /// <summary>The last <paramref name="lines"/> lines of the current file (the <c>logs</c> verb).</summary>
    public IReadOnlyList<string> Tail(int lines)
    {
        lock (_gate)
        {
            if (_path.Length == 0 || !File.Exists(_path)) return [];
            using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var queue = new Queue<string>(lines + 1);
            while (reader.ReadLine() is { } line)
            {
                queue.Enqueue(line);
                if (queue.Count > lines) queue.Dequeue();
            }
            return queue.ToList();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _stream?.Dispose();
            _stream = null;
        }
    }
}

/// <summary>
/// The local audit log (design 4.9, 6.2): one JSON line per server request, refused or answered, and
/// per channel the server opens, in <c>audit/yyyy-MM-dd.jsonl</c>; the newest 30 days are kept. It is
/// the node owner's record of what the server asked; it never holds payloads.
/// </summary>
public sealed class AuditLog
{
    public const int KeepDays = 30;

    private readonly object _gate = new();
    private readonly string _folder;
    private readonly TimeProvider _time;
    private string? _pruned;

    public AuditLog(string folder, TimeProvider? time = null)
    {
        _folder = folder;
        _time = time ?? TimeProvider.System;
    }

    public string Folder => _folder;

    /// <summary>Appends one record: <c>{at, kind, name, outcome, detail?}</c>.</summary>
    public void Record(string kind, string name, string outcome, string? detail = null, string? linkSessionId = null)
    {
        var now = _time.GetUtcNow();
        var line = new JsonObject
        {
            ["at"] = now.ToString("O"),
            ["kind"] = kind,
            ["name"] = name,
            ["outcome"] = outcome,
        };
        if (detail is not null) line["detail"] = detail;
        if (linkSessionId is not null) line["linkSession"] = linkSessionId;
        var day = now.ToString("yyyy-MM-dd");
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(_folder);
                using (var stream = new FileStream(Path.Combine(_folder, day + ".jsonl"), FileMode.Append, FileAccess.Write,
                           FileShare.ReadWrite | FileShare.Delete))
                {
                    stream.Write(Encoding.UTF8.GetBytes(line.ToJsonString() + "\n"));
                }
                if (_pruned != day)
                {
                    _pruned = day;
                    foreach (var old in Directory.EnumerateFiles(_folder, "*.jsonl").Order(StringComparer.Ordinal).SkipLast(KeepDays))
                        File.Delete(old);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>Every record, oldest first (tests and <c>leaf-node status</c>'s counts).</summary>
    public IReadOnlyList<JsonObject> Read()
    {
        lock (_gate)
        {
            if (!Directory.Exists(_folder)) return [];
            return Directory.EnumerateFiles(_folder, "*.jsonl").Order(StringComparer.Ordinal)
                .SelectMany(File.ReadLines)
                .Select(line => JsonNode.Parse(line) as JsonObject)
                .OfType<JsonObject>()
                .ToList();
        }
    }
}
