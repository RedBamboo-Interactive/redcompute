using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using RedCompute.PluginSdk;

namespace RedCompute.Plugin.ClaudeCode;

public class ClaudeSessionService
{
    private readonly ConcurrentDictionary<string, ManagedSession> _sessions = new();
    private readonly ConcurrentDictionary<string, Process> _runningProcesses = new();
    private readonly ClaudeConfig _config;
    private readonly IJobTracker _jobTracker;
    private readonly IClaudeSessionStore _sessionStore;
    private readonly Action<string, Guid?> _log;

    public event Action<ClaudeSessionInfo>? SessionCreated;
    public event Action<ClaudeSessionInfo>? SessionUpdated;
    public event Action<string, string, string?>? SessionEnded;
    public event Action<string, ClaudeStreamEvent>? StreamEvent;

    public void EmitStreamEvent(string key, ClaudeStreamEvent evt) => StreamEvent?.Invoke(key, evt);

    public ClaudeSessionService(ClaudeConfig config, IJobTracker jobTracker, IClaudeSessionStore sessionStore, Action<string, Guid?> log)
    {
        _config = config;
        _jobTracker = jobTracker;
        _sessionStore = sessionStore;
        _log = log;
        RecoverSessions();
    }

    private void RecoverSessions()
    {
        try
        {
            var active = _sessionStore.GetActiveSessions();
            foreach (var s in active)
            {
                s.Status = "Stopped";
                _log($"[Claude] Marked orphaned session {s.Id} ({s.ProjectName}) as stopped", null);
                BackfillFromJsonl(s);
                _sessionStore.SaveSession(s);
            }
        }
        catch (Exception ex)
        {
            _log($"[Claude] Failed to recover sessions: {ex.Message}", null);
        }
    }

    private void BackfillFromJsonl(ClaudeSessionRecord session)
    {
        try
        {
            var claudeSessionId = session.ClaudeSessionId;
            if (string.IsNullOrEmpty(claudeSessionId) || string.IsNullOrEmpty(session.ProjectPath))
                return;

            var slug = session.ProjectPath.Replace(":", "-").Replace("\\", "-").Replace("/", "-");
            var jsonlPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".claude", "projects", slug, $"{claudeSessionId}.jsonl");

            if (!File.Exists(jsonlPath)) return;

            var lastTimestamp = _sessionStore.GetLastMessageTimestamp(session.Id);

            var newMessages = new List<ClaudeMessageRecord>();
            foreach (var line in File.ReadLines(jsonlPath))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;

                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;

                    if (!root.TryGetProperty("timestamp", out var tsProp)) continue;
                    if (!DateTimeOffset.TryParse(tsProp.GetString(), out var ts)) continue;
                    if (ts <= lastTimestamp) continue;

                    var type = root.TryGetProperty("type", out var tp) ? tp.GetString() : null;
                    if (type != "assistant" && type != "user") continue;
                    if (!root.TryGetProperty("message", out var message)) continue;
                    if (!message.TryGetProperty("content", out var content)) continue;

                    var msgId = message.TryGetProperty("id", out var mid) ? mid.GetString() : null;

                    if (content.ValueKind == JsonValueKind.String)
                    {
                        if (type == "user")
                        {
                            newMessages.Add(new ClaudeMessageRecord
                            {
                                SessionId = session.Id, Role = "user", EventType = "text",
                                Content = content.GetString(), Timestamp = ts
                            });
                        }
                        continue;
                    }

                    if (content.ValueKind != JsonValueKind.Array) continue;

                    foreach (var block in content.EnumerateArray())
                    {
                        var bt = block.TryGetProperty("type", out var btp) ? btp.GetString() : null;

                        switch (bt)
                        {
                            case "thinking" when type == "assistant":
                            {
                                var text = block.TryGetProperty("thinking", out var th) ? th.GetString() : null;
                                if (string.IsNullOrEmpty(text)) continue;
                                newMessages.Add(new ClaudeMessageRecord
                                {
                                    SessionId = session.Id, Role = "assistant", EventType = "thinking",
                                    Content = text, MessageId = msgId, Timestamp = ts
                                });
                                break;
                            }
                            case "text" when type == "assistant":
                            {
                                var text = block.TryGetProperty("text", out var txt) ? txt.GetString() : null;
                                if (string.IsNullOrEmpty(text)) continue;
                                newMessages.Add(new ClaudeMessageRecord
                                {
                                    SessionId = session.Id, Role = "assistant", EventType = "text",
                                    Content = text, MessageId = msgId, Timestamp = ts
                                });
                                break;
                            }
                            case "tool_use" when type == "assistant":
                            {
                                var toolName = block.TryGetProperty("name", out var n) ? n.GetString() : null;
                                var toolInput = block.TryGetProperty("input", out var inp) ? inp.ToString() : null;
                                var toolId = block.TryGetProperty("id", out var tid) ? tid.GetString() : null;
                                newMessages.Add(new ClaudeMessageRecord
                                {
                                    SessionId = session.Id, Role = "assistant", EventType = "tool_use",
                                    ToolName = toolName, ToolInput = toolInput, MessageId = toolId, Timestamp = ts
                                });
                                break;
                            }
                            case "tool_result" when type == "user":
                            {
                                var resultContent = block.TryGetProperty("content", out var c)
                                    ? ExtractTextFromContent(c) : null;
                                var toolUseId = block.TryGetProperty("tool_use_id", out var tuid)
                                    ? tuid.GetString() : null;
                                newMessages.Add(new ClaudeMessageRecord
                                {
                                    SessionId = session.Id, Role = "assistant", EventType = "tool_result",
                                    Content = resultContent, ToolResult = resultContent,
                                    MessageId = toolUseId, Timestamp = ts
                                });
                                break;
                            }
                        }
                    }
                }
                catch (JsonException) { }
            }

            if (newMessages.Count > 0)
            {
                _sessionStore.AddMessages(newMessages);
                _log($"[Claude] Backfilled {newMessages.Count} messages for session {session.Id} ({session.ProjectName}) from JSONL", null);
            }
        }
        catch (Exception ex)
        {
            _log($"[Claude] Failed to backfill session {session.Id}: {ex.Message}", null);
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

    public record OneshotResult(bool Success, string? Text, string? StreamOutput, string? Model, int InputTokens, int OutputTokens, double? CostUsd, string? Error);

    public record ExecuteResult(bool Success, string? Text, string? StreamOutput, string? Model,
                                int InputTokens, int OutputTokens, double? CostUsd, string? Error);

    public async Task<ExecuteResult> ExecuteAgentAsync(
        string prompt, string? container, string? workingDir,
        string? model, string? effort, int maxTurns,
        string[]? allowedTools, string[]? addDirs, int timeout,
        CancellationToken ct,
        string? streamKey = null,
        Dictionary<string, string>? env = null)
    {
        var useDocker = !string.IsNullOrWhiteSpace(container);

        if (!useDocker)
        {
            var claudePath = ResolveClaudePath();
            if (claudePath == null)
                return new ExecuteResult(false, null, null, null, 0, 0, null,
                    "Could not find 'claude' CLI. Install it or set ClaudePath in config.");
        }

        var startInfo = new ProcessStartInfo
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        if (useDocker)
        {
            DockerExecHelper.ConfigureForDockerExec(startInfo, container!, "claude", workingDir, env);
            var args = new List<string>();
            AddAgentArgs(args, model, effort, maxTurns, allowedTools, addDirs);
            foreach (var a in args) startInfo.ArgumentList.Add(a);
        }
        else
        {
            startInfo.FileName = ResolveClaudePath()!;
            if (!string.IsNullOrWhiteSpace(workingDir))
                startInfo.WorkingDirectory = workingDir;
            if (env is not null)
                foreach (var (k, v) in env)
                    startInfo.EnvironmentVariables[k] = v;
            var args = new List<string>();
            AddAgentArgs(args, model, effort, maxTurns, allowedTools, addDirs);
            foreach (var a in args) startInfo.ArgumentList.Add(a);
        }

        var sw = Stopwatch.StartNew();
        using var process = Process.Start(startInfo);
        if (process == null)
            return new ExecuteResult(false, null, null, null, 0, 0, null, "Failed to start process");
        if (streamKey != null)
            _runningProcesses[streamKey] = process;
        _log($"[Claude] TIMING Process.Start took {sw.ElapsedMilliseconds}ms (docker={useDocker})", null);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeout));

        var sb = new StringBuilder();
        try
        {
            var stderrTask = process.StandardError.ReadToEndAsync(timeoutCts.Token);

            // Start stdin write immediately (not via Task.Run which is subject to
            // thread pool scheduling delays — the Claude CLI has a 3s stdin timeout)
            var stdinTask = WriteStdinAsync(process, prompt, timeoutCts.Token, sw);

            // Read stdout line-by-line, broadcasting stream events in real time
            var firstLineLogged = false;
            while (await process.StandardOutput.ReadLineAsync(timeoutCts.Token) is { } line)
            {
                if (!firstLineLogged)
                {
                    _log($"[Claude] TIMING first stdout after {sw.ElapsedMilliseconds}ms (prompt {prompt.Length} chars)", null);
                    firstLineLogged = true;
                }
                sb.AppendLine(line);
                if (streamKey != null && !string.IsNullOrWhiteSpace(line))
                {
                    try
                    {
                        foreach (var evt in ParseExecuteStreamLine(line))
                            StreamEvent?.Invoke(streamKey, evt);
                    }
                    catch { /* ignore parse errors during streaming */ }
                }
            }

            try { await stdinTask; } catch { /* stdin may already be closed */ }
            var stderr = await stderrTask;
            await process.WaitForExitAsync(timeoutCts.Token);

            var stdout = sb.ToString();

            if (process.ExitCode != 0 && string.IsNullOrWhiteSpace(stdout))
            {
                if (streamKey != null) _runningProcesses.TryRemove(streamKey, out _);
                var errMsg = stderr.Length > 500 ? stderr[..500] : stderr;
                _log($"[Claude] Execute exited {process.ExitCode}: {errMsg}", null);
                return new ExecuteResult(false, null, null, null, 0, 0, null,
                    $"claude exited with code {process.ExitCode}: {errMsg}");
            }

            if (streamKey != null) _runningProcesses.TryRemove(streamKey, out _);
            return ParseStreamJsonOutput(stdout, model);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            if (streamKey != null) _runningProcesses.TryRemove(streamKey, out _);
            try { process.Kill(entireProcessTree: true); } catch { }
            var partialOutput = sb.ToString();
            if (!string.IsNullOrWhiteSpace(partialOutput))
            {
                var partial = ParseStreamJsonOutput(partialOutput, model);
                return new ExecuteResult(false, partial.Text, partial.StreamOutput, partial.Model,
                    partial.InputTokens, partial.OutputTokens, partial.CostUsd,
                    $"Execution timed out after {timeout}s");
            }
            return new ExecuteResult(false, null, null, null, 0, 0, null,
                $"Execution timed out after {timeout}s");
        }
        catch (OperationCanceledException)
        {
            if (streamKey != null) _runningProcesses.TryRemove(streamKey, out _);
            try { process.Kill(entireProcessTree: true); } catch { }
            throw;
        }
    }

    public void CancelExecution(string key)
    {
        if (_runningProcesses.TryRemove(key, out var process))
        {
            try { process.Kill(entireProcessTree: true); }
            catch { /* process may have already exited */ }
        }
    }

    private static void AddAgentArgs(List<string> args, string? model, string? effort,
        int maxTurns, string[]? allowedTools, string[]? addDirs)
    {
        args.AddRange(["--print", "--output-format", "stream-json", "--verbose", "--include-partial-messages"]);
        args.AddRange(["--max-turns", maxTurns.ToString()]);
        args.Add("--dangerously-skip-permissions");
        if (!string.IsNullOrEmpty(model))
        {
            args.Add("--model");
            args.Add(model);
        }
        if (!string.IsNullOrEmpty(effort))
        {
            args.Add("--effort");
            args.Add(effort);
        }
        if (allowedTools is { Length: > 0 })
        {
            args.Add("--allowed-tools");
            args.Add(string.Join(",", allowedTools));
        }
        if (addDirs != null)
        {
            foreach (var dir in addDirs)
            {
                args.Add("--add-dir");
                args.Add(dir);
            }
        }
    }

    private static List<ClaudeStreamEvent> ParseExecuteStreamLine(string line)
    {
        var events = new List<ClaudeStreamEvent>();
        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;
        var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;

        if (type == "system")
        {
            var sessionId = root.TryGetProperty("session_id", out var sid) ? sid.GetString() : null;
            if (sessionId is not null)
                events.Add(new ClaudeStreamEvent { Type = "system", Content = sessionId });
            return events;
        }

        if (type == "stream_event")
        {
            if (root.TryGetProperty("event", out var evt))
            {
                var evtType = evt.TryGetProperty("type", out var et) ? et.GetString() : null;
                if (evtType == "content_block_delta" && evt.TryGetProperty("delta", out var delta))
                {
                    var deltaType = delta.TryGetProperty("type", out var dt) ? dt.GetString() : null;
                    if (deltaType == "text_delta")
                    {
                        var text = delta.TryGetProperty("text", out var txt) ? txt.GetString() : null;
                        if (text != null)
                            events.Add(new ClaudeStreamEvent { Type = "text", Content = text, IsPartial = true });
                    }
                    else if (deltaType == "thinking_delta")
                    {
                        var thinking = delta.TryGetProperty("thinking", out var th) ? th.GetString() : null;
                        if (thinking != null)
                            events.Add(new ClaudeStreamEvent { Type = "thinking", Content = thinking, IsPartial = true });
                    }
                }
            }
            return events;
        }

        if (type == "user")
        {
            if (root.TryGetProperty("message", out var userMsg)
                && userMsg.TryGetProperty("content", out var userContent))
            {
                foreach (var block in userContent.EnumerateArray())
                {
                    var bt = block.TryGetProperty("type", out var btp) ? btp.GetString() : null;
                    if (bt != "tool_result") continue;
                    var resultContent = block.TryGetProperty("content", out var c) ? ExtractTextFromContent(c) : null;
                    var toolUseId = block.TryGetProperty("tool_use_id", out var tuid) ? tuid.GetString() : null;
                    events.Add(new ClaudeStreamEvent
                    {
                        Type = "tool_result",
                        ToolResult = resultContent,
                        Content = resultContent,
                        MessageId = toolUseId,
                    });
                }
            }
            return events;
        }

        if (type != "assistant" || !root.TryGetProperty("message", out var msg)
                                || !msg.TryGetProperty("content", out var content))
            return events;

        var msgId = msg.TryGetProperty("id", out var mid) ? mid.GetString() : null;

        foreach (var block in content.EnumerateArray())
        {
            var bt = block.TryGetProperty("type", out var btp) ? btp.GetString() : null;
            switch (bt)
            {
                // text/thinking are streamed via stream_event deltas above — skip the
                // complete assistant-message snapshots to avoid duplicates
                case "thinking":
                case "text":
                    continue;
                case "tool_use":
                    events.Add(new ClaudeStreamEvent
                    {
                        Type = "tool_use",
                        ToolName = block.TryGetProperty("name", out var n) ? n.GetString() : null,
                        ToolInput = block.TryGetProperty("input", out var inp) ? inp.Clone() : null,
                        MessageId = msgId,
                    });
                    break;
            }
        }

        return events;
    }

    private ExecuteResult ParseStreamJsonOutput(string stdout, string? requestedModel)
    {
        var lastAssistantText = "";
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

                if (evtType == "result")
                {
                    var resultText = root.TryGetProperty("result", out var r) && r.ValueKind == JsonValueKind.String
                        ? r.GetString() : null;
                    if (root.TryGetProperty("usage", out var usage))
                    {
                        if (usage.TryGetProperty("input_tokens", out var it)) inputTokens = it.GetInt32();
                        if (usage.TryGetProperty("output_tokens", out var ot)) outputTokens = ot.GetInt32();
                    }
                    if (root.TryGetProperty("total_cost_usd", out var cost))
                        costUsd = cost.GetDouble();

                    var text = resultText ?? lastAssistantText;
                    if (string.IsNullOrEmpty(text) && !hadToolUse)
                        text = "No response generated";

                    _log($"[Claude] Execute done: {inputTokens}in/{outputTokens}out" +
                         (costUsd.HasValue ? $" ${costUsd:F4}" : ""), null);

                    return new ExecuteResult(true, text, stdout, actualModel, inputTokens, outputTokens, costUsd, null);
                }

                if (evtType == "assistant")
                {
                    if (root.TryGetProperty("message", out var msg) && msg.TryGetProperty("content", out var content))
                    {
                        foreach (var block in content.EnumerateArray())
                        {
                            var blockType = block.TryGetProperty("type", out var bt) ? bt.GetString() : null;
                            if (blockType == "text" && block.TryGetProperty("text", out var txt))
                                lastAssistantText = txt.GetString() ?? "";
                            else if (blockType == "tool_use")
                                hadToolUse = true;
                        }
                    }
                }

                if (evtType == "system" && root.TryGetProperty("model", out var m))
                    actualModel = m.GetString() ?? requestedModel;
            }
            catch (JsonException) { }
        }

        return new ExecuteResult(
            !string.IsNullOrEmpty(lastAssistantText) || hadToolUse,
            lastAssistantText,
            stdout,
            actualModel, inputTokens, outputTokens, costUsd,
            string.IsNullOrEmpty(lastAssistantText) && !hadToolUse ? "No result event in stream output" : null);
    }

    public async Task<OneshotResult> ExecuteOneshotAsync(string? model, string? system, JsonElement messages, int maxTokens, CancellationToken ct, string? effort = null)
    {
        var claudePath = ResolveClaudePath();
        if (claudePath == null)
            return new OneshotResult(false, null, null, null, 0, 0, null, "Could not find 'claude' CLI. Install it or set ClaudePath in config.");

        var resolvedModel = model ?? _config.DefaultOneshotModel;

        var prompt = BuildOneshotPrompt(messages);
        if (string.IsNullOrWhiteSpace(prompt))
            return new OneshotResult(false, null, null, null, 0, 0, null, "messages produced empty prompt");

        var startInfo = new ProcessStartInfo
        {
            FileName = claudePath,
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        startInfo.ArgumentList.Add("-p");
        startInfo.ArgumentList.Add("--output-format");
        startInfo.ArgumentList.Add("stream-json");
        startInfo.ArgumentList.Add("--model");
        startInfo.ArgumentList.Add(resolvedModel);
        startInfo.ArgumentList.Add("--no-session-persistence");
        startInfo.ArgumentList.Add("--allowedTools");
        startInfo.ArgumentList.Add("Read,Glob,Grep");
        if (!string.IsNullOrEmpty(system))
        {
            startInfo.ArgumentList.Add("--system-prompt");
            startInfo.ArgumentList.Add(system);
        }
        if (!string.IsNullOrEmpty(effort))
        {
            startInfo.ArgumentList.Add("--effort");
            startInfo.ArgumentList.Add(effort);
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();

        using var process = Process.Start(startInfo);
        if (process == null)
            return new OneshotResult(false, null, null, null, 0, 0, null, "Failed to start claude process");

        _log($"[Claude] Oneshot process started in {sw.ElapsedMilliseconds}ms", null);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(60));

        var rawSb = new StringBuilder();
        var tsSb = new StringBuilder();
        try
        {
            var stderrTask = process.StandardError.ReadToEndAsync(timeoutCts.Token);

            var stdinTask = WriteStdinAsync(process, prompt, timeoutCts.Token, sw);

            var firstLineLogged = false;
            while (await process.StandardOutput.ReadLineAsync(timeoutCts.Token) is { } line)
            {
                if (!firstLineLogged)
                {
                    _log($"[Claude] Oneshot first stdout after {sw.ElapsedMilliseconds}ms", null);
                    firstLineLogged = true;
                }
                rawSb.AppendLine(line);
                tsSb.Append(DateTimeOffset.UtcNow.ToString("o"));
                tsSb.Append('\t');
                tsSb.AppendLine(line);
            }

            try { await stdinTask; } catch { /* stdin may already be closed */ }
            var stderr = await stderrTask;
            await process.WaitForExitAsync(timeoutCts.Token);

            _log($"[Claude] Oneshot process exited in {sw.ElapsedMilliseconds}ms (code {process.ExitCode})", null);

            var rawStdout = rawSb.ToString();
            if (process.ExitCode != 0 && string.IsNullOrWhiteSpace(rawStdout))
            {
                var errMsg = stderr.Length > 300 ? stderr[..300] : stderr;
                _log($"[Claude] Oneshot exited {process.ExitCode}: {errMsg}", null);
                return new OneshotResult(false, null, null, null, 0, 0, null, $"claude exited with code {process.ExitCode}: {errMsg}");
            }

            var result = ParseStreamJsonOutput(rawStdout, resolvedModel);
            var streamOutput = tsSb.ToString();

            _log($"[Claude] Oneshot {result.Model} done ({result.Text?.Length ?? 0} chars)", null);
            return new OneshotResult(result.Success, result.Text, streamOutput, result.Model,
                result.InputTokens, result.OutputTokens, result.CostUsd, result.Error);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            var rawStdout = rawSb.ToString();
            if (!string.IsNullOrWhiteSpace(rawStdout))
            {
                var partial = ParseStreamJsonOutput(rawStdout, resolvedModel);
                return new OneshotResult(false, partial.Text, tsSb.ToString(), partial.Model,
                    partial.InputTokens, partial.OutputTokens, partial.CostUsd,
                    "Execution timed out after 60s");
            }
            return new OneshotResult(false, null, null, null, 0, 0, null, "Execution timed out after 60s");
        }
    }

    private static string BuildOneshotPrompt(JsonElement messages)
    {
        if (messages.ValueKind != JsonValueKind.Array) return "";
        var sb = new StringBuilder();
        foreach (var msg in messages.EnumerateArray())
        {
            var role = msg.TryGetProperty("role", out var r) ? r.GetString() : "user";
            string? content = null;
            if (msg.TryGetProperty("content", out var c))
                content = c.ValueKind == JsonValueKind.String ? c.GetString() : ExtractTextFromContent(c);
            if (string.IsNullOrEmpty(content)) continue;
            sb.AppendLine(role == "assistant" ? $"[Assistant]: {content}" : $"[User]: {content}");
        }
        return sb.ToString().TrimEnd();
    }

    public ClaudeSessionInfo? StartSession(string projectPath, string? callerInfo = null, string? model = null, string? userId = null, string? userName = null, string? userAvatarUrl = null, string? effort = null)
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
            StartedAt = DateTimeOffset.UtcNow,
            Source = callerInfo,
            UserId = userId,
            Effort = effort
        };

        var claudePath = ResolveClaudePath();
        if (claudePath == null)
        {
            LastStartError = "Could not find 'claude' CLI. Install it or set ClaudePath in config.";
            _log("[Claude] Could not find 'claude' CLI on PATH", null);
            return null;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = claudePath,
            WorkingDirectory = projectPath,
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        PopulateSessionArgs(startInfo, model: model, effort: effort);

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
        var inputJson = System.Text.Json.JsonSerializer.Serialize(new { projectPath, projectName = info.ProjectName, sessionId = info.Id });
        var job = _jobTracker.CreateJob("ai-session", "Claude Code", inputJson, callerInfo: callerInfo, name: info.ProjectName, rationale: "Interactive session",
            userId: userId, userName: userName, userAvatarUrl: userAvatarUrl);
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

        var sessionRecord = _sessionStore.FindSession(sessionId);
        if (sessionRecord == null)
        {
            LastStartError = "Session not found";
            return null;
        }

        if (string.IsNullOrEmpty(sessionRecord.ClaudeSessionId))
        {
            LastStartError = "Session has no Claude session ID to resume";
            return null;
        }

        if (!Directory.Exists(sessionRecord.ProjectPath))
        {
            LastStartError = $"Project path not found: {sessionRecord.ProjectPath}";
            return null;
        }

        sessionRecord.Dismissed = false;
        _sessionStore.SaveSession(sessionRecord);

        var claudePath = ResolveClaudePath();
        if (claudePath == null)
        {
            LastStartError = "Could not find 'claude' CLI. Install it or set ClaudePath in config.";
            return null;
        }

        var info = new ClaudeSessionInfo
        {
            Id = sessionRecord.Id,
            ProjectName = sessionRecord.ProjectName,
            ProjectPath = sessionRecord.ProjectPath,
            Status = SessionStatus.Starting,
            StartedAt = sessionRecord.StartedAt,
            Model = sessionRecord.Model,
            ClaudeSessionId = sessionRecord.ClaudeSessionId,
            Title = sessionRecord.Title,
            MessageCount = sessionRecord.MessageCount,
            CostUsd = sessionRecord.CostUsd,
            InputTokens = sessionRecord.InputTokens,
            OutputTokens = sessionRecord.OutputTokens,
            CacheReadInputTokens = sessionRecord.CacheReadInputTokens,
            CacheCreationInputTokens = sessionRecord.CacheCreationInputTokens,
            ContextTokens = sessionRecord.ContextTokens,
            ContextWindow = sessionRecord.ContextWindow,
            Effort = sessionRecord.Effort,
            Source = sessionRecord.Source,
        };

        var startInfo = new ProcessStartInfo
        {
            FileName = claudePath,
            WorkingDirectory = sessionRecord.ProjectPath,
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        PopulateSessionArgs(startInfo, sessionRecord.ClaudeSessionId, sessionRecord.Model, sessionRecord.Effort);

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

        if (sessionRecord.JobId.HasValue)
        {
            info.JobId = sessionRecord.JobId.Value;
            _jobTracker.MarkRunning(sessionRecord.JobId.Value);
        }
        else
        {
            var job = _jobTracker.CreateJob("ai-session", "Claude Code",
                System.Text.Json.JsonSerializer.Serialize(new { projectPath = sessionRecord.ProjectPath, projectName = sessionRecord.ProjectName, resumed = true }),
                callerInfo: "Dashboard", name: sessionRecord.ProjectName, rationale: "Resumed session");
            _jobTracker.MarkRunning(job.Id);
            info.JobId = job.Id;
        }

        PersistSessionRecord(info);

        _log($"[Claude] Session {sessionId} resumed for {info.ProjectName} (PID {process.Id}, Job {info.JobId})", null);
        SessionCreated?.Invoke(info);

        return info;
    }

    public async Task<bool> SendMessage(string sessionId, string content, ImageAttachment[]? images = null)
    {
        if (!_sessions.TryGetValue(sessionId, out var session))
            return false;

        if (session.Info.Status is SessionStatus.Stopped or SessionStatus.Error)
            return false;

        if (session.Info.Status == SessionStatus.Active)
        {
            _log($"[Claude] Session {sessionId} is active, restarting for new message", null);
            session.RestartPending = true;

            StreamEvent?.Invoke(sessionId, new ClaudeStreamEvent { Type = "status", Content = "interrupted" });

            try { session.Process.Kill(entireProcessTree: true); } catch { }
            try { await session.Process.WaitForExitAsync(); } catch { }

            var resumed = ResumeSession(sessionId);
            if (resumed == null)
                return false;

            if (!_sessions.TryGetValue(sessionId, out session))
                return false;
        }

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
            session.Info.StopReason = null;
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

    public bool SendAnswer(string sessionId, string answer)
    {
        if (!_sessions.TryGetValue(sessionId, out var session))
            return false;

        if (session.Info.Status is SessionStatus.Stopped or SessionStatus.Error)
            return false;

        try
        {
            var msg = new
            {
                type = "user",
                message = new { role = "user", content = (object)answer },
                parent_tool_use_id = (string?)null
            };
            session.Process.StandardInput.WriteLine(JsonSerializer.Serialize(msg));
            session.Process.StandardInput.Flush();
            session.Info.Status = SessionStatus.Active;
            SessionUpdated?.Invoke(session.Info);

            PersistMessage(sessionId, "user", "text", answer, null, null, null, null);

            return true;
        }
        catch (Exception ex)
        {
            _log($"[Claude] Failed to send answer to {sessionId}: {ex.Message}", null);
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
            var requestId = $"interrupt-{++session.ControlRequestCounter}";
            var msg = new
            {
                type = "control_request",
                request_id = requestId,
                request = new { subtype = "interrupt" }
            };
            session.Process.StandardInput.WriteLine(JsonSerializer.Serialize(msg));
            session.Process.StandardInput.Flush();
            session.InterruptPending = true;

            _log($"[Claude] Interrupt sent for session {sessionId} (request {requestId})", null);

            StreamEvent?.Invoke(sessionId, new ClaudeStreamEvent
            {
                Type = "status",
                Content = "interrupting"
            });

            // CLI often acknowledges but doesn't actually abort — kill after timeout
            _ = ForceInterruptAfterTimeout(sessionId, session, TimeSpan.FromMilliseconds(500));

            return InterruptResult.Interrupted;
        }
        catch (Exception ex)
        {
            _log($"[Claude] Failed to send interrupt to {sessionId}: {ex.Message}", null);
            return InterruptResult.Error;
        }
    }

    private async Task ForceInterruptAfterTimeout(string sessionId, ManagedSession session, TimeSpan timeout)
    {
        try
        {
            await Task.Delay(timeout, session.Cts.Token);
        }
        catch (OperationCanceledException) { return; }

        if (!session.InterruptPending)
            return;

        _log($"[Claude] Interrupt not honored after {timeout.TotalSeconds}s, killing session {sessionId}", null);

        session.RestartPending = true;

        // Emit interrupted status so the frontend stops streaming immediately
        StreamEvent?.Invoke(sessionId, new ClaudeStreamEvent { Type = "status", Content = "interrupted" });

        try { session.Process.Kill(entireProcessTree: true); } catch { }

        // Wait for OnProcessExited cleanup, then auto-resume
        try { await session.Process.WaitForExitAsync(); } catch { }
        await Task.Delay(300);

        if (!string.IsNullOrEmpty(session.Info.ClaudeSessionId))
        {
            _log($"[Claude] Auto-resuming session {sessionId} after forced interrupt", null);
            ResumeSession(sessionId);
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

        // Try graceful interrupt first, then kill quickly
        try
        {
            var requestId = $"stop-{++session.ControlRequestCounter}";
            var msg = new
            {
                type = "control_request",
                request_id = requestId,
                request = new { subtype = "interrupt" }
            };
            session.Process.StandardInput.WriteLine(JsonSerializer.Serialize(msg));
            session.Process.StandardInput.Flush();
        }
        catch { }

        var exited = await WaitForExit(session.Process, TimeSpan.FromSeconds(3));
        if (!exited)
        {
            try { session.Process.Kill(entireProcessTree: true); } catch { }
        }

        session.Cts.Cancel();
        session.Info.Status = SessionStatus.Stopped;
        session.Info.StopReason = "user_stopped";
        _sessions.TryRemove(sessionId, out _);
        PersistSessionRecord(session.Info);

        if (session.Info.JobId.HasValue)
            _jobTracker.MarkCompleted(session.Info.JobId.Value, resultJson: $"{{\"messages\":{session.Info.MessageCount}}}", costUsd: session.Info.CostUsd);

        SessionEnded?.Invoke(sessionId, "stopped", session.Info.StopReason);
    }

    public async Task ForceKill(string sessionId)
    {
        if (!_sessions.TryRemove(sessionId, out var session))
            return;

        try { session.Process.Kill(entireProcessTree: true); } catch { }
        session.Cts.Cancel();
        session.Info.Status = SessionStatus.Stopped;
        session.Info.StopReason = "user_stopped";
        PersistSessionRecord(session.Info);

        if (session.Info.JobId.HasValue)
            _jobTracker.MarkCompleted(session.Info.JobId.Value, resultJson: $"{{\"messages\":{session.Info.MessageCount}}}", costUsd: session.Info.CostUsd);

        SessionEnded?.Invoke(sessionId, "killed", session.Info.StopReason);
        await Task.CompletedTask;
    }

    public async Task<ClaudeSessionInfo?> UpdateSessionConfig(string sessionId, string? model, string? effort)
    {
        if (_sessions.ContainsKey(sessionId))
            await StopSession(sessionId);

        var record = _sessionStore.FindSession(sessionId);
        if (record == null) return null;

        if (model != null) record.Model = model;
        if (effort != null) record.Effort = effort;
        _sessionStore.SaveSession(record);

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
            _sessionStore.DismissSession(sessionId);
        }
        catch (Exception ex)
        {
            _log($"[Claude] Failed to dismiss session {sessionId}: {ex.Message}", null);
        }
    }

    public List<ClaudeSessionInfo> GetSessions(int limit = 20, bool includeDismissed = false)
    {
        var live = _sessions.Values.Select(s => s.Info).ToList();
        var activeIds = _sessions.Keys.ToHashSet();

        var recent = _sessionStore.GetRecentSessions(activeIds, limit, includeDismissed);
        var dbSessions = recent.Select(ToSessionInfo).ToList();

        return [.. live, .. dbSessions];
    }

    public (ClaudeSessionInfo? Info, List<ClaudeMessageRecord> History) GetSession(string sessionId)
    {
        if (_sessions.TryGetValue(sessionId, out var session))
            return (session.Info, GetHistory(sessionId));

        var record = _sessionStore.FindSession(sessionId);
        if (record == null) return (null, []);
        return (ToSessionInfo(record), GetHistory(sessionId));
    }

    public Dictionary<Guid, SessionStatus> GetSessionStatusesByJobIds(IEnumerable<Guid> jobIds)
    {
        var result = new Dictionary<Guid, SessionStatus>();
        var remaining = new HashSet<Guid>(jobIds);

        foreach (var session in _sessions.Values)
        {
            if (session.Info.JobId is { } jid && remaining.Remove(jid))
                result[jid] = session.Info.Status;
        }

        if (remaining.Count > 0)
        {
            var dbStatuses = _sessionStore.GetSessionStatusesByJobIds(remaining);
            foreach (var (jobId, statusStr) in dbStatuses)
            {
                if (Enum.TryParse<SessionStatus>(statusStr, out var status))
                    result[jobId] = status;
            }
        }

        return result;
    }

    public (ClaudeSessionInfo? Info, List<ClaudeMessageRecord> History) GetSessionByJobId(Guid jobId)
    {
        var live = _sessions.Values.FirstOrDefault(s => s.Info.JobId == jobId);
        if (live != null)
            return (live.Info, GetHistory(live.Info.Id));

        var record = _sessionStore.FindSessionByJobId(jobId);
        if (record == null) return (null, []);
        return (ToSessionInfo(record), GetHistory(record.Id));
    }

    private static ClaudeSessionInfo ToSessionInfo(ClaudeSessionRecord r) => new()
    {
        Id = r.Id,
        ProjectName = r.ProjectName,
        ProjectPath = r.ProjectPath,
        Status = Enum.TryParse<SessionStatus>(r.Status, out var s) ? s : SessionStatus.Stopped,
        StopReason = r.StopReason,
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
        ContextTokens = r.ContextTokens,
        ContextWindow = r.ContextWindow,
        Effort = r.Effort,
        JobId = r.JobId,
        Source = r.Source
    };

    public List<ClaudeMessageRecord> GetHistory(string sessionId, int limit = 50_000)
    {
        return _sessionStore.GetMessages(sessionId, limit);
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

    private void PopulateSessionArgs(ProcessStartInfo startInfo, string? resumeClaudeSessionId = null, string? model = null, string? effort = null)
    {
        foreach (var arg in new[] { "--output-format", "stream-json", "--verbose",
            "--input-format", "stream-json", "--include-partial-messages",
            "--permission-mode", "bypassPermissions" })
            startInfo.ArgumentList.Add(arg);

        var m = model ?? _config.Model;
        if (m != null)
        {
            startInfo.ArgumentList.Add("--model");
            startInfo.ArgumentList.Add(m);
        }
        if (effort != null)
        {
            startInfo.ArgumentList.Add("--effort");
            startInfo.ArgumentList.Add(effort);
        }
        if (resumeClaudeSessionId != null)
        {
            startInfo.ArgumentList.Add("--resume");
            startInfo.ArgumentList.Add(resumeClaudeSessionId);
        }
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
                            if (session.Info.JobId != null)
                                _jobTracker.UpdateName(session.Info.JobId.Value, session.Info.Title);
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

            case "user":
                return ParseUserEvent(root);

            case "result":
                return ParseResultEvent(root, session);

            case "control_response":
                HandleControlResponse(root, session);
                return null;

            case "stream_event":
                UpdateContextFromStreamEvent(root, session);
                return ParseStreamEvent(root);

            case "rate_limit_event":
                return null;

            default:
                _log($"[Claude] Unhandled event type '{type}': {line.Substring(0, Math.Min(line.Length, 500))}", null);
                return null;
        }
    }

    private static string? ExtractTextFromContent(JsonElement el)
    {
        if (el.ValueKind == JsonValueKind.String)
            return el.GetString();
        if (el.ValueKind != JsonValueKind.Array)
            return el.ToString();
        var sb = new StringBuilder();
        foreach (var part in el.EnumerateArray())
        {
            if (part.TryGetProperty("text", out var txt))
                sb.Append(txt.GetString());
        }
        return sb.Length > 0 ? sb.ToString() : null;
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
            return null;

        if (!message.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var block in content.EnumerateArray())
        {
            var blockType = block.TryGetProperty("type", out var bt) ? bt.GetString() : null;

            switch (blockType)
            {
                // text and thinking arrive as token-level stream_event deltas — skip the complete blocks
                case "text":
                case "thinking":
                    continue;

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
            }
        }

        return null;
    }

    private static ClaudeStreamEvent? ParseUserEvent(JsonElement root)
    {
        if (!root.TryGetProperty("message", out var message))
            return null;

        if (!message.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var block in content.EnumerateArray())
        {
            var blockType = block.TryGetProperty("type", out var bt) ? bt.GetString() : null;
            if (blockType != "tool_result") continue;

            var resultContent = block.TryGetProperty("content", out var c) ? ExtractTextFromContent(c) : null;
            var toolUseId = block.TryGetProperty("tool_use_id", out var tuid) ? tuid.GetString() : null;
            var isError = block.TryGetProperty("is_error", out var ie) && ie.GetBoolean();

            return new ClaudeStreamEvent
            {
                Type = "tool_result",
                ToolResult = resultContent,
                Content = resultContent,
                MessageId = toolUseId,
                IsPartial = false
            };
        }

        return null;
    }

    private static ClaudeStreamEvent? ParseStreamEvent(JsonElement root)
    {
        if (!root.TryGetProperty("event", out var evt))
            return null;

        var evtType = evt.TryGetProperty("type", out var et) ? et.GetString() : null;

        if (evtType != "content_block_delta")
            return null;

        if (!evt.TryGetProperty("delta", out var delta))
            return null;

        var deltaType = delta.TryGetProperty("type", out var dt) ? dt.GetString() : null;

        return deltaType switch
        {
            "text_delta" => new ClaudeStreamEvent
            {
                Type = "text",
                Content = delta.TryGetProperty("text", out var t) ? t.GetString() : null,
                IsPartial = true
            },
            "thinking_delta" => new ClaudeStreamEvent
            {
                Type = "thinking",
                Content = delta.TryGetProperty("thinking", out var th) ? th.GetString() : null,
                IsPartial = true
            },
            _ => null
        };
    }

    private ClaudeStreamEvent? ParseResultEvent(JsonElement root, ManagedSession session)
    {
        var subtype = root.TryGetProperty("subtype", out var sub) ? sub.GetString() : null;

        if (subtype == "success")
        {
            session.InterruptPending = false;
            session.Info.Status = SessionStatus.Idle;
            session.Info.StopReason = "completed";

            if (root.TryGetProperty("total_cost_usd", out var cost))
                session.Info.CostUsd = cost.GetDouble();
            ParseTokenUsage(root, session);

            SyncTitleFromClaudeSession(session);
            PersistSessionRecord(session.Info);
            SessionUpdated?.Invoke(session.Info);

            return new ClaudeStreamEvent { Type = "status", Content = "idle" };
        }

        if (subtype == "error")
        {
            var error = root.TryGetProperty("error", out var e)
                ? (e.ValueKind == JsonValueKind.String ? e.GetString() : e.ToString()) ?? "unknown error"
                : "unknown error";
            _log($"[Claude] Session {session.Info.Id} result error: {error}", null);

            var isUsageLimit = IsUsageLimitError(error);
            if (isUsageLimit)
            {
                session.Info.StopReason = "usage_limit";
                _log($"[Claude] Session {session.Info.Id} hit usage limit", null);
            }

            if (session.InterruptPending)
            {
                session.InterruptPending = false;
                session.Info.Status = SessionStatus.Idle;

                if (root.TryGetProperty("total_cost_usd", out var intCost))
                    session.Info.CostUsd = intCost.GetDouble();
                ParseTokenUsage(root, session);

                PersistSessionRecord(session.Info);
                SessionUpdated?.Invoke(session.Info);
                return new ClaudeStreamEvent { Type = "status", Content = "interrupted" };
            }

            session.Info.Status = SessionStatus.Idle;
            if (root.TryGetProperty("total_cost_usd", out var errCost))
                session.Info.CostUsd = errCost.GetDouble();
            ParseTokenUsage(root, session);
            PersistSessionRecord(session.Info);
            SessionUpdated?.Invoke(session.Info);

            return new ClaudeStreamEvent { Type = "error", Content = error };
        }

        // tool_result or other result types
        var content = root.TryGetProperty("content", out var c) ? ExtractTextFromContent(c) : null;
        var toolUseId = root.TryGetProperty("tool_use_id", out var tuid) ? tuid.GetString() : null;
        return new ClaudeStreamEvent
        {
            Type = "tool_result",
            ToolResult = content,
            MessageId = toolUseId
        };
    }

    private static bool IsUsageLimitError(string error)
    {
        var lower = error.ToLowerInvariant();
        return lower.Contains("usage limit") || lower.Contains("spending limit")
            || lower.Contains("spend limit") || lower.Contains("rate limit")
            || lower.Contains("quota exceeded") || lower.Contains("budget")
            || (lower.Contains("limit") && lower.Contains("exceed"));
    }

    private void UpdateContextFromStreamEvent(JsonElement root, ManagedSession session)
    {
        if (!root.TryGetProperty("event", out var evt)) return;
        var evtType = evt.TryGetProperty("type", out var et) ? et.GetString() : null;
        if (evtType != "message_start") return;
        if (!evt.TryGetProperty("message", out var msg)) return;
        if (!msg.TryGetProperty("usage", out var usage)) return;

        var input = usage.TryGetProperty("input_tokens", out var it) ? it.GetInt32() : 0;
        var cacheRead = usage.TryGetProperty("cache_read_input_tokens", out var cr) ? cr.GetInt32() : 0;
        var cacheWrite = usage.TryGetProperty("cache_creation_input_tokens", out var cc) ? cc.GetInt32() : 0;
        var ctx = input + cacheRead + cacheWrite;
        if (ctx > 0)
        {
            session.Info.ContextTokens = ctx;
            _log($"[Claude] Context from message_start: {ctx:N0} tokens (input={input} cacheRead={cacheRead} cacheWrite={cacheWrite})", null);
        }
    }

    private void ParseTokenUsage(JsonElement root, ManagedSession session)
    {
        if (root.TryGetProperty("usage", out var usage))
        {
            int turnInput = 0, turnCacheRead = 0, turnCacheWrite = 0;

            if (usage.TryGetProperty("input_tokens", out var inTok))
            {
                turnInput = inTok.GetInt32();
                session.Info.InputTokens = (session.Info.InputTokens ?? 0) + turnInput;
            }
            if (usage.TryGetProperty("output_tokens", out var outTok))
                session.Info.OutputTokens = (session.Info.OutputTokens ?? 0) + outTok.GetInt32();
            if (usage.TryGetProperty("cache_read_input_tokens", out var cacheR))
            {
                turnCacheRead = cacheR.GetInt32();
                session.Info.CacheReadInputTokens = (session.Info.CacheReadInputTokens ?? 0) + turnCacheRead;
            }
            if (usage.TryGetProperty("cache_creation_input_tokens", out var cacheC))
            {
                turnCacheWrite = cacheC.GetInt32();
                session.Info.CacheCreationInputTokens = (session.Info.CacheCreationInputTokens ?? 0) + turnCacheWrite;
            }
        }

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

        if (session.RestartPending)
        {
            _log($"[Claude] Session {sessionId} process exited for restart (code {exitCode})", null);
            return;
        }

        if (session.Info.Status != SessionStatus.Stopped)
        {
            session.Info.Status = SessionStatus.Error;
            session.Info.StopReason ??= "error";
            PersistSessionRecord(session.Info);
            _log($"[Claude] Session {sessionId} process exited unexpectedly (code {exitCode}, reason={session.Info.StopReason})", null);

            if (session.Info.JobId.HasValue)
                _jobTracker.MarkFailed(session.Info.JobId.Value, $"Process exited with code {exitCode}");

            SessionEnded?.Invoke(sessionId, $"process_exited:{exitCode}", session.Info.StopReason);
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

    private async Task WriteStdinAsync(Process process, string data, CancellationToken ct, Stopwatch sw)
    {
        await process.StandardInput.WriteAsync(data.AsMemory(), ct);
        process.StandardInput.Close();
        _log($"[Claude] TIMING stdin write took {sw.ElapsedMilliseconds}ms ({data.Length} chars)", null);
    }

    private static async Task<bool> WaitForExit(Process process, TimeSpan timeout)
    {
        try
        {
            using var cts = new CancellationTokenSource(timeout);
            await process.WaitForExitAsync(cts.Token);
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
            _sessionStore.SaveSession(new ClaudeSessionRecord
            {
                Id = info.Id,
                ProjectName = info.ProjectName,
                ProjectPath = info.ProjectPath,
                Status = info.Status.ToString(),
                StopReason = info.StopReason,
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
                ContextTokens = info.ContextTokens,
                ContextWindow = info.ContextWindow,
                Effort = info.Effort,
                JobId = info.JobId,
                Source = info.Source
            });
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
            _sessionStore.AddMessage(new ClaudeMessageRecord
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
        public List<ClaudeStreamEvent> MessageHistory { get; } = new();
        public bool InterruptPending { get; set; }
        public bool RestartPending { get; set; }
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
