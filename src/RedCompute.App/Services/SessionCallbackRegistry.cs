using System.Collections.Concurrent;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using RedBamboo.AppHost.Auth;
using RedCompute.Core.Sessions;

namespace RedCompute.App.Services;

public class SessionCallbackRegistry
{
    private record CallbackEntry(string Url, string? UserId, string CallbackId);

    private readonly ConcurrentDictionary<string, CallbackEntry> _callbacks = new();
    private readonly HttpClient _http;
    private readonly Action<string, Guid?> _log;

    public SessionCallbackRegistry(Action<string, Guid?> log, AuthenticatedHttpClientFactory? authFactory = null)
    {
        _log = log;
        _http = authFactory?.CreateClient("http://localhost", TimeSpan.FromSeconds(10))
            ?? new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
    }

    internal SessionCallbackRegistry(HttpClient http, Action<string, Guid?> log)
    {
        _http = http;
        _log = log;
    }

    // One delivery identity per registration, shared by every HTTP retry. Callers
    // with an existing invocation identity (e.g. a delegated prompt UID) reuse it.
    public void Register(string sessionId, string callbackUrl, string? userId = null, string? callbackId = null)
    {
        _callbacks[sessionId] = new CallbackEntry(callbackUrl, userId, callbackId ?? Guid.NewGuid().ToString("N"));
        _log($"[Callbacks] Registered callback for session {sessionId}", null);
    }

    public bool RegisterIfStillActive(string sessionId, string callbackUrl, SessionStatus currentStatus,
        string? userId = null, string? stopReason = null, bool force = false, string? callbackId = null)
    {
        if (!force && currentStatus is SessionStatus.Idle or SessionStatus.Stopped or SessionStatus.Error)
        {
            _ = FireAsync(callbackUrl, sessionId, currentStatus.ToString(), callbackId ?? Guid.NewGuid().ToString("N"), userId: userId, stopReason: stopReason);
            return false;
        }

        Register(sessionId, callbackUrl, userId, callbackId);
        return true;
    }

    public void OnSessionEvent(string eventType, object data)
    {
        if (eventType == "session.updated" && data is UnifiedSessionInfo session)
        {
            if (session.Status is not (SessionStatus.Idle or SessionStatus.Stopped or SessionStatus.Error))
                return;

            if (_callbacks.TryRemove(session.Id, out var entry))
                _ = FireAsync(entry.Url, session.Id, session.Status.ToString(), entry.CallbackId, session.ProjectPath, session.Title,
                    userId: session.UserId ?? entry.UserId, stopReason: session.StopReason);
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
                    var stopReason = doc.RootElement.TryGetProperty("stopReason", out var sr) ? sr.GetString() : null;
                    if (_callbacks.TryRemove(id, out var entry))
                        _ = FireAsync(entry.Url, id, "Ended", entry.CallbackId, reason: reason, userId: entry.UserId, stopReason: stopReason);
                }
            }
            catch { }
        }
    }

    private async Task FireAsync(string url, string sessionId, string status, string callbackId,
        string? projectPath = null, string? title = null, string? reason = null, string? userId = null, string? stopReason = null)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                using var response = await _http.PostAsJsonAsync(url, new
                {
                    sessionId,
                    callbackId,
                    status,
                    projectPath,
                    title,
                    reason,
                    userId,
                    stopReason,
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
