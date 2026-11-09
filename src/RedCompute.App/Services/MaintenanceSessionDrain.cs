using RedCompute.Core.Sessions;
using RedCompute.PluginSdk;

namespace RedCompute.App.Services;

internal sealed class MaintenanceSessionDrain
{
    internal const string StopReason = "maintenance_restart";

    private readonly CapabilityRegistry _registry;
    private readonly Action<string, Guid?> _log;
    private readonly TimeSpan _gracefulTimeout;
    private readonly TimeSpan _forcedTimeout;
    private readonly TimeSpan _pollInterval;
    private readonly Action<int>? _reportBlockers;

    public MaintenanceSessionDrain(
        CapabilityRegistry registry,
        Action<string, Guid?> log,
        TimeSpan gracefulTimeout,
        TimeSpan forcedTimeout,
        TimeSpan pollInterval, Action<int>? reportBlockers = null)
    {
        _registry = registry;
        _log = log;
        _gracefulTimeout = gracefulTimeout;
        _forcedTimeout = forcedTimeout;
        _pollInterval = pollInterval;
        _reportBlockers = reportBlockers;
    }

    public async Task<IReadOnlyList<PlannedRestartSession>> DrainAsync(
        Func<CancellationToken, Task> waitForDeliveryQuiescence,
        CancellationToken cancellationToken = default)
    {
        var restartSessions = new Dictionary<(string Provider, string SessionId), PlannedRestartSession>();
        using var graceful = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        graceful.CancelAfter(_gracefulTimeout);

        try
        {
            await waitForDeliveryQuiescence(graceful.Token);
            await WaitForSessionQuiescenceAsync(restartSessions, graceful.Token);
            return restartSessions.Values.ToArray();
        }
        catch (OperationCanceledException) when (
            graceful.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            var blockers = CurrentSessions(restartSessions)
                .Where(IsBlocking)
                .ToArray();
            _log(
                blockers.Length == 0
                    ? $"[Maintenance] Graceful drain exceeded {_gracefulTimeout.TotalMinutes:0.##} minutes while queue delivery was settling; forcing provider quiescence"
                    : $"[Maintenance] Graceful drain exceeded {_gracefulTimeout.TotalMinutes:0.##} minutes; force-stopping {blockers.Length} blocking session(s): {Describe(blockers)}",
                null);

            using var forced = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            forced.CancelAfter(_forcedTimeout);
            await ForceStopAsync(blockers, forced.Token);

            try
            {
                await waitForDeliveryQuiescence(forced.Token);
                await WaitForSessionQuiescenceAsync(restartSessions, forced.Token);
            }
            catch (OperationCanceledException) when (
                forced.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                var remaining = CurrentSessions(restartSessions)
                    .Where(IsBlocking)
                    .ToArray();
                var detail = remaining.Length == 0
                    ? "queue delivery did not become quiescent"
                    : $"provider sessions remained active: {Describe(remaining)}";
                throw new TimeoutException(
                    $"Forced maintenance drain did not complete within {_forcedTimeout.TotalSeconds:0.##} seconds; {detail}");
            }

            _log("[Maintenance] Forced maintenance drain reached quiescence; deployment will proceed", null);
            return restartSessions.Values.ToArray();
        }
    }

    private async Task WaitForSessionQuiescenceAsync(
        Dictionary<(string Provider, string SessionId), PlannedRestartSession> restartSessions,
        CancellationToken ct)
    {
        string? lastBlockerDescription = null;
        while (true)
        {
            var sessions = CurrentSessions(restartSessions);
            var blockers = sessions.Where(IsBlocking).ToArray();
            ReleaseParkedApprovals(blockers);
            _reportBlockers?.Invoke(blockers.Length);
            if (blockers.Length == 0)
            {
                await Task.Delay(_pollInterval, ct);
                sessions = CurrentSessions(restartSessions);
                if (!sessions.Any(IsBlocking)) return;
            }
            else
            {
                var description = Describe(blockers);
                if (!string.Equals(description, lastBlockerDescription, StringComparison.Ordinal))
                {
                    _log($"[Maintenance] Waiting for {blockers.Length} active provider session(s): {description}", null);
                    lastBlockerDescription = description;
                }
            }

            await Task.Delay(_pollInterval, ct);
        }
    }

    private SessionEntry[] CurrentSessions(
        Dictionary<(string Provider, string SessionId), PlannedRestartSession> restartSessions)
    {
        var sessions = _registry.FindProviders<ISessionProvider>()
            .Distinct<ISessionProvider>(ReferenceEqualityComparer.Instance)
            .SelectMany(provider => provider
                .GetSessions(100_000, includeDismissed: false)
                .Select(session => new SessionEntry(provider, session)))
            .ToArray();

        foreach (var entry in sessions.Where(entry =>
                     entry.Session.Status is SessionStatus.Idle or SessionStatus.Active or SessionStatus.Starting))
        {
            var session = entry.Session;
            restartSessions[(session.Provider, session.Id)] = new PlannedRestartSession(
                session.Provider, session.Id, session.JobId);
        }

        return sessions;
    }

    private async Task ForceStopAsync(IReadOnlyList<SessionEntry> blockers, CancellationToken ct)
    {
        if (blockers.Count == 0) return;

        var attempts = blockers
            .DistinctBy(entry => (entry.Provider, entry.Session.Id))
            .Select(async entry =>
            {
                try
                {
                    await entry.Provider
                        .ForceKillAsync(entry.Session.Id, StopReason)
                        .WaitAsync(ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _log(
                        $"[Maintenance] Failed to force-stop {entry.Session.Provider}/{entry.Session.Id}: {ex.Message}",
                        entry.Session.JobId);
                }
            });

        await Task.WhenAll(attempts);
    }

    /// <summary>The reason a model reads when the restart denies what it was waiting on.</summary>
    internal const string RestartDenial =
        "Denied: RedCompute is restarting for a planned update and this was not approved before it. "
        + "Nothing ran. Finish what you can without it; ask again after the restart if it is still needed.";

    private readonly HashSet<string> _releasedApprovals = new(StringComparer.Ordinal);

    /// <summary>
    /// A session parked on an approval holds the drain until the forced stop (15 minutes), which
    /// then kills its turn. Its approvals are denied at once with a reason the model can read, so
    /// it finishes its turn cleanly and the restart is not held hostage. Ordinary questions stay.
    /// </summary>
    private void ReleaseParkedApprovals(IEnumerable<SessionEntry> blockers)
    {
        foreach (var entry in blockers)
        {
            IReadOnlyList<PendingSessionQuestion> parked;
            try { parked = entry.Provider.GetPendingQuestions(entry.Session.Id); }
            catch (Exception) { continue; }
            foreach (var question in parked)
            {
                if (question.Kind != PendingSessionQuestion.PermissionKind
                    || !_releasedApprovals.Add($"{entry.Session.Provider}/{entry.Session.Id}/{question.RequestId}"))
                    continue;
                try
                {
                    var result = entry.Provider.SubmitQuestionAnswer(entry.Session.Id, new SessionQuestionAnswer
                    {
                        RequestId = question.RequestId,
                        Decision = SessionPermissionDecisions.Deny,
                        DeclineReason = RestartDenial,
                    });
                    _log($"[Maintenance] Denied approval {question.RequestId} ({question.Permission?.ToolName ?? "tool"}) parked on "
                        + $"{entry.Session.Provider}/{entry.Session.Id} so the restart does not wait on it: {result}", entry.Session.JobId);
                }
                catch (Exception ex)
                {
                    _log($"[Maintenance] Could not deny approval {question.RequestId} on {entry.Session.Provider}/{entry.Session.Id}: {ex.Message}",
                        entry.Session.JobId);
                }
            }
        }
    }

    private static bool IsBlocking(SessionEntry entry)
        => entry.Session.Status is SessionStatus.Active or SessionStatus.Starting;

    private static string Describe(IEnumerable<SessionEntry> blockers)
        => string.Join(", ", blockers.Select(entry =>
            $"{entry.Session.Provider}/{entry.Session.Id} ({entry.Session.Status}, {entry.Session.Title ?? entry.Session.ProjectName})"));

    private sealed record SessionEntry(ISessionProvider Provider, UnifiedSessionInfo Session);
}
