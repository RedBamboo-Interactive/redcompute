using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using RedCompute.App.Data;
using RedCompute.App.Services.Jobs;
using RedCompute.Core.Claude;
using RedCompute.Core.Configuration;

namespace RedCompute.App.Services.Claude;

public class ClaudeSessionService
{
    private readonly ConcurrentDictionary<string, ManagedSession> _sessions = new();
    private readonly ClaudeConfig _config;
    private readonly JobTrackingService _jobTracker;
    private readonly Action<string, Guid?> _log;

    public event Action<ClaudeSessionInfo>? SessionCreated;
    public event Action<ClaudeSessionInfo>? SessionUpdated;
    public event Action<string, string>? SessionEnded;
    public event Action<string, ClaudeStreamEvent>? StreamEvent;

    public ClaudeSessionService(ClaudeConfig config, JobTrackingService jobTracker, Action<string, Guid?> log)
    {
        _config = config;
        _jobTracker = jobTracker;
        _log = log;
        RecoverSessions();
    }

    private void RecoverSessions()
    {
        try
        {
            using var db = new RedComputeDbContext();
            var active = db.ClaudeSessions
                .Where(s => s.Status == "Active" || s.Status == "Idle" || s.Status == "Starting")
                .ToList();
            foreach (var s in active)
            {
                s.Status = "Stopped";
                _log($"[Claude] Marked orphaned session {s.Id} ({s.ProjectName}) as stopped", null);
            }
            if (active.Count > 0)
                db.SaveChanges();
        }
        catch (Exception ex)
        {
            _log($"[Claude] Failed to recover sessions: {ex.Message}", null);
        }
    }

    public List<ProjectInfo> ListProjects()
    {
        var root = _config.ProjectsRoot;
        if (!Directory.Exists(root))
            return [];

        return Directory.GetDirectories(root)
            .Select(d => new ProjectInfo
            {
                Name = Path.GetFileName(d),
                Path = d,
                HasClaudeMd = File.Exists(Path.Combine(d, "CLAUDE.md")),
                HasIcon = FindProjectIcon(d) != null
            })
            .OrderBy(p => p.Name)
            .ToList();
    }

    public string? LastStartError { get; private set; }

    public ClaudeSessionInfo? StartSession(string projectPath)
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

        var id = Guid.NewGuid().ToString("N")[..12];
        var info = new ClaudeSessionInfo
        {
            Id = id,
            ProjectName = Path.GetFileName(projectPath),
            ProjectPath = projectPath,
            Status = SessionStatus.Starting,
            StartedAt = DateTimeOffset.UtcNow
        };

        var claudePath = ResolveClaudePath();
        if (claudePath == null)
        {
            LastStartError = "Could not find 'claude' CLI. Install it or set ClaudePath in config.";
            _log("[Claude] Could not find 'claude' CLI on PATH", null);
            return null;
        }

        var args = BuildArgs();

        var startInfo = new ProcessStartInfo
        {
            FileName = claudePath,
            Arguments = args,
            WorkingDirectory = projectPath,
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        Process process;
        try
        {
            process = Process.Start(startInfo)!;
        }
        catch (Exception ex)
        {
            LastStartError = $"Failed to start process: {ex.Message}";
            _log($"[Claude] Failed to start process: {ex.Message}", null);
            return null;
        }

        var cts = new CancellationTokenSource();
        var session = new ManagedSession(info, process, cts);
        _sessions[id] = session;

        process.EnableRaisingEvents = true;
        process.Exited += (_, _) => OnProcessExited(id);

        _ = ReadStdout(session);
        _ = ReadStderr(session);

        // Process is alive and waiting for input — mark as idle (ready)
        info.Status = SessionStatus.Idle;

        // Create a job record for this session
        var inputJson = System.Text.Json.JsonSerializer.Serialize(new { projectPath, projectName = info.ProjectName });
        var job = _jobTracker.CreateJob("ai-session", "Claude Code", inputJson, name: info.ProjectName);
        _jobTracker.MarkRunning(job.Id);
        info.JobId = job.Id;

        PersistSessionRecord(info);

        _log($"[Claude] Session {id} started for {info.ProjectName} (PID {process.Id}, Job {job.Id})", null);
        SessionCreated?.Invoke(info);

        return info;
    }

    public ClaudeSessionInfo? ResumeSession(string sessionId)
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

        using var db = new RedComputeDbContext();
        var record = db.ClaudeSessions.Find(sessionId);
        if (record == null)
        {
            LastStartError = "Session not found";
            return null;
        }

        if (string.IsNullOrEmpty(record.ClaudeSessionId))
        {
            LastStartError = "Session has no Claude session ID to resume";
            return null;
        }

        if (!Directory.Exists(record.ProjectPath))
        {
            LastStartError = $"Project path not found: {record.ProjectPath}";
            return null;
        }

        record.Dismissed = false;
        db.SaveChanges();

        var claudePath = ResolveClaudePath();
        if (claudePath == null)
        {
            LastStartError = "Could not find 'claude' CLI. Install it or set ClaudePath in config.";
            return null;
        }

        var info = new ClaudeSessionInfo
        {
            Id = record.Id,
            ProjectName = record.ProjectName,
            ProjectPath = record.ProjectPath,
            Status = SessionStatus.Starting,
            StartedAt = record.StartedAt,
            Model = record.Model,
            ClaudeSessionId = record.ClaudeSessionId,
            Title = record.Title,
            MessageCount = record.MessageCount,
            CostUsd = record.CostUsd,
            InputTokens = record.InputTokens,
            OutputTokens = record.OutputTokens,
            CacheReadInputTokens = record.CacheReadInputTokens,
            CacheCreationInputTokens = record.CacheCreationInputTokens,
            ContextWindow = record.ContextWindow,
            Effort = record.Effort,
        };

        var args = BuildArgs(record.ClaudeSessionId, record.Model, record.Effort);
        var startInfo = new ProcessStartInfo
        {
            FileName = claudePath,
            Arguments = args,
            WorkingDirectory = record.ProjectPath,
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        Process process;
        try
        {
            process = Process.Start(startInfo)!;
        }
        catch (Exception ex)
        {
            LastStartError = $"Failed to start process: {ex.Message}";
            return null;
        }

        var cts = new CancellationTokenSource();
        var session = new ManagedSession(info, process, cts);
        _sessions[sessionId] = session;

        process.EnableRaisingEvents = true;
        process.Exited += (_, _) => OnProcessExited(sessionId);

        _ = ReadStdout(session);
        _ = ReadStderr(session);

        info.Status = SessionStatus.Idle;

        var job = _jobTracker.CreateJob("ai-session", "Claude Code",
            System.Text.Json.JsonSerializer.Serialize(new { projectPath = record.ProjectPath, projectName = record.ProjectName, resumed = true }),
            name: record.ProjectName);
        _jobTracker.MarkRunning(job.Id);
        info.JobId = job.Id;

        PersistSessionRecord(info);

        _log($"[Claude] Session {sessionId} resumed for {info.ProjectName} (PID {process.Id}, Job {job.Id})", null);
        SessionCreated?.Invoke(info);

        return info;
    }

    public bool SendMessage(string sessionId, string content, ImageAttachment[]? images = null)
    {
        if (!_sessions.TryGetValue(sessionId, out var session))
            return false;

        if (session.Info.Status is SessionStatus.Stopped or SessionStatus.Error)
            return false;

        try
        {
            object contentPayload;
            if (images != null && images.Length > 0)
            {
                var blocks = new List<object>();
                if (!string.IsNullOrWhiteSpace(content))
                    blocks.Add(new { type = "text", text = content });
                foreach (var img in images)
                    blocks.Add(new { type = "image", source = new { type = "base64", media_type = img.MediaType, data = img.Base64 } });
                contentPayload = blocks;
            }
            else
            {
                contentPayload = content;
            }

            var msg = new
            {
                type = "user",
                message = new { role = "user", content = contentPayload },
                parent_tool_use_id = (string?)null
            };
            session.Process.StandardInput.WriteLine(JsonSerializer.Serialize(msg));
            session.Process.StandardInput.Flush();
            session.Info.MessageCount++;
            session.Info.Status = SessionStatus.Active;
            SessionUpdated?.Invoke(session.Info);

            PersistMessage(sessionId, "user", "text", content, null, null, null, null);

            return true;
        }
        catch (Exception ex)
        {
            _log($"[Claude] Failed to send message to {sessionId}: {ex.Message}", null);
            return false;
        }
    }

    public enum InterruptResult { Interrupted, NotActive, NotFound, Error }

    public InterruptResult InterruptSession(string sessionId)
    {
        if (!_sessions.TryGetValue(sessionId, out var session))
            return InterruptResult.NotFound;

        if (session.Info.Status != SessionStatus.Active)
            return InterruptResult.NotActive;

        try
        {
            var msg = new { type = "abort" };
            session.Process.StandardInput.WriteLine(JsonSerializer.Serialize(msg));
            session.Process.StandardInput.Flush();
            session.InterruptPending = true;

            _log($"[Claude] Interrupt sent for session {sessionId}", null);

            StreamEvent?.Invoke(sessionId, new ClaudeStreamEvent
            {
                Type = "status",
                Content = "interrupting"
            });

            return InterruptResult.Interrupted;
        }
        catch (Exception ex)
        {
            _log($"[Claude] Failed to send interrupt to {sessionId}: {ex.Message}", null);
            return InterruptResult.Error;
        }
    }

    public bool SetPermissionMode(string sessionId, string mode)
    {
        if (!_sessions.TryGetValue(sessionId, out var session))
            return false;

        try
        {
            var requestId = $"perm-{++session.ControlRequestCounter}";
            var msg = new
            {
                type = "control_request",
                request_id = requestId,
                request = new { subtype = "set_permission_mode", mode }
            };
            session.Process.StandardInput.WriteLine(JsonSerializer.Serialize(msg));
            session.Process.StandardInput.Flush();

            session.Info.PermissionMode = mode;
            SessionUpdated?.Invoke(session.Info);

            _log($"[Claude] Permission mode set to '{mode}' for session {sessionId}", null);
            return true;
        }
        catch (Exception ex)
        {
            _log($"[Claude] Failed to set permission mode for {sessionId}: {ex.Message}", null);
            return false;
        }
    }

    public async Task StopSession(string sessionId)
    {
        if (!_sessions.TryGetValue(sessionId, out var session))
            return;

        _log($"[Claude] Stopping session {sessionId}", null);

        try
        {
            session.Process.StandardInput.Close();
        }
        catch { }

        var exited = await WaitForExit(session.Process, TimeSpan.FromSeconds(10));
        if (!exited)
        {
            try { session.Process.Kill(entireProcessTree: true); } catch { }
        }

        session.Cts.Cancel();
        session.Info.Status = SessionStatus.Stopped;
        _sessions.TryRemove(sessionId, out _);
        PersistSessionRecord(session.Info);

        if (session.Info.JobId.HasValue)
            _jobTracker.MarkCompleted(session.Info.JobId.Value, resultJson: $"{{\"messages\":{session.Info.MessageCount}}}");

        SessionEnded?.Invoke(sessionId, "stopped");
    }

    public async Task ForceKill(string sessionId)
    {
        if (!_sessions.TryRemove(sessionId, out var session))
            return;

        try { session.Process.Kill(entireProcessTree: true); } catch { }
        session.Cts.Cancel();
        session.Info.Status = SessionStatus.Stopped;
        PersistSessionRecord(session.Info);

        if (session.Info.JobId.HasValue)
            _jobTracker.MarkCompleted(session.Info.JobId.Value, resultJson: $"{{\"messages\":{session.Info.MessageCount}}}");

        SessionEnded?.Invoke(sessionId, "killed");
        await Task.CompletedTask;
    }

    public async Task<ClaudeSessionInfo?> UpdateSessionConfig(string sessionId, string? model, string? effort)
    {
        if (_sessions.ContainsKey(sessionId))
            await StopSession(sessionId);

        using var db = new RedComputeDbContext();
        var record = db.ClaudeSessions.Find(sessionId);
        if (record == null) return null;

        if (model != null) record.Model = model;
        if (effort != null) record.Effort = effort;
        db.SaveChanges();

        if (!string.IsNullOrEmpty(record.ClaudeSessionId))
            return ResumeSession(sessionId);

        var info = ToSessionInfo(record);
        SessionUpdated?.Invoke(info);
        return info;
    }

    public void DismissSession(string sessionId)
    {
        try
        {
            using var db = new RedComputeDbContext();
            var record = db.ClaudeSessions.Find(sessionId);
            if (record != null)
            {
                record.Dismissed = true;
                db.SaveChanges();
            }
        }
        catch (Exception ex)
        {
            _log($"[Claude] Failed to dismiss session {sessionId}: {ex.Message}", null);
        }
    }

    public List<ClaudeSessionInfo> GetSessions()
    {
        var live = _sessions.Values.Select(s => s.Info).ToList();
        var liveIds = live.Select(s => s.Id).ToHashSet();

        using var db = new RedComputeDbContext();
        var dbSessions = db.ClaudeSessions
            .Where(s => !liveIds.Contains(s.Id) && !s.Dismissed)
            .OrderByDescending(s => s.StartedAt)
            .Take(20)
            .ToList()
            .Select(ToSessionInfo)
            .ToList();

        return [.. live, .. dbSessions];
    }

    public (ClaudeSessionInfo? Info, List<ClaudeMessageRecord> History) GetSession(string sessionId)
    {
        if (_sessions.TryGetValue(sessionId, out var session))
            return (session.Info, GetHistory(sessionId));

        using var db = new RedComputeDbContext();
        var record = db.ClaudeSessions.Find(sessionId);
        if (record == null) return (null, []);
        return (ToSessionInfo(record), GetHistory(sessionId));
    }

    private static ClaudeSessionInfo ToSessionInfo(ClaudeSessionRecord r) => new()
    {
        Id = r.Id,
        ProjectName = r.ProjectName,
        ProjectPath = r.ProjectPath,
        Status = Enum.TryParse<SessionStatus>(r.Status, out var s) ? s : SessionStatus.Stopped,
        StartedAt = r.StartedAt,
        Model = r.Model,
        ClaudeSessionId = r.ClaudeSessionId,
        Title = r.Title,
        MessageCount = r.MessageCount,
        CostUsd = r.CostUsd,
        InputTokens = r.InputTokens,
        OutputTokens = r.OutputTokens,
        CacheReadInputTokens = r.CacheReadInputTokens,
        CacheCreationInputTokens = r.CacheCreationInputTokens,
        ContextWindow = r.ContextWindow,
        Effort = r.Effort,
        JobId = r.JobId
    };

    public List<ClaudeMessageRecord> GetHistory(string sessionId, int limit = 500)
    {
        using var db = new RedComputeDbContext();
        return db.ClaudeMessages
            .Where(m => m.SessionId == sessionId)
            .OrderByDescending(m => m.Id)
            .Take(limit)
            .ToList()
            .OrderBy(m => m.Id)
            .ToList();
    }

    public async Task StopAllAsync()
    {
        var ids = _sessions.Keys.ToList();
        foreach (var id in ids)
            await StopSession(id);
    }

    private string? ResolveClaudePath()
    {
        if (_config.ClaudePath != null)
            return File.Exists(_config.ClaudePath) ? _config.ClaudePath : null;

        // Prefer the native claude.exe from the Agent SDK (supports stream-json IPC)
        var npmGlobal = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var sdkExe = Path.Combine(npmGlobal, "npm", "node_modules", "@anthropic-ai",
            "claude-agent-sdk", "node_modules", "@anthropic-ai",
            "claude-agent-sdk-win32-x64", "claude.exe");
        if (File.Exists(sdkExe))
            return sdkExe;

        // Fallback: search PATH for claude.cmd / claude.exe
        var pathDirs = Environment.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator) ?? [];
        foreach (var dir in pathDirs)
        {
            foreach (var ext in new[] { ".exe", ".cmd", "" })
            {
                var candidate = Path.Combine(dir, $"claude{ext}");
                if (File.Exists(candidate))
                    return candidate;
            }
        }
        return null;
    }

    private string BuildArgs(string? resumeClaudeSessionId = null, string? model = null, string? effort = null)
    {
        var sb = new StringBuilder("--output-format stream-json --verbose --input-format stream-json --permission-mode bypassPermissions");
        var m = model ?? _config.Model;
        if (m != null)
            sb.Append($" --model {m}");
        if (effort != null)
            sb.Append($" --effort {effort}");
        if (resumeClaudeSessionId != null)
            sb.Append($" --resume {resumeClaudeSessionId}");
        return sb.ToString();
    }

    private async Task ReadStdout(ManagedSession session)
    {
        var reader = session.Process.StandardOutput;
        var ct = session.Cts.Token;
        var lastTitleCheck = DateTimeOffset.MinValue;

        try
        {
            while (!ct.IsCancellationRequested && await reader.ReadLineAsync(ct) is { } line)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;

                try
                {
                    var evt = ParseStreamLine(line, session);
                    if (evt != null)
                    {
                        StreamEvent?.Invoke(session.Info.Id, evt);
                        session.MessageHistory.Add(evt);
                        TrimHistory(session);
                        PersistMessage(session.Info.Id, "assistant", evt.Type, evt.Content,
                            evt.ToolName, evt.ToolInput?.ToString(), evt.ToolResult, evt.MessageId);
                    }

                    if (session.Info.Title == null && DateTimeOffset.UtcNow - lastTitleCheck > TimeSpan.FromSeconds(3))
                    {
                        lastTitleCheck = DateTimeOffset.UtcNow;
                        SyncTitleFromClaudeSession(session);
                        if (session.Info.Title != null)
                        {
                            PersistSessionRecord(session.Info);
                            SessionUpdated?.Invoke(session.Info);
                        }
                    }
                }
                catch (JsonException)
                {
                    // Non-JSON output, emit as raw text
                    var raw = new ClaudeStreamEvent { Type = "text", Content = line, IsPartial = false };
                    StreamEvent?.Invoke(session.Info.Id, raw);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _log($"[Claude] Stdout reader error for {session.Info.Id}: {ex.Message}", null);
        }
    }

    private async Task ReadStderr(ManagedSession session)
    {
        var reader = session.Process.StandardError;
        var ct = session.Cts.Token;

        try
        {
            while (!ct.IsCancellationRequested && await reader.ReadLineAsync(ct) is { } line)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                _log($"[Claude:{session.Info.ProjectName}] {line}", null);
            }
        }
        catch (OperationCanceledException) { }
        catch { }
    }

    private ClaudeStreamEvent? ParseStreamLine(string line, ManagedSession session)
    {
        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;

        var type = root.GetProperty("type").GetString()!;

        switch (type)
        {
            case "system":
                HandleSystemEvent(root, session);
                return null;

            case "assistant":
                return ParseAssistantEvent(root);

            case "result":
                return ParseResultEvent(root, session);

            case "control_response":
                HandleControlResponse(root, session);
                return null;

            case "rate_limit_event":
                return null;

            default:
                return null;
        }
    }

    private static int? ParseContextFromModelHint(string? model)
    {
        if (model == null) return null;
        var match = Regex.Match(model, @"\[(\d+)([km])\]", RegexOptions.IgnoreCase);
        if (!match.Success) return null;
        var n = int.Parse(match.Groups[1].Value);
        return n * (match.Groups[2].Value.ToLowerInvariant() == "m" ? 1_000_000 : 1_000);
    }

    private void HandleSystemEvent(JsonElement root, ManagedSession session)
    {
        if (root.TryGetProperty("subtype", out var subtype) && subtype.GetString() == "init")
        {
            if (root.TryGetProperty("session_id", out var sid))
                session.Info.ClaudeSessionId = sid.GetString();
            if (root.TryGetProperty("model", out var model))
            {
                session.Info.Model = model.GetString();
                session.Info.ContextWindow ??= ParseContextFromModelHint(model.GetString());
            }
            if (root.TryGetProperty("permissionMode", out var pm))
                session.Info.PermissionMode = pm.GetString() ?? "bypassPermissions";

            session.Info.Status = SessionStatus.Active;
            PersistSessionRecord(session.Info);
            SessionUpdated?.Invoke(session.Info);
            _log($"[Claude] Session {session.Info.Id} active (model: {session.Info.Model}, mode: {session.Info.PermissionMode})", null);
        }
    }

    private void HandleControlResponse(JsonElement root, ManagedSession session)
    {
        if (root.TryGetProperty("response", out var resp))
        {
            var subtype = resp.TryGetProperty("subtype", out var sub) ? sub.GetString() : null;
            var reqId = resp.TryGetProperty("request_id", out var rid) ? rid.GetString() : null;

            if (subtype == "error")
            {
                var error = resp.TryGetProperty("error", out var e) ? e.GetString() : "unknown";
                _log($"[Claude] Control request {reqId} failed: {error}", null);
            }
            else
            {
                _log($"[Claude] Control request {reqId} succeeded", null);
            }
        }
    }

    private static ClaudeStreamEvent? ParseAssistantEvent(JsonElement root)
    {
        if (!root.TryGetProperty("message", out var message))
        {
            // Might be a content_block_delta style event
            if (root.TryGetProperty("content_block", out var block))
            {
                var blockType = block.TryGetProperty("type", out var bt) ? bt.GetString() : null;
                if (blockType == "thinking")
                {
                    var thinking = block.TryGetProperty("thinking", out var t) ? t.GetString() : null;
                    return new ClaudeStreamEvent { Type = "thinking", Content = thinking };
                }
            }
            return null;
        }

        if (!message.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var block in content.EnumerateArray())
        {
            var blockType = block.TryGetProperty("type", out var bt) ? bt.GetString() : null;

            switch (blockType)
            {
                case "text":
                    var text = block.TryGetProperty("text", out var t) ? t.GetString() : null;
                    return new ClaudeStreamEvent { Type = "text", Content = text };

                case "tool_use":
                    var toolName = block.TryGetProperty("name", out var n) ? n.GetString() : null;
                    var toolInput = block.TryGetProperty("input", out var inp) ? (object?)inp.ToString() : null;
                    var toolId = block.TryGetProperty("id", out var tid) ? tid.GetString() : null;
                    return new ClaudeStreamEvent
                    {
                        Type = "tool_use",
                        ToolName = toolName,
                        ToolInput = toolInput,
                        MessageId = toolId
                    };

                case "thinking":
                    var thinkContent = block.TryGetProperty("thinking", out var th) ? th.GetString() : null;
                    return new ClaudeStreamEvent { Type = "thinking", Content = thinkContent };
            }
        }

        return null;
    }

    private ClaudeStreamEvent? ParseResultEvent(JsonElement root, ManagedSession session)
    {
        var subtype = root.TryGetProperty("subtype", out var sub) ? sub.GetString() : null;

        if (subtype == "success")
        {
            session.InterruptPending = false;
            session.Info.Status = SessionStatus.Idle;

            if (root.TryGetProperty("total_cost_usd", out var cost))
                session.Info.CostUsd = (session.Info.CostUsd ?? 0) + cost.GetDouble();
            ParseTokenUsage(root, session);

            SyncTitleFromClaudeSession(session);
            PersistSessionRecord(session.Info);
            SessionUpdated?.Invoke(session.Info);

            var resultText = root.TryGetProperty("result", out var r) ? r.GetString() : null;
            return new ClaudeStreamEvent { Type = "status", Content = "idle" };
        }

        if (subtype == "error")
        {
            var error = root.TryGetProperty("error", out var e) ? e.GetString() : "unknown error";

            if (session.InterruptPending)
            {
                session.InterruptPending = false;
                session.Info.Status = SessionStatus.Idle;

                if (root.TryGetProperty("total_cost_usd", out var intCost))
                    session.Info.CostUsd = (session.Info.CostUsd ?? 0) + intCost.GetDouble();
                ParseTokenUsage(root, session);

                PersistSessionRecord(session.Info);
                SessionUpdated?.Invoke(session.Info);
                return new ClaudeStreamEvent { Type = "status", Content = "interrupted" };
            }

            return new ClaudeStreamEvent { Type = "error", Content = error };
        }

        // tool_result or other result types
        var content = root.TryGetProperty("content", out var c) ? c.GetString() : null;
        var toolUseId = root.TryGetProperty("tool_use_id", out var tuid) ? tuid.GetString() : null;
        return new ClaudeStreamEvent
        {
            Type = "tool_result",
            ToolResult = content,
            MessageId = toolUseId
        };
    }

    private void ParseTokenUsage(JsonElement root, ManagedSession session)
    {
        var numTurns = root.TryGetProperty("num_turns", out var nt) ? nt.GetInt32() : 1;
        if (numTurns < 1) numTurns = 1;

        // usage contains cumulative tokens across all API sub-calls in the turn
        // Divide by num_turns to get per-call context size
        if (root.TryGetProperty("usage", out var usage))
        {
            if (usage.TryGetProperty("input_tokens", out var inTok))
                session.Info.InputTokens = inTok.GetInt32() / numTurns;
            if (usage.TryGetProperty("output_tokens", out var outTok))
                session.Info.OutputTokens = outTok.GetInt32() / numTurns;
            if (usage.TryGetProperty("cache_read_input_tokens", out var cacheR))
                session.Info.CacheReadInputTokens = cacheR.GetInt32() / numTurns;
            if (usage.TryGetProperty("cache_creation_input_tokens", out var cacheC))
                session.Info.CacheCreationInputTokens = cacheC.GetInt32() / numTurns;
        }

        // modelUsage contains contextWindow (the actual max for this model)
        if (root.TryGetProperty("modelUsage", out var modelUsage))
        {
            foreach (var model in modelUsage.EnumerateObject())
            {
                if (model.Value.TryGetProperty("contextWindow", out var cw))
                    session.Info.ContextWindow = cw.GetInt32();
                break;
            }
        }
    }

    private void OnProcessExited(string sessionId)
    {
        if (!_sessions.TryRemove(sessionId, out var session))
            return;

        var exitCode = session.Process.ExitCode;
        session.Cts.Cancel();

        if (session.Info.Status != SessionStatus.Stopped)
        {
            session.Info.Status = SessionStatus.Error;
            PersistSessionRecord(session.Info);
            _log($"[Claude] Session {sessionId} process exited unexpectedly (code {exitCode})", null);

            if (session.Info.JobId.HasValue)
                _jobTracker.MarkFailed(session.Info.JobId.Value, $"Process exited with code {exitCode}");

            SessionEnded?.Invoke(sessionId, $"process_exited:{exitCode}");
        }
    }

    private void SyncTitleFromClaudeSession(ManagedSession session)
    {
        try
        {
            var claudeSessionId = session.Info.ClaudeSessionId;
            if (string.IsNullOrEmpty(claudeSessionId) || string.IsNullOrEmpty(session.Info.ProjectPath))
                return;

            var slug = session.Info.ProjectPath.Replace(":", "-").Replace("\\", "-").Replace("/", "-");
            var jsonlPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".claude", "projects", slug, $"{claudeSessionId}.jsonl");

            if (!File.Exists(jsonlPath)) return;

            string? aiTitle = null;
            foreach (var line in File.ReadLines(jsonlPath))
            {
                if (line.Contains("\"ai-title\""))
                {
                    using var doc = JsonDocument.Parse(line);
                    if (doc.RootElement.TryGetProperty("aiTitle", out var t))
                        aiTitle = t.GetString();
                }
            }

            if (!string.IsNullOrEmpty(aiTitle))
                session.Info.Title = aiTitle;
        }
        catch (Exception ex)
        {
            _log($"[Claude] Failed to read session title for {session.Info.Id}: {ex.Message}", null);
        }
    }

    private static async Task<bool> WaitForExit(Process process, TimeSpan timeout)
    {
        try
        {
            await process.WaitForExitAsync(new CancellationTokenSource(timeout).Token);
            return true;
        }
        catch { return false; }
    }

    private static void TrimHistory(ManagedSession session)
    {
        if (session.MessageHistory.Count > 500)
            session.MessageHistory.RemoveRange(0, session.MessageHistory.Count - 400);
    }

    private void PersistSessionRecord(ClaudeSessionInfo info)
    {
        try
        {
            using var db = new RedComputeDbContext();
            var existing = db.ClaudeSessions.Find(info.Id);
            if (existing != null)
            {
                existing.Status = info.Status.ToString();
                existing.Model = info.Model;
                existing.ClaudeSessionId = info.ClaudeSessionId;
                existing.Title = info.Title;
                existing.MessageCount = info.MessageCount;
                existing.CostUsd = info.CostUsd;
                existing.InputTokens = info.InputTokens;
                existing.OutputTokens = info.OutputTokens;
                existing.CacheReadInputTokens = info.CacheReadInputTokens;
                existing.CacheCreationInputTokens = info.CacheCreationInputTokens;
                existing.ContextWindow = info.ContextWindow;
                existing.Effort = info.Effort;
                existing.JobId = info.JobId;
            }
            else
            {
                db.ClaudeSessions.Add(new ClaudeSessionRecord
                {
                    Id = info.Id,
                    ProjectName = info.ProjectName,
                    ProjectPath = info.ProjectPath,
                    Status = info.Status.ToString(),
                    StartedAt = info.StartedAt,
                    Model = info.Model,
                    ClaudeSessionId = info.ClaudeSessionId,
                    Title = info.Title,
                    MessageCount = info.MessageCount,
                    CostUsd = info.CostUsd,
                    InputTokens = info.InputTokens,
                    OutputTokens = info.OutputTokens,
                    CacheReadInputTokens = info.CacheReadInputTokens,
                    CacheCreationInputTokens = info.CacheCreationInputTokens,
                    ContextWindow = info.ContextWindow,
                    Effort = info.Effort,
                    JobId = info.JobId
                });
            }
            db.SaveChanges();
        }
        catch (Exception ex)
        {
            _log($"[Claude] Failed to persist session record: {ex.Message}", null);
        }
    }

    private void PersistMessage(string sessionId, string role, string eventType, string? content,
        string? toolName, string? toolInput, string? toolResult, string? messageId)
    {
        try
        {
            using var db = new RedComputeDbContext();
            db.ClaudeMessages.Add(new ClaudeMessageRecord
            {
                SessionId = sessionId,
                Role = role,
                EventType = eventType,
                Content = content,
                ToolName = toolName,
                ToolInput = toolInput,
                ToolResult = toolResult,
                MessageId = messageId,
                Timestamp = DateTimeOffset.UtcNow
            });
            db.SaveChanges();
        }
        catch (Exception ex)
        {
            _log($"[Claude] Failed to persist message: {ex.Message}", null);
        }
    }

    private static readonly string[] IconCandidates =
    [
        "public/favicon.ico", "public/favicon.png", "public/favicon.svg",
        "public/logo.png", "public/logo.svg",
        "favicon.ico", "favicon.png", "favicon.svg",
        "logo.png", "logo.svg",
        "icon.png", "icon.ico", "icon.svg",
        "wwwroot/favicon.ico",
    ];

    internal static string? FindProjectIcon(string projectPath)
    {
        foreach (var candidate in IconCandidates)
        {
            var full = Path.Combine(projectPath, candidate.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(full))
                return full;
        }
        return null;
    }

    public string? GetProjectIconPath(string projectName)
    {
        var root = _config.ProjectsRoot;
        if (!Directory.Exists(root)) return null;
        var projectDir = Path.Combine(root, projectName);
        if (!Directory.Exists(projectDir)) return null;
        return FindProjectIcon(projectDir);
    }

    private class ManagedSession
    {
        public ClaudeSessionInfo Info { get; }
        public Process Process { get; }
        public CancellationTokenSource Cts { get; }
        public List<object> MessageHistory { get; } = new();
        public bool InterruptPending { get; set; }
        public int ControlRequestCounter { get; set; }

        public ManagedSession(ClaudeSessionInfo info, Process process, CancellationTokenSource cts)
        {
            Info = info;
            Process = process;
            Cts = cts;
        }
    }
}

public record ImageAttachment(string MediaType, string Base64);
