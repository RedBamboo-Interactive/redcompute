using RedCompute.Core.Sessions;

namespace RedCompute.PluginSdk;

public interface ISessionProvider
{
    string ProviderId { get; }
    string ProviderDisplayName { get; }
    SessionCapabilities Capabilities { get; }

    // Session lifecycle (PersistentSessions)
    Task<UnifiedSessionInfo?> StartSessionAsync(string projectPath, string? callerInfo = null, string? model = null);
    Task<UnifiedSessionInfo?> ResumeSessionAsync(string sessionId);
    Task StopSessionAsync(string sessionId);
    Task ForceKillAsync(string sessionId);
    void DismissSession(string sessionId);

    // Messaging (SendMessage)
    Task<bool> SendMessageAsync(string sessionId, string content, ImageAttachment[]? images = null);
    bool SendAnswer(string sessionId, string answer);

    // Interrupt
    InterruptResult InterruptSession(string sessionId);

    // Configuration (ConfigUpdate / PermissionMode)
    Task<UnifiedSessionInfo?> UpdateSessionConfigAsync(string sessionId, string? model, string? effort);
    bool SetPermissionMode(string sessionId, string mode);

    // Querying
    List<UnifiedSessionInfo> GetSessions(int limit = 20, bool includeDismissed = false);
    (UnifiedSessionInfo? Info, List<UnifiedMessageRecord> History) GetSession(string sessionId);
    (UnifiedSessionInfo? Info, List<UnifiedMessageRecord> History) GetSessionByJobId(Guid jobId);
    Dictionary<Guid, SessionStatus> GetSessionStatusesByJobIds(IEnumerable<Guid> jobIds);

    // Stateless execution
    Task<SessionExecuteResult> ExecuteAsync(string prompt, string? workingDir, string? model,
        int timeout, CancellationToken ct, string? streamKey = null,
        Dictionary<string, string>? env = null, Dictionary<string, object?>? providerParams = null);

    // LLM completion (Generate)
    Task<SessionGenerateResult> GenerateAsync(string? model, string? system,
        string messagesJson, int maxTokens, CancellationToken ct);

    // Discovery
    List<SessionProjectInfo> ListProjects();
    List<ModelInfo> GetAvailableModels();

    // Message injection (without triggering inference)
    Task<bool> InjectMessageAsync(string sessionId, string role, string content)
        => Task.FromResult(false);

    // Events
    event Action<string, UnifiedStreamEvent>? SessionStreamEvent;
    void CancelExecution(string key);
    Task StopAllAsync();
    string? LastStartError { get; }
}
