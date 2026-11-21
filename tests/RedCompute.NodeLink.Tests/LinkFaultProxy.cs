using RedCompute.NodeLink;

namespace RedCompute.NodeLink.Tests;

/// <summary>
/// What the proxy does to frames (design 8.2). Probabilities apply per frame and direction; the
/// one-off events (cut, half-open, corrupt) hit the first connections at a random frame, so every run
/// still finishes.
/// </summary>
internal sealed record FaultSchedule(string Name, double Drop = 0, double Duplicate = 0, double Reorder = 0, double Delay = 0,
    double SlowReader = 0, int CutConnections = 0, int HalfOpenConnections = 0, int CorruptConnections = 0, int TamperConnections = 0)
{
    public override string ToString() => Name;

    public static IReadOnlyList<FaultSchedule> All { get; } =
    [
        new("clean"),
        new("drop", Drop: 0.04),
        new("duplicate", Duplicate: 0.05),
        new("reorder", Reorder: 0.05),
        new("delay", Delay: 0.05),
        new("slow-reader", SlowReader: 0.01),
        new("cut", CutConnections: 3),
        new("half-open", HalfOpenConnections: 1),
        new("corrupt", CorruptConnections: 3),
        new("mixed", Drop: 0.01, Duplicate: 0.01, Reorder: 0.01, Delay: 0.01, SlowReader: 0.002, CutConnections: 1, CorruptConnections: 1),
    ];

    /// <summary>
    /// The schedules for an end-to-end encrypted link: all of the above, plus <c>tamper</c> (flip one bit
    /// anywhere in a message). A plaintext link has no integrity of its own (TLS gives it), so tamper runs
    /// encrypted only.
    /// </summary>
    public static IReadOnlyList<FaultSchedule> Encrypted { get; } = [.. All, new("tamper", TamperConnections: 3)];

    public static FaultSchedule Named(string name) => Encrypted.Single(s => s.Name == name);
}

/// <summary>
/// An in-process man-in-the-middle between the link client and the server (design 8.2): every dial
/// gets a fresh pair of pipes with two pumps that drop, duplicate (at once or k frames later),
/// reorder, delay, stall, cut, go half-open (stop forwarding, no close) or corrupt a header, all from
/// a seeded schedule so a failing seed reproduces.
/// </summary>
internal sealed class LinkFaultProxy(FaultSchedule schedule, int seed, Func<ILinkTransport, Task> accept)
{
    private int _connections;

    public int Connections => Volatile.Read(ref _connections);
    public int Corrupted;
    public int Tampered;
    public int Dropped;

    /// <summary>Messages relayed that were not a Noise handshake message or record (an encrypted link relays none).</summary>
    public int PlaintextMessages;

    /// <summary>Refuses every further dial as the kernel would refuse a revoked node.</summary>
    public bool Refuse { get; set; }

    private sealed class Connection
    {
        public int Frames;
        public int EventAt;
        public string? Event;
        public volatile bool HalfOpen;
    }

    public Task<ILinkTransport> DialAsync(CancellationToken ct)
    {
        if (Refuse) throw new LinkDialException(403, "node_revoked", "This node was revoked.");
        var index = Interlocked.Increment(ref _connections);
        var random = new Random(HashCode.Combine(seed, index, schedule.Name));
        var connection = new Connection();
        if (index <= schedule.CutConnections) (connection.Event, connection.EventAt) = ("cut", random.Next(10, 300));
        else if (index <= schedule.HalfOpenConnections) (connection.Event, connection.EventAt) = ("half-open", random.Next(10, 300));
        else if (index <= schedule.CorruptConnections) (connection.Event, connection.EventAt) = ("corrupt", random.Next(10, 300));
        else if (index <= schedule.TamperConnections) (connection.Event, connection.EventAt) = ("tamper", random.Next(10, 300));

        var (node, proxyNode) = LinkPipe.Create(16);
        var (proxyServer, server) = LinkPipe.Create(16);
        _ = Task.Run(() => accept(server), CancellationToken.None);
        _ = Task.Run(() => PumpAsync(proxyNode, proxyServer, new Random(random.Next()), connection, ct), CancellationToken.None);
        _ = Task.Run(() => PumpAsync(proxyServer, proxyNode, new Random(random.Next()), connection, ct), CancellationToken.None);
        return Task.FromResult(node);
    }

    private async Task PumpAsync(ILinkTransport from, ILinkTransport to, Random random, Connection connection, CancellationToken ct)
    {
        var held = new List<(byte[] Message, int After)>();
        try
        {
            while (true)
            {
                byte[] message;
                try
                {
                    message = await from.ReceiveAsync(ct);
                }
                catch (LinkClosedException closed)
                {
                    if (!connection.HalfOpen) await to.CloseAsync(closed.Code ?? LinkCloseCodes.Abnormal, closed.Reason, CancellationToken.None);
                    return;
                }
                if (connection.HalfOpen) continue;
                if (message.Length == 0 || message[0] is not (Noise.NoiseLink.InitiatorHandshake or Noise.NoiseLink.ResponderHandshake or Noise.NoiseLink.Record))
                    Interlocked.Increment(ref PlaintextMessages);

                var n = Interlocked.Increment(ref connection.Frames);
                if (n == connection.EventAt)
                {
                    switch (connection.Event)
                    {
                        case "cut":
                            await from.CloseAsync(LinkCloseCodes.Abnormal, "cut", CancellationToken.None);
                            await to.CloseAsync(LinkCloseCodes.Abnormal, "cut", CancellationToken.None);
                            return;
                        case "half-open":
                            connection.HalfOpen = true;
                            continue;
                        case "corrupt":
                            message = (byte[])message.Clone();
                            message[0] = 0x7F; // an unknown format: the receiver must close 4400, never deliver
                            Interlocked.Increment(ref Corrupted);
                            break;
                        case "tamper":
                            message = (byte[])message.Clone();
                            message[random.Next(message.Length)] ^= (byte)(1 << random.Next(8)); // anywhere: the AEAD must refuse it
                            Interlocked.Increment(ref Tampered);
                            break;
                    }
                }
                if (Roll(random, schedule.Drop))
                {
                    Interlocked.Increment(ref Dropped);
                    continue;
                }
                if (Roll(random, schedule.Delay)) await Task.Delay(random.Next(1, 15), ct);
                if (Roll(random, schedule.SlowReader)) await Task.Delay(random.Next(20, 80), ct);
                if (Roll(random, schedule.Reorder))
                {
                    held.Add((message, random.Next(1, 5)));
                    continue;
                }
                await to.SendAsync(message, ct);
                if (Roll(random, schedule.Duplicate))
                {
                    if (random.Next(2) == 0) await to.SendAsync(message, ct);
                    else held.Add((message, random.Next(1, 9)));
                }
                for (var i = 0; i < held.Count; i++)
                {
                    var (heldMessage, after) = held[i];
                    if (after > 1)
                    {
                        held[i] = (heldMessage, after - 1);
                        continue;
                    }
                    held.RemoveAt(i--);
                    await to.SendAsync(heldMessage, ct);
                }
            }
        }
        catch (LinkClosedException)
        {
            await from.CloseAsync(LinkCloseCodes.Abnormal, "peer gone", CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static bool Roll(Random random, double p) => p > 0 && random.NextDouble() < p;
}
