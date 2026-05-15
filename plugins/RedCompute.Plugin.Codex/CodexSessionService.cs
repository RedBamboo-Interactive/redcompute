using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using RedCompute.PluginSdk;

namespace RedCompute.Plugin.Codex;

public class CodexSessionService
{
    private readonly ConcurrentDictionary<string, Process> _runningProcesses = new();
    private readonly CodexConfig _config;
    private readonly IJobTracker _jobTracker;
    private readonly ICodexSessionStore _store;
    private readonly Action<string, Guid?> _log;

    public event Action<CodexSessionInfo>? SessionCreated;
    public event Action<CodexSessionInfo>? SessionUpdated;
    public event Action<string, string>? SessionEnded;
    public event Action<string, CodexStreamEvent>? StreamEvent;

    public void EmitStreamEvent(string key, CodexStreamEvent evt) => StreamEvent?.Invoke(key, evt);

    public CodexSessionService(CodexConfig config, IJobTracker jobTracker, ICodexSessionStore store, Action<string, Guid?> log)
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
                _log($"[Codex] Marked orphaned session {s.Id} ({s.ProjectName}) as stopped", null);
                _store.SaveSession(s);
            }
        }
        catch (Exception ex)
        {
            _log($"[Codex] Failed to recover sessions: {ex.Message}", null);
        }
    }

    public record ExecuteResult(bool Success, string? Text, string? StreamOutput, string? Model,
                                int InputTokens, int OutputTokens, double? CostUsd, string? Error);

    public async Task<ExecuteResult> ExecuteExecAsync(
        string prompt, string? workingDir,
        string? model, string? sandbox, int timeout,
        CancellationToken ct,
        string? streamKey = null,
        Dictionary<string, string>? env = null)
    {
        var codexPath = ResolveCodexPath();
        if (codexPath == null)
            return new ExecuteResult(false, null, null, null, 0, 0, null,
                "Could not find 'codex' CLI. Install @openai/codex or set CodexPath in config.");

        var startInfo = new ProcessStartInfo
        {
            FileName = codexPath,
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        if (!string.IsNullOrWhiteSpace(workingDir))
            startInfo.WorkingDirectory = workingDir;

        if (env is not null)
            foreach (var (k, v) in env)
                startInfo.EnvironmentVariables[k] = v;

        BuildExecArgs(startInfo, model, sandbox);

        var sw = Stopwatch.StartNew();
        using var process = Process.Start(startInfo);
        if (process == null)
            return new ExecuteResult(false, null, null, null, 0, 0, null, "Failed to start codex process");
        if (streamKey != null)
            _runningProcesses[streamKey] = process;
        _log($"[Codex] Process started in {sw.ElapsedMilliseconds}ms", null);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeout));

        try
        {
            var stderrTask = process.StandardError.ReadToEndAsync(timeoutCts.Token);

            var stdinTask = Task.Run(async () =>
            {
                await process.StandardInput.WriteAsync(prompt.AsMemory(), timeoutCts.Token);
                process.StandardInput.Close();
            }, timeoutCts.Token);

            var sb = new StringBuilder();
            var firstLineLogged = false;
            while (await process.StandardOutput.ReadLineAsync(timeoutCts.Token) is { } line)
            {
                if (!firstLineLogged)
                {
                    _log($"[Codex] First stdout after {sw.ElapsedMilliseconds}ms", null);
                    firstLineLogged = true;
                }
                sb.AppendLine(line);
                if (streamKey != null && !string.IsNullOrWhiteSpace(line))
                {
                    try
                    {
                        foreach (var evt in ParseExecStreamLine(line))
                            StreamEvent?.Invoke(streamKey, evt);
                    }
                    catch { }
                }
            }

            try { await stdinTask; } catch { }
            var stderr = await stderrTask;
            await process.WaitForExitAsync(timeoutCts.Token);

            var stdout = sb.ToString();

            if (process.ExitCode != 0 && string.IsNullOrWhiteSpace(stdout))
            {
                if (streamKey != null) _runningProcesses.TryRemove(streamKey, out _);
                var errMsg = stderr.Length > 500 ? stderr[..500] : stderr;
                _log($"[Codex] Execute exited {process.ExitCode}: {errMsg}", null);
                return new ExecuteResult(false, null, null, null, 0, 0, null,
                    $"codex exited with code {process.ExitCode}: {errMsg}");
            }

            if (streamKey != null) _runningProcesses.TryRemove(streamKey, out _);
            return ParseExecOutput(stdout, model);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            if (streamKey != null) _runningProcesses.TryRemove(streamKey, out _);
            try { process.Kill(entireProcessTree: true); } catch { }
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
            catch { }
        }
    }

    private void BuildExecArgs(ProcessStartInfo startInfo, string? model, string? sandbox)
    {
        startInfo.ArgumentList.Add("exec");
        startInfo.ArgumentList.Add("-");
        startInfo.ArgumentList.Add("--json");

        var resolvedSandbox = sandbox ?? _config.SandboxMode;
        startInfo.ArgumentList.Add("--sandbox");
        startInfo.ArgumentList.Add(resolvedSandbox);

        var resolvedModel = model ?? _config.Model ?? _config.DefaultExecModel;
        if (!string.IsNullOrEmpty(resolvedModel))
        {
            startInfo.ArgumentList.Add("--model");
            startInfo.ArgumentList.Add(resolvedModel);
        }
    }

    internal static List<CodexStreamEvent> ParseExecStreamLine(string line)
    {
        var events = new List<CodexStreamEvent>();
        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;
        var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;

        if (type == "item.completed")
        {
            var item = root.TryGetProperty("item", out var i) ? i : root;
            var itemType = item.TryGetProperty("type", out var it) ? it.GetString() : null;

            switch (itemType)
            {
                case "agentMessage":
                {
                    var content = ExtractTextContent(item);
                    if (content != null)
                        events.Add(new CodexStreamEvent { Type = "text", Content = content });
                    break;
                }
                case "reasoning":
                {
                    var summary = item.TryGetProperty("summary", out var s) && s.ValueKind == JsonValueKind.Array
                        ? string.Join("\n", s.EnumerateArray()
                            .Where(e => e.TryGetProperty("text", out _))
                            .Select(e => e.GetProperty("text").GetString()))
                        : null;
                    if (summary != null)
                        events.Add(new CodexStreamEvent { Type = "thinking", Content = summary });
                    break;
                }
                case "commandExecution":
                {
                    var command = item.TryGetProperty("command", out var cmd) ? cmd.GetString() : null;
                    var output = item.TryGetProperty("output", out var o) ? o.GetString() : null;
                    events.Add(new CodexStreamEvent
                    {
                        Type = "tool_use",
                        ToolName = "Command",
                        ToolInput = command,
                    });
                    if (output != null)
                    {
                        events.Add(new CodexStreamEvent
                        {
                            Type = "tool_result",
                            ToolResult = output,
                            Content = output,
                        });
                    }
                    break;
                }
                case "fileChange":
                {
                    var filename = item.TryGetProperty("filename", out var fn) ? fn.GetString() : null;
                    var changeType = item.TryGetProperty("changeType", out var ct) ? ct.GetString() : null;
                    events.Add(new CodexStreamEvent
                    {
                        Type = "tool_use",
                        ToolName = "FileEdit",
                        ToolInput = new { filename, changeType },
                    });
                    break;
                }
                case "mcpToolCall":
                {
                    var toolName = item.TryGetProperty("name", out var n) ? n.GetString() : null;
                    var input = item.TryGetProperty("arguments", out var a) ? (object)a.Clone() : null;
                    var result = item.TryGetProperty("output", out var o) ? o.GetString() : null;
                    events.Add(new CodexStreamEvent
                    {
                        Type = "tool_use",
                        ToolName = toolName,
                        ToolInput = input,
                    });
                    if (result != null)
                    {
                        events.Add(new CodexStreamEvent
                        {
                            Type = "tool_result",
                            ToolResult = result,
                            Content = result,
                        });
                    }
                    break;
                }
            }
        }
        else if (type == "item.started")
        {
            var item = root.TryGetProperty("item", out var i) ? i : root;
            var itemType = item.TryGetProperty("type", out var it) ? it.GetString() : null;

            if (itemType == "agentMessage")
            {
                var content = ExtractTextContent(item);
                if (content != null)
                    events.Add(new CodexStreamEvent { Type = "text", Content = content, IsPartial = true });
            }
        }

        return events;
    }

    private static string? ExtractTextContent(JsonElement item)
    {
        if (item.TryGetProperty("content", out var content))
        {
            if (content.ValueKind == JsonValueKind.String)
                return content.GetString();

            if (content.ValueKind == JsonValueKind.Array)
            {
                var sb = new StringBuilder();
                foreach (var block in content.EnumerateArray())
                {
                    if (block.TryGetProperty("type", out var bt) && bt.GetString() == "output_text"
                        && block.TryGetProperty("text", out var txt))
                        sb.Append(txt.GetString());
                    else if (block.TryGetProperty("text", out var txt2))
                        sb.Append(txt2.GetString());
                }
                var text = sb.ToString();
                return string.IsNullOrEmpty(text) ? null : text;
            }
        }

        if (item.TryGetProperty("text", out var directText))
            return directText.GetString();

        return null;
    }

    private ExecuteResult ParseExecOutput(string stdout, string? requestedModel)
    {
        var lastText = "";
        var hadToolUse = false;
        var inputTokens = 0;
        var outputTokens = 0;
        var cachedInputTokens = 0;
        double? costUsd = null;
        string? actualModel = requestedModel;

        foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                var evtType = root.TryGetProperty("type", out var t) ? t.GetString() : null;

                if (evtType == "turn.completed")
                {
                    if (root.TryGetProperty("usage", out var usage))
                    {
                        if (usage.TryGetProperty("input_tokens", out var it)) inputTokens += it.GetInt32();
                        if (usage.TryGetProperty("output_tokens", out var ot)) outputTokens += ot.GetInt32();
                        if (usage.TryGetProperty("cached_input_tokens", out var cit)) cachedInputTokens += cit.GetInt32();
                    }
                    if (root.TryGetProperty("cost_usd", out var cost))
                        costUsd = (costUsd ?? 0) + cost.GetDouble();
                }

                if (evtType == "item.completed")
                {
                    var item = root.TryGetProperty("item", out var i) ? i : root;
                    var itemType = item.TryGetProperty("type", out var it2) ? it2.GetString() : null;

                    if (itemType == "agentMessage")
                    {
                        var text = ExtractTextContent(item);
                        if (!string.IsNullOrEmpty(text))
                            lastText = text;
                    }
                    else if (itemType is "commandExecution" or "fileChange" or "mcpToolCall")
                    {
                        hadToolUse = true;
                    }
                }

                if (evtType == "thread.started")
                {
                    if (root.TryGetProperty("model", out var m))
                        actualModel = m.GetString() ?? requestedModel;
                }
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

    public List<CodexSessionInfo> GetSessions(int limit = 20, bool includeDismissed = false)
    {
        var recent = _store.GetRecentSessions(new HashSet<string>(), limit, includeDismissed);
        return recent.Select(ToSessionInfo).ToList();
    }

    public (CodexSessionInfo? info, List<CodexMessageRecord> messages) GetSession(string sessionId)
    {
        var record = _store.FindSession(sessionId);
        if (record == null) return (null, new());
        return (ToSessionInfo(record), _store.GetMessages(sessionId));
    }

    public (CodexSessionInfo? info, List<CodexMessageRecord> messages) GetSessionByJobId(Guid jobId)
    {
        var record = _store.FindSessionByJobId(jobId);
        if (record == null) return (null, new());
        return (ToSessionInfo(record), _store.GetMessages(record.Id));
    }

    public Dictionary<Guid, string> GetSessionStatusesByJobIds(IEnumerable<Guid> jobIds)
        => _store.GetSessionStatusesByJobIds(jobIds);

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

    public async Task StopAllAsync()
    {
        foreach (var key in _runningProcesses.Keys.ToList())
            CancelExecution(key);
        await Task.CompletedTask;
    }

    private static CodexSessionInfo ToSessionInfo(CodexSessionRecord r) => new()
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
        CachedInputTokens = r.CachedInputTokens,
        JobId = r.JobId,
    };

    public string? ResolveCodexPath()
    {
        if (_config.CodexPath != null)
            return File.Exists(_config.CodexPath) ? _config.CodexPath : null;

        var pathDirs = Environment.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator) ?? [];
        foreach (var dir in pathDirs)
        {
            foreach (var ext in new[] { ".exe", ".cmd", "" })
            {
                var candidate = Path.Combine(dir, $"codex{ext}");
                if (File.Exists(candidate))
                    return candidate;
            }
        }

        var npmGlobal = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var npmCandidate = Path.Combine(npmGlobal, "npm", "codex.cmd");
        if (File.Exists(npmCandidate))
            return npmCandidate;

        return null;
    }
}
