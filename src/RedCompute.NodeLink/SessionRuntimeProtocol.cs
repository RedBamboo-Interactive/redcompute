namespace RedCompute.NodeLink;

/// <summary>Names shared by the controller, node agent and registered persistent session runtime.</summary>
public static class SessionRuntimeProtocol
{
    public const string Kind = "ai-session.v1";
    public const string HealthPath = "/ai-session/v1/health";
    public const int MaxExchangeBodyBytes = 2 * 1024 * 1024;

    private static readonly IReadOnlyDictionary<SessionRuntimeOperation, string> CommandPaths =
        new Dictionary<SessionRuntimeOperation, string>
        {
            [SessionRuntimeOperation.Start] = "/ai-session/v1/start",
            [SessionRuntimeOperation.Resume] = "/ai-session/v1/resume",
            [SessionRuntimeOperation.SendInput] = "/ai-session/v1/send-input",
            [SessionRuntimeOperation.Park] = "/ai-session/v1/park",
            [SessionRuntimeOperation.Inspect] = "/ai-session/v1/inspect",
            [SessionRuntimeOperation.ReadEvents] = "/ai-session/v1/read-events",
            [SessionRuntimeOperation.AcknowledgeEvents] = "/ai-session/v1/acknowledge-events",
        };

    public static string PathFor(SessionRuntimeOperation operation) => CommandPaths[operation];

    public static SessionRuntimeOperation? OperationForPath(string path)
    {
        foreach (var (operation, commandPath) in CommandPaths)
            if (path == commandPath) return operation;
        return null;
    }
}
