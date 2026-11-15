using RedCompute.App.Api.Endpoints;
using RedCompute.Core.Sessions;
using RedCompute.PluginSdk;

namespace RedCompute.App.Services;

/// <summary>
/// What every broadcast session.created/session.updated carries beyond the provider's own info.
/// One place, shared by RelayServer's event bridge and the tests that read the event stream.
/// </summary>
internal static class SessionEventProjection
{
    /// <summary>
    /// The session's node exactly as the API reports it (<see cref="UnifiedSessionEndpoints.WithNode"/>):
    /// a row with no recorded node is the legacy node, so an event and a GET of the same row agree.
    /// </summary>
    internal static void ApplyNode(UnifiedSessionInfo session, INodeRegistry? nodes)
        => UnifiedSessionEndpoints.WithNode(session, nodes);

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
