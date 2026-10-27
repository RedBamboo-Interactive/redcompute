using System.Text;
using System.Text.Json;
using RedCompute.Core.Sessions;

namespace RedCompute.App.Services.SessionArtifacts;

/// <summary>Deduplicating accumulator: folds per-record candidates into one artifact per dedup key.</summary>
internal sealed class SessionArtifactAccumulator
{
    private sealed class Entry
    {
        public required ArtifactCandidate Base { get; init; }
        public string? Title { get; set; }
        public int? Line { get; set; }
        public required ArtifactOccurrence First { get; init; }
        public required ArtifactOccurrence Last { get; set; }
        public int Occurrences { get; set; }
        // Latest occurrence per origin, so a filtered read can answer for the origins it asked about.
        public Dictionary<string, (string Role, ArtifactOccurrence At)> Origins { get; } = new(StringComparer.Ordinal);
    }

    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    public int Count => _entries.Count;

    public void Add(UnifiedMessageRecord record, IReadOnlyList<ArtifactCandidate> candidates)
    {
        if (candidates.Count == 0) return;
        var at = new ArtifactOccurrence
        {
            MessageUid = record.MessageUid,
            RecordId = record.Id,
            Sequence = record.Sequence ?? record.Id,
            Timestamp = record.Timestamp,
        };

        foreach (var candidate in candidates)
        {
            if (!_entries.TryGetValue(candidate.Key, out var entry))
            {
                entry = new Entry
                {
                    Base = candidate, Title = candidate.Title, Line = candidate.File?.Line,
                    First = at, Last = at,
                };
                _entries[candidate.Key] = entry;
            }
            else
            {
                entry.Last = at;
                entry.Title ??= candidate.Title;
                entry.Line ??= candidate.File?.Line;
            }

            entry.Occurrences++;
            entry.Origins[candidate.Origin] = (candidate.Role, at);
        }
    }

    /// <summary>Artifacts matching the filters, ordered by last occurrence, newest first.</summary>
    public List<SessionArtifact> Query(
        ISet<string>? kinds, ISet<string>? origins, long? sinceSequence)
    {
        var items = new List<SessionArtifact>();
        foreach (var (key, entry) in _entries)
        {
            if (kinds is not null && !kinds.Contains(entry.Base.Kind)) continue;
            if (sinceSequence is { } since && entry.Last.Sequence <= since) continue;

            var origin = default(KeyValuePair<string, (string Role, ArtifactOccurrence At)>);
            var matched = false;
            foreach (var pair in entry.Origins)
            {
                if (origins is not null && !origins.Contains(pair.Key)) continue;
                if (matched && Newer(origin.Value.At, pair.Value.At)) continue;
                origin = pair;
                matched = true;
            }
            if (!matched) continue;

            var b = entry.Base;
            items.Add(new SessionArtifact
            {
                Key = key,
                Kind = b.Kind,
                Origin = origin.Key,
                Role = origin.Value.Role,
                Target = b.Target,
                Title = entry.Title,
                MediaType = b.MediaType,
                Name = b.Name,
                Size = b.Size,
                Entity = b.Entity,
                File = b.File is null ? null : new ArtifactFileRef { Path = b.File.Path, Line = entry.Line },
                PayloadRef = b.PayloadRef,
                Internal = b.Kind == ArtifactKinds.Link && b.Internal ? true : null,
                First = entry.First,
                Last = entry.Last,
                Occurrences = entry.Occurrences,
            });
        }

        items.Sort(Compare);
        return items;
    }

    private static bool Newer(ArtifactOccurrence current, ArtifactOccurrence candidate)
        => current.Sequence > candidate.Sequence
            || (current.Sequence == candidate.Sequence && current.RecordId >= candidate.RecordId);

    /// <summary>Newest last-occurrence first; the key breaks ties so paging is deterministic.</summary>
    internal static int Compare(SessionArtifact a, SessionArtifact b)
    {
        var bySequence = b.Last.Sequence.CompareTo(a.Last.Sequence);
        if (bySequence != 0) return bySequence;
        var byRecord = b.Last.RecordId.CompareTo(a.Last.RecordId);
        return byRecord != 0 ? byRecord : string.CompareOrdinal(a.Key, b.Key);
    }
}

internal sealed record SessionArtifactFilter(
    ISet<string>? Kinds = null, ISet<string>? Origins = null, long? SinceSequence = null);

internal sealed record SessionArtifactSnapshot(long ThroughSequence, IReadOnlyList<SessionArtifact> Items);

/// <summary>Reads transcript records strictly after a record id, ascending (the reader's `after` page).</summary>
internal delegate Task<TranscriptPageReadResult> ArtifactRecordReader(long afterRecordId, CancellationToken ct);

/// <summary>
/// In-process artifact index per (sessionId, epoch). Nothing is persisted: a cold scan walks the
/// canonical records once, later requests extend it from a record watermark. Bounded by LRU.
/// </summary>
internal sealed class SessionArtifactIndex
{
    public const int DefaultCapacity = 64;

    private sealed class State(string epoch)
    {
        public readonly SemaphoreSlim Gate = new(1, 1);
        public string Epoch { get; set; } = epoch;
        public string? ProjectPath { get; set; }
        public SessionArtifactAccumulator Accumulator { get; set; } = new();
        public long WatermarkRecordId { get; set; }
        public long LastSequence { get; set; } = long.MinValue;
        public long ThroughSequence { get; set; }
    }

    private readonly int _capacity;
    private readonly object _lru = new();
    private readonly Dictionary<string, LinkedListNode<(string Key, State State)>> _nodes = new(StringComparer.Ordinal);
    private readonly LinkedList<(string Key, State State)> _order = new();

    public SessionArtifactIndex(int capacity = DefaultCapacity)
    {
        _capacity = Math.Max(1, capacity);
    }

    public int SessionCount { get { lock (_lru) return _nodes.Count; } }

    public bool Contains(string sessionId)
    {
        lock (_lru) return _nodes.ContainsKey(sessionId);
    }

    /// <summary>Bring the session's index up to date with the canonical records, then answer the filter.</summary>
    public async Task<SessionArtifactSnapshot> QueryAsync(
        string sessionId, string epoch, ArtifactRecordReader read, SessionArtifactFilter filter, CancellationToken ct,
        string? projectPath = null)
    {
        var state = Acquire(sessionId, epoch);
        await state.Gate.WaitAsync(ct);
        try
        {
            // The working folder decides which written files count, so a changed folder rebuilds.
            if (!string.Equals(state.Epoch, epoch, StringComparison.Ordinal)
                || !string.Equals(state.ProjectPath, projectPath, StringComparison.Ordinal))
            {
                Reset(state, epoch);
                state.ProjectPath = projectPath;
            }

            await ExtendAsync(state, epoch, read, ct);
            return new SessionArtifactSnapshot(
                state.ThroughSequence,
                state.Accumulator.Query(filter.Kinds, filter.Origins, filter.SinceSequence));
        }
        finally
        {
            state.Gate.Release();
        }
    }

    private static void Reset(State state, string epoch)
    {
        state.Epoch = epoch;
        state.Accumulator = new SessionArtifactAccumulator();
        state.WatermarkRecordId = 0;
        state.LastSequence = long.MinValue;
        state.ThroughSequence = 0;
    }

    private static async Task ExtendAsync(State state, string epoch, ArtifactRecordReader read, CancellationToken ct)
    {
        while (true)
        {
            var page = await read(state.WatermarkRecordId, ct);
            if (page.Messages.Count == 0)
            {
                // Records vanished behind the watermark (transcript cleared): rebuild from nothing.
                if (state.WatermarkRecordId > 0 && !page.HasEarlier)
                    Reset(state, epoch);
                return;
            }

            // Sequence going backwards means the transcript was rewritten under the watermark.
            var first = page.Messages[0].Sequence;
            if (state.WatermarkRecordId > 0 && first is { } firstSequence && firstSequence < state.LastSequence)
            {
                Reset(state, epoch);
                continue;
            }

            foreach (var message in page.Messages)
            {
                state.Accumulator.Add(message, SessionArtifactExtractor.Extract(message, state.ProjectPath));
                if (message.Sequence is { } sequence)
                {
                    state.LastSequence = Math.Max(state.LastSequence, sequence);
                    if (string.Equals(message.Epoch, epoch, StringComparison.Ordinal))
                        state.ThroughSequence = Math.Max(state.ThroughSequence, sequence);
                }
            }

            state.WatermarkRecordId = page.NewestRecordId ?? state.WatermarkRecordId;
            if (!page.HasLater) return;
        }
    }

    private State Acquire(string sessionId, string epoch)
    {
        lock (_lru)
        {
            if (_nodes.TryGetValue(sessionId, out var node))
            {
                _order.Remove(node);
                _order.AddFirst(node);
                return node.Value.State;
            }

            var state = new State(epoch);
            _nodes[sessionId] = _order.AddFirst((sessionId, state));
            while (_nodes.Count > _capacity)
            {
                var oldest = _order.Last!;
                _order.RemoveLast();
                _nodes.Remove(oldest.Value.Key);
            }
            return state;
        }
    }
}

/// <summary>Opaque keyset cursor over the index ordering (last sequence, last record, key).</summary>
internal static class SessionArtifactCursor
{
    private sealed record Payload(string S, string E, long Q, long R, string K);

    public static string Encode(string sessionId, string epoch, SessionArtifact last)
        => Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(
                new Payload(sessionId, epoch, last.Last.Sequence, last.Last.RecordId, last.Key)))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>Throws <see cref="TranscriptPageCursorException"/> for malformed or foreign cursors.</summary>
    public static (long Sequence, long RecordId, string Key) Decode(string cursor, string sessionId, string epoch)
    {
        Payload? payload;
        try
        {
            var padded = cursor.Replace('-', '+').Replace('_', '/');
            padded = padded.PadRight(padded.Length + (4 - padded.Length % 4) % 4, '=');
            payload = JsonSerializer.Deserialize<Payload>(Convert.FromBase64String(padded));
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            throw new TranscriptPageCursorException("invalid_cursor", "Artifact cursor is malformed");
        }

        if (payload is null || payload.K is null || payload.S is null || payload.E is null)
            throw new TranscriptPageCursorException("invalid_cursor", "Artifact cursor is malformed");
        if (!string.Equals(payload.S, sessionId, StringComparison.Ordinal))
            throw new TranscriptPageCursorException("transcript_cursor_mismatch", "Cursor belongs to another session");
        if (!string.Equals(payload.E, epoch, StringComparison.Ordinal))
            throw new TranscriptPageCursorException("transcript_cursor_stale", "Cursor belongs to a previous epoch");
        return (payload.Q, payload.R, payload.K);
    }

    /// <summary>Items strictly after the cursor position, at most <paramref name="limit"/>, plus the next cursor.</summary>
    public static (List<SessionArtifact> Items, string? NextCursor) Slice(
        IReadOnlyList<SessionArtifact> ordered, (long Sequence, long RecordId, string Key)? after,
        int limit, string sessionId, string epoch)
    {
        var start = 0;
        if (after is { } position)
        {
            // The first item that sorts strictly after the cursor position continues the page.
            start = ordered.Count;
            for (var i = 0; i < ordered.Count; i++)
            {
                var item = ordered[i];
                var order = item.Last.Sequence.CompareTo(position.Sequence);
                if (order == 0) order = item.Last.RecordId.CompareTo(position.RecordId);
                if (order == 0) order = string.CompareOrdinal(position.Key, item.Key);
                if (order < 0) { start = i; break; }
            }
        }

        var page = ordered.Skip(start).Take(limit).ToList();
        var hasMore = start + page.Count < ordered.Count;
        return (page, hasMore && page.Count > 0 ? Encode(sessionId, epoch, page[^1]) : null);
    }
}
