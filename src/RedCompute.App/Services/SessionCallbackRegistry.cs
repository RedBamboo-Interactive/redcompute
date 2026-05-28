using System.Collections.Concurrent;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using RedCompute.Core.Sessions;

namespace RedCompute.App.Services;

public class SessionCallbackRegistry
{
    private readonly ConcurrentDictionary<string, string> _callbacks = new();
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private readonly Action<string, Guid?> _log;

    public SessionCallbackRegistry(Action<string, Guid?> log) => _log = log;

    public void Register(string sessionId, string callbackUrl)
    {
        _callbacks[sessionId] = callbackUrl;
        _log($"[Callbacks] Registered callback for session {sessionId}", null);
    }

    public bool RegisterIfStillActive(string sessionId, string callbackUrl, SessionStatus currentStatus)
    {
        if (currentStatus is SessionStatus.Idle or SessionStatus.Stopped or SessionStatus.Error)
        {
            _ = FireAsync(callbackUrl, sessionId, currentStatus.ToString());
            return false;
        }

        Register(sessionId, callbackUrl);
        return true;
    }

    public void OnSessionEvent(string eventType, object data)
    {
        if (eventType == "session.updated" && data is UnifiedSessionInfo session)
        {
            if (session.Status is not (SessionStatus.Idle or SessionStatus.Stopped or SessionStatus.Error))
                return;

            if (_callbacks.TryRemove(session.Id, out var url))
                _ = FireAsync(url, session.Id, session.Status.ToString(), session.ProjectPath, session.Title);
        }
        else if (eventType == "session.ended")
        {
            try
            {
                var json = JsonSerializer.Serialize(data);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("id", out var idEl))
                {
                    var id = idEl.GetString()!;
                    var reason = doc.RootElement.TryGetProperty("reason", out var r) ? r.GetString() : "ended";
                    if (_callbacks.TryRemove(id, out var url))
                        _ = FireAsync(url, id, "Ended", reason: reason);
                }
            }
            catch { }
        }
    }

    private async Task FireAsync(string url, string sessionId, string status,
        string? projectPath = null, string? title = null, string? reason = null)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                var response = await _http.PostAsJsonAsync(url, new
                {
                    sessionId,
                    status,
                    projectPath,
                    title,
                    reason,
                });

                if (response.IsSuccessStatusCode)
                {
                    _log($"[Callbacks] Fired for session {sessionId} (status={status})", null);
                    return;
                }

                _log($"[Callbacks] Attempt {attempt + 1} failed for {sessionId}: HTTP {(int)response.StatusCode}", null);
            }
            catch (Exception ex)
            {
                _log($"[Callbacks] Attempt {attempt + 1} failed for {sessionId}: {ex.Message}", null);
            }

            if (attempt < 2)
                await Task.Delay(500 * (attempt + 1));
        }

        _log($"[Callbacks] All 3 attempts failed for session {sessionId}, giving up", null);
    }
}
