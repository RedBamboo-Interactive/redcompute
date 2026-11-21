using System.Text.Json;
using System.Text.Json.Nodes;

namespace RedCompute.NodeLink;

/// <summary>
/// Liveness for one connection (design 4.5): a PING every interval, and the link is dead after
/// <see cref="LinkOptions.MissedHeartbeats"/> intervals without any frame from the peer. A PONG gives
/// the round trip and the peer's clock offset. Monotonic time comes from the <see cref="TimeProvider"/>.
/// </summary>
public sealed class LinkHeartbeat
{
    private readonly TimeProvider _time;
    private readonly TimeSpan _interval;
    private readonly TimeSpan _deadAfter;
    private long _lastReceived;
    private long _lastPing;

    public LinkHeartbeat(LinkOptions options)
    {
        _time = options.Time;
        _interval = options.HeartbeatInterval;
        _deadAfter = options.DeadAfter;
        _lastReceived = _time.GetTimestamp();
        _lastPing = long.MinValue;
    }

    public TimeSpan? Rtt { get; private set; }

    /// <summary>The peer's wall clock minus ours, from the last PONG.</summary>
    public TimeSpan? ClockOffset { get; private set; }

    public void OnFrameReceived() => _lastReceived = _time.GetTimestamp();

    public bool PingDue()
        => _lastPing == long.MinValue || _time.GetElapsedTime(_lastPing) >= _interval;

    public bool IsDead() => _time.GetElapsedTime(_lastReceived) >= _deadAfter;

    public TimeSpan SinceLastFrame => _time.GetElapsedTime(_lastReceived);

    /// <summary>A PING payload: <c>{t, epoch}</c>, t in our wall-clock milliseconds.</summary>
    public byte[] NextPing(string? epoch)
    {
        _lastPing = _time.GetTimestamp();
        var body = new JsonObject { ["t"] = _time.GetUtcNow().ToUnixTimeMilliseconds() };
        if (epoch is not null) body["epoch"] = epoch;
        return JsonSerializer.SerializeToUtf8Bytes(body);
    }

    /// <summary>The PONG answering <paramref name="ping"/>: <c>{t, peerTime}</c>.</summary>
    public byte[] PongFor(ReadOnlySpan<byte> ping)
    {
        long t = 0;
        try
        {
            if (JsonNode.Parse(ping) is JsonObject body && body["t"] is JsonValue value) value.TryGetValue(out t);
        }
        catch (JsonException)
        {
        }
        return JsonSerializer.SerializeToUtf8Bytes(new JsonObject { ["t"] = t, ["peerTime"] = _time.GetUtcNow().ToUnixTimeMilliseconds() });
    }

    public void OnPong(ReadOnlySpan<byte> pong)
    {
        try
        {
            if (JsonNode.Parse(pong) is not JsonObject body) return;
            if (body["t"] is not JsonValue tv || !tv.TryGetValue<long>(out var t) || t <= 0) return;
            var now = _time.GetUtcNow().ToUnixTimeMilliseconds();
            var rtt = Math.Max(0, now - t);
            Rtt = TimeSpan.FromMilliseconds(rtt);
            if (body["peerTime"] is JsonValue pv && pv.TryGetValue<long>(out var peerTime))
                ClockOffset = TimeSpan.FromMilliseconds(peerTime - (t + rtt / 2.0));
        }
        catch (JsonException)
        {
        }
    }
}

/// <summary>
/// Redial delays (design 4.7): full jitter over 1 s, 2 s, 4 s ... capped at 60 s; the attempt count
/// resets once a link stays up 60 s; the first retry after a 1012 (service restart) is 1 s +/- 0.5 s.
/// </summary>
public sealed class ReconnectBackoff(Random random, TimeSpan? baseDelay = null, TimeSpan? cap = null, TimeSpan? resetAfter = null)
{
    private readonly TimeSpan _base = baseDelay ?? TimeSpan.FromSeconds(1);
    private readonly TimeSpan _cap = cap ?? TimeSpan.FromSeconds(60);
    private readonly TimeSpan _resetAfter = resetAfter ?? TimeSpan.FromSeconds(60);

    public int Attempt { get; private set; }

    /// <summary>The delay before the next dial, after a failure that ended with <paramref name="closeCode"/>.</summary>
    public TimeSpan Next(int? closeCode = null)
    {
        var attempt = Attempt++;
        if (attempt == 0 && closeCode == LinkCloseCodes.ServiceRestart)
            return TimeSpan.FromTicks((long)(_base.Ticks * (0.5 + random.NextDouble())));
        var ceiling = Math.Min(_cap.Ticks, _base.Ticks * Math.Pow(2, Math.Min(attempt, 30)));
        return TimeSpan.FromTicks((long)(random.NextDouble() * ceiling));
    }

    /// <summary>A link that stayed up at least the reset interval starts the schedule over.</summary>
    public void OnLinkEnded(TimeSpan upFor)
    {
        if (upFor >= _resetAfter) Attempt = 0;
    }

    public void Reset() => Attempt = 0;
}

/// <summary>
/// The wait after a verdict redialling cannot fix (needs update, needs re-pin, a revocation a pinned node could not
/// authenticate): the first wait, then twice as long each time up to the cap (an hour), each somewhere between half and
/// all of its step. One stray event costs a minute, not an hour; a lasting one settles at hourly. Reset once the link is up.
/// </summary>
public sealed class VerdictBackoff(Random random, TimeSpan first, TimeSpan cap)
{
    public int Attempt { get; private set; }

    public TimeSpan Next()
    {
        var attempt = Attempt++;
        var step = Math.Min(cap.Ticks, first.Ticks * Math.Pow(2, Math.Min(attempt, 30)));
        return TimeSpan.FromTicks((long)(step * (0.5 + random.NextDouble() / 2)));
    }

    public void Reset() => Attempt = 0;
}

/// <summary>What the node does after a dial or a link ends (design 4.7).</summary>
public enum DialAction
{
    /// <summary>Network error, 502/503/504, 1012, 1001, a timeout: back off and retry forever.</summary>
    Retry,
    /// <summary>401 assertion_invalid with serverTime: fix the clock offset, retry once at once.</summary>
    RetryNowWithServerTime,
    /// <summary>
    /// 403 node_revoked, 404 node_unknown, close 4403: stop, clean up locally. A node with E2E acts only on the
    /// server's sealed, in-band verdict; for it an HTTP status or a bare close code is <see cref="StopUnverified"/>.
    /// </summary>
    Stop,
    /// <summary>426 or close 4426: "needs update", retry hourly.</summary>
    NeedsUpdate,
    /// <summary>429 or close 4429: honour Retry-After.</summary>
    RetryAfter,
    /// <summary>Close 4409: another connection of this node won; back off.</summary>
    Superseded,
    /// <summary>
    /// Close 4495, 4496 or 4497: the encrypted link cannot be set up with the key pinned (or not pinned) at
    /// pairing. The node keeps its pin, never re-pins by itself, and retries hourly until its owner re-pins it.
    /// </summary>
    NeedsRepin,
    /// <summary>
    /// A stop an E2E node cannot authenticate (an HTTP 403/404, or 4403 without the in-band verdict): it deletes
    /// nothing, says "revoked, unverified" and retries with <see cref="VerdictBackoff"/>.
    /// </summary>
    StopUnverified,
}

/// <summary>A dial the server refused before the WebSocket opened.</summary>
public sealed class LinkDialException(int? httpStatus, string? errorCode, string message, TimeSpan? retryAfter = null,
    DateTimeOffset? serverTime = null) : Exception(message)
{
    public int? HttpStatus { get; } = httpStatus;
    public string? ErrorCode { get; } = errorCode;
    public TimeSpan? RetryAfter { get; } = retryAfter;
    public DateTimeOffset? ServerTime { get; } = serverTime;
}

public static class DialOutcomes
{
    public static DialAction Classify(int? httpStatus, string? errorCode, int? closeCode)
    {
        switch (httpStatus)
        {
            case 401 when errorCode == "assertion_invalid": return DialAction.RetryNowWithServerTime;
            case 403 or 404: return DialAction.Stop;
            case 426: return DialAction.NeedsUpdate;
            case 429: return DialAction.RetryAfter;
        }
        return closeCode switch
        {
            LinkCloseCodes.Revoked => DialAction.Stop,
            LinkCloseCodes.UpgradeRequired => DialAction.NeedsUpdate,
            LinkCloseCodes.RateLimited => DialAction.RetryAfter,
            LinkCloseCodes.Superseded => DialAction.Superseded,
            LinkCloseCodes.LinkKeyUnknown or LinkCloseCodes.E2eRequired or LinkCloseCodes.LinkIdentityInvalid => DialAction.NeedsRepin,
            _ => DialAction.Retry,
        };
    }
}
