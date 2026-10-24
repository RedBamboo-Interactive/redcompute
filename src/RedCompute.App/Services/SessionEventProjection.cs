using RedCompute.Core.Sessions;
using RedCompute.PluginSdk;

namespace RedCompute.App.Services;

/// <summary>
/// What every broadcast session.created/session.updated carries beyond the provider's own info.
/// One place, shared by RelayServer's event bridge and the tests that read the event stream.
/// </summary>
internal static class SessionEventProjection
{
    internal static void ApplyWorkState(UnifiedSessionInfo session, ISessionProvider? provider, Action<string, Guid?> log)
    {
        try
        {
            session.WorkState = SessionWorkStates.Derive(session.Status,
                provider?.GetPendingQuestions(session.Id).Count ?? 0, session.BackgroundTasks);
        }
        catch (Exception ex)
        {
            log($"[Sessions] Work state unavailable for {session.Provider}/{session.Id}: {ex.Message}", null);
        }
    }
}
