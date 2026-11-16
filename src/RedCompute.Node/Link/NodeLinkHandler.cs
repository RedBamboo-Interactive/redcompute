using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using RedCompute.Node.Reports;
using RedCompute.NodeLink;

namespace RedCompute.Node.Link;

/// <summary>
/// The node's side of the link in Phase 3 (design 4.9, 6.2): it answers <c>node.describe</c>,
/// <c>node.hardware</c>, <c>node.cli</c> and <c>diag.echo.open</c>, accepts <c>diag.echo</c> channels
/// (echoing every byte back), and refuses everything else with <c>method_not_allowed</c> or
/// <c>class_not_supported</c>. Every request and channel, answered or refused, gets a line in the
/// local audit log. There is no file, process, port or shell primitive to reach.
/// </summary>
public sealed class NodeLinkHandler(string nodeId, NodeReporter reporter, AuditLog audit, NodeLog log,
    Func<string?> credentialStore, Func<LinkSession, JsonArray, Task>? cliChanged = null) : LinkHandler
{
    public const string Describe = "node.describe";
    public const string Hardware = "node.hardware";
    public const string Cli = "node.cli";
    public const string EchoOpen = "diag.echo.open";
    public const string Report = "node.report";
    public const string Bye = "link.bye";

    /// <summary>The largest echo run the server may ask for (the acceptance test sends 256 MiB).</summary>
    public const long MaxEchoBytes = 4L * 1024 * 1024 * 1024;

    private readonly ConcurrentDictionary<LinkChannelHandle, byte> _echoes = new();

    public int OpenEchoChannels => _echoes.Count;

    public override ChannelAcceptance? AcceptChannel(LinkSession session, ChannelOpenInfo info)
    {
        if (info.Class == ChannelClasses.DiagEcho.Name && session.HasFeature(LinkFeatures.DiagEcho))
        {
            audit.Record("channel", info.Class, "accepted", $"channel {info.Id}", session.LinkSessionId);
            return new ChannelAcceptance(new EchoHandler(this));
        }
        audit.Record("channel", info.Class, "refused", $"channel {info.Id}: class not supported", session.LinkSessionId);
        return null;
    }

    public override async ValueTask<JsonNode?> OnRequestAsync(LinkSession session, string method, JsonElement parameters,
        CancellationToken ct)
    {
        JsonNode? answer;
        try
        {
            answer = method switch
            {
                Describe when session.HasFeature(LinkFeatures.DescribeV1)
                    => await reporter.DescribeAsync(nodeId, session, credentialStore(), ct),
                Hardware when session.HasFeature(LinkFeatures.HardwareV1)
                    => await reporter.HardwareAsync(nodeId, ct),
                Cli when session.HasFeature(LinkFeatures.CliV1) => await CliAsync(session, parameters, ct),
                EchoOpen when session.HasFeature(LinkFeatures.DiagEcho) => EchoOpenAnswer(parameters),
                _ => null,
            };
        }
        catch (LinkRpcException error)
        {
            audit.Record("request", method, "error", error.Code, session.LinkSessionId);
            throw;
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            audit.Record("request", method, "error", error.GetType().Name, session.LinkSessionId);
            log.Warn($"{method} failed: {error.Message}");
            throw new LinkRpcException(LinkErrorCodes.Internal, $"{method} failed on the node.");
        }
        if (answer is null)
        {
            audit.Record("request", method, "refused", "method not allowed", session.LinkSessionId);
            throw new LinkRpcException(LinkErrorCodes.MethodNotAllowed, $"This node does not do '{method}'.");
        }
        audit.Record("request", method, "answered", null, session.LinkSessionId);
        return answer;
    }

    public override ValueTask OnNotificationAsync(LinkSession session, string method, JsonElement parameters, CancellationToken ct)
    {
        audit.Record("notification", method, "ignored", null, session.LinkSessionId);
        return ValueTask.CompletedTask;
    }

    /// <summary>The kill switch in Phase 3 (Architecture 4.5): there is no work, so it ends every echo channel.</summary>
    public int KillAll()
    {
        var killed = 0;
        foreach (var channel in _echoes.Keys)
        {
            if (!_echoes.TryRemove(channel, out _)) continue;
            try
            {
                channel.Reset("killed_by_owner", "The node's owner ran kill-all.");
                killed++;
            }
            catch (Exception error) when (error is LinkChannelResetException or InvalidOperationException or ObjectDisposedException)
            {
            }
        }
        audit.Record("local", "kill-all", "done", $"{killed} channel(s)");
        return killed;
    }

    private async Task<JsonNode> CliAsync(LinkSession session, JsonElement parameters, CancellationToken ct)
    {
        var refresh = parameters.ValueKind == JsonValueKind.Object
                      && parameters.TryGetProperty("refresh", out var value) && value.ValueKind == JsonValueKind.True;
        var (clis, changed) = await reporter.CliAsync(refresh, ct);
        if (changed && cliChanged is not null) _ = cliChanged(session, (JsonArray)clis.DeepClone());
        return new JsonObject { ["clis"] = clis };
    }

    private static JsonNode EchoOpenAnswer(JsonElement parameters)
    {
        long bytes = 0;
        if (parameters.ValueKind == JsonValueKind.Object && parameters.TryGetProperty("bytes", out var value)
            && !value.TryGetInt64(out bytes))
            throw new LinkRpcException(LinkErrorCodes.InvalidParams, "bytes must be a whole number.");
        if (bytes is < 0 or > MaxEchoBytes)
            throw new LinkRpcException(LinkErrorCodes.InvalidParams, $"bytes must be between 0 and {MaxEchoBytes}.");
        return new JsonObject { ["accepted"] = true, ["class"] = ChannelClasses.DiagEcho.Name, ["maxBytes"] = MaxEchoBytes };
    }

    /// <summary>Echoes every frame back on the same channel and closes when the server does.</summary>
    private sealed class EchoHandler(NodeLinkHandler owner) : IChannelHandler
    {
        public async ValueTask OnDataAsync(LinkChannelHandle channel, ChannelData data, CancellationToken ct)
        {
            owner._echoes.TryAdd(channel, 0);
            await channel.SendAsync(data.Payload.ToArray(), ct);
        }

        public ValueTask OnRemoteCloseAsync(LinkChannelHandle channel, CancellationToken ct)
        {
            owner._echoes.TryRemove(channel, out _);
            channel.Close();
            return ValueTask.CompletedTask;
        }

        public void OnReset(LinkChannelHandle channel, string code, string message) => owner._echoes.TryRemove(channel, out _);
    }
}
