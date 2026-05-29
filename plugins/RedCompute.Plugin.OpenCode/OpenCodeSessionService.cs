using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using RedCompute.Core.Sessions;
using RedCompute.PluginSdk;

namespace RedCompute.Plugin.OpenCode;

public class OpenCodeSessionService
{
    private readonly ConcurrentDictionary<string, ManagedSession> _sessions = new();
    private readonly ConcurrentDictionary<string, Process> _runningProcesses = new();
    private readonly OpenCodeConfig _config;
    private readonly IJobTracker _jobTracker;
    private readonly IOpenCodeSessionStore _store;
    private readonly Action<string, Guid?> _log;

    public event Action<OpenCodeSessionInfo>? SessionCreated;
    public event Action<OpenCodeSessionInfo>? SessionUpdated;
    public event Action<string, string>? SessionEnded;
    public event Action<string, OpenCodeStreamEvent>? StreamEvent;

    public void EmitStreamEvent(string key, OpenCodeStreamEvent evt) => StreamEvent?.Invoke(key, evt);

    public string? LastStartError { get; private set; }

    private class ManagedSession
    {
        public OpenCodeSessionInfo Info { get; }
        public Process Process { get; }
        public CancellationTokenSource Cts { get; }
        public List<OpenCodeStreamEvent> MessageHistory { get; } = new();
        public string? AcpSessionId { get; set; }
        public ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> PendingRequests { get; } = new();
        private int _nextRequestId;
        private readonly SemaphoreSlim _writeLock = new(1, 1);

        public ManagedSession(OpenCodeSessionInfo info, Process process, CancellationTokenSource cts)
        {
            Info = info;
            Process = process;
            Cts = cts;
        }

        public int GetNextRequestId() => Interlocked.Increment(ref _nextRequestId);
        public SemaphoreSlim WriteLock => _writeLock;
    }

    public OpenCodeSessionService(OpenCodeConfig config, IJobTracker jobTracker, IOpenCodeSessionStore store, Action<string, Guid?> log)
    {
        _config = config;
        _jobTracker = jobTracker;
        _store = store;
        _log = log;
        RecoverSessions();
    }

    private void RecoverSessions()
    {
        try
        {
            var active = _store.GetActiveSessions();
            foreach (var s in active)
            {
                s.Status = "Stopped";
                _log($"[OpenCode] Marked orphaned session {s.Id} ({s.ProjectName}) as stopped", null);
                _store.SaveSession(s);
            }
        }
        catch (Exception ex)
        {
            _log($"[OpenCode] Failed to recover sessions: {ex.Message}", null);
        }
    }

    // ===== ACP Interactive Session Methods =====

    public async Task<OpenCodeSessionInfo?> StartSession(string projectPath, string? callerInfo = null, string? model = null)
    {
        if (_sessions.Count >= _config.MaxSessions)
        {
            LastStartError = $"Max sessions reached ({_config.MaxSessions})";
            return null;
        }
        if (!Directory.Exists(projectPath))
        {
            LastStartError = $"Project path not found: {projectPath}";
            return null;
        }

        var opencodePath = ResolveOpenCodePath();
        if (opencodePath == null)
        {
            LastStartError = "Could not find 'opencode' CLI. Install opencode or set OpenCodePath in config.";
            return null;
        }

        var id = Guid.NewGuid().ToString("N")[..12];
        var info = new OpenCodeSessionInfo
        {
            Id = id,
            ProjectName = Path.GetFileName(projectPath),
            ProjectPath = projectPath,
            Status = "Starting",
            StartedAt = DateTimeOffset.UtcNow,
            Source = callerInfo,
        };

        var (session, error) = await SpawnAcpSession(info, opencodePath, projectPath, null, model);
        if (session == null)
        {
            LastStartError = error;
            return null;
        }

        info.Status = "Idle";

        var inputJson = JsonSerializer.Serialize(new { projectPath, projectName = info.ProjectName, sessionId = id });
        var job = _jobTracker.CreateJob("ai-session", "OpenCode", inputJson, callerInfo: callerInfo, name: info.ProjectName, rationale: "Interactive session");
        _jobTracker.MarkRunning(job.Id);
        info.JobId = job.Id;

        PersistSessionRecord(info);
        _log($"[OpenCode] Session {id} started for {info.ProjectName} (ACP session {session.AcpSessionId}, Job {job.Id})", null);
        SessionCreated?.Invoke(info);

        return info;
    }

    public async Task<OpenCodeSessionInfo?> ResumeSession(string sessionId)
    {
        if (_sessions.ContainsKey(sessionId))
        {
            LastStartError = "Session is already running";
            return null;
        }
        if (_sessions.Count >= _config.MaxSessions)
        {
            LastStartError = $"Max sessions reached ({_config.MaxSessions})";
            return null;
        }

        var record = _store.FindSession(sessionId);
        if (record == null)
        {
            LastStartError = "Session not found";
            return null;
        }
        if (string.IsNullOrEmpty(record.OpenCodeSessionId))
        {
            LastStartError = "Session has no OpenCode session ID to resume";
            return null;
        }
        if (!Directory.Exists(record.ProjectPath))
        {
            LastStartError = $"Project path not found: {record.ProjectPath}";
            return null;
        }

        record.Dismissed = false;
        _store.SaveSession(record);

        var opencodePath = ResolveOpenCodePath();
        if (opencodePath == null)
        {
            LastStartError = "Could not find 'opencode' CLI.";
            return null;
        }

        var info = new OpenCodeSessionInfo
        {
            Id = record.Id,
            ProjectName = record.ProjectName,
            ProjectPath = record.ProjectPath,
            Status = "Starting",
            StartedAt = record.StartedAt,
            Model = record.Model,
            OpenCodeSessionId = record.OpenCodeSessionId,
            Title = record.Title,
            MessageCount = record.MessageCount,
            CostUsd = record.CostUsd,
            InputTokens = record.InputTokens,
            OutputTokens = record.OutputTokens,
            Effort = record.Effort,
            Source = record.Source,
        };

        var (session, error) = await SpawnAcpSession(info, opencodePath, record.ProjectPath, record.OpenCodeSessionId);
        if (session == null)
        {
            LastStartError = error;
            return null;
        }

        info.Status = "Idle";

        if (record.JobId.HasValue)
        {
            info.JobId = record.JobId.Value;
            _jobTracker.MarkRunning(record.JobId.Value);
        }
        else
        {
            var job = _jobTracker.CreateJob("ai-session", "OpenCode",
                JsonSerializer.Serialize(new { projectPath = record.ProjectPath, projectName = record.ProjectName, resumed = true }),
                callerInfo: "Dashboard", name: record.ProjectName, rationale: "Resumed session");
            _jobTracker.MarkRunning(job.Id);
            info.JobId = job.Id;
        }

        PersistSessionRecord(info);
        _log($"[OpenCode] Session {sessionId} resumed for {info.ProjectName} (ACP session {session.AcpSessionId}, Job {info.JobId})", null);
        SessionCreated?.Invoke(info);

        return info;
    }

    private async Task<(ManagedSession? session, string? error)> SpawnAcpSession(
        OpenCodeSessionInfo info, string opencodePath, string projectPath, string? existingSessionId,
        string? model = null)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = opencodePath,
            WorkingDirectory = projectPath,
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        startInfo.ArgumentList.Add("acp");

        var resolvedModel = model ?? _config.Model;
        if (!string.IsNullOrEmpty(resolvedModel))
        {
            startInfo.EnvironmentVariables["OPENCODE_MODEL"] = resolvedModel;
            startInfo.EnvironmentVariables["OPENCODE_CONFIG_CONTENT"] =
                JsonSerializer.Serialize(new { model = resolvedModel });
        }

        Process process;
        try
        {
            process = Process.Start(startInfo)!;
        }
        catch (Exception ex)
        {
            return (null, $"Failed to start opencode acp: {ex.Message}");
        }

        process.StandardInput.NewLine = "\n";

        var cts = new CancellationTokenSource();
        var session = new ManagedSession(info, process, cts);

        process.EnableRaisingEvents = true;
        process.Exited += (_, _) => OnProcessExited(info.Id);

        _ = ReadStdout(session);
        _ = ReadStderr(session);

        try
        {
            await SendRequest(session, "initialize", new
            {
                protocolVersion = 1,
                clientCapabilities = new
                {
                    fs = new { readTextFile = true, writeTextFile = true },
                },
                clientInfo = new { name = "RedCompute", title = "RedCompute", version = "1.0.0" },
            });

            JsonElement sessionResult;
            if (existingSessionId != null)
            {
                sessionResult = await SendRequest(session, "session/load", new
                {
                    sessionId = existingSessionId,
                    cwd = projectPath,
                    mcpServers = Array.Empty<object>(),
                }, timeoutSeconds: 60);
                session.AcpSessionId = existingSessionId;
            }
            else
            {
                var newParams = !string.IsNullOrEmpty(resolvedModel)
                    ? (object)new { cwd = projectPath, mcpServers = Array.Empty<object>(), model = resolvedModel }
                    : new { cwd = projectPath, mcpServers = Array.Empty<object>() };
                sessionResult = await SendRequest(session, "session/new", newParams);

                if (sessionResult.TryGetProperty("sessionId", out var sid))
                {
                    session.AcpSessionId = sid.GetString();
                    info.OpenCodeSessionId = session.AcpSessionId;
                }
            }

            if (sessionResult.TryGetProperty("_meta", out var meta)
                && meta.TryGetProperty("opencode", out var oc)
                && oc.TryGetProperty("modelId", out var modelId))
            {
                info.Model = modelId.GetString();
            }

            if (!string.IsNullOrEmpty(resolvedModel) && existingSessionId == null)
                info.Model = resolvedModel;

            _sessions[info.Id] = session;
            return (session, null);
        }
        catch (Exception ex)
        {
            _log($"[OpenCode] ACP initialization failed for {info.Id}: {ex.Message}", null);
            CleanupSessionResources(session);
            return (null, $"ACP initialization failed: {ex.Message}");
        }
    }

    public async Task<bool> SendMessage(string sessionId, string content, ImageAttachment[]? images = null)
    {
        if (!_sessions.TryGetValue(sessionId, out var session))
            return false;

        if (session.Info.Status is "Stopped" or "Error")
            return false;

        if (session.Info.Status == "Active")
        {
            _log($"[OpenCode] Session {sessionId} is active, cancelling for new message", null);
            await SendNotification(session, "session/cancel", new { sessionId = session.AcpSessionId });
            await Task.Delay(500);
        }

        try
        {
            var promptBlocks = new List<object>();
            if (!string.IsNullOrWhiteSpace(content))
                promptBlocks.Add(new { type = "text", text = content });
            if (images != null)
                foreach (var img in images)
                    promptBlocks.Add(new { type = "image", data = img.Base64, mimeType = img.MediaType });

            var requestId = session.GetNextRequestId();
            var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
            session.PendingRequests[requestId] = tcs;

            await WriteJsonLine(session, new
            {
                jsonrpc = "2.0",
                id = requestId,
                method = "session/prompt",
                @params = new { sessionId = session.AcpSessionId, prompt = promptBlocks },
            });

            session.Info.MessageCount++;
            session.Info.Status = "Active";
            SessionUpdated?.Invoke(session.Info);

            _store.AddMessage(new OpenCodeMessageRecord
            {
                SessionId = sessionId,
                Role = "user",
                EventType = "text",
                Content = content,
                Timestamp = DateTimeOffset.UtcNow,
            });

            _ = HandlePromptResponse(session, tcs.Task);

            return true;
        }
        catch (Exception ex)
        {
            _log($"[OpenCode] Failed to send message to {sessionId}: {ex.Message}", null);
            return false;
        }
    }

    private async Task HandlePromptResponse(ManagedSession session, Task<JsonElement> responseTask)
    {
        try
        {
            var result = await responseTask.WaitAsync(session.Cts.Token);

            if (result.TryGetProperty("usage", out var usage))
            {
                if (usage.TryGetProperty("inputTokens", out var it))
                    session.Info.InputTokens = (session.Info.InputTokens ?? 0) + it.GetInt32();
                if (usage.TryGetProperty("outputTokens", out var ot))
                    session.Info.OutputTokens = (session.Info.OutputTokens ?? 0) + ot.GetInt32();
            }

            var stopReason = result.TryGetProperty("stopReason", out var sr) ? sr.GetString() : null;
            if (stopReason == "cancelled")
                StreamEvent?.Invoke(session.Info.Id, new OpenCodeStreamEvent { Type = "status", Content = "interrupted" });

            session.Info.Status = "Idle";
            TryFetchOpenCodeTitle(session);
            PersistSessionRecord(session.Info);
            SessionUpdated?.Invoke(session.Info);

            if (string.IsNullOrEmpty(session.Info.Title))
                _ = RetryFetchTitle(session);
        }
        catch (OperationCanceledException)
        {
            // Session stopped/cancelled — lifecycle handled by StopSession/ForceKill
        }
        catch (Exception ex)
        {
            _log($"[OpenCode] Prompt response error for {session.Info.Id}: {ex.Message}", null);
            if (_sessions.ContainsKey(session.Info.Id))
            {
                session.Info.Status = "Idle";
                StreamEvent?.Invoke(session.Info.Id, new OpenCodeStreamEvent { Type = "error", Content = ex.Message });
                PersistSessionRecord(session.Info);
                SessionUpdated?.Invoke(session.Info);
            }
        }
    }

    private void TryFetchOpenCodeTitle(ManagedSession session)
    {
        if (!string.IsNullOrEmpty(session.Info.Title)) return;
        var acpSessionId = session.AcpSessionId;
        if (string.IsNullOrEmpty(acpSessionId)) return;

        try
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var dbPath = Path.Combine(home, ".local", "share", "opencode", "opencode.db");
            if (!File.Exists(dbPath)) return;

            using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT title FROM session WHERE id = @id";
            cmd.Parameters.AddWithValue("@id", acpSessionId);
            var title = cmd.ExecuteScalar() as string;
            if (!string.IsNullOrEmpty(title) && !title.StartsWith("New session - "))
            {
                session.Info.Title = title;
                if (session.Info.JobId.HasValue)
                    _jobTracker.UpdateName(session.Info.JobId.Value, title);
            }
        }
        catch (Exception ex) { _log($"[OpenCode] Failed to fetch title for {session.Info.Id}: {ex.Message}", null); }
    }

    private async Task RetryFetchTitle(ManagedSession session)
    {
        for (var i = 0; i < 3; i++)
        {
            await Task.Delay(3000);
            TryFetchOpenCodeTitle(session);
            if (!string.IsNullOrEmpty(session.Info.Title))
            {
                PersistSessionRecord(session.Info);
                SessionUpdated?.Invoke(session.Info);
                return;
            }
        }
    }

    public bool SendAnswer(string sessionId, string answer) => false;

    public InterruptResult InterruptSession(string sessionId)
    {
        if (!_sessions.TryGetValue(sessionId, out var session))
            return InterruptResult.NotFound;

        if (session.Info.Status != "Active")
            return InterruptResult.NotActive;

        try
        {
            _ = SendNotification(session, "session/cancel", new { sessionId = session.AcpSessionId });
            return InterruptResult.Interrupted;
        }
        catch
        {
            return InterruptResult.Error;
        }
    }

    public async Task StopSession(string sessionId)
    {
        if (!_sessions.TryRemove(sessionId, out var session))
            return;

        try
        {
            if (session.Info.Status == "Active")
                await SendNotification(session, "session/cancel", new { sessionId = session.AcpSessionId });

            if (session.AcpSessionId != null)
            {
                try { await SendRequest(session, "session/close", new { sessionId = session.AcpSessionId }, timeoutSeconds: 5); }
                catch (Exception ex) { _log($"[OpenCode] session/close failed for {sessionId}: {ex.Message}", null); }
            }
        }
        catch (Exception ex) { _log($"[OpenCode] Error during graceful stop of {sessionId}: {ex.Message}", null); }

        session.Info.Status = "Stopped";

        foreach (var (_, tcs) in session.PendingRequests)
            tcs.TrySetCanceled();
        session.PendingRequests.Clear();

        CleanupSessionResources(session);

        CompleteSessionJob(session);
        PersistSessionRecord(session.Info);

        _log($"[OpenCode] Session {sessionId} stopped", null);
        SessionEnded?.Invoke(sessionId, "stopped");
    }

    public void ForceKill(string sessionId)
    {
        if (!_sessions.TryRemove(sessionId, out var session))
        {
            CancelExecution(sessionId);
            return;
        }

        session.Info.Status = "Stopped";

        foreach (var (_, tcs) in session.PendingRequests)
            tcs.TrySetCanceled();
        session.PendingRequests.Clear();

        CleanupSessionResources(session);

        CompleteSessionJob(session);
        PersistSessionRecord(session.Info);

        SessionEnded?.Invoke(sessionId, "force_killed");
    }

    public async Task<OpenCodeSessionInfo?> UpdateSessionConfig(string sessionId, string? model, string? effort)
    {
        if (!_sessions.TryGetValue(sessionId, out var session))
            return null;

        try
        {
            if (model != null && session.AcpSessionId != null)
            {
                await SendRequest(session, "session/set_config_option", new
                {
                    sessionId = session.AcpSessionId,
                    configOption = new { id = "model", value = model },
                });
                session.Info.Model = model;
            }

            if (effort != null && session.AcpSessionId != null)
            {
                await SendRequest(session, "session/set_config_option", new
                {
                    sessionId = session.AcpSessionId,
                    configOption = new { id = "effort", value = effort },
                });
                session.Info.Effort = effort;
            }

            PersistSessionRecord(session.Info);
            SessionUpdated?.Invoke(session.Info);
            return session.Info;
        }
        catch (Exception ex)
        {
            _log($"[OpenCode] Failed to update config for {sessionId}: {ex.Message}", null);
            return null;
        }
    }

    // ===== JSON-RPC over NDJSON =====

    private async Task<JsonElement> SendRequest(ManagedSession session, string method, object? @params = null, int timeoutSeconds = 30)
    {
        var requestId = session.GetNextRequestId();
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.PendingRequests[requestId] = tcs;

        await WriteJsonLine(session, new { jsonrpc = "2.0", id = requestId, method, @params });

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(session.Cts.Token);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        try
        {
            return await tcs.Task.WaitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!session.Cts.Token.IsCancellationRequested)
        {
            session.PendingRequests.TryRemove(requestId, out _);
            throw new TimeoutException($"ACP request '{method}' timed out after {timeoutSeconds}s");
        }
        catch
        {
            session.PendingRequests.TryRemove(requestId, out _);
            throw;
        }
    }

    private async Task SendNotification(ManagedSession session, string method, object? @params = null)
    {
        await WriteJsonLine(session, new { jsonrpc = "2.0", method, @params });
    }

    private async Task WriteJsonLine(ManagedSession session, object message)
    {
        var json = JsonSerializer.Serialize(message);
        await WriteRawJsonLine(session, json);
    }

    private async Task WriteRawJsonLine(ManagedSession session, string json)
    {
        await session.WriteLock.WaitAsync(session.Cts.Token);
        try
        {
            await session.Process.StandardInput.WriteLineAsync(json);
            await session.Process.StandardInput.FlushAsync();
        }
        finally
        {
            session.WriteLock.Release();
        }
    }

    private async Task RespondToRequest(ManagedSession session, JsonElement requestRoot, object result)
    {
        var idJson = requestRoot.GetProperty("id").GetRawText();
        var resultJson = JsonSerializer.Serialize(result);
        await WriteRawJsonLine(session, $"{{\"jsonrpc\":\"2.0\",\"id\":{idJson},\"result\":{resultJson}}}");
    }

    private async Task RespondToRequestError(ManagedSession session, JsonElement requestRoot, string message)
    {
        var idJson = requestRoot.GetProperty("id").GetRawText();
        var errorJson = JsonSerializer.Serialize(new { code = -32000, message });
        await WriteRawJsonLine(session, $"{{\"jsonrpc\":\"2.0\",\"id\":{idJson},\"error\":{errorJson}}}");
    }

    // ===== Background Read Tasks =====

    private async Task ReadStdout(ManagedSession session)
    {
        var reader = session.Process.StandardOutput;
        try
        {
            while (await reader.ReadLineAsync(session.Cts.Token) is { } line)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;

                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;

                    var hasId = root.TryGetProperty("id", out var idProp);
                    var hasMethod = root.TryGetProperty("method", out var methodProp);
                    var hasResult = root.TryGetProperty("result", out _);
                    var hasError = root.TryGetProperty("error", out var errorProp);

                    if (hasId && (hasResult || hasError) && !hasMethod)
                    {
                        var requestId = idProp.GetInt32();
                        if (session.PendingRequests.TryRemove(requestId, out var tcs))
                        {
                            if (hasResult)
                                tcs.TrySetResult(root.GetProperty("result").Clone());
                            else
                                tcs.TrySetException(new Exception($"ACP error: {errorProp}"));
                        }
                    }
                    else if (hasId && hasMethod)
                    {
                        _ = HandleAgentRequest(session, methodProp.GetString()!, root.Clone());
                    }
                    else if (hasMethod && !hasId)
                    {
                        var method = methodProp.GetString();
                        if (method == "session/update" && root.TryGetProperty("params", out var notifParams))
                            HandleSessionUpdate(session, notifParams);
                    }
                }
                catch (JsonException)
                {
                    _log($"[OpenCode] Non-JSON stdout: {(line.Length > 200 ? line[..200] + "..." : line)}", null);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _log($"[OpenCode] ReadStdout error for {session.Info.Id}: {ex.Message}", null);
        }
    }

    private async Task ReadStderr(ManagedSession session)
    {
        try
        {
            var reader = session.Process.StandardError;
            while (await reader.ReadLineAsync(session.Cts.Token) is { } line)
            {
                if (!string.IsNullOrEmpty(line))
                    _log($"[OpenCode:{session.Info.ProjectName}] {line}", null);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _log($"[OpenCode] ReadStderr error for {session.Info.Id}: {ex.Message}", null); }
    }

    // ===== ACP Agent Request Handlers =====

    private async Task HandleAgentRequest(ManagedSession session, string method, JsonElement root)
    {
        try
        {
            switch (method)
            {
                case "session/request_permission":
                    await HandlePermissionRequest(session, root);
                    break;
                case "fs/read_text_file":
                    await HandleFileRead(session, root);
                    break;
                case "fs/write_text_file":
                    await HandleFileWrite(session, root);
                    break;
                default:
                    await RespondToRequestError(session, root, $"Method '{method}' not supported");
                    break;
            }
        }
        catch (Exception ex)
        {
            try { await RespondToRequestError(session, root, ex.Message); }
            catch (Exception logEx) { _log($"[OpenCode] Failed to send error response for {session.Info.Id}: {logEx.Message}", null); }
        }
    }

    private async Task HandlePermissionRequest(ManagedSession session, JsonElement root)
    {
        var options = root.GetProperty("params").GetProperty("options");

        string? selectedOptionId = null;
        foreach (var opt in options.EnumerateArray())
        {
            var kind = opt.GetProperty("kind").GetString();
            if (kind == "allow_always")
            {
                selectedOptionId = opt.GetProperty("optionId").GetString();
                break;
            }
            if (kind == "allow_once" && selectedOptionId == null)
                selectedOptionId = opt.GetProperty("optionId").GetString();
        }

        if (selectedOptionId != null)
            await RespondToRequest(session, root, new { outcome = new { outcome = "selected", optionId = selectedOptionId } });
        else
            await RespondToRequest(session, root, new { outcome = new { outcome = "cancelled" } });
    }

    private async Task HandleFileRead(ManagedSession session, JsonElement root)
    {
        var path = root.GetProperty("params").GetProperty("path").GetString()!;
        try
        {
            var content = await File.ReadAllTextAsync(path);
            await RespondToRequest(session, root, new { content });
        }
        catch (Exception ex)
        {
            await RespondToRequestError(session, root, ex.Message);
        }
    }

    private async Task HandleFileWrite(ManagedSession session, JsonElement root)
    {
        var @params = root.GetProperty("params");
        var path = @params.GetProperty("path").GetString()!;
        var content = @params.GetProperty("content").GetString()!;
        try
        {
            await File.WriteAllTextAsync(path, content);
            await RespondToRequest(session, root, new { });
        }
        catch (Exception ex)
        {
            await RespondToRequestError(session, root, ex.Message);
        }
    }

    // ===== Session Update Notification Handler =====

    private void HandleSessionUpdate(ManagedSession session, JsonElement @params)
    {
        if (!@params.TryGetProperty("update", out var update)) return;

        var updateType = update.TryGetProperty("sessionUpdate", out var ut) ? ut.GetString() : null;

        switch (updateType)
        {
            case "agent_message_chunk":
            {
                var text = update.TryGetProperty("content", out var c) && c.TryGetProperty("text", out var t)
                    ? t.GetString() : null;
                if (text != null)
                {
                    var evt = new OpenCodeStreamEvent
                    {
                        Type = "text", Content = text, IsPartial = true,
                        MessageId = update.TryGetProperty("messageId", out var mid) ? mid.GetString() : null,
                    };
                    EmitAndStore(session, evt);
                }
                break;
            }
            case "agent_thought_chunk":
            {
                var text = update.TryGetProperty("content", out var c) && c.TryGetProperty("text", out var t)
                    ? t.GetString() : null;
                if (text != null)
                {
                    var evt = new OpenCodeStreamEvent
                    {
                        Type = "thinking", Content = text, IsPartial = true,
                        MessageId = update.TryGetProperty("messageId", out var mid) ? mid.GetString() : null,
                    };
                    EmitAndStore(session, evt);
                }
                break;
            }
            case "tool_call":
            {
                var toolName = update.TryGetProperty("title", out var title) ? title.GetString() : null;
                var toolCallId = update.TryGetProperty("toolCallId", out var tcid) ? tcid.GetString() : null;
                object? input = update.TryGetProperty("rawInput", out var ri) ? ri.Clone() : null;
                EmitAndStore(session, new OpenCodeStreamEvent { Type = "tool_use", ToolName = toolName, ToolInput = input, MessageId = toolCallId });
                break;
            }
            case "tool_call_update":
            {
                var status = update.TryGetProperty("status", out var s) ? s.GetString() : null;
                var toolCallId = update.TryGetProperty("toolCallId", out var tcid) ? tcid.GetString() : null;

                if (status is "completed" or "failed")
                {
                    string? resultContent = null;
                    if (update.TryGetProperty("content", out var contentArr) && contentArr.ValueKind == JsonValueKind.Array)
                    {
                        var sb = new StringBuilder();
                        foreach (var item in contentArr.EnumerateArray())
                        {
                            if (item.TryGetProperty("type", out var itemType) && itemType.GetString() == "content"
                                && item.TryGetProperty("content", out var innerContent)
                                && innerContent.TryGetProperty("text", out var text))
                            {
                                sb.Append(text.GetString());
                            }
                        }
                        if (sb.Length > 0) resultContent = sb.ToString();
                    }

                    EmitAndStore(session, new OpenCodeStreamEvent
                    {
                        Type = "tool_result",
                        Content = resultContent,
                        ToolResult = resultContent,
                        ToolName = update.TryGetProperty("title", out var title) ? title.GetString() : null,
                        MessageId = toolCallId,
                    });
                }
                break;
            }
            case "usage_update":
            {
                if (update.TryGetProperty("cost", out var cost) && cost.TryGetProperty("amount", out var amount))
                    session.Info.CostUsd = amount.GetDouble();
                if (update.TryGetProperty("used", out var used))
                    session.Info.InputTokens = used.GetInt32();
                if (update.TryGetProperty("generated", out var generated))
                    session.Info.OutputTokens = generated.GetInt32();
                PersistSessionRecord(session.Info);
                SessionUpdated?.Invoke(session.Info);
                break;
            }
            case "session_info_update":
            {
                if (update.TryGetProperty("title", out var title))
                    session.Info.Title = title.GetString();
                SessionUpdated?.Invoke(session.Info);
                break;
            }
        }
    }

    private void EmitAndStore(ManagedSession session, OpenCodeStreamEvent evt)
    {
        StreamEvent?.Invoke(session.Info.Id, evt);
        session.MessageHistory.Add(evt);
        if (session.MessageHistory.Count > 500)
            session.MessageHistory.RemoveRange(0, session.MessageHistory.Count - 400);

        _store.AddMessage(new OpenCodeMessageRecord
        {
            SessionId = session.Info.Id,
            Role = "assistant",
            EventType = evt.Type,
            Content = evt.Content,
            ToolName = evt.ToolName,
            ToolInput = evt.ToolInput is string s ? s : evt.ToolInput != null ? JsonSerializer.Serialize(evt.ToolInput) : null,
            ToolResult = evt.ToolResult,
            MessageId = evt.MessageId,
            Timestamp = DateTimeOffset.UtcNow,
        });
    }

    // ===== Process Lifecycle =====

    private void OnProcessExited(string sessionId)
    {
        if (!_sessions.TryRemove(sessionId, out var session)) return;

        _log($"[OpenCode] ACP process exited unexpectedly for session {sessionId}", null);
        session.Info.Status = "Stopped";

        foreach (var (_, tcs) in session.PendingRequests)
            tcs.TrySetException(new Exception("ACP process exited"));
        session.PendingRequests.Clear();

        CleanupSessionResources(session);

        CompleteSessionJob(session);
        PersistSessionRecord(session.Info);

        SessionEnded?.Invoke(sessionId, "process_exited");
    }

    private void CleanupSessionResources(ManagedSession session)
    {
        try { session.Cts.Cancel(); } catch (ObjectDisposedException) { }
        try { session.Process.Kill(entireProcessTree: true); } catch { }
        try { session.Process.Dispose(); } catch { }
    }

    private void CompleteSessionJob(ManagedSession session)
    {
        if (!session.Info.JobId.HasValue) return;
        var resultJson = JsonSerializer.Serialize(new
        {
            sessionId = session.Info.Id,
            messageCount = session.Info.MessageCount,
            model = session.Info.Model,
        });
        _jobTracker.MarkCompleted(session.Info.JobId.Value, resultJson: resultJson, costUsd: session.Info.CostUsd);
    }

    private void PersistSessionRecord(OpenCodeSessionInfo info)
    {
        _store.SaveSession(new OpenCodeSessionRecord
        {
            Id = info.Id,
            ProjectName = info.ProjectName,
            ProjectPath = info.ProjectPath,
            Status = info.Status,
            StartedAt = info.StartedAt,
            Model = info.Model,
            Title = info.Title,
            MessageCount = info.MessageCount,
            CostUsd = info.CostUsd,
            InputTokens = info.InputTokens,
            OutputTokens = info.OutputTokens,
            JobId = info.JobId,
            OpenCodeSessionId = info.OpenCodeSessionId,
            Effort = info.Effort,
            Source = info.Source,
        });
    }

    // ===== Stateless Execution (unchanged) =====

    public record ExecuteResult(bool Success, string? Text, string? StreamOutput, string? Model,
                                int InputTokens, int OutputTokens, double? CostUsd, string? Error);

    public async Task<ExecuteResult> ExecuteAsync(
        string prompt, string? container, string? workingDir,
        string? model, int timeout,
        CancellationToken ct,
        string? streamKey = null,
        Dictionary<string, string>? env = null)
    {
        var useDocker = !string.IsNullOrWhiteSpace(container);

        if (!useDocker)
        {
            var opencodePath = ResolveOpenCodePath();
            if (opencodePath == null)
                return new ExecuteResult(false, null, null, null, 0, 0, null,
                    "Could not find 'opencode' CLI. Install opencode or set OpenCodePath in config.");
        }

        var startInfo = new ProcessStartInfo
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        if (useDocker)
        {
            DockerExecHelper.ConfigureForDockerExec(startInfo, container!, "opencode", workingDir, env);
        }
        else
        {
            startInfo.FileName = ResolveOpenCodePath()!;
            if (!string.IsNullOrWhiteSpace(workingDir))
                startInfo.WorkingDirectory = workingDir;
            if (env is not null)
                foreach (var (k, v) in env)
                    startInfo.EnvironmentVariables[k] = v;
        }

        BuildExecArgs(startInfo, model);

        var sw = Stopwatch.StartNew();
        using var process = Process.Start(startInfo);
        if (process == null)
            return new ExecuteResult(false, null, null, null, 0, 0, null, "Failed to start opencode process");
        if (streamKey != null)
            _runningProcesses[streamKey] = process;
        _log($"[OpenCode] Process started in {sw.ElapsedMilliseconds}ms", null);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeout));

        try
        {
            await process.StandardInput.WriteAsync(prompt);
            process.StandardInput.Close();

            var stdoutBuilder = new StringBuilder();
            var stderrBuilder = new StringBuilder();
            var sessionId = streamKey ?? Guid.NewGuid().ToString("N")[..12];
            var messages = new List<OpenCodeMessageRecord>();

            string? line;
            while ((line = await process.StandardOutput.ReadLineAsync(timeoutCts.Token)) != null)
            {
                stdoutBuilder.AppendLine(line);

                try
                {
                    var events = ParseStreamLine(line);
                    foreach (var evt in events)
                    {
                        StreamEvent?.Invoke(sessionId, evt);
                        messages.Add(new OpenCodeMessageRecord
                        {
                            SessionId = sessionId,
                            Role = evt.Type is "tool_result" ? "user" : "assistant",
                            EventType = evt.Type,
                            Content = evt.Content,
                            ToolName = evt.ToolName,
                            ToolInput = evt.ToolInput is string s ? s : evt.ToolInput != null ? JsonSerializer.Serialize(evt.ToolInput) : null,
                            ToolResult = evt.ToolResult,
                            MessageId = evt.MessageId,
                            Timestamp = DateTimeOffset.UtcNow,
                        });
                    }
                }
                catch (JsonException) { }
            }

            var stderrTask = process.StandardError.ReadToEndAsync(timeoutCts.Token);
            var stderr = await stderrTask;
            if (!string.IsNullOrEmpty(stderr))
                stderrBuilder.Append(stderr);

            await process.WaitForExitAsync(timeoutCts.Token);

            if (streamKey != null) _runningProcesses.TryRemove(streamKey, out _);

            var stdout = stdoutBuilder.ToString();
            var result = ParseOutput(stdout, model);

            if (messages.Count > 0)
                _store.AddMessages(messages);

            return result;
        }
        catch (OperationCanceledException)
        {
            if (streamKey != null) _runningProcesses.TryRemove(streamKey, out _);
            try { process.Kill(entireProcessTree: true); } catch { }
            throw;
        }
    }

    private void BuildExecArgs(ProcessStartInfo startInfo, string? model)
    {
        startInfo.ArgumentList.Add("--json");

        var resolvedModel = model ?? _config.Model ?? _config.DefaultModel;
        if (!string.IsNullOrEmpty(resolvedModel))
        {
            startInfo.ArgumentList.Add("--model");
            startInfo.ArgumentList.Add(resolvedModel);
        }
    }

    internal static List<OpenCodeStreamEvent> ParseStreamLine(string line)
    {
        var events = new List<OpenCodeStreamEvent>();
        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;
        var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;

        switch (type)
        {
            case "text":
            case "content":
            {
                var content = root.TryGetProperty("content", out var c) ? c.GetString()
                    : root.TryGetProperty("text", out var tx) ? tx.GetString() : null;
                if (content != null)
                    events.Add(new OpenCodeStreamEvent { Type = "text", Content = content });
                break;
            }
            case "thinking":
            case "reasoning":
            {
                var content = root.TryGetProperty("content", out var c) ? c.GetString()
                    : root.TryGetProperty("thinking", out var th) ? th.GetString() : null;
                if (content != null)
                    events.Add(new OpenCodeStreamEvent { Type = "thinking", Content = content });
                break;
            }
            case "tool_use":
            case "tool_call":
            {
                var toolName = root.TryGetProperty("name", out var n) ? n.GetString()
                    : root.TryGetProperty("tool", out var tl) ? tl.GetString() : null;
                var input = root.TryGetProperty("input", out var i) ? (object)i.Clone()
                    : root.TryGetProperty("arguments", out var a) ? (object)a.Clone() : null;
                events.Add(new OpenCodeStreamEvent { Type = "tool_use", ToolName = toolName, ToolInput = input });
                break;
            }
            case "tool_result":
            {
                var content = root.TryGetProperty("content", out var c) ? c.GetString()
                    : root.TryGetProperty("output", out var o) ? o.GetString() : null;
                events.Add(new OpenCodeStreamEvent { Type = "tool_result", Content = content, ToolResult = content });
                break;
            }
            case "assistant":
            {
                var content = root.TryGetProperty("message", out var msg) && msg.TryGetProperty("content", out var mc)
                    ? ExtractTextFromContent(mc) : null;
                if (content != null)
                    events.Add(new OpenCodeStreamEvent { Type = "text", Content = content });
                break;
            }
            case "error":
            {
                var message = root.TryGetProperty("message", out var m) ? m.GetString()
                    : root.TryGetProperty("error", out var e) ? e.GetString() : null;
                events.Add(new OpenCodeStreamEvent { Type = "error", Content = message });
                break;
            }
            case "status":
            {
                var status = root.TryGetProperty("status", out var s) ? s.GetString() : null;
                events.Add(new OpenCodeStreamEvent { Type = "status", Content = status });
                break;
            }
        }

        return events;
    }

    private static string? ExtractTextFromContent(JsonElement content)
    {
        if (content.ValueKind == JsonValueKind.String)
            return content.GetString();

        if (content.ValueKind == JsonValueKind.Array)
        {
            var sb = new StringBuilder();
            foreach (var block in content.EnumerateArray())
            {
                if (block.TryGetProperty("type", out var bt) && bt.GetString() == "text"
                    && block.TryGetProperty("text", out var txt))
                    sb.Append(txt.GetString());
                else if (block.TryGetProperty("text", out var txt2))
                    sb.Append(txt2.GetString());
            }
            var text = sb.ToString();
            return string.IsNullOrEmpty(text) ? null : text;
        }

        return null;
    }

    private ExecuteResult ParseOutput(string stdout, string? requestedModel)
    {
        var lastText = "";
        var hadToolUse = false;
        var inputTokens = 0;
        var outputTokens = 0;
        double? costUsd = null;
        string? actualModel = requestedModel;

        foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                var evtType = root.TryGetProperty("type", out var t) ? t.GetString() : null;

                if (evtType is "usage" or "turn.completed")
                {
                    var usage = root.TryGetProperty("usage", out var u) ? u : root;
                    if (usage.TryGetProperty("input_tokens", out var it)) inputTokens += it.GetInt32();
                    if (usage.TryGetProperty("output_tokens", out var ot)) outputTokens += ot.GetInt32();
                    if (root.TryGetProperty("cost_usd", out var cost) || root.TryGetProperty("cost", out cost))
                        costUsd = (costUsd ?? 0) + cost.GetDouble();
                }

                if (evtType is "text" or "content" or "assistant")
                {
                    var text = root.TryGetProperty("content", out var c) ? c.GetString()
                        : root.TryGetProperty("text", out var tx) ? tx.GetString() : null;
                    if (!string.IsNullOrEmpty(text))
                        lastText = text;
                }

                if (evtType is "tool_use" or "tool_call")
                    hadToolUse = true;

                if (root.TryGetProperty("model", out var m) && m.ValueKind == JsonValueKind.String)
                    actualModel = m.GetString() ?? requestedModel;
            }
            catch (JsonException) { }
        }

        var success = !string.IsNullOrEmpty(lastText) || hadToolUse;
        return new ExecuteResult(
            success,
            string.IsNullOrEmpty(lastText) && !hadToolUse ? "No response generated" : lastText,
            stdout,
            actualModel, inputTokens, outputTokens, costUsd,
            success ? null : "No output in stream");
    }

    // ===== Query Methods =====

    public List<OpenCodeSessionInfo> GetSessions(int limit = 20, bool includeDismissed = false)
    {
        var activeSessions = _sessions.Values.Select(s => s.Info).ToList();
        var activeIds = activeSessions.Select(s => s.Id).ToHashSet();
        var dbSessions = _store.GetRecentSessions(activeIds, limit, includeDismissed);

        return activeSessions
            .Concat(dbSessions.Select(ToSessionInfo))
            .OrderByDescending(s => s.StartedAt)
            .Take(limit)
            .ToList();
    }

    public (OpenCodeSessionInfo? info, List<OpenCodeMessageRecord> messages) GetSession(string sessionId)
    {
        if (_sessions.TryGetValue(sessionId, out var managed))
            return (managed.Info, _store.GetMessages(sessionId));

        var record = _store.FindSession(sessionId);
        if (record == null) return (null, new());
        return (ToSessionInfo(record), _store.GetMessages(sessionId));
    }

    public (OpenCodeSessionInfo? info, List<OpenCodeMessageRecord> messages) GetSessionByJobId(Guid jobId)
    {
        var managed = _sessions.Values.FirstOrDefault(s => s.Info.JobId == jobId);
        if (managed != null)
            return (managed.Info, _store.GetMessages(managed.Info.Id));

        var record = _store.FindSessionByJobId(jobId);
        if (record == null) return (null, new());
        return (ToSessionInfo(record), _store.GetMessages(record.Id));
    }

    public Dictionary<Guid, string> GetSessionStatusesByJobIds(IEnumerable<Guid> jobIds)
    {
        var result = _store.GetSessionStatusesByJobIds(jobIds);
        foreach (var session in _sessions.Values)
        {
            if (session.Info.JobId.HasValue)
                result[session.Info.JobId.Value] = session.Info.Status;
        }
        return result;
    }

    public void DismissSession(string sessionId) => _store.DismissSession(sessionId);

    public List<ProjectInfo> ListProjects()
    {
        var root = _config.ProjectsRoot;
        if (!Directory.Exists(root)) return new();

        return Directory.GetDirectories(root)
            .Select(dir => new ProjectInfo
            {
                Name = Path.GetFileName(dir),
                Path = dir,
                HasClaudeMd = File.Exists(Path.Combine(dir, "CLAUDE.md")),
            })
            .OrderBy(p => p.Name)
            .ToList();
    }

    // ===== Process Management =====

    public void CancelExecution(string key)
    {
        if (_sessions.TryGetValue(key, out _))
        {
            ForceKill(key);
            return;
        }

        if (_runningProcesses.TryRemove(key, out var process))
        {
            try { process.Kill(entireProcessTree: true); }
            catch { }
        }
    }

    public async Task StopAllAsync()
    {
        var sessionIds = _sessions.Keys.ToList();
        foreach (var id in sessionIds)
            await StopSession(id);

        foreach (var key in _runningProcesses.Keys.ToList())
            CancelExecution(key);
    }

    private static OpenCodeSessionInfo ToSessionInfo(OpenCodeSessionRecord r) => new()
    {
        Id = r.Id,
        ProjectName = r.ProjectName,
        ProjectPath = r.ProjectPath,
        Status = r.Status,
        StartedAt = r.StartedAt,
        Model = r.Model,
        Title = r.Title,
        MessageCount = r.MessageCount,
        CostUsd = r.CostUsd,
        InputTokens = r.InputTokens,
        OutputTokens = r.OutputTokens,
        JobId = r.JobId,
        OpenCodeSessionId = r.OpenCodeSessionId,
        Effort = r.Effort,
        Source = r.Source,
    };

    public string? ResolveOpenCodePath()
    {
        if (_config.OpenCodePath != null)
            return File.Exists(_config.OpenCodePath) ? _config.OpenCodePath : null;

        var pathDirs = Environment.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator) ?? [];
        foreach (var dir in pathDirs)
        {
            foreach (var ext in new[] { ".exe", ".cmd", "" })
            {
                var candidate = Path.Combine(dir, $"opencode{ext}");
                if (File.Exists(candidate))
                    return candidate;
            }
        }

        var npmGlobal = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var npmCandidate = Path.Combine(npmGlobal, "npm", "opencode.cmd");
        if (File.Exists(npmCandidate))
            return npmCandidate;

        return null;
    }
}
