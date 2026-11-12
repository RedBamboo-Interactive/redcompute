using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using RedCompute.Core.Sessions;

namespace RedCompute.Plugin.ClaudeCode;

internal enum ClaudeForkProbeStatus { Accepted, Rejected, Unavailable }

internal sealed record ClaudeForkProbeResult(ClaudeForkProbeStatus Status, string? Message = null);

public partial class ClaudeSessionService
{
    private static readonly TimeSpan ForkProbeTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Test seam: proves a fork to the CLI without a model call.</summary>
    internal Func<ProcessStartInfo, CancellationToken, Task<ClaudeForkProbeResult>> ProbeFork { get; set; } = ProbeForkWithProcessAsync;

    internal string? ProjectsRootOverride { get; set; }

    /// <summary>
    /// Conversation only: files are not touched. Nothing changes until the CLI has accepted the fork
    /// at boot, so a refusal leaves the session exactly as it was. The session then rests with its
    /// process parked and a durable fork target, and the next message's ordinary resume applies it.
    /// </summary>
    public async Task<SessionRollbackResult> RollbackAsync(SessionRollbackRequest request, CancellationToken ct = default)
    {
        var sessionId = request.SessionId;
        var record = _sessionStore.FindSession(sessionId);
        if (record is null)
            return SessionRollbackResult.Refused(SessionRollbackRefusals.TargetNotFound, $"Session '{sessionId}' was not found");
        // The target is durable before the mark, so a crash between them lands here: finish the mark.
        if (ClaudeForkTarget.Parse(_sessionStore.GetForkTarget(sessionId))?.RollbackId == request.RollbackId)
        {
            _sessionStore.MarkMessagesRolledBack(sessionId, request.TargetMessageUid);
            return SessionRollbackResult.Applied();
        }

        var claudePath = ResolveClaudePath();
        if (claudePath is null)
            return SessionRollbackResult.Unavailable("Could not find the 'claude' CLI");

        // A resident process holds the whole conversation in memory, so it has to go first.
        if (_sessions.ContainsKey(sessionId)
            && !await TryParkIdleSessionAsync(sessionId, SessionStopReasons.IdleTimeout, ct))
            return SessionRollbackResult.Refused(SessionRollbackRefusals.NotIdle, "The session is not idle");
        record = _sessionStore.FindSession(sessionId)!;

        if (string.IsNullOrEmpty(record.ClaudeSessionId))
            return Conclude(record, request, new ClaudeForkTarget(request.RollbackId, null, null, []));

        var transcript = ClaudeTranscriptPaths.FindTranscript(record.ProjectPath, record.ClaudeSessionId, ProjectsRootOverride);
        if (transcript is null)
            return SessionRollbackResult.Refused(SessionRollbackRefusals.ProviderHistoryMissing,
                "The provider's conversation file could not be found");

        ClaudeChain chain;
        try { chain = await Task.Run(() => ClaudeChain.Load(transcript), ct); }
        catch (IOException ex) { return SessionRollbackResult.Unavailable($"Could not read the provider's conversation: {ex.Message}"); }

        var promptUuids = _sessionStore.GetMessages(sessionId)
            .Where(message => message.Role == "user" && message.MessageUid is not null && message.PromptUuid is not null)
            .GroupBy(message => message.MessageUid!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Last().PromptUuid!, StringComparer.Ordinal);
        var (plan, refusal) = chain.ResolveRollback(request, promptUuids);
        if (plan is null) return refusal!;

        var target = new ClaudeForkTarget(request.RollbackId, plan.ForkAtUuid, plan.DropsTurnUuid, plan.DroppedPromptUuids);
        // Dropping the first message keeps nothing: there is no fork, the next launch starts fresh.
        if (target.ForkAtUuid is null)
        {
            _log($"[Claude] Session {sessionId} rolled back to empty; conversation {record.ClaudeSessionId} is kept on disk and no longer resumed", null);
            record.ClaudeSessionId = null;
            return Conclude(record, request, target);
        }

        var probe = await ProbeFork(ForkProbeStartInfo(claudePath, record, target), ct);
        return probe.Status switch
        {
            ClaudeForkProbeStatus.Accepted => Conclude(record, request, target),
            ClaudeForkProbeStatus.Rejected => SessionRollbackResult.Refused(SessionRollbackRefusals.ProviderRejected,
                probe.Message ?? "The provider refused to rewind to this message"),
            _ => SessionRollbackResult.Unavailable(probe.Message ?? "The provider did not answer"),
        };
    }

    private SessionRollbackResult Conclude(ClaudeSessionRecord record, SessionRollbackRequest request, ClaudeForkTarget target)
    {
        record.MessageCount = request.KeptUserTurns.Count;
        _sessionStore.SaveSession(record);
        _sessionStore.SetForkTarget(record.Id, target.Serialize());
        _sessionStore.MarkMessagesRolledBack(record.Id, request.TargetMessageUid);
        SessionUpdated?.Invoke(ToUnloadedSessionInfo(record));
        _log($"[Claude] Session {record.Id} rolled back before {request.TargetMessageUid} (rollback {request.RollbackId})", null);
        return SessionRollbackResult.Applied();
    }

    /// <summary>
    /// Decides what a launch must do about a pending rollback. The answer comes from the chain on
    /// disk, so a crash at any point cannot lose or repeat it. A refusal here is deterministic: the
    /// target is cleared, the session resumes plainly and the transcript says so. Never retried.
    /// </summary>
    private (ClaudeForkTarget? Target, string? Error) PrepareForkForLaunch(ClaudeSessionRecord record, string claudePath)
    {
        var target = ClaudeForkTarget.Parse(_sessionStore.GetForkTarget(record.Id));
        if (target is null) return (null, null);

        // The fresh conversation of a rollback to empty has no fork to apply.
        if (target.ForkAtUuid is null || string.IsNullOrEmpty(record.ClaudeSessionId))
        {
            _sessionStore.SetForkTarget(record.Id, null);
            return (null, null);
        }

        var transcript = ClaudeTranscriptPaths.FindTranscript(record.ProjectPath, record.ClaudeSessionId, ProjectsRootOverride);
        if (transcript is not null)
        {
            try
            {
                if (ClaudeChain.Load(transcript).HasConsumed(target))
                {
                    _sessionStore.SetForkTarget(record.Id, null);
                    return (null, null);
                }
            }
            catch (IOException ex)
            {
                return (null, $"Could not read the provider's conversation: {ex.Message}");
            }
        }

        // Synchronous caller: run the probe on the pool so its awaits never need the blocked thread
        // (on a UI thread they would wait on it forever), as the transcript persistence bridge does.
        var startInfo = ForkProbeStartInfo(claudePath, record, target);
        var probe = Task.Run(() => ProbeFork(startInfo, CancellationToken.None)).GetAwaiter().GetResult();
        switch (probe.Status)
        {
            case ClaudeForkProbeStatus.Accepted:
                return (target, null);
            case ClaudeForkProbeStatus.Rejected:
                _sessionStore.SetForkTarget(record.Id, null);
                ReportForkRejected(record.Id, probe.Message);
                return (null, null);
            default:
                return (null, probe.Message ?? "The provider did not answer");
        }
    }

    private void ReportForkRejected(string sessionId, string? reason)
    {
        var text = "The rewind could not be applied: the provider refused it and still has the removed messages in its context. "
            + "They stay out of this transcript. Start a new conversation if that matters."
            + (string.IsNullOrWhiteSpace(reason) ? "" : $"\n\n{reason}");
        _log($"[Claude] Fork refused for {sessionId}, resuming plainly and not retrying: {reason}", null);
        PersistMessage(sessionId, "assistant", "error", text, null, null, null, null, null, Guid.NewGuid().ToString("N"));
        StreamEvent?.Invoke(sessionId, new ClaudeStreamEvent { Type = "error", Content = text, IsPartial = false });
    }

    private static void AddForkArgs(ProcessStartInfo startInfo, ClaudeForkTarget? target)
    {
        if (target?.ForkAtUuid is null) return;
        startInfo.ArgumentList.Add("--resume-session-at");
        startInfo.ArgumentList.Add(target.ForkAtUuid);
        if (target.DropsTurnUuid is null) return;
        startInfo.ArgumentList.Add("--resume-drops-turn");
        startInfo.ArgumentList.Add(target.DropsTurnUuid);
    }

    private ProcessStartInfo ForkProbeStartInfo(string claudePath, ClaudeSessionRecord record, ClaudeForkTarget target)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = claudePath,
            WorkingDirectory = record.ProjectPath,
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        PopulateSessionArgs(startInfo, PermissionModeFor(record.AccessLevel), record.ClaudeSessionId, record.Model, record.Effort, record.DeveloperInstructions);
        AddForkArgs(startInfo, target);
        return startInfo;
    }

    /// <summary>
    /// The CLI validates the fork as it boots. It answers a control request once that has passed, and
    /// answers with an error result otherwise, so no prompt is sent and no model is called.
    /// </summary>
    private static async Task<ClaudeForkProbeResult> ProbeForkWithProcessAsync(ProcessStartInfo startInfo, CancellationToken ct)
    {
        using var process = Process.Start(startInfo);
        if (process is null) return new(ClaudeForkProbeStatus.Unavailable, "The CLI did not start");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ForkProbeTimeout);
        try
        {
            process.ErrorDataReceived += (_, _) => { };
            process.BeginErrorReadLine();
            await process.StandardInput.WriteLineAsync(ForkProbeRequest);
            await process.StandardInput.FlushAsync(timeout.Token);
            while (await process.StandardOutput.ReadLineAsync(timeout.Token) is { } line)
            {
                if (ClassifyForkProbeLine(line) is { } result) return result;
            }
            return new(ClaudeForkProbeStatus.Rejected, "The CLI exited before accepting the fork");
        }
        catch (OperationCanceledException)
        {
            return new(ClaudeForkProbeStatus.Unavailable, "The CLI did not answer in time");
        }
        catch (IOException)
        {
            // Its stdin closes when a refused fork exits first: the result is still on stdout.
            var rest = await process.StandardOutput.ReadToEndAsync(CancellationToken.None);
            foreach (var line in rest.Split('\n'))
                if (ClassifyForkProbeLine(line) is { } result) return result;
            return new(ClaudeForkProbeStatus.Rejected, "The CLI exited before accepting the fork");
        }
        finally
        {
            try { process.Kill(entireProcessTree: true); } catch { }
        }
    }

    internal const string ForkProbeRequest =
        """{"type":"control_request","request_id":"rollback-probe","request":{"subtype":"set_permission_mode","mode":"bypassPermissions"}}""";

    /// <summary>Null means the line says nothing either way.</summary>
    internal static ClaudeForkProbeResult? ClassifyForkProbeLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (!root.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String) return null;
            switch (type.GetString())
            {
                case "control_response":
                    return new(ClaudeForkProbeStatus.Accepted);
                case "result":
                    var message = root.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array
                        ? string.Join("; ", errors.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()))
                        : root.TryGetProperty("result", out var text) && text.ValueKind == JsonValueKind.String ? text.GetString() : null;
                    return new(ClaudeForkProbeStatus.Rejected, message);
                default:
                    return null;
            }
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
