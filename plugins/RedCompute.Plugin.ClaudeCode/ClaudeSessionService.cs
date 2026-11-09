using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using RedCompute.Core.Jobs;
using RedCompute.Core.Sessions;
using RedCompute.PluginSdk;

namespace RedCompute.Plugin.ClaudeCode;

public partial class ClaudeSessionService
{
    private readonly ConcurrentDictionary<string, ManagedSession> _sessions = new();
    // Sessions whose process was killed for an automatic relaunch and is not yet replaced.
    // They are absent from _sessions for that window, but they are restarting, not stopped.
    private readonly ConcurrentDictionary<string, Guid?> _restarting = new();
    private readonly ConcurrentDictionary<string, Process> _runningProcesses = new();
    private readonly ClaudeConfig _config;
    private readonly IJobTracker _jobTracker;
    private readonly IClaudeSessionStore _sessionStore;
    private readonly IProviderQualityModeResolver _qualityModes;
    private readonly ClaudeAccountUsageService _accountUsage = new();
    private readonly Func<string?> _titleQualityTier;
    private readonly ConcurrentDictionary<string, Lazy<Task>> _titleTasks = new();
    private readonly Action<string, Guid?> _log;

    // A boundary result means Claude has finished the old command and should promptly replay the
    // committed stdin message. Two minutes matches the provider request timeout while leaving
    // ample room for CLI bookkeeping; normal model/tool work cannot trigger it because replay
    // precedes that work and all stdout activity resets the clock.
    internal TimeSpan SteerBoundarySilenceTimeout { get; set; } = TimeSpan.FromSeconds(120);

    // Background-task progress is chatty (one line per subagent tool call). Session updates for
    // progress are coalesced to this interval per session; lifecycle changes publish immediately.
    internal TimeSpan BackgroundProgressInterval { get; set; } = TimeSpan.FromMilliseconds(500);
    // stop_task is answered by a control_response right after the CLI kills the task.
    internal TimeSpan BackgroundTaskStopTimeout { get; set; } = TimeSpan.FromSeconds(10);
    private readonly Action<Action> _whenMirrorInstalled;

    public event Action<ClaudeSessionInfo>? SessionCreated;
    public event Action<ClaudeSessionInfo>? SessionUpdated;
    public event Action<string, string, string?>? SessionEnded;
    public event Action<string, ClaudeStreamEvent>? StreamEvent;

    public void EmitStreamEvent(string key, ClaudeStreamEvent evt) => StreamEvent?.Invoke(key, evt);

    /// <summary>
    /// Launches the persistent interactive CLI process (start and resume). Tests replace it to
    /// observe the exact argv without spawning a real Claude Code session.
    /// </summary>
    internal Func<ProcessStartInfo, Process?> LaunchSessionProcess { get; set; } = Process.Start;

    // Claude Code 2.1.300 rejects a bare `{}` ("mcpServers: expected record"). `--bare` is not used:
    // it also skips OAuth/keychain credentials, which breaks subscription-authenticated providers.
    // `--tools ""` alone removes built-ins; the init-event check below enforces it at runtime.
    internal const string EmptyMcpConfig = "{\"mcpServers\":{}}";

    /// <summary>Test seam for the short-lived, tool-free completion process.</summary>
    internal Func<ProcessStartInfo, Process?> LaunchOneshotProcess { get; set; } = Process.Start;
    /// <summary>Test seam for the agent execution process.</summary>
    internal Func<ProcessStartInfo, Process?> LaunchAgentProcess { get; set; } = Process.Start;
    internal ICliProcessLaunchGate LaunchGate { get; set; } = UnrestrictedCliProcessLaunchGate.Instance;

    public ClaudeSessionService(
        ClaudeConfig config,
        IJobTracker jobTracker,
        IClaudeSessionStore sessionStore,
        IProviderQualityModeResolver qualityModes,
        Func<string?> titleQualityTier,
        Action<string, Guid?> log,
        Action<Action>? whenMirrorInstalled = null)
    {
        _config = config;
        _jobTracker = jobTracker;
        _sessionStore = sessionStore;
        _qualityModes = qualityModes;
        _titleQualityTier = titleQualityTier;
        _log = log;
        _whenMirrorInstalled = whenMirrorInstalled ?? SuiteMirror.WhenInstalled;
        // Recover at construction, like Codex and OpenCode. StartAsync only runs for a
        // capability's default provider, so a non-default Claude provider never recovered.
        RecoverSessions();
    }

    internal Task<ProviderUsageSnapshot?> GetProviderUsageAsync(bool forceRefresh, CancellationToken ct)
        => _accountUsage.GetAsync(forceRefresh, ct);

    // Last-resort safety net for InterruptSession. The CLI's control_response ack for
    // subtype:"interrupt" is sent immediately on receipt of the request -- before the
    // in-flight turn actually unwinds -- so it cannot be used as the "turn is over"
    // signal. Only the `result` event that follows (handled in ParseResultEvent, which
    // completes ManagedSession.InterruptCompletion) tells us the turn actually ended.
    // This timeout only fires if that event never arrives (e.g. a tool ignoring
    // cancellation), so it's sized for "something is stuck", not "how long does a
    // normal interrupt take".
    private static readonly TimeSpan InterruptGraceTimeout = TimeSpan.FromSeconds(10);

    internal void RecoverSessions()
    {
        try
        {
            var recovered = new List<string>();
            var active = _sessionStore.GetActiveSessions();
            foreach (var s in active)
            {
                recovered.Add(s.Id);
                var pid = s.ProcessId;
                TryKillByPid(pid);
                s.Status = "Stopped";
                s.StopReason = PlannedRestartCheckpoint.ContainsSession("claude-code", s.Id)
                    ? "maintenance_restart"
                    : "orphaned_on_restart";
                s.StoppedAt = DateTimeOffset.UtcNow;
                s.ProcessId = null;
                _log($"[Claude] Recovered session {s.Id} ({s.ProjectName}, PID {pid}) as stopped ({s.StopReason})", null);
                BackfillFromJsonl(s);
                _sessionStore.SaveSession(s);
            }

            // Repair rows that older builds left as Error/"completed" after an idle process died.
            // Their process is long gone and its PID may now belong to something unrelated, so
            // relabel only: never kill by the recorded PID here.
            var stranded = _sessionStore.GetStrandedIdleExitSessions();
            foreach (var s in stranded)
            {
                s.Status = "Stopped";
                s.StopReason = SessionStopReasons.OrphanedOnRestart;
                s.StoppedAt ??= DateTimeOffset.UtcNow;
                s.ProcessId = null;
                _sessionStore.SaveSession(s);
                recovered.Add(s.Id);
            }
            if (stranded.Count > 0)
                _log($"[Claude] Repaired {stranded.Count} session(s) stranded as Error/completed by an earlier idle process exit", null);

            // Background tasks are in-process children of the CLI, so every task a row still lists
            // died with its process. Settle them once the suite mirror can carry the records:
            // recovery runs before the app installs it, and RedLeaf is the transcript read path.
            foreach (var s in _sessionStore.GetSessionsWithBackgroundTasks())
            {
                var sessionId = s.Id;
                var tasks = ReadBackgroundTasks(s.BackgroundTasksJson);
                _whenMirrorInstalled(() => SettleRecoveredBackgroundTasks(sessionId, tasks));
            }

            // The saves above ran before the app installed the suite mirror, so RedLeaf, which
            // serves every session read, still holds the pre-restart row: a turn the restart
            // killed reads Active forever and its discussion shows thinking with nothing behind
            // it (6 Nov, 3f02ef9566b2). Codex republishes for the same reason.
            if (recovered.Count > 0)
                _whenMirrorInstalled(() => RepublishRecoveredSessions(recovered));
        }
        catch (Exception ex)
        {
            _log($"[Claude] Failed to recover sessions: {ex.Message}", null);
        }

    }

    private void RepublishRecoveredSessions(IReadOnlyList<string> sessionIds)
    {
        foreach (var sessionId in sessionIds)
        {
            // A resumed process owns the row again and has published its own state.
            if (_sessions.ContainsKey(sessionId)) continue;
            if (_sessionStore.FindSession(sessionId) is { } record)
                _sessionStore.SaveSession(record);
        }
    }

    internal static IReadOnlyList<BackgroundTaskInfo> ReadBackgroundTasks(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try { return JsonSerializer.Deserialize<List<BackgroundTaskInfo>>(json) ?? []; }
        catch (JsonException) { return []; }
    }

    private void SettleRecoveredBackgroundTasks(string sessionId, IReadOnlyList<BackgroundTaskInfo> tasks)
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var task in tasks.Where(task => !BackgroundTaskStatuses.IsTerminal(task.Status)))
        {
            var lost = new BackgroundTaskInfo
            {
                TaskId = task.TaskId, Kind = task.Kind, Status = BackgroundTaskStatuses.Lost,
                Description = task.Description, AgentType = task.AgentType, Summary = task.Summary,
                Usage = task.Usage, StartedAt = task.StartedAt, EndedAt = now,
                ParentToolUseId = task.ParentToolUseId, MessageUid = task.MessageUid,
                ParentTaskId = task.ParentTaskId,
            };
            PersistMessage(sessionId, "system", "task", null, null,
                ClaudeBackgroundTaskTracker.RecordInput(lost), null, lost.TaskId,
                messageUid: lost.MessageUid, parentToolUseId: lost.ParentToolUseId);
        }

        // A live process owns the column again; its own saves already replaced the old list.
        if (_sessions.ContainsKey(sessionId)) return;
        var record = _sessionStore.FindSession(sessionId);
        if (record is null || record.BackgroundTasksJson is null) return;
        record.BackgroundTasksJson = null;
        _sessionStore.SaveSession(record);
        _log($"[Claude] Session {sessionId}: {tasks.Count} background task(s) lost with the previous process", null);
    }

    private void TryKillByPid(int? pid)
        => OwnedProcessKiller.TryKill(pid, "claude", m => _log($"[Claude] {m}", null));

    private void BackfillFromJsonl(ClaudeSessionRecord session)
    {
        try
        {
            var claudeSessionId = session.ClaudeSessionId;
            if (string.IsNullOrEmpty(claudeSessionId) || string.IsNullOrEmpty(session.ProjectPath))
                return;

            var jsonlPath = ClaudeTranscriptPaths.FindTranscript(session.ProjectPath, claudeSessionId);
            if (jsonlPath is null) return;

            var lastTimestamp = _sessionStore.GetLastMessageTimestamp(session.Id);

            var newMessages = new List<ClaudeMessageRecord>();
            // API message ids that went on to call a tool: their text was commentary.
            var toolUseMessageIds = new HashSet<string>(StringComparer.Ordinal);
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
                                if (msgId is not null) toolUseMessageIds.Add(msgId);
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

            // The native transcript stores one entry per completed block, so the same rule the live
            // stream uses applies: text whose API message also called a tool was commentary.
            foreach (var text in newMessages.Where(m => m is { Role: "assistant", EventType: "text" }))
                text.Phase = text.MessageId is { } id && toolUseMessageIds.Contains(id)
                    ? ClaudeTextBlockTracker.Commentary
                    : ClaudeTextBlockTracker.FinalAnswer;

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

    private string? _lastStartError;

    /// <summary>Setting the message clears <see cref="LastStartErrorCode"/>; set the code after the message.</summary>
    public string? LastStartError
    {
        get => _lastStartError;
        private set { _lastStartError = value; LastStartErrorCode = null; }
    }

    public string? LastStartErrorCode { get; private set; }

    /// <summary>
    /// A resume that failed because the folder is gone must not be retried by auto-resume paths:
    /// park the record under a stop reason that is not auto-resumable. An explicit resume still works once the folder returns.
    /// </summary>
    private void MarkProjectPathMissing(ClaudeSessionRecord record)
    {
        record.StopReason = SessionStopReasons.ProjectPathMissing;
        record.StoppedAt ??= DateTimeOffset.UtcNow;
        try { _sessionStore.SaveSession(record); }
        catch (Exception ex) { _log($"[Claude] Could not persist project_path_missing for {record.Id}: {ex.Message}", null); }
    }

    public record OneshotResult(bool Success, string? Text, string? StreamOutput, string? Model, int InputTokens, int OutputTokens, double? CostUsd, string? Error);

    public record ExecuteResult(bool Success, string? Text, string? StreamOutput, string? Model,
                                int InputTokens, int OutputTokens, double? CostUsd, string? Error);

    public Task<ExecuteResult> ExecuteAgentAsync(
        string prompt, string? container, string? workingDir,
        string? model, string? effort, int maxTurns,
        string[]? tools, string[]? allowedTools, string[]? addDirs, int timeout,
        CancellationToken ct,
        string? streamKey = null,
        Dictionary<string, string>? env = null,
        string? accessLevel = null)
        => LaunchGate.RunWhenInstallIdleAsync(
            spawned => ExecuteAgentCoreAsync(prompt, container, workingDir, model, effort, maxTurns,
                tools, allowedTools, addDirs, timeout, ct, streamKey, env, spawned, accessLevel),
            ct);

    private async Task<ExecuteResult> ExecuteAgentCoreAsync(
        string prompt, string? container, string? workingDir,
        string? model, string? effort, int maxTurns,
        string[]? tools, string[]? allowedTools, string[]? addDirs, int timeout,
        CancellationToken ct,
        string? streamKey,
        Dictionary<string, string>? env,
        Action spawned,
        string? accessLevel)
    {
        var useDocker = !string.IsNullOrWhiteSpace(container);
        var useCliArg = Encoding.UTF8.GetByteCount(prompt) < 32 * 1024;

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
            AddAgentArgs(args, model, effort, maxTurns, tools, allowedTools, addDirs, accessLevel);
            if (useCliArg) { var idx = args.IndexOf("--print"); if (idx >= 0) args.Insert(idx + 1, prompt); }
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
            AddAgentArgs(args, model, effort, maxTurns, tools, allowedTools, addDirs, accessLevel);
            if (useCliArg) { var idx = args.IndexOf("--print"); if (idx >= 0) args.Insert(idx + 1, prompt); }
            foreach (var a in args) startInfo.ArgumentList.Add(a);
        }

        var sw = Stopwatch.StartNew();
        using var process = LaunchAgentProcess(startInfo);
        if (process == null)
            return new ExecuteResult(false, null, null, null, 0, 0, null, "Failed to start process");
        if (streamKey != null)
            _runningProcesses[streamKey] = process;
        spawned();
        _log($"[Claude] TIMING Process.Start took {sw.ElapsedMilliseconds}ms (docker={useDocker})", null);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeout));

        var sb = new StringBuilder();
        try
        {
            var stderrTask = process.StandardError.ReadToEndAsync(timeoutCts.Token);

            Task? stdinTask = null;
            if (useCliArg)
            {
                process.StandardInput.Close();
                _log($"[Claude] TIMING agent prompt passed as CLI arg ({prompt.Length} chars)", null);
            }
            else
            {
                stdinTask = WriteStdinOnDedicatedThread(process, prompt, timeoutCts.Token, sw);
            }

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

            if (stdinTask != null)
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

    /// <summary>
    /// File tools a one-shot at ask may use without a person: a print-mode run has nobody to
    /// approve anything, so ask runs non-interactively (dontAsk) with these pre-approved.
    /// </summary>
    internal static readonly string[] OneshotAskFileTools = ["Read", "Glob", "Grep", "Edit", "Write", "NotebookEdit"];

    /// <summary>
    /// A one-shot has nobody to approve anything, and the CLI's own denial does not say why or
    /// where a person changes that, so ask and auto runs are told up front (appended to Claude
    /// Code's system prompt, never replacing it).
    /// </summary>
    private static void AddOneshotAccessNote(List<string> args, string level)
        => args.AddRange(["--append-system-prompt", Core.Sessions.SessionAccessNotices.LaunchNote(level, unattended: true)]);

    /// <param name="accessLevel">
    /// The resolved one-shot level, which alone decides permissions: plan runs in plan mode, ask
    /// non-interactively (dontAsk plus file tools), auto with Claude's classifier, full skipping
    /// every prompt. Null (no level resolved, an internal caller) fails closed to ask. An explicit
    /// <paramref name="tools"/> list only narrows which tools exist; it never skips a prompt.
    /// <paramref name="allowedTools"/> pre-approvals are honoured in full only at full; below it
    /// only file tools can be pre-approved, so a caller cannot pre-approve Bash or the network past
    /// its level (at auto the classifier still decides those).
    /// </param>
    internal static void AddAgentArgs(List<string> args, string? model, string? effort,
        int maxTurns, string[]? tools, string[]? allowedTools, string[]? addDirs, string? accessLevel = null)
    {
        args.AddRange(["--print", "--output-format", "stream-json", "--verbose", "--include-partial-messages"]);
        args.AddRange(["--max-turns", maxTurns.ToString()]);
        var preApproved = new List<string>();
        var level = accessLevel ?? Core.Sessions.SessionAccessLevels.Ask;
        switch (level)
        {
            case Core.Sessions.SessionAccessLevels.Full:
                args.Add("--dangerously-skip-permissions");
                break;
            case Core.Sessions.SessionAccessLevels.Auto:
                args.AddRange(["--permission-mode", "auto"]);
                AddOneshotAccessNote(args, level);
                break;
            case Core.Sessions.SessionAccessLevels.Plan:
                args.AddRange(["--permission-mode", "plan"]);
                break;
            default:
                level = Core.Sessions.SessionAccessLevels.Ask;
                args.AddRange(["--permission-mode", "dontAsk"]);
                preApproved.AddRange(tools is null
                    ? OneshotAskFileTools
                    : OneshotAskFileTools.Where(tool => tools.Contains(tool, StringComparer.Ordinal)));
                AddOneshotAccessNote(args, Core.Sessions.SessionAccessLevels.Ask);
                break;
        }
        if (tools is not null)
        {
            // `--allowedTools` controls permission prompts, not tool availability. An explicit
            // empty `tools` grant must remove every built-in and prevent ambient plugins/MCP from
            // repopulating the tool surface for narrow reviewer jobs.
            args.Add("--tools");
            args.Add(string.Join(',', tools));
            args.Add("--strict-mcp-config");
            args.Add("--mcp-config");
            args.Add(EmptyMcpConfig);
        }
        // Opus 4.7+ and Fable 5 default thinking.display to "omitted", so thinking blocks
        // stream with an empty body (signature only). Request "summarized" to restore the
        // visible reasoning text. Harmless on 4.6 (already its default) and ignored when
        // thinking is disabled.
        args.AddRange(["--thinking-display", "summarized"]);
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
            preApproved.AddRange(level == Core.Sessions.SessionAccessLevels.Full
                ? allowedTools
                : level == Core.Sessions.SessionAccessLevels.Plan
                    ? []
                    : allowedTools.Where(tool => OneshotAskFileTools.Contains(tool, StringComparer.Ordinal)));
        if (preApproved.Count > 0)
        {
            args.Add("--allowed-tools");
            args.Add(string.Join(",", preApproved.Distinct(StringComparer.Ordinal)));
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

    public Task<OneshotResult> ExecuteOneshotAsync(string? model, string? system, JsonElement messages,
        int maxTokens, CancellationToken ct, string? effort = null, int timeout = 120,
        IReadOnlyList<GenerateImage>? images = null)
        => LaunchGate.RunWhenInstallIdleAsync(
            spawned => ExecuteOneshotCoreAsync(model, system, messages, maxTokens, ct, effort, timeout, images, spawned),
            ct);

    private async Task<OneshotResult> ExecuteOneshotCoreAsync(string? model, string? system,
        JsonElement messages, int maxTokens, CancellationToken ct, string? effort, int timeout,
        IReadOnlyList<GenerateImage>? images, Action spawned)
    {
        var claudePath = ResolveClaudePath();
        if (claudePath == null)
            return new OneshotResult(false, null, null, null, 0, 0, null, "Could not find 'claude' CLI. Install it or set ClaudePath in config.");

        var prompt = BuildOneshotPrompt(messages);
        if (string.IsNullOrWhiteSpace(prompt))
            return new OneshotResult(false, null, null, null, 0, 0, null, "messages produced empty prompt");

        // Images travel inline as base64 image blocks in one stream-json user message on stdin.
        // Nothing is written to disk, so there is no file for any tool to reach, and the tool-free
        // flags and init-event check below are exactly the same as for a text-only call.
        var hasImages = images is { Count: > 0 };
        var stdinPayload = hasImages ? OneshotImageInput(prompt, images!) : prompt;

        using var workingDirectory = IsolatedWorkingDirectory.Create("redcompute-claude-generate");
        var startInfo = new ProcessStartInfo
        {
            FileName = claudePath,
            WorkingDirectory = workingDirectory.Path,
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        var options = new List<string> { "--output-format", "stream-json" };
        // Current CLIs refuse --print with stream-json unless --verbose is also present.
        options.Add("--verbose");
        if (hasImages)
        {
            options.Add("--input-format");
            options.Add("stream-json");
        }
        if (!string.IsNullOrWhiteSpace(model))
        {
            options.Add("--model");
            options.Add(model);
        }
        options.Add("--no-session-persistence");
        // Tool-free by construction. --allowedTools only changes permission prompts; it does not
        // remove built-ins. These flags also prevent user settings, plugins, and MCP servers from
        // repopulating the surface.
        options.Add("--tools");
        options.Add("");
        options.Add("--strict-mcp-config");
        options.Add("--mcp-config");
        options.Add(EmptyMcpConfig);
        // Restore visible thinking text on Opus 4.7+/Fable 5 (display defaults to "omitted").
        options.Add("--thinking-display");
        options.Add("summarized");
        if (!string.IsNullOrEmpty(system))
        {
            options.Add("--system-prompt");
            options.Add(system);
        }
        if (!string.IsNullOrEmpty(effort))
        {
            options.Add("--effort");
            options.Add(effort);
        }

        if (OneshotCommandLineLength(claudePath, options) > MaxOneshotCommandLineChars)
            return new OneshotResult(false, null, null, null, 0, 0, null,
                "The system prompt is too long for the claude command line.");
        var useCliArg = UsePromptArgument(claudePath, prompt, options, hasImages);
        startInfo.ArgumentList.Add("-p");
        if (useCliArg)
            startInfo.ArgumentList.Add(prompt);
        foreach (var option in options)
            startInfo.ArgumentList.Add(option);

        var sw = System.Diagnostics.Stopwatch.StartNew();

        using var process = LaunchOneshotProcess(startInfo);
        if (process == null)
            return new OneshotResult(false, null, null, null, 0, 0, null, "Failed to start claude process");
        spawned();

        _log($"[Claude] Oneshot process started in {sw.ElapsedMilliseconds}ms", null);

        var clampedTimeout = Math.Clamp(timeout, 10, 600);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(clampedTimeout));

        var rawSb = new StringBuilder();
        var tsSb = new StringBuilder();
        try
        {
            var stderrTask = process.StandardError.ReadToEndAsync(timeoutCts.Token);

            Task? stdinTask = null;
            if (useCliArg)
            {
                process.StandardInput.Close();
                _log($"[Claude] TIMING oneshot prompt passed as CLI arg ({prompt.Length} chars)", null);
            }
            else
            {
                stdinTask = WriteStdinOnDedicatedThread(process, stdinPayload, timeoutCts.Token, sw);
            }

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

            if (stdinTask != null)
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

            var streamOutput = tsSb.ToString();
            var result = ParseStreamJsonOutput(rawStdout, model);
            if (ToolFreeInitError(rawStdout) is { } toolError)
                return new OneshotResult(false, null, streamOutput, result.Model,
                    result.InputTokens, result.OutputTokens, result.CostUsd, toolError);

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
                var partial = ParseStreamJsonOutput(rawStdout, model);
                if (ToolFreeInitError(rawStdout) is { } toolError)
                    return new OneshotResult(false, null, tsSb.ToString(), partial.Model,
                        partial.InputTokens, partial.OutputTokens, partial.CostUsd, toolError);
                return new OneshotResult(false, partial.Text, tsSb.ToString(), partial.Model,
                    partial.InputTokens, partial.OutputTokens, partial.CostUsd,
                    $"Execution timed out after {clampedTimeout}s");
            }
            return new OneshotResult(false, null, null, null, 0, 0, null, $"Execution timed out after {clampedTimeout}s");
        }
        catch (OperationCanceledException)
        {
            // Leaving now ends this run's hold on installs, so the process must not outlive it.
            try { process.Kill(entireProcessTree: true); } catch { }
            throw;
        }
    }

    /// <summary>One stream-json user message: the flattened prompt, then each validated image.</summary>
    /// <summary>
    /// Windows limits a whole command line to 32,767 UTF-16 characters. The prompt rides on the
    /// command line only when everything fits with margin; otherwise it goes through stdin.
    /// Measuring the prompt alone let a long system prompt push release notes over the limit
    /// ("The filename or extension is too long", 2026-10-28).
    /// </summary>
    internal const int MaxOneshotCommandLineChars = 30_000;

    internal static bool UsePromptArgument(
        string fileName, string prompt, IReadOnlyList<string> options, bool hasImages)
        => !hasImages
           && OneshotCommandLineLength(fileName, [.. options, "-p", prompt]) <= MaxOneshotCommandLineChars;

    /// <summary>Upper bound of the quoted command line Process builds from these arguments.</summary>
    internal static int OneshotCommandLineLength(string fileName, IEnumerable<string> arguments)
    {
        var length = fileName.Length + 3;
        foreach (var argument in arguments)
            length += argument.Length + 3 + argument.Count(c => c is '"' or '\\');
        return length;
    }

    internal static string OneshotImageInput(string prompt, IReadOnlyList<GenerateImage> images)
    {
        var content = new List<object> { new { type = "text", text = prompt } };
        content.AddRange(images.Select(image => (object)new
        {
            type = "image",
            source = new { type = "base64", media_type = image.MediaType, data = image.Base64 },
        }));
        return JsonSerializer.Serialize(new { type = "user", message = new { role = "user", content } }) + "\n";
    }

    internal static string? ToolFreeInitError(string stdout)
    {
        foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object
                    || !root.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String
                    || type.GetString() != "system"
                    || !root.TryGetProperty("subtype", out var subtype) || subtype.ValueKind != JsonValueKind.String
                    || subtype.GetString() != "init")
                    continue;

                if (!root.TryGetProperty("tools", out var tools) || tools.ValueKind != JsonValueKind.Array)
                    return "Claude Code tool-free generate could not be verified: init event did not report a tools array";
                if (tools.GetArrayLength() == 0)
                    return null;

                var names = tools.EnumerateArray().Select(tool => tool.ValueKind == JsonValueKind.String
                    ? tool.GetString()
                    : tool.GetRawText());
                return $"Claude Code tool-free generate refused a non-empty init tool list: {string.Join(", ", names)}";
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException)
            {
                // Other stream lines are parsed by the ordinary result parser. Only a valid init
                // event can establish the security invariant; an unreadable line establishes nothing.
            }
        }

        return "Claude Code tool-free generate could not be verified: init event was missing";
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

    public ClaudeSessionInfo? StartSession(string projectPath, string? model, string? userId,
        string? userName, string? userAvatarUrl, string? effort, string? qualityTier,
        string? providerEntity, Guid? repositoryId, JobProvenance provenance,
        string? scratchDirectory = null, bool confidential = false,
        string? developerInstructions = null)
        => StartSessionAsync(projectPath, model, userId, userName, userAvatarUrl, effort,
            qualityTier, providerEntity, repositoryId, provenance, scratchDirectory,
            confidential, developerInstructions).GetAwaiter().GetResult();

    public Task<ClaudeSessionInfo?> StartSessionAsync(string projectPath, string? model, string? userId,
        string? userName, string? userAvatarUrl, string? effort, string? qualityTier,
        string? providerEntity, Guid? repositoryId, JobProvenance provenance,
        string? scratchDirectory = null, bool confidential = false,
        string? developerInstructions = null, CancellationToken ct = default)
    {
        // Read here: the launch gate may run the start outside this call's async flow.
        var access = SessionAccessScope.Current;
        return LaunchGate.RunWhenInstallIdleAsync(() => Task.FromResult(StartSessionCore(
            projectPath, model, userId, userName, userAvatarUrl, effort, qualityTier,
            providerEntity, repositoryId, provenance, scratchDirectory, confidential,
            developerInstructions, access)), ct);
    }

    private ClaudeSessionInfo? StartSessionCore(string projectPath, string? model, string? userId,
        string? userName, string? userAvatarUrl, string? effort, string? qualityTier,
        string? providerEntity, Guid? repositoryId, JobProvenance provenance,
        string? scratchDirectory, bool confidential,
        string? developerInstructions, Core.Sessions.SessionAccessState? access)
    {
        if (_sessions.Count >= _config.MaxSessions)
        {
            LastStartError = $"Max sessions reached ({_config.MaxSessions})";
            return null;
        }

        if (!Directory.Exists(projectPath))
        {
            LastStartError = $"Project path not found: {projectPath}";
            LastStartErrorCode = SessionStopReasons.ProjectPathMissing;
            return null;
        }

        if (string.IsNullOrWhiteSpace(developerInstructions))
            developerInstructions = null;

        var id = Guid.NewGuid().ToString("N")[..12];
        var info = new ClaudeSessionInfo
        {
            Id = id,
            ProjectName = Path.GetFileName(projectPath),
            ProjectPath = projectPath,
            ScratchDirectory = scratchDirectory,
            RepositoryId = repositoryId,
            Status = SessionStatus.Starting,
            StartedAt = DateTimeOffset.UtcNow,
            UserId = userId,
            UserName = userName,
            UserAvatarUrl = userAvatarUrl,
            // Persist the requested model so a relaunch after a restart keeps it instead of
            // falling back to the provider default.
            Model = string.IsNullOrWhiteSpace(model) ? null : model.Trim(),
            Effort = effort,
            QualityTier = qualityTier,
            ProviderEntity = providerEntity,
            DeveloperInstructions = developerInstructions,
            // No resolved state means RedCompute did not start this through its access rules: ask.
            Access = access is { AccessLevel: not null } ? access : new Core.Sessions.SessionAccessState(Core.Sessions.SessionAccessLevels.Ask),
        };
        info.PermissionMode = PermissionModeFor(info.Access.AccessLevel);

        var claudePath = ResolveClaudePath();
        if (claudePath == null)
        {
            LastStartError = "Could not find 'claude' CLI. Install it or set ClaudePath in config.";
            _log("[Claude] Could not find 'claude' CLI on PATH", null);
            return null;
        }
        if (DeveloperInstructionLaunchError(claudePath, developerInstructions) is { } instructionError)
        {
            LastStartError = instructionError;
            _log($"[Claude] {instructionError}", null);
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
        PopulateSessionArgs(startInfo, info.PermissionMode, model: model, effort: effort,
            developerInstructions: developerInstructions);
        if (SessionScratch.Environment(scratchDirectory, sessionId: id) is { } scratchEnvironment)
            foreach (var (key, value) in scratchEnvironment)
                startInfo.Environment[key] = value;

        Process process;
        try
        {
            process = LaunchSessionProcess(startInfo)
                ?? throw new InvalidOperationException("Process.Start returned no process");
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
        info.ProcessId = process.Id;

        // Create a job record for this session
        var inputJson = System.Text.Json.JsonSerializer.Serialize(new
        {
            projectPath,
            repositoryId = info.RepositoryId,
            projectName = info.ProjectName,
            sessionId = info.Id,
        });
        var job = _jobTracker.CreateJob(new JobSubmission("ai-session", "Claude Code", inputJson,
            provenance, Name: info.ProjectName, Rationale: "Interactive session",
            Confidential: confidential));
        if (!job.IsIdempotencyReuse) _jobTracker.StartInvocation(job.Id, provenance);
        info.JobId = job.Id;

        PersistSessionRecord(info);

        _log($"[Claude] Session {id} started for {info.ProjectName} (PID {process.Id}, Job {job.Id})", null);
        SessionCreated?.Invoke(info);

        return info;
    }

    public ClaudeSessionInfo? ResumeSession(string sessionId, JobProvenance? provenance = null)
        => ResumeSessionAsync(sessionId, provenance).GetAwaiter().GetResult();

    public Task<ClaudeSessionInfo?> ResumeSessionAsync(string sessionId,
        JobProvenance? provenance = null, CancellationToken ct = default)
        => LaunchGate.RunWhenInstallIdleAsync(
            () => Task.FromResult(ResumeSessionCore(sessionId, provenance)), ct);

    private ClaudeSessionInfo? ResumeSessionCore(string sessionId, JobProvenance? provenance)
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

        if (ResumeLaunchError(sessionRecord.ClaudeSessionId, sessionRecord.MessageCount) is { } resumeError)
        {
            LastStartError = resumeError;
            return null;
        }

        if (!Directory.Exists(sessionRecord.ProjectPath))
        {
            LastStartError = $"Project path not found: {sessionRecord.ProjectPath}";
            LastStartErrorCode = SessionStopReasons.ProjectPathMissing;
            MarkProjectPathMissing(sessionRecord);
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
        // A session created with developer instructions must never be relaunched without them:
        // resuming bare would silently drop the caller's security boundary.
        if (DeveloperInstructionLaunchError(claudePath, sessionRecord.DeveloperInstructions) is { } instructionError)
        {
            LastStartError = instructionError;
            _log($"[Claude] {instructionError}", null);
            return null;
        }

        var (forkTarget, forkError) = PrepareForkForLaunch(sessionRecord, claudePath);
        if (forkError is not null)
        {
            // The rewind stays pending: resuming without it would bring the removed turns back.
            LastStartError = forkError;
            _log($"[Claude] {forkError}", null);
            return null;
        }

        var info = new ClaudeSessionInfo
        {
            Id = sessionRecord.Id,
            ProjectName = sessionRecord.ProjectName,
            ProjectPath = sessionRecord.ProjectPath,
            ScratchDirectory = sessionRecord.ScratchDirectory,
            RepositoryId = sessionRecord.RepositoryId,
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
            QualityTier = sessionRecord.QualityTier,
            ProviderEntity = sessionRecord.ProviderEntity,
            DeveloperInstructions = sessionRecord.DeveloperInstructions,
            Source = sessionRecord.Source,
            UserId = sessionRecord.UserId,
            UserName = sessionRecord.UserName,
            UserAvatarUrl = sessionRecord.UserAvatarUrl,
            // The persisted level, never the old hardcoded bypass. RedCompute resolves a legacy row
            // before it resumes one; a relaunch that still finds none runs at ask.
            Access = AccessOf(sessionRecord),
            PermissionMode = PermissionModeFor(sessionRecord.AccessLevel),
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
        PopulateSessionArgs(startInfo, info.PermissionMode,
            string.IsNullOrEmpty(sessionRecord.ClaudeSessionId) ? null : sessionRecord.ClaudeSessionId,
            sessionRecord.Model, sessionRecord.Effort,
            sessionRecord.DeveloperInstructions);
        AddForkArgs(startInfo, forkTarget);
        if (SessionScratch.Environment(sessionRecord.ScratchDirectory, sessionId: sessionRecord.Id) is { } scratchEnvironment)
            foreach (var (key, value) in scratchEnvironment)
                startInfo.Environment[key] = value;

        Process process;
        try
        {
            process = LaunchSessionProcess(startInfo)
                ?? throw new InvalidOperationException("Process.Start returned no process");
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
        info.ProcessId = process.Id;

        provenance ??= sessionRecord.JobId is { } existingJobId
            ? _jobTracker.GetJob(existingJobId)?.CreationProvenance
            : null;
        if (provenance is null)
        {
            LastStartError = "A tracked job with provenance is required to resume this session";
            try { process.Kill(entireProcessTree: true); } catch { }
            _sessions.TryRemove(sessionId, out _);
            return null;
        }
        if (sessionRecord.JobId.HasValue)
        {
            info.JobId = sessionRecord.JobId.Value;
            _jobTracker.StartInvocation(sessionRecord.JobId.Value, provenance, JobEventKind.Resumed);
        }
        else
        {
            var job = _jobTracker.CreateJob(new JobSubmission("ai-session", "Claude Code",
                System.Text.Json.JsonSerializer.Serialize(new { projectPath = sessionRecord.ProjectPath, projectName = sessionRecord.ProjectName, resumed = true }),
                provenance, Name: sessionRecord.ProjectName, Rationale: "Resumed session"));
            _jobTracker.StartInvocation(job.Id, provenance, JobEventKind.Resumed);
            info.JobId = job.Id;
        }

        PersistSessionRecord(info);

        _log($"[Claude] Session {sessionId} resumed for {info.ProjectName} (PID {process.Id}, Job {info.JobId})", null);
        SessionCreated?.Invoke(info);

        return info;
    }

    public async Task<bool> SendInput(string sessionId, IReadOnlyList<SessionInputPart> input,
        string? attachmentsJson = null, string? messageUid = null, string? displayContent = null)
    {
        if (!_sessions.TryGetValue(sessionId, out var session))
        {
            _log($"[Claude] Refused input for {sessionId}: no live process"
                + (_restarting.ContainsKey(sessionId) ? " (restarting)" : ""), null);
            return false;
        }

        if (session.Info.Status is SessionStatus.Stopped or SessionStatus.Error or SessionStatus.Active)
        {
            _log($"[Claude] Refused input for {sessionId}: session is {session.Info.Status}", null);
            return false;
        }

        try
        {
            var content = SessionInputFormatting.UserText(input);
            var contentPayload = BuildContentPayload(input);
            // Random per write, never derived from messageUid: the CLI swallows a uuid already on
            // its chain and the turn would never end (checked against 2.1.318).
            var promptUuid = Guid.NewGuid().ToString();

            var msg = new
            {
                type = "user",
                message = new { role = "user", content = contentPayload },
                parent_tool_use_id = (string?)null,
                uuid = promptUuid,
            };
            // Set before the write: the reader may see the CLI's replay before this method resumes.
            session.UserInputUnconsumed = true;
            try { await WriteControlLineAsync(session, msg); }
            catch { session.UserInputUnconsumed = false; throw; }
            session.Info.MessageCount++;
            session.Info.Status = SessionStatus.Active;
            session.Info.StopReason = null;
            session.Info.StoppedAt = null;
            SessionUpdated?.Invoke(session.Info);

            // A user message ends the previous assistant turn.
            session.CurrentAssistantUid = null;
            var transcriptContent = SessionInputFormatting.TranscriptText(input, displayContent);
            PersistMessage(sessionId, "user", "text", transcriptContent, null, null, null, null, attachmentsJson,
                messageUid ?? Guid.NewGuid().ToString("N"), promptUuid: promptUuid);
            EnsureTitle(session, transcriptContent);

            return true;
        }
        catch (Exception ex)
        {
            _log($"[Claude] Failed to send message to {sessionId}: {ex.Message}", null);
            return false;
        }
    }

    public async Task<SessionInputDeliveryResult> TrySteerInputAsync(string sessionId,
        IReadOnlyList<SessionInputPart> input, string? attachmentsJson = null,
        string? messageUid = null, string? displayContent = null)
    {
        if (!_sessions.TryGetValue(sessionId, out var session)
            || session.Info.Status != SessionStatus.Active)
            return SessionInputDeliveryResult.NotSteerable();
        if (!session.PendingQuestions.IsEmpty)
            return SessionInputDeliveryResult.NotSteerable("question_parked",
                "The active turn is waiting for a question answer");
        if (!input.Any(part => part.Type == "text" && !string.IsNullOrWhiteSpace(part.Text)
                || part.Attachment is not null || part.LegacyImage is not null))
            return SessionInputDeliveryResult.Rejected("empty_input", "Steering input is empty");

        var correlationId = Guid.NewGuid().ToString();
        var pending = new PendingSteer
        {
            MessageUid = messageUid ?? Guid.NewGuid().ToString("N"),
            TranscriptContent = SessionInputFormatting.TranscriptText(input, displayContent),
            AttachmentsJson = attachmentsJson,
            Completion = new(TaskCreationOptions.RunContinuationsAsynchronously),
        };
        if (!session.PendingSteers.TryAdd(correlationId, pending))
            return SessionInputDeliveryResult.Busy();

        try
        {
            var msg = new
            {
                type = "user",
                message = new { role = "user", content = BuildContentPayload(input) },
                parent_tool_use_id = (string?)null,
                uuid = correlationId,
            };
            await WriteControlLineAsync(session, msg, () => pending.Committed = true);
            // A successful stdin write is the provider's commit point. Claude may not replay the
            // UUID until after the current result (and may run it as the next CLI turn), so session
            // cancellation cannot turn this into a retry. Process-loss paths settle it explicitly.
            return await pending.Completion.Task;
        }
        catch (OperationCanceledException)
        {
            if (pending.Committed)
                return await pending.Completion.Task;
            session.PendingSteers.TryRemove(correlationId, out _);
            return SessionInputDeliveryResult.NotSteerable("steer_discarded",
                "The session ended before steering was written");
        }
        catch (Exception ex)
        {
            if (pending.Committed)
            {
                _log($"[Claude] Steer write for {sessionId} failed after stdin commit; awaiting replay or process loss: {ex.Message}", null);
                return await pending.Completion.Task;
            }
            session.PendingSteers.TryRemove(correlationId, out _);
            _log($"[Claude] Failed to steer session {sessionId}: {ex.Message}", null);
            return SessionInputDeliveryResult.Unavailable("steer_write_failed", ex.Message, retryable: true);
        }
    }

    private void EnsureTitle(ManagedSession session, string firstUserMessage)
    {
        if (!string.IsNullOrWhiteSpace(session.Info.Title)) return;

        var fallback = SessionTitleFormatter.DeriveFallback(firstUserMessage);
        if (fallback is not null)
            ApplyTitle(session, fallback);

        if (_titleQualityTier() is not { } qualityTier) return;

        var candidate = new Lazy<Task>(
            () => GenerateTitleAsync(session, firstUserMessage, qualityTier),
            LazyThreadSafetyMode.ExecutionAndPublication);
        var tracked = _titleTasks.GetOrAdd(session.Info.Id, candidate);
        if (ReferenceEquals(tracked, candidate))
            _ = tracked.Value.ContinueWith(
                completed => _titleTasks.TryRemove(session.Info.Id, out _),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
    }

    private async Task GenerateTitleAsync(
        ManagedSession session,
        string firstUserMessage,
        string qualityTier)
    {
        var opening = SessionTitleFormatter.StripContext(firstUserMessage);
        if (string.IsNullOrWhiteSpace(opening)) return;

        var provider = session.Info.ProviderEntity ?? "claude-code";
        if (!_qualityModes.TryResolveRequested(qualityTier, provider, out var quality)
            || quality is null)
        {
            _log($"[Claude] Title generation skipped for {session.Info.Id}: " +
                 $"quality tier '{qualityTier}' has no model for provider '{provider}'", null);
            return;
        }

        var titleJob = StartTitleGeneration(session.Info, quality);
        if (titleJob is null)
        {
            _log($"[Claude] Title generation skipped for {session.Info.Id}: " +
                 "the parent session has no verified job provenance", null);
            return;
        }

        try
        {
            var messages = JsonSerializer.SerializeToElement(new[]
            {
                new { role = "user", content = SessionTitleFormatter.BuildPrompt(opening) },
            });
            var result = await ExecuteOneshotAsync(
                quality.Model,
                system: null,
                messages,
                maxTokens: 80,
                session.Cts.Token,
                quality.Effort,
                Math.Clamp(quality.TimeoutSeconds ?? 60, 10, 600));

            if (!result.Success)
            {
                var error = result.Error ?? "Title inference returned no result";
                FailTitleGeneration(titleJob, error);
                _log($"[Claude] Title generation failed for {session.Info.Id}: {error}", null);
                return;
            }

            var title = SessionTitleFormatter.CleanGenerated(result.Text ?? "");
            if (title is null)
            {
                const string error = "Title inference returned an invalid title";
                FailTitleGeneration(titleJob, error);
                _log($"[Claude] {error} for {session.Info.Id}", null);
                return;
            }

            ApplyTitle(session, title);
            _jobTracker.MarkCompleted(titleJob.Id, resultJson: JsonSerializer.Serialize(new
            {
                title,
                model = result.Model ?? quality.Model,
                qualityTier = quality.QualityTier,
                inputTokens = result.InputTokens,
                outputTokens = result.OutputTokens,
            }), costUsd: result.CostUsd);
        }
        catch (OperationCanceledException) when (session.Cts.IsCancellationRequested)
        {
            _jobTracker.MarkCancelled(titleJob.Id);
        }
        catch (Exception ex)
        {
            FailTitleGeneration(titleJob, ex.Message);
            _log($"[Claude] Title generation failed for {session.Info.Id}: {ex.Message}", null);
        }
    }

    private JobRecord? StartTitleGeneration(ClaudeSessionInfo info, ProviderQualityMode quality)
    {
        var parent = info.JobId is { } parentId ? _jobTracker.GetJob(parentId) : null;
        if (parent?.CreationProvenance is not { } provenance) return null;
        var childProvenance = provenance with
        {
            Trace = provenance.Trace with { ParentJobId = parent.Id.ToString() },
        };

        var job = _jobTracker.CreateJob(new JobSubmission(
            "ai-session",
            "Claude Code",
            JsonSerializer.Serialize(new
            {
                operation = "session-title",
                sessionId = info.Id,
                qualityTier = quality.QualityTier,
                model = quality.Model,
            }),
            childProvenance,
            $"ai-session:claude-code:{info.Id}:title",
            "Generate discussion title",
            "Asynchronous semantic title for an interactive session",
            Confidential: parent.Confidential));

        if (!job.IsIdempotencyReuse)
            _jobTracker.StartInvocation(job.Id, childProvenance);
        return job;
    }

    private void FailTitleGeneration(JobRecord job, string error)
        => _jobTracker.MarkFailed(job.Id, error, resultJson: JsonSerializer.Serialize(new
        {
            operation = "session-title",
            fallback = "opening-message",
        }));

    private void ApplyTitle(ManagedSession session, string title)
    {
        if (session.Info.Title == title) return;
        session.Info.Title = title;
        if (session.Info.JobId is { } jobId)
            _jobTracker.UpdateName(jobId, title);
        PersistSessionRecord(session.Info);
        SessionUpdated?.Invoke(session.Info);
    }

    internal static object BuildContentPayload(IReadOnlyList<SessionInputPart> input)
    {
        if (!input.Any(part => part.Attachment is not null || part.LegacyImage is not null))
            return SessionInputFormatting.UserText(input);

        var blocks = new List<object>();
        foreach (var part in input)
        {
            if (part.Type == "text" && !string.IsNullOrWhiteSpace(part.Text))
                blocks.Add(new { type = "text", text = part.Text });
            else if (part.LegacyImage is { } legacy)
                blocks.Add(new { type = "image", source = new { type = "base64", media_type = legacy.MediaType, data = legacy.Base64 } });
            else if (part.Attachment is { Kind: "image" } image)
                blocks.Add(new { type = "image", source = new { type = "base64", media_type = image.MediaType, data = Convert.ToBase64String(File.ReadAllBytes(image.StoredPath)) } });
            else if (part.Attachment is { } file)
                blocks.Add(new { type = "text", text = SessionInputFormatting.FileReference(file) });
        }
        return blocks;
    }

    public Task<bool> SendMessage(string sessionId, string content, ImageAttachment[]? images = null, string? attachmentsJson = null, string? messageUid = null)
    {
        var input = new List<SessionInputPart>();
        if (!string.IsNullOrWhiteSpace(content)) input.Add(SessionInputPart.TextPart(content));
        if (images is not null)
            input.AddRange(images.Select(i => SessionInputPart.LegacyImagePart(new RedCompute.Core.Sessions.ImageAttachment(i.MediaType, i.Base64))));
        return SendInput(sessionId, input, attachmentsJson, messageUid);
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
            session.UserInputUnconsumed = true;
            try { WriteControlLine(session, msg); }
            catch { session.UserInputUnconsumed = false; throw; }
            session.Info.Status = SessionStatus.Active;
            SessionUpdated?.Invoke(session.Info);

            // An answer is a user record too — it splits the assistant run on
            // reload, so the next assistant events must mint a fresh turn uid.
            session.CurrentAssistantUid = null;
            PersistMessage(sessionId, "user", "text", answer, null, null, null, null,
                messageUid: Guid.NewGuid().ToString("N"));

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

            // Completed by ParseResultEvent once the `result` event for the
            // aborted (or already-finishing) turn actually arrives -- that is
            // the real "the process is idle again" signal, not the control_response
            // ack below.
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            session.InterruptCompletion = completion;
            session.InterruptPending = true;

            WriteControlLine(session, msg);

            _log($"[Claude] Interrupt sent for session {sessionId} (request {requestId})", null);

            StreamEvent?.Invoke(sessionId, new ClaudeStreamEvent
            {
                Type = "status",
                Content = "interrupting"
            });

            _ = ForceInterruptAfterTimeout(sessionId, session, completion, InterruptGraceTimeout);

            return InterruptResult.Interrupted;
        }
        catch (Exception ex)
        {
            _log($"[Claude] Failed to send interrupt to {sessionId}: {ex.Message}", null);
            return InterruptResult.Error;
        }
    }

    /// <summary>
    /// Asks the CLI to stop one background task. The control_response only confirms the request;
    /// the task's terminal state arrives through its own task_updated/task_notification lines.
    /// The CLI answers success for a task that already ended, so a repeated stop is harmless.
    /// </summary>
    public async Task<BackgroundTaskStopResult> TryStopBackgroundTaskAsync(string sessionId, string taskId)
    {
        if (!_sessions.TryGetValue(sessionId, out var session))
            return new(BackgroundTaskStopStatus.SessionNotFound, $"Session '{sessionId}' is not running");
        if (!session.BackgroundTasks.Knows(taskId))
            return new(BackgroundTaskStopStatus.TaskNotFound,
                $"Background task '{taskId}' is not known to the running session");

        var requestId = $"stop-task-{Guid.NewGuid():N}";
        var response = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.PendingControlRequests[requestId] = response;
        try
        {
            await WriteControlLineAsync(session, new
            {
                type = "control_request",
                request_id = requestId,
                request = new { subtype = "stop_task", task_id = taskId },
            });
        }
        catch (Exception ex)
        {
            session.PendingControlRequests.TryRemove(requestId, out _);
            _log($"[Claude] Failed to stop background task {taskId} on {sessionId}: {ex.Message}", null);
            return new(BackgroundTaskStopStatus.Failed, ex.Message);
        }

        _log($"[Claude] Stop requested for background task {taskId} on session {sessionId} (request {requestId})", null);
        var timeout = Task.Delay(BackgroundTaskStopTimeout);
        if (await Task.WhenAny(response.Task, timeout) != response.Task)
        {
            session.PendingControlRequests.TryRemove(requestId, out _);
            return new(BackgroundTaskStopStatus.Failed, "The CLI did not confirm the stop request");
        }
        return await response.Task is { } error
            ? new(BackgroundTaskStopStatus.Failed, error)
            : BackgroundTaskStopResult.Accepted();
    }

    private async Task ForceInterruptAfterTimeout(string sessionId, ManagedSession session, TaskCompletionSource<bool> completion, TimeSpan timeout)
    {
        try
        {
            var timeoutTask = Task.Delay(timeout, session.Cts.Token);
            var winner = await Task.WhenAny(completion.Task, timeoutTask);
            if (winner != timeoutTask)
                return; // graceful interrupt already resolved via the result event
        }
        catch (OperationCanceledException) { return; }

        if (!session.InterruptPending)
            return; // resolved between the race above and this check

        _log($"[Claude] Interrupt not honored after {timeout.TotalSeconds}s, killing session {sessionId}", null);

        session.RestartPending = true;
        session.InterruptPending = false;
        session.InterruptCompletion = null;
        _restarting[sessionId] = session.Info.JobId;
        try
        {
            await KillAndResumeAfterForcedInterrupt(sessionId, session);
        }
        finally
        {
            _restarting.TryRemove(sessionId, out _);
        }
    }

    private async Task KillAndResumeAfterForcedInterrupt(string sessionId, ManagedSession session)
    {
        // Distinct from the graceful "interrupted" status: this is a kill, the
        // process is about to die and be replaced. Clients must not treat this
        // as safe-to-write; wait for the "idle" status emitted after resume below.
        StreamEvent?.Invoke(sessionId, new ClaudeStreamEvent { Type = "status", Content = "killed" });
        // The interrupt ended this turn, by replacing the process rather than by the CLI's own abort.
        MarkTurnEnded(session, Core.Sessions.SessionTurnOutcomes.Interrupted);

        try { session.Process.Kill(entireProcessTree: true); } catch { }

        // Wait for OnProcessExited cleanup, then auto-resume
        try { await session.Process.WaitForExitAsync(); } catch { }
        await Task.Delay(300);

        if (string.IsNullOrEmpty(session.Info.ClaudeSessionId))
            return;

        _log($"[Claude] Auto-resuming session {sessionId} after forced interrupt", null);
        var resumed = await ResumeSessionAsync(sessionId);
        if (resumed == null)
        {
            _log($"[Claude] Failed to auto-resume session {sessionId} after forced interrupt: {LastStartError}", null);
            StreamEvent?.Invoke(sessionId, new ClaudeStreamEvent { Type = "error", Content = $"Session killed and failed to resume: {LastStartError}" });
            return;
        }

        StreamEvent?.Invoke(sessionId, new ClaudeStreamEvent { Type = "status", Content = "idle" });
    }

    /// <summary>
    /// Legacy entry point. A mode is only ever a mapped access level now; RedCompute's routes
    /// authorize changes and call <see cref="SetAccessState"/>, so this applies no rules itself.
    /// </summary>
    public bool SetPermissionMode(string sessionId, string mode)
        => Core.Sessions.SessionAccessLevels.TryFromPermissionMode(mode, out var level)
            && GetAccessState(sessionId) is { } current
            && SetAccessState(sessionId, (current with { AccessLevel = level }).AsExplicit());

    public async Task StopSession(string sessionId, string? reason = null)
    {
        if (_sessions.TryGetValue(sessionId, out var session))
        {
            _log($"[Claude] Stopping session {sessionId} (in-memory, PID {session.Process.Id})", null);

            try
            {
                var requestId = $"stop-{++session.ControlRequestCounter}";
                var msg = new
                {
                    type = "control_request",
                    request_id = requestId,
                    request = new { subtype = "interrupt" }
                };
                await WriteControlLineAsync(session, msg);
            }
            catch { }

            var exited = await WaitForExit(session.Process, TimeSpan.FromSeconds(3));
            if (!exited)
            {
                try { session.Process.Kill(entireProcessTree: true); } catch { }
            }

            DiscardPendingQuestions(session, "session_ended");
            DiscardPendingSteers(session);
            session.Cts.Cancel();
            SettleTurnContent(session);
            SettleLostBackgroundTasks(session);
            session.Info.Status = SessionStatus.Stopped;
            session.Info.StopReason = reason ?? "user_stopped";
            session.Info.StoppedAt = DateTimeOffset.UtcNow;
            session.Info.ProcessId = null;
            _sessions.TryRemove(sessionId, out _);
            PersistSessionRecord(session.Info);

            if (session.Info.JobId.HasValue)
                _jobTracker.MarkCompleted(session.Info.JobId.Value, resultJson: $"{{\"messages\":{session.Info.MessageCount}}}", costUsd: session.Info.CostUsd);

            SessionEnded?.Invoke(sessionId, "stopped", session.Info.StopReason);
            return;
        }

        var record = _sessionStore.FindSession(sessionId);
        if (record == null) return;
        var requestedReason = reason ?? SessionStopReasons.UserStopped;
        var explicitStopOfParked = requestedReason == SessionStopReasons.UserStopped
            && SessionStopReasons.IsAutoResumable(record.StopReason);
        if ((record.Status is "Stopped" or "Error") && !explicitStopOfParked) return;

        _log($"[Claude] Stopping unloaded session {sessionId} (PID {record.ProcessId})", null);

        TryKillByPid(record.ProcessId);

        record.Status = "Stopped";
        record.StopReason = requestedReason;
        record.StoppedAt = DateTimeOffset.UtcNow;
        record.ProcessId = null;
        _sessionStore.SaveSession(record);

        if (record.JobId.HasValue)
            _jobTracker.MarkCompleted(record.JobId.Value, resultJson: $"{{\"messages\":{record.MessageCount}}}", costUsd: record.CostUsd);

        SessionEnded?.Invoke(sessionId, "stopped", record.StopReason);
    }

    public async Task ForceKill(string sessionId, string? reason = null)
    {
        if (_sessions.TryRemove(sessionId, out var session))
        {
            try { session.Process.Kill(entireProcessTree: true); } catch { }
            DiscardPendingQuestions(session, "session_ended");
            DiscardPendingSteers(session);
            session.Cts.Cancel();
            SettleTurnContent(session);
            SettleLostBackgroundTasks(session);
            session.Info.Status = SessionStatus.Stopped;
            session.Info.StopReason = reason ?? "user_stopped";
            session.Info.StoppedAt = DateTimeOffset.UtcNow;
            session.Info.ProcessId = null;
            PersistSessionRecord(session.Info);

            if (!SessionStopReasons.IsAutoResumable(reason) && session.Info.JobId.HasValue)
                _jobTracker.MarkCompleted(session.Info.JobId.Value, resultJson: $"{{\"messages\":{session.Info.MessageCount}}}", costUsd: session.Info.CostUsd);

            SessionEnded?.Invoke(sessionId, "killed", session.Info.StopReason);
            return;
        }

        var record = _sessionStore.FindSession(sessionId);
        if (record == null) return;
        var requestedReason = reason ?? SessionStopReasons.UserStopped;
        var explicitStopOfParked = requestedReason == SessionStopReasons.UserStopped
            && SessionStopReasons.IsAutoResumable(record.StopReason);
        if ((record.Status is "Stopped" or "Error")
            && !SessionStopReasons.IsAutoResumable(requestedReason)
            && !explicitStopOfParked) return;

        TryKillByPid(record.ProcessId);

        record.Status = "Stopped";
        record.StopReason = requestedReason;
        record.StoppedAt = DateTimeOffset.UtcNow;
        record.ProcessId = null;
        _sessionStore.SaveSession(record);

        if (!SessionStopReasons.IsAutoResumable(requestedReason) && record.JobId.HasValue)
            _jobTracker.MarkCompleted(record.JobId.Value, resultJson: $"{{\"messages\":{record.MessageCount}}}", costUsd: record.CostUsd);

        SessionEnded?.Invoke(sessionId, "killed", record.StopReason);
        await Task.CompletedTask;
    }

    public IReadOnlyList<ClaudeSessionInfo> GetResidentIdleSessions()
        => _sessions.Values
            .Where(session => session.Info.Status == SessionStatus.Idle)
            .Select(session => session.Info)
            .ToArray();

    public Task<bool> TryParkIdleSessionAsync(
        string sessionId, string stopReason, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!_sessions.TryGetValue(sessionId, out var session)
            || session.Info.Status != SessionStatus.Idle
            || session.Info.BackgroundTasks.Any(task => !BackgroundTaskStatuses.IsTerminal(task.Status)))
            return Task.FromResult(false);
        if (!_sessions.TryRemove(sessionId, out var removed) || !ReferenceEquals(session, removed))
            return Task.FromResult(false);
        if (session.Info.Status != SessionStatus.Idle
            || session.Info.BackgroundTasks.Any(task => !BackgroundTaskStatuses.IsTerminal(task.Status)))
        {
            _sessions.TryAdd(sessionId, session);
            return Task.FromResult(false);
        }

        try { session.Process.Kill(entireProcessTree: true); } catch { }
        DiscardPendingQuestions(session, "session_parked");
        DiscardPendingSteers(session);
        session.Cts.Cancel();
        SettleTurnContent(session);
        SettleLostBackgroundTasks(session);
        session.Info.Status = SessionStatus.Stopped;
        session.Info.StopReason = stopReason;
        session.Info.StoppedAt = DateTimeOffset.UtcNow;
        session.Info.ProcessId = null;
        PersistSessionRecord(session.Info);
        SessionUpdated?.Invoke(session.Info);
        return Task.FromResult(true);
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

    public bool RestoreSession(string sessionId)
    {
        var record = _sessionStore.FindSession(sessionId);
        if (record == null) return false;
        if (!record.Dismissed) return true;
        record.Dismissed = false;
        _sessionStore.SaveSession(record);
        return true;
    }

    public List<ClaudeSessionInfo> GetSessions(int limit = 20, bool includeDismissed = false)
    {
        var live = _sessions.Values.Select(s => s.Info).ToList();
        var activeIds = _sessions.Keys.ToHashSet();

        var recent = _sessionStore.GetRecentSessions(activeIds, limit, includeDismissed);
        var dbSessions = recent.Select(ToUnloadedSessionInfo).ToList();

        return [.. live, .. dbSessions];
    }

    public (ClaudeSessionInfo? Info, List<ClaudeMessageRecord> History) GetSession(string sessionId)
    {
        if (_sessions.TryGetValue(sessionId, out var session))
            return (session.Info, GetHistory(sessionId));

        var record = _sessionStore.FindSession(sessionId);
        if (record == null) return (null, []);
        return (ToUnloadedSessionInfo(record), GetHistory(sessionId));
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
                    result[jobId] = UnloadedStatus(status, _restarting.Values.Contains(jobId)).Status;
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
        return (ToUnloadedSessionInfo(record), GetHistory(record.Id));
    }

    /// <summary>
    /// Maps a stored record for a session that is not loaded in this process. Only a loaded
    /// session has a CLI behind it, so a stored Idle/Active/Starting row is stale (a restart
    /// that skipped recovery, or a late write after a stop). Reporting it as live makes every
    /// send fail as provider_rejected; reporting it as orphaned lets the input queue resume it.
    /// </summary>
    private ClaudeSessionInfo ToUnloadedSessionInfo(ClaudeSessionRecord record)
    {
        var info = ToSessionInfo(record);
        var (status, stopReason) = UnloadedStatus(info.Status, _restarting.ContainsKey(record.Id));
        if (status != info.Status)
        {
            info.Status = status;
            info.StopReason = stopReason ?? info.StopReason;
        }
        return info;
    }

    internal static (SessionStatus Status, string? StopReason) UnloadedStatus(SessionStatus stored, bool restarting)
    {
        if (stored is not (SessionStatus.Idle or SessionStatus.Active or SessionStatus.Starting))
            return (stored, null);
        return restarting
            ? (SessionStatus.Starting, null)
            : (SessionStatus.Stopped, SessionStopReasons.OrphanedOnRestart);
    }

    private static ClaudeSessionInfo ToSessionInfo(ClaudeSessionRecord r) => new()
    {
        Id = r.Id,
        ProjectName = r.ProjectName,
        ProjectPath = r.ProjectPath,
        ScratchDirectory = r.ScratchDirectory,
        Status = Enum.TryParse<SessionStatus>(r.Status, out var s) ? s : SessionStatus.Stopped,
        StopReason = r.StopReason,
        StoppedAt = r.StoppedAt,
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
        QualityTier = r.QualityTier,
        ProviderEntity = r.ProviderEntity,
        DeveloperInstructions = r.DeveloperInstructions,
        JobId = r.JobId,
        Source = r.Source,
        Access = AccessOf(r),
        PermissionMode = PermissionModeFor(r.AccessLevel),
    };

    internal static Core.Sessions.SessionAccessState AccessOf(ClaudeSessionRecord r)
        => new(Core.Sessions.SessionAccessLevels.IsKnown(r.AccessLevel) ? r.AccessLevel : null,
            Core.Sessions.SessionAccessLevels.IsKnown(r.MaxAccessLevel) ? r.MaxAccessLevel : null,
            r.Unattended,
            Core.Sessions.SessionAccessLevels.IsKnown(r.AccessLevelBeforePlan) ? r.AccessLevelBeforePlan : null,
            r.AccessLevelSource);

    /// <summary>Live background tasks of a loaded session; a session without a process has none.</summary>
    public IReadOnlyList<BackgroundTaskInfo> GetBackgroundTasks(string sessionId)
        => _sessions.TryGetValue(sessionId, out var session) ? session.Info.BackgroundTasks : [];

    /// <summary>
    /// Questions a loaded session is parked on. Every resolution path (answer, decline, CLI
    /// cancel, expiry, process end) removes the entry before it emits question_resolved, so this
    /// never reports a question the session has stopped waiting for.
    /// </summary>
    public IReadOnlyList<Core.Sessions.PendingSessionQuestion> GetPendingQuestions(string sessionId)
    {
        if (!_sessions.TryGetValue(sessionId, out var session)) return [];
        return session.PendingQuestions.Values
            .OrderBy(q => q.AskedAt)
            .Select(q => new Core.Sessions.PendingSessionQuestion
            {
                RequestId = q.RequestId,
                Kind = q.Permission is null
                    ? Core.Sessions.PendingSessionQuestion.QuestionKind
                    : Core.Sessions.PendingSessionQuestion.PermissionKind,
                Permission = q.Permission,
                ToolName = q.ToolName ?? AskUserQuestionTool,
                ToolUseId = q.ToolUseId,
                Questions = q.Permission is null && q.Input.ValueKind == JsonValueKind.Object
                    && q.Input.TryGetProperty("questions", out var questions) ? questions : EmptyJsonArray,
                Input = q.Input.ValueKind == JsonValueKind.Undefined ? EmptyJsonObject : q.Input,
                AskedAt = q.AskedAt,
                ExpiresAt = q.ExpiresAt,
            })
            .ToArray();
    }

    private static readonly JsonElement EmptyJsonArray = JsonDocument.Parse("[]").RootElement.Clone();
    private static readonly JsonElement EmptyJsonObject = JsonDocument.Parse("{}").RootElement.Clone();

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
        => ResolveClaudePath(
            _config.ClaudePath,
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Environment.GetEnvironmentVariable("PATH"));

    internal static string? ResolveClaudePath(
        string? configuredPath,
        string applicationData,
        string? pathEnvironment)
    {
        if (configuredPath != null)
            return File.Exists(configuredPath) ? configuredPath : null;

        // Prefer the native binary from the globally installed Claude Code package. This is the
        // package RedCompute exposes for version management, so the version we report, install,
        // and launch must all refer to the same bytes.
        var globalCliExe = Path.Combine(applicationData, "npm", "node_modules", "@anthropic-ai",
            "claude-code", "bin", "claude.exe");
        if (File.Exists(globalCliExe))
            return globalCliExe;

        // The Agent SDK also bundles a native CLI that supports stream-json IPC. Keep it as a
        // compatibility fallback for machines without a global Claude Code package.
        var sdkExe = Path.Combine(applicationData, "npm", "node_modules", "@anthropic-ai",
            "claude-agent-sdk", "node_modules", "@anthropic-ai",
            "claude-agent-sdk-win32-x64", "claude.exe");
        if (File.Exists(sdkExe))
            return sdkExe;

        // Claude Desktop bundles versioned native CLIs outside PATH.
        var desktopRoot = Path.Combine(applicationData, "Claude", "claude-code");
        if (Directory.Exists(desktopRoot))
        {
            var desktopExe = Directory.GetDirectories(desktopRoot)
                .Select(directory => new
                {
                    Path = Path.Combine(directory, "claude.exe"),
                    Version = Version.TryParse(Path.GetFileName(directory), out var version)
                        ? version
                        : new Version(0, 0),
                })
                .Where(candidate => File.Exists(candidate.Path))
                .OrderByDescending(candidate => candidate.Version)
                .Select(candidate => candidate.Path)
                .FirstOrDefault();
            if (desktopExe != null)
                return desktopExe;
        }

        // Fallback: search PATH for claude.cmd / claude.exe
        var pathDirs = pathEnvironment?.Split(Path.PathSeparator) ?? [];
        foreach (var dir in pathDirs)
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            foreach (var ext in new[] { ".exe", ".cmd", "" })
            {
                var candidate = Path.Combine(dir.Trim().Trim('"'), $"claude{ext}");
                if (File.Exists(candidate))
                    return candidate;
            }
        }
        return null;
    }

    private void PopulateSessionArgs(ProcessStartInfo startInfo, string permissionMode, string? resumeClaudeSessionId = null,
        string? model = null, string? effort = null, string? developerInstructions = null)
    {
        foreach (var arg in new[] { "--output-format", "stream-json", "--verbose",
            "--input-format", "stream-json", "--include-partial-messages", "--replay-user-messages",
            // The session's persisted access level, mapped (PermissionModeFor). Every launch passes
            // it: start, resume, rollback and forced-interrupt recovery all come through here.
            "--permission-mode", permissionMode,
            // Makes bypassPermissions reachable by a later set_permission_mode without starting in
            // it (CLI 2.1.318: "Enable bypassing all permission checks as an option, without it
            // being enabled by default"). Without it the CLI refuses a live raise to full with
            // "...not launched with --dangerously-skip-permissions". The mode in force is still
            // --permission-mode above; only RedCompute writes set_permission_mode, and only for a
            // level it authorized (person-present raise, policy, cap), so this widens nothing.
            "--allow-dangerously-skip-permissions",
            // Routes tool asks to us as inbound `control_request`s instead of letting the CLI
            // self-deny them: under ask and auto those become the permission cards a person
            // answers. Under bypassPermissions the evaluator allows Bash/Edit/etc before the ask
            // path, so only tools that declare requiresUserInteraction() -- AskUserQuestion,
            // ExitPlanMode -- reach us. Without this flag an ask has nowhere to go and comes back
            // to the model as `<error>Answer questions?</error>` (the ask's own text as the denial).
            "--permission-prompt-tool", "stdio",
            // Restore visible thinking text on Opus 4.7+/Fable 5 (display defaults to "omitted").
            "--thinking-display", "summarized" })
            startInfo.ArgumentList.Add(arg);

        // Blank means unset: the provider config ships "Model": "", and a "--model ''" launch
        // fails every turn with "model: String should have at least 1 character".
        var m = LaunchModel(model, _config.Model);
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
        if (developerInstructions != null)
        {
            // Appended to Claude Code's own system prompt, never replacing it (--system-prompt)
            // and never demoted to a user turn. One argv element: ArgumentList quotes it for
            // CreateProcess, so quotes, newlines and markup like <discord-no-reply/> survive.
            startInfo.ArgumentList.Add("--append-system-prompt");
            startInfo.ArgumentList.Add(developerInstructions);
        }
        if (resumeClaudeSessionId != null)
        {
            startInfo.ArgumentList.Add("--resume");
            startInfo.ArgumentList.Add(resumeClaudeSessionId);
        }
    }

    /// <summary>The CLI 2.1.318 permission mode for an access level. Unknown or legacy (null) is ask.</summary>
    internal static string PermissionModeFor(string? accessLevel) => accessLevel switch
    {
        Core.Sessions.SessionAccessLevels.Plan => "plan",
        Core.Sessions.SessionAccessLevels.Auto => "auto",
        Core.Sessions.SessionAccessLevels.Full => "bypassPermissions",
        _ => "acceptEdits",
    };

    internal static readonly IReadOnlyList<string> SupportedAccessLevels = Core.Sessions.SessionAccessLevels.All;

    /// <summary>
    /// Claude issues its conversation id on the first completed turn. A session stopped before
    /// any turn (for example a fresh Discord session that only received outreach appends when a
    /// restart hit) has no Claude conversation to lose, so it relaunches fresh with the same
    /// instructions. A session that did complete turns but lost its id must not silently
    /// restart without its history.
    /// </summary>
    internal static string? LaunchModel(string? requested, string? configured)
        => !string.IsNullOrWhiteSpace(requested) ? requested.Trim()
            : !string.IsNullOrWhiteSpace(configured) ? configured.Trim()
            : null;

    internal static string? ResumeLaunchError(string? claudeSessionId, int messageCount)
        => string.IsNullOrEmpty(claudeSessionId) && messageCount > 0
            ? "Session has no Claude session ID to resume"
            : null;

    /// <summary>
    /// Developer instructions are a security boundary, so a launch that cannot deliver them
    /// verbatim must fail instead of starting without them. A batch shim (claude.cmd) runs
    /// through cmd.exe, which ends the command at the first newline and expands %VAR% and ^,
    /// so multi-line instruction text cannot reach the CLI intact.
    /// </summary>
    internal static string? DeveloperInstructionLaunchError(string claudePath, string? developerInstructions)
    {
        if (developerInstructions == null) return null;
        var extension = Path.GetExtension(claudePath);
        return extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".bat", StringComparison.OrdinalIgnoreCase)
                ? $"Developer instructions require a native Claude Code executable; '{claudePath}' is a batch shim that cannot pass multi-line arguments intact"
                : null;
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
                    HandleOutputLine(session, line);

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
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // One event we could not read must not stop the reader: every later event,
                    // interrupt acknowledgements included, arrives through this loop.
                    HandleUnreadableOutputLine(session, line, ex);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _log($"[Claude] Stdout reader error for {session.Info.Id}: {ex.Message}", null);
        }
    }

    /// <summary>Test seam: feeds one CLI stdout line through the same path as the reader.</summary>
    internal bool ProcessOutputLine(string sessionId, string line)
    {
        if (!_sessions.TryGetValue(sessionId, out var session)) return false;
        HandleOutputLine(session, line);
        return true;
    }

    /// <summary>Test seam: one line through the reader's failure handling, as the loop calls it.</summary>
    internal bool ProcessUnreadableOutputLine(string sessionId, string line, Exception error)
    {
        if (!_sessions.TryGetValue(sessionId, out var session)) return false;
        HandleUnreadableOutputLine(session, line, error);
        return true;
    }

    // Only a result ends a turn, so only a result we failed to read leaves one running forever.
    private void HandleUnreadableOutputLine(ManagedSession session, string line, Exception error)
    {
        _log($"[Claude] Session {session.Info.Id}: could not process output event: {error.Message}", null);

        string? terminalMessage;
        try
        {
            using var doc = JsonDocument.Parse(line);
            if (!doc.RootElement.TryGetProperty("type", out var type)
                || type.ValueKind != JsonValueKind.String
                || type.GetString() != "result")
                return;
            terminalMessage = ReadTerminalResultError(doc.RootElement);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException) { return; }

        if (session.Cts.IsCancellationRequested || session.RestartPending
            || !_sessions.TryGetValue(session.Info.Id, out var current) || !ReferenceEquals(current, session))
            return;

        SettleTurnContent(session);
        session.InterruptPending = false;
        session.InterruptCompletion?.TrySetResult(true);
        session.InterruptCompletion = null;
        session.Info.Status = SessionStatus.Idle;
        MarkTurnEnded(session, Core.Sessions.SessionTurnOutcomes.Failed);
        PersistSessionRecord(session.Info);
        SessionUpdated?.Invoke(session.Info);
        Publish(session, new ClaudeStreamEvent
        {
            Type = "error",
            Content = terminalMessage ?? "The turn ended, but RedCompute could not read how it ended.",
        });
    }

    private void HandleOutputLine(ManagedSession session, string line)
    {
        ResetSteerBoundaryWatchdogs(session);
        var evt = ParseStreamLine(line, session);
        if (evt == null) return;
        Publish(session, evt);
    }

    private void Publish(ManagedSession session, ClaudeStreamEvent evt)
    {
        // Stamp the turn uid before the event is broadcast or persisted so the
        // streamed copy and the stored record carry the same identity.
        // status/error close the turn. Background work (a subagent's line, tagged with the tool
        // call that launched it) keeps the launching turn's uid and never opens, closes or
        // reuses the main loop's current turn.
        if (evt.Type is "status" or "error")
            session.CurrentAssistantUid = null;
        else if (evt.ParentToolUseId is { } parentToolUseId)
            evt.MessageUid = ResolveLaunchUid(session, parentToolUseId);
        else
        {
            if (session.CurrentAssistantUid is null)
            {
                session.CurrentAssistantUid = Guid.NewGuid().ToString("N");
                if (session.PendingLiveTurnOrigin is { } origin)
                {
                    evt.TurnOrigin = origin;
                    session.PendingLiveTurnOrigin = null;
                    session.DurableTurnOrigin = (session.CurrentAssistantUid, origin);
                }
            }
            evt.MessageUid = session.CurrentAssistantUid;
        }
        // Children of a tool call (nested subagents included) resolve to the turn that made it.
        if (evt is { Type: "tool_use", MessageId: { } toolUseId })
            session.ToolTurnUids[toolUseId] = evt.MessageUid;
        Deliver(session, evt);
    }

    /// <summary>Broadcasts, retains and persists an event whose turn uid is already stamped.</summary>
    private void Deliver(ManagedSession session, ClaudeStreamEvent evt)
    {
        // Streamed text and thinking are live-only, as Codex's agent-message/reasoning deltas are.
        // Each block is persisted once, whole: text when its commentary/final phase is decided,
        // thinking at content_block_stop (or a terminal flush). Persisting each delta makes token
        // cadence determine durable transcript cardinality.
        var deferred = evt is { Type: "text" or "thinking", IsPartial: true, BlockIndex: not null };
        if (deferred)
            evt.MessageId = evt.Type == "thinking"
                ? session.TextBlocks.AppendThinking(
                    evt.ParentToolUseId, evt.BlockIndex!.Value, evt.Content, evt.MessageUid)
                : session.TextBlocks.Append(
                    evt.ParentToolUseId, evt.BlockIndex!.Value, evt.Content, evt.MessageUid);

        StreamEvent?.Invoke(session.Info.Id, evt);
        session.MessageHistory.Add(evt);
        TrimHistory(session);
        if (!deferred)
            PersistMessage(session.Info.Id, "assistant", evt.Type, evt.Content,
                evt.ToolName, evt.ToolInput?.ToString(), evt.ToolResult, evt.MessageId,
                messageUid: evt.MessageUid, phase: evt.Phase,
                parentToolUseId: evt.ParentToolUseId, turnOrigin: evt.Type == "status" ? evt.TurnOrigin : null);
    }

    /// <summary>
    /// The logical turn that launched a tool call. Filled as tool calls stream; a launching call
    /// from before this process (none in practice: tasks die with their process) is looked up in
    /// the durable history. Never mints: an unresolvable child stays unattached to any turn.
    /// </summary>
    private string? ResolveLaunchUid(ManagedSession session, string toolUseId)
    {
        if (session.ToolTurnUids.TryGetValue(toolUseId, out var uid)) return uid;
        try { uid = _sessionStore.FindToolUseMessageUid(session.Info.Id, toolUseId); }
        catch (Exception ex)
        {
            _log($"[Claude] Failed to resolve the turn of tool call {toolUseId}: {ex.Message}", null);
            uid = null;
        }
        session.ToolTurnUids[toolUseId] = uid;
        return uid;
    }

    /// <summary>
    /// Applies one <c>stream_event</c> to the text-phase lifecycle and returns the text blocks it
    /// settled as commentary. The model continuing with a tool (a tool_use block starting, or the
    /// API message stopping for tool use) is what proves earlier text was not the answer.
    /// </summary>
    internal static IReadOnlyList<ClaudeTextBlockTracker.SettledText> ApplyTextLifecycle(
        ClaudeTextBlockTracker tracker, JsonElement root)
    {
        if (!root.TryGetProperty("event", out var evt) || evt.ValueKind != JsonValueKind.Object)
            return [];
        var parent = ParentToolUseId(root);
        var evtType = evt.TryGetProperty("type", out var et) && et.ValueKind == JsonValueKind.String
            ? et.GetString() : null;

        switch (evtType)
        {
            case "message_start":
                var apiMessageId = evt.TryGetProperty("message", out var message)
                    && message.ValueKind == JsonValueKind.Object
                    && message.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String
                        ? id.GetString() : null;
                tracker.BeginMessage(parent, apiMessageId);
                return [];

            case "content_block_start":
                var blockType = evt.TryGetProperty("content_block", out var block)
                    && block.ValueKind == JsonValueKind.Object
                    && block.TryGetProperty("type", out var bt) && bt.ValueKind == JsonValueKind.String
                        ? bt.GetString() : null;
                return blockType is "tool_use" or "server_tool_use" or "mcp_tool_use"
                    ? tracker.SettleCommentary(parent)
                    : [];

            case "message_delta":
                var stopReason = evt.TryGetProperty("delta", out var delta)
                    && delta.ValueKind == JsonValueKind.Object
                    && delta.TryGetProperty("stop_reason", out var sr) && sr.ValueKind == JsonValueKind.String
                        ? sr.GetString() : null;
                // A subagent's text is never the parent turn's answer, whatever ends it.
                return parent is not null || stopReason is "tool_use" or "pause_turn"
                    ? tracker.SettleCommentary(parent)
                    : [];

            default:
                return [];
        }
    }

    /// <summary>
    /// Returns a whole thinking block only when its matching <c>content_block_stop</c> arrives.
    /// Unlike text, thinking has no phase settlement.
    /// </summary>
    internal static IReadOnlyList<ClaudeTextBlockTracker.SettledThinking> ApplyThinkingLifecycle(
        ClaudeTextBlockTracker tracker, JsonElement root)
    {
        if (!root.TryGetProperty("event", out var evt) || evt.ValueKind != JsonValueKind.Object)
            return [];
        if (!evt.TryGetProperty("type", out var et) || et.ValueKind != JsonValueKind.String
            || et.GetString() != "content_block_stop")
            return [];
        if (!evt.TryGetProperty("index", out var index) || !index.TryGetInt32(out var blockIndex))
            return [];
        return tracker.SettleThinking(ParentToolUseId(root), blockIndex);
    }

    private static string? ParentToolUseId(JsonElement root)
        => root.TryGetProperty("parent_tool_use_id", out var parent) && parent.ValueKind == JsonValueKind.String
            ? parent.GetString()
            : null;

    private void PersistSettledText(ManagedSession session,
        IReadOnlyList<ClaudeTextBlockTracker.SettledText> settled)
    {
        foreach (var text in settled)
            PersistMessage(session.Info.Id, "assistant", "text", text.Content, null, null, null,
                text.MessageId,
                messageUid: text.ParentToolUseId is null ? text.MessageUid ?? session.CurrentAssistantUid : text.MessageUid,
                phase: text.Phase, parentToolUseId: text.ParentToolUseId);
    }

    private void PersistSettledThinking(ManagedSession session,
        IReadOnlyList<ClaudeTextBlockTracker.SettledThinking> settled)
    {
        foreach (var thinking in settled)
            PersistMessage(session.Info.Id, "assistant", "thinking", thinking.Content, null, null, null,
                thinking.MessageId,
                messageUid: thinking.ParentToolUseId is null ? thinking.MessageUid ?? session.CurrentAssistantUid : thinking.MessageUid,
                parentToolUseId: thinking.ParentToolUseId);
    }

    /// <summary>
    /// The turn is over (result, exit, stop or kill): preserve live thinking first, then settle
    /// text, matching the normal thinking-before-text stream order.
    /// </summary>
    private void SettleTurnContent(ManagedSession session)
    {
        SettleCompaction(session, "Context compaction ended with the turn");
        PersistSettledThinking(session, session.TextBlocks.SettleTurnThinking());
        PersistSettledText(session, session.TextBlocks.SettleTurn());
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
                if (TryAcceptSteerReplay(root, session))
                    return null;
                // The CLI echoes a written user message when it takes it into a turn.
                if (root.TryGetProperty("isReplay", out var replayed) && replayed.ValueKind == JsonValueKind.True)
                    session.UserInputUnconsumed = false;
                return ParseUserEvent(root);

            case "result":
                return ParseResultEvent(root, session);

            case "control_response":
                HandleControlResponse(root, session);
                return null;

            // The CLI asking US something. Opposite direction to the control_request we
            // send for interrupt/set_permission_mode -- same envelope, inbound.
            case "control_request":
                HandleInboundControlRequest(root, session);
                return null;

            // The CLI abandoning a request it sent us (turn aborted, session tearing down).
            case "control_cancel_request":
                HandleControlCancelRequest(root, session);
                return null;

            case "stream_event":
                UpdateContextFromStreamEvent(root, session);
                PersistSettledText(session, ApplyTextLifecycle(session.TextBlocks, root));
                PersistSettledThinking(session, ApplyThinkingLifecycle(session.TextBlocks, root));
                return ParseStreamEvent(root);

            case "rate_limit_event":
                _accountUsage.ApplyEvent(root);
                return null;

            default:
                _log($"[Claude] Unhandled event type '{type}': {line.Substring(0, Math.Min(line.Length, 500))}", null);
                return null;
        }
    }

    private bool TryAcceptSteerReplay(JsonElement root, ManagedSession session)
    {
        if (!root.TryGetProperty("isReplay", out var replay)
            || replay.ValueKind != JsonValueKind.True
            || !root.TryGetProperty("uuid", out var uuidElement)
            || uuidElement.ValueKind != JsonValueKind.String
            || uuidElement.GetString() is not { } correlationId
            || !session.PendingSteers.TryRemove(correlationId, out var pending))
            return false;

        CancelSteerBoundaryWatchdog(pending);
        session.Info.MessageCount++;
        session.CurrentAssistantUid = null;
        PersistMessage(session.Info.Id, "user", "text", pending.TranscriptContent,
            null, null, null, null, pending.AttachmentsJson, pending.MessageUid, promptUuid: correlationId);
        PersistSessionRecord(session.Info);
        SessionUpdated?.Invoke(session.Info);
        pending.Completion.TrySetResult(SessionInputDeliveryResult.Accepted());
        return true;
    }

    private static void SettlePendingSteers(ManagedSession session, SessionInputDeliveryResult result)
    {
        foreach (var correlationId in session.PendingSteers.Keys)
            if (session.PendingSteers.TryRemove(correlationId, out var pending))
            {
                CancelSteerBoundaryWatchdog(pending);
                pending.Completion.TrySetResult(result);
            }
    }

    private void StartSteerBoundaryWatchdogs(ManagedSession session)
    {
        foreach (var entry in session.PendingSteers)
            StartOrResetSteerBoundaryWatchdog(session, entry.Key, entry.Value, start: true);
    }

    private void ResetSteerBoundaryWatchdogs(ManagedSession session)
    {
        foreach (var entry in session.PendingSteers)
            StartOrResetSteerBoundaryWatchdog(session, entry.Key, entry.Value, start: false);
    }

    private void StartOrResetSteerBoundaryWatchdog(
        ManagedSession session, string correlationId, PendingSteer pending, bool start)
    {
        CancellationTokenSource watchdog;
        lock (pending.WatchdogGate)
        {
            if (!start && pending.WatchdogCts is null) return;
            pending.WatchdogCts?.Cancel();
            pending.WatchdogCts?.Dispose();
            watchdog = new CancellationTokenSource();
            pending.WatchdogCts = watchdog;
        }
        _ = WatchSteerBoundarySilenceAsync(session, correlationId, pending, watchdog);
    }

    private async Task WatchSteerBoundarySilenceAsync(
        ManagedSession session, string correlationId, PendingSteer pending,
        CancellationTokenSource watchdog)
    {
        try
        {
            await Task.Delay(SteerBoundarySilenceTimeout, watchdog.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        lock (pending.WatchdogGate)
        {
            if (!ReferenceEquals(pending.WatchdogCts, watchdog)) return;
            pending.WatchdogCts = null;
        }
        watchdog.Dispose();

        if (!_sessions.TryGetValue(session.Info.Id, out var current)
            || !ReferenceEquals(current, session)
            || session.Process.HasExited
            || !session.PendingSteers.TryRemove(correlationId, out var removed)
            || !ReferenceEquals(removed, pending))
            return;

        session.Info.Status = SessionStatus.Idle;
        session.Info.StopReason = "steer_unconfirmed";
        PersistSessionRecord(session.Info);
        _log($"[Claude] Session {session.Info.Id} became idle after {SteerBoundarySilenceTimeout.TotalSeconds:N0}s without stdout or replay for a committed steer", null);
        SessionUpdated?.Invoke(session.Info);
        StreamEvent?.Invoke(session.Info.Id,
            new ClaudeStreamEvent { Type = "status", Content = "idle" });
        pending.Completion.TrySetResult(SessionInputDeliveryResult.Unavailable(
            "steer_unconfirmed",
            "Claude did not replay the committed steer after the preceding turn ended",
            retryable: false));
    }

    private static void CancelSteerBoundaryWatchdog(PendingSteer pending)
    {
        lock (pending.WatchdogGate)
        {
            pending.WatchdogCts?.Cancel();
            pending.WatchdogCts?.Dispose();
            pending.WatchdogCts = null;
        }
    }

    private static void DiscardPendingSteers(ManagedSession session) =>
        SettlePendingSteers(session, SessionInputDeliveryResult.NotSteerable(
            "steer_discarded", "The Claude process ended before replaying the committed steer"));

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

    private void HandleSystemEvent(JsonElement root, ManagedSession session)
    {
        var subtype = root.TryGetProperty("subtype", out var subtypeElement)
            && subtypeElement.ValueKind == JsonValueKind.String ? subtypeElement.GetString() : null;
        var now = DateTimeOffset.UtcNow;
        switch (subtype)
        {
            case "init":
            {
                if (root.TryGetProperty("session_id", out var sid))
                    session.Info.ClaudeSessionId = sid.GetString();
                if (root.TryGetProperty("model", out var model)
                    && model.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(model.GetString()))
                    session.Info.Model = model.GetString();
                // The CLI reports its mode; it describes the process. A mismatch with the level (an
                // account without auto, a mode the CLI changed itself) gets one set_permission_mode
                // attempt; see ReconcilePermissionModeAsync. Not awaited: its answer comes through
                // this very reader.
                if (root.TryGetProperty("permissionMode", out var pm) && pm.ValueKind == JsonValueKind.String
                    && pm.GetString() is { Length: > 0 } reportedMode)
                {
                    session.Info.PermissionMode = reportedMode;
                    if (reportedMode != PermissionModeFor(session.Info.Access.AccessLevel))
                        _ = ReconcilePermissionModeAsync(session, reportedMode);
                }

                // init opens every CLI turn. With no user input waiting, a turn after a background
                // task finished is the CLI waking itself up to report it.
                if (!session.UserInputUnconsumed && session.WakeTasks.TryDequeue(out var wakeTaskId))
                {
                    var origin = new TurnOrigin { Kind = TurnOrigin.BackgroundTask, TaskId = wakeTaskId };
                    session.WakeTurnOrigin = origin;
                    session.PendingLiveTurnOrigin = origin;
                }

                session.Info.Status = SessionStatus.Active;
                PersistSessionRecord(session.Info);
                SessionUpdated?.Invoke(session.Info);
                _log($"[Claude] Session {session.Info.Id} active (model: {session.Info.Model}, mode: {session.Info.PermissionMode})", null);
                return;
            }

            case "task_started":
                ApplyBackgroundChange(session,
                    session.BackgroundTasks.Started(root, toolUseId => ResolveLaunchUid(session, toolUseId), now));
                return;

            case "task_progress":
                ApplyBackgroundChange(session, session.BackgroundTasks.Progress(root));
                return;

            case "task_updated":
                ApplyBackgroundChange(session, session.BackgroundTasks.Updated(root));
                return;

            case "task_notification":
            {
                var change = session.BackgroundTasks.Notification(root, now);
                if (change.WakeTaskId is { } finished)
                    session.WakeTasks.Enqueue(finished);
                else if (change.Kind == ClaudeBackgroundTaskTracker.ChangeKind.None
                    && !change.FinishedInForeground
                    && root.TryGetProperty("task_id", out var unknown))
                    _log($"[Claude] Session {session.Info.Id}: ignoring notification for background task {unknown} from a previous process", null);
                ApplyBackgroundChange(session, change);
                return;
            }

            case "background_tasks_changed":
                ApplyBackgroundChange(session, session.BackgroundTasks.Snapshot(root, now));
                return;

            case "status":
                HandleCompactionStatus(root, session);
                return;

            case "compact_boundary":
                HandleCompactBoundary(root, session);
                return;

            // The turn's result carries the message the user sees. This one says why.
            case "model_refusal_no_fallback":
                _log($"[Claude] Session {session.Info.Id}: refused by model safeguards "
                    + $"(category: {ReadOptionalString(root, "api_refusal_category") ?? "unknown"}, "
                    + $"request: {ReadOptionalString(root, "request_id") ?? "unknown"})", null);
                return;

            default:
                if (session.LoggedSystemSubtypes.TryAdd(subtype ?? "", 0))
                    _log($"[Claude] Session {session.Info.Id}: ignoring system subtype '{subtype}' (logged once per session)", null);
                return;
        }
    }

    // Compaction is part of a running turn, not a terminal status. As for Codex, it is one
    // ContextCompaction tool pair: opened by status "compacting" so clients show the long silent
    // pause immediately, settled by compact_boundary (which carries the token counts), a failed
    // compact_result, or the end of the turn. The CLI repeats "compacting" while it works and also
    // sends status null/"requesting" outside compaction, so only those signals open or settle.
    internal const string ContextCompactionToolName = "ContextCompaction";

    private void HandleCompactionStatus(JsonElement root, ManagedSession session)
    {
        var status = root.TryGetProperty("status", out var statusElement)
            && statusElement.ValueKind == JsonValueKind.String ? statusElement.GetString() : null;
        if (status == "compacting")
        {
            OpenCompaction(session);
            return;
        }

        var result = root.TryGetProperty("compact_result", out var resultElement)
            && resultElement.ValueKind == JsonValueKind.String ? resultElement.GetString() : null;
        var error = root.TryGetProperty("compact_error", out var errorElement)
            && errorElement.ValueKind == JsonValueKind.String ? errorElement.GetString() : null;
        // Success is settled by the compact_boundary that follows it, which carries the token counts.
        if (result == "failed" || !string.IsNullOrWhiteSpace(error))
            SettleCompaction(session, string.IsNullOrWhiteSpace(error)
                ? "Context compaction failed"
                : $"Context compaction failed: {error}");
    }

    private void HandleCompactBoundary(JsonElement root, ManagedSession session)
    {
        // A boundary without a preceding "compacting" (a CLI that does not report it) still
        // shows as one settled pair.
        OpenCompaction(session);
        SettleCompaction(session, DescribeCompactBoundary(root));
    }

    internal static string DescribeCompactBoundary(JsonElement root)
    {
        if (!root.TryGetProperty("compact_metadata", out var metadata) || metadata.ValueKind != JsonValueKind.Object)
            return "Context compacted";
        var trigger = metadata.TryGetProperty("trigger", out var t) && t.ValueKind == JsonValueKind.String
            ? t.GetString() : null;
        long? pre = metadata.TryGetProperty("pre_tokens", out var p) && p.TryGetInt64(out var preTokens)
            ? preTokens : null;
        long? post = metadata.TryGetProperty("post_tokens", out var q) && q.TryGetInt64(out var postTokens)
            ? postTokens : null;
        var text = string.IsNullOrWhiteSpace(trigger) ? "Context compacted" : $"Context compacted ({trigger})";
        var culture = System.Globalization.CultureInfo.InvariantCulture;
        if (pre is { } before && post is { } after)
            text += string.Format(culture, ": {0:N0} -> {1:N0} tokens", before, after);
        else if (pre is { } only)
            text += string.Format(culture, ": {0:N0} tokens before", only);
        return text;
    }

    private void OpenCompaction(ManagedSession session)
    {
        if (session.OpenCompaction is not null) return;
        var evt = new ClaudeStreamEvent
        {
            Type = "tool_use",
            ToolName = ContextCompactionToolName,
            MessageId = "compaction-" + Guid.NewGuid().ToString("N"),
        };
        Publish(session, evt);
        session.OpenCompaction = (evt.MessageId!, evt.MessageUid);
    }

    private void SettleCompaction(ManagedSession session, string content)
    {
        if (session.OpenCompaction is not { } open) return;
        session.OpenCompaction = null;
        Deliver(session, new ClaudeStreamEvent
        {
            Type = "tool_result",
            Content = content,
            ToolResult = content,
            MessageId = open.ToolUseId,
            MessageUid = open.TurnUid,
        });
    }

    private void ApplyBackgroundChange(ManagedSession session, ClaudeBackgroundTaskTracker.Change change)
    {
        foreach (var task in change.Records)
            EmitTaskRecord(session, task);
        if (change.Kind == ClaudeBackgroundTaskTracker.ChangeKind.None)
            return;

        session.Info.BackgroundTasks = session.BackgroundTasks.Running;
        if (change.Kind == ClaudeBackgroundTaskTracker.ChangeKind.Lifecycle)
        {
            // Recovery reads the running set from the row, so persist every lifecycle change.
            PersistSessionRecord(session.Info);
            PublishBackgroundTasks(session, immediate: true);
        }
        else
        {
            PublishBackgroundTasks(session, immediate: false);
        }
    }

    /// <summary>
    /// One durable `task` record per start and terminal transition, attributed to the tool call
    /// that launched the task and the turn that made that call. Stamped and broadcast like any
    /// record; clients fold it into the launching square instead of rendering it as a part.
    /// </summary>
    private void EmitTaskRecord(ManagedSession session, BackgroundTaskInfo task)
    {
        var input = ClaudeBackgroundTaskTracker.RecordInput(task);
        var evt = new ClaudeStreamEvent
        {
            Type = "task",
            ToolInput = input,
            MessageId = task.TaskId,
            MessageUid = task.MessageUid,
            ParentToolUseId = task.ParentToolUseId,
        };
        StreamEvent?.Invoke(session.Info.Id, evt);
        session.MessageHistory.Add(evt);
        TrimHistory(session);
        PersistMessage(session.Info.Id, "system", "task", null, null, input, null, task.TaskId,
            messageUid: task.MessageUid, parentToolUseId: task.ParentToolUseId);
    }

    private void PublishBackgroundTasks(ManagedSession session, bool immediate)
    {
        TimeSpan? delay = null;
        var publish = false;
        lock (session.BackgroundPublishGate)
        {
            var now = DateTimeOffset.UtcNow;
            var elapsed = now - session.LastBackgroundPublish;
            if (immediate || elapsed >= BackgroundProgressInterval)
            {
                session.LastBackgroundPublish = now;
                publish = true;
            }
            else if (!session.BackgroundPublishScheduled)
            {
                session.BackgroundPublishScheduled = true;
                delay = BackgroundProgressInterval - elapsed;
            }
        }

        if (publish)
            SessionUpdated?.Invoke(session.Info);
        else if (delay is { } wait)
            _ = PublishBackgroundTasksLaterAsync(session, wait);
    }

    private async Task PublishBackgroundTasksLaterAsync(ManagedSession session, TimeSpan delay)
    {
        try { await Task.Delay(delay, session.Cts.Token); }
        catch (OperationCanceledException) { return; }
        lock (session.BackgroundPublishGate)
        {
            session.BackgroundPublishScheduled = false;
            session.LastBackgroundPublish = DateTimeOffset.UtcNow;
        }
        if (_sessions.TryGetValue(session.Info.Id, out var current) && ReferenceEquals(current, session))
            SessionUpdated?.Invoke(session.Info);
    }

    /// <summary>
    /// The CLI process is gone (exit, stop, kill, forced restart): its background tasks died with
    /// it. Record each one as lost so no square stays running forever, and clear the live list.
    /// </summary>
    private void SettleLostBackgroundTasks(ManagedSession session)
    {
        foreach (var pending in session.PendingControlRequests.Keys)
            if (session.PendingControlRequests.TryRemove(pending, out var response))
                response.TrySetResult(ProcessEndedControlError);

        var lost = session.BackgroundTasks.MarkAllLost(DateTimeOffset.UtcNow);
        foreach (var task in lost)
            EmitTaskRecord(session, task);
        session.WakeTasks.Clear();
        if (session.Info.BackgroundTasks.Count == 0 && lost.Count == 0)
            return;
        session.Info.BackgroundTasks = [];
        if (lost.Count > 0)
            _log($"[Claude] Session {session.Info.Id}: {lost.Count} background task(s) lost with the process", null);
        PersistSessionRecord(session.Info);
    }

    private void HandleControlResponse(JsonElement root, ManagedSession session)
    {
        if (root.TryGetProperty("response", out var resp))
        {
            var subtype = resp.TryGetProperty("subtype", out var sub) ? sub.GetString() : null;
            var reqId = resp.TryGetProperty("request_id", out var rid) ? rid.GetString() : null;

            string? error = null;
            if (subtype == "error")
            {
                error = resp.TryGetProperty("error", out var e) ? e.GetString() ?? "unknown" : "unknown";
                _log($"[Claude] Control request {reqId} failed: {error}", null);
            }
            else
            {
                _log($"[Claude] Control request {reqId} succeeded", null);
            }
            if (reqId is not null && session.PendingControlRequests.TryRemove(reqId, out var pending))
                pending.TrySetResult(error);
        }
    }

    // The only tool whose ask we can actually satisfy: it is a request for data, not a
    // safety gate, and the answer rides back in the tool's own input. Everything else that
    // reaches the ask path is a genuine permission prompt with no UI behind it.
    private const string AskUserQuestionTool = "AskUserQuestion";

    // A parked question holds a turn open, so this is sized for "a human wandered off",
    // not for "how long does answering take". On expiry we deny explicitly rather than
    // leaving the CLI blocked on a promise that never settles -- silence is what made the
    // original bug invisible.
    internal TimeSpan QuestionAnswerTimeout { get; set; } = TimeSpan.FromMinutes(30);

    private void HandleInboundControlRequest(JsonElement root, ManagedSession session)
    {
        var requestId = root.TryGetProperty("request_id", out var rid) ? rid.GetString() : null;
        if (string.IsNullOrEmpty(requestId))
        {
            _log($"[Claude] Inbound control_request without request_id on session {session.Info.Id}, ignoring", null);
            return;
        }

        if (!root.TryGetProperty("request", out var request))
        {
            RespondControlError(session, requestId, "control_request had no 'request' payload");
            return;
        }

        var subtype = request.TryGetProperty("subtype", out var st) ? st.GetString() : null;
        if (subtype != "can_use_tool")
        {
            // request_user_dialog, elicitation, hook_callback, ... Nothing here can service
            // them, and an unanswered request wedges the turn, so refuse in the shape the
            // CLI's own bridge uses for requests it cannot handle.
            _log($"[Claude] Unsupported inbound control_request '{subtype}' on session {session.Info.Id}, refusing", null);
            RespondControlError(session, requestId, $"RedCompute does not implement control_request subtype '{subtype}'");
            return;
        }

        var toolName = request.TryGetProperty("tool_name", out var tn) ? tn.GetString() : null;
        if (toolName != AskUserQuestionTool)
        {
            // A genuine permission ask: under ask (acceptEdits) and auto anything the CLI will not
            // allow by itself, under plan ExitPlanMode. It becomes a permission card a person
            // answers, or, with nobody watching, an immediate denial.
            HandlePermissionRequest(session, requestId, toolName ?? "unknown", request);
            return;
        }

        var input = request.TryGetProperty("input", out var inp)
            ? inp.Clone()   // the JsonDocument is disposed when ParseStreamLine returns
            : default;
        var toolUseId = request.TryGetProperty("tool_use_id", out var tui) ? tui.GetString() : null;

        var askedAt = DateTimeOffset.UtcNow;
        var pending = new PendingQuestion
        {
            RequestId = requestId,
            ToolUseId = toolUseId,
            Input = input,
            AskedAt = askedAt,
            ExpiresAt = askedAt + QuestionAnswerTimeout,
        };
        pending.ExpiryTimer = new Timer(_ => ExpireQuestion(session, requestId), null,
            QuestionAnswerTimeout, Timeout.InfiniteTimeSpan);

        if (!session.PendingQuestions.TryAdd(requestId, pending))
        {
            pending.ExpiryTimer.Dispose();
            _log($"[Claude] Duplicate question request {requestId} on session {session.Info.Id}, ignoring", null);
            return;
        }

        _log($"[Claude] Session {session.Info.Id} parked on a question (request {requestId})", null);

        // Emitted directly rather than returned from ParseStreamLine: this is transient
        // control state, not conversation. The AskUserQuestion tool_use block was already
        // streamed and persisted just before this, so a reload still renders the question
        // card -- what a reload cannot replay is a live request_id, which is exactly why
        // this event is not persisted.
        StreamEvent?.Invoke(session.Info.Id, new ClaudeStreamEvent
        {
            Type = "question",
            RequestId = requestId,
            ToolName = AskUserQuestionTool,
            ToolInput = input.ValueKind == JsonValueKind.Undefined ? null : input.ToString(),
            MessageId = toolUseId,
        });
        // The turn's work state is now awaiting_answer. Status stays Active, so without this no
        // session.updated would tell clients until some unrelated change.
        SessionUpdated?.Invoke(session.Info);
    }

    /// <summary>
    /// Parks one permission ask (CLI 2.1.318 <c>can_use_tool</c>, captured in
    /// <c>tests/RedCompute.App.Tests/Fixtures/claude-permissions/</c>) as a pending entry of kind
    /// permission. An unattended session denies at once and never parks: there is nobody to
    /// answer, and a parked ask would hold the turn until it expired. A call the person already
    /// allowed for this session (a RedCompute rule, when the CLI offered no suggestion) runs.
    /// </summary>
    private void HandlePermissionRequest(ManagedSession session, string requestId, string toolName, JsonElement request)
    {
        var input = request.TryGetProperty("input", out var inp) && inp.ValueKind == JsonValueKind.Object
            ? inp.Clone() : EmptyJsonObject;
        var toolUseId = request.TryGetProperty("tool_use_id", out var tui) && tui.ValueKind == JsonValueKind.String
            ? tui.GetString() : null;
        var suggestions = request.TryGetProperty("permission_suggestions", out var ps) && ps.ValueKind == JsonValueKind.Array
            && ps.GetArrayLength() > 0 ? ps.Clone() : (JsonElement?)null;
        var permission = new Core.Sessions.PendingPermissionRequest
        {
            ToolName = toolName,
            ToolUseId = toolUseId,
            Summary = PermissionSummary(toolName, request, input),
            Description = PermissionDescription(request, input),
            Input = input,
            Cwd = session.Info.ProjectPath,
        };

        if (session.Info.Access.Unattended)
        {
            var reason = Core.Sessions.SessionAccessNotices.UnattendedDenial(session.Info.Access.AccessLevel, $"'{toolName}'");
            _log($"[Claude] Session {session.Info.Id} is unattended; denied '{toolName}' ({permission.Summary})", null);
            RespondPermissionDeny(session, requestId, reason);
            // Live notice; the denial message is also the tool result the transcript keeps.
            StreamEvent?.Invoke(session.Info.Id, new ClaudeStreamEvent
            {
                Type = "permission_denied",
                Kind = Core.Sessions.PendingSessionQuestion.PermissionKind,
                RequestId = requestId,
                ToolName = toolName,
                ToolInput = input.ToString(),
                MessageId = toolUseId,
                Content = reason,
                Permission = permission,
            });
            return;
        }

        if (session.AllowRules.Any(rule => rule.Matches(toolName, input)))
        {
            _log($"[Claude] Session {session.Info.Id} allowed '{toolName}' under a rule the person set for this session", null);
            RespondPermissionAllow(session, requestId, input, null);
            return;
        }

        var askedAt = DateTimeOffset.UtcNow;
        var pending = new PendingQuestion
        {
            RequestId = requestId,
            ToolUseId = toolUseId,
            Input = input,
            AskedAt = askedAt,
            ExpiresAt = askedAt + QuestionAnswerTimeout,
            ToolName = toolName,
            Permission = permission,
            Suggestions = suggestions,
        };
        pending.ExpiryTimer = new Timer(_ => ExpireQuestion(session, requestId), null,
            QuestionAnswerTimeout, Timeout.InfiniteTimeSpan);
        if (!session.PendingQuestions.TryAdd(requestId, pending))
        {
            pending.ExpiryTimer.Dispose();
            _log($"[Claude] Duplicate permission request {requestId} on session {session.Info.Id}, ignoring", null);
            return;
        }

        _log($"[Claude] Session {session.Info.Id} parked on a permission for '{toolName}' (request {requestId})", null);
        StreamEvent?.Invoke(session.Info.Id, new ClaudeStreamEvent
        {
            Type = "question",
            Kind = Core.Sessions.PendingSessionQuestion.PermissionKind,
            RequestId = requestId,
            ToolName = toolName,
            ToolInput = input.ToString(),
            MessageId = toolUseId,
            Permission = permission,
        });
        SessionUpdated?.Invoke(session.Info);
    }

    /// <summary>One line for the card: the CLI's own description, else the command, path or URL.</summary>
    internal static string PermissionSummary(string toolName, JsonElement request, JsonElement input)
    {
        static string? Text(JsonElement element, string name)
            => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
                && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
                ? value.GetString() : null;
        return PermissionSummaries.Of(
            Text(input, "command") ?? Text(input, "file_path") ?? Text(input, "notebook_path") ?? Text(input, "url")
                ?? Text(input, "path") ?? Text(input, "pattern"),
            Text(request, "description") ?? Text(input, "description"),
            Text(request, "decision_reason") ?? $"Use {toolName}").Summary;
    }

    /// <summary>The model's own words for a call whose card shows the exact action, else null.</summary>
    internal static string? PermissionDescription(JsonElement request, JsonElement input)
    {
        static string? Text(JsonElement element, string name)
            => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
                && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
                ? value.GetString() : null;
        return PermissionSummaries.Of(
            Text(input, "command") ?? Text(input, "file_path") ?? Text(input, "notebook_path") ?? Text(input, "url")
                ?? Text(input, "path") ?? Text(input, "pattern"),
            Text(request, "description") ?? Text(input, "description"), "").Description;
    }

    private void HandleControlCancelRequest(JsonElement root, ManagedSession session)
    {
        var requestId = root.TryGetProperty("request_id", out var rid) ? rid.GetString() : null;
        if (requestId == null || !session.PendingQuestions.TryRemove(requestId, out var pending))
            return;

        pending.Dispose();
        _log($"[Claude] Question {requestId} cancelled by the CLI on session {session.Info.Id}", null);

        // No control_response: the CLI has already abandoned the request. This only tells
        // clients to drop the card.
        StreamEvent?.Invoke(session.Info.Id, new ClaudeStreamEvent
        {
            Type = "question_resolved",
            RequestId = requestId,
            Content = "cancelled",
        });
        SessionUpdated?.Invoke(session.Info);
    }

    private void ExpireQuestion(ManagedSession session, string requestId)
    {
        if (!session.PendingQuestions.TryRemove(requestId, out var pending))
            return;
        pending.Dispose();

        _log($"[Claude] {(pending.Permission is null ? "Question" : "Permission")} {requestId} on session {session.Info.Id} " +
             $"went unanswered for {QuestionAnswerTimeout.TotalMinutes:0} minutes, denying", null);

        RespondPermissionDeny(session, requestId, pending.Permission is null
            ? "The question timed out with no answer from the user. Proceed with your best judgement " +
              "and state the assumption you made."
            : Core.Sessions.SessionAccessNotices.ExpiredDenial(session.Info.Access.AccessLevel, $"'{pending.ToolName}'",
                QuestionAnswerTimeout));

        if (pending.Permission is not null)
            EmitPermissionResolved(session, requestId, Core.Sessions.SessionPermissionDecisions.Deny, "timeout");
        else
            StreamEvent?.Invoke(session.Info.Id, new ClaudeStreamEvent
            {
                Type = "question_resolved",
                RequestId = requestId,
                Content = "timeout",
            });
        SessionUpdated?.Invoke(session.Info);
    }

    public Core.Sessions.QuestionAnswerResult SubmitQuestionAnswer(string sessionId, Core.Sessions.SessionQuestionAnswer answer)
    {
        if (!_sessions.TryGetValue(sessionId, out var session))
            return Core.Sessions.QuestionAnswerResult.SessionNotFound;

        if (session.PendingQuestions.TryGetValue(answer.RequestId, out var peek)
            && (peek.Permission is null) != (answer.Decision is null))
            // A decision answers only a permission and a permission only a decision. Refused
            // before the claim, so the entry stays parked for a correct answer.
            return Core.Sessions.QuestionAnswerResult.InvalidDecision;

        if (!session.PendingQuestions.TryRemove(answer.RequestId, out var pending))
            return Core.Sessions.QuestionAnswerResult.RequestNotFound;

        pending.Dispose();

        if (pending.Permission is not null)
            return AnswerPermission(session, pending, answer.Decision!, answer.DeclineReason);

        try
        {
            if (answer.Decline)
            {
                RespondPermissionDeny(session, answer.RequestId,
                    string.IsNullOrWhiteSpace(answer.DeclineReason)
                        ? "The user dismissed the question without answering. Proceed with your best " +
                          "judgement and state the assumption you made."
                        : answer.DeclineReason);
                EmitQuestionResolved(session, answer.RequestId, "declined");
                SessionUpdated?.Invoke(session.Info);
                return Core.Sessions.QuestionAnswerResult.Declined;
            }

            // updatedInput is validated against the tool's full input schema, so the
            // questions array has to be echoed back verbatim alongside the answers.
            var updatedInput = new Dictionary<string, object?>();
            if (pending.Input.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in pending.Input.EnumerateObject())
                    updatedInput[prop.Name] = prop.Value;
            }

            var answers = BuildAnswerMap(pending, answer);
            if (answers.Count > 0)
                updatedInput["answers"] = answers;

            // `response` is the "user typed their own thing instead of choosing" channel and
            // it OUTRANKS answers -- when set, the tool result is just "The user responded:
            // <text>" and the selections are dropped (verified against the CLI). So only send
            // it when there is nothing structured to send. Free text that accompanies a
            // choice belongs in that choice's label, comma-joined, not here.
            else if (!string.IsNullOrWhiteSpace(answer.Response))
                updatedInput["response"] = answer.Response;

            WriteControlLine(session, new
            {
                type = "control_response",
                response = new
                {
                    subtype = "success",
                    request_id = answer.RequestId,
                    response = new { behavior = "allow", updatedInput }
                }
            });

            _log($"[Claude] Answered question {answer.RequestId} on session {sessionId}", null);
            EmitQuestionResolved(session, answer.RequestId, "answered");
            SessionUpdated?.Invoke(session.Info);
            return Core.Sessions.QuestionAnswerResult.Answered;
        }
        catch (Exception ex)
        {
            _log($"[Claude] Failed to answer question {answer.RequestId} on {sessionId}: {ex.Message}", null);
            return Core.Sessions.QuestionAnswerResult.Error;
        }
    }

    /// <summary>
    /// Writes the person's decision on a parked permission. allow_session uses the CLI's own
    /// permission_suggestions, moved to the session destination so nothing is written to the
    /// project's settings files (verified on 2.1.318: the repeat runs without a second ask). With
    /// no suggestion, RedCompute remembers the exact tool, and for Bash the exact command.
    /// Approving ExitPlanMode leaves plan for the level the session entered plan from.
    /// </summary>
    private Core.Sessions.QuestionAnswerResult AnswerPermission(ManagedSession session, PendingQuestion pending, string decision,
        string? denyReason = null)
    {
        var sessionId = session.Info.Id;
        try
        {
            if (decision == Core.Sessions.SessionPermissionDecisions.Deny)
            {
                RespondPermissionDeny(session, pending.RequestId, string.IsNullOrWhiteSpace(denyReason)
                    ? $"The user denied '{pending.ToolName}'. Do not retry it; continue another way or ask the user."
                    : denyReason);
                EmitPermissionResolved(session, pending.RequestId, decision);
                SessionUpdated?.Invoke(session.Info);
                _log($"[Claude] Permission {pending.RequestId} for '{pending.ToolName}' denied on session {sessionId}", null);
                return Core.Sessions.QuestionAnswerResult.Denied;
            }

            List<object>? updatedPermissions = null;
            // Leaving plan is approved one plan at a time: remembered for the session it would let
            // a later return to plan (which anyone may ask for) slip back out without a person.
            if (decision == Core.Sessions.SessionPermissionDecisions.AllowSession && pending.ToolName != ExitPlanModeTool)
            {
                if (pending.Suggestions is { } suggestions)
                    updatedPermissions = [.. SessionScopedSuggestions(suggestions)];
                else
                    session.AllowRules.Add(SessionAllowRule.For(pending.ToolName!, pending.Input));
            }

            var leavesPlan = pending.ToolName == ExitPlanModeTool
                && session.Info.Access.AccessLevel == Core.Sessions.SessionAccessLevels.Plan;
            var afterPlan = leavesPlan
                ? session.Info.Access with
                {
                    // Within the session's cap, which may have been lowered while it was in plan.
                    AccessLevel = session.Info.Access.MaxAccessLevel is { } cap
                        ? Core.Sessions.SessionAccessLevels.Lower(session.Info.Access.AccessLevelBeforePlan ?? Core.Sessions.SessionAccessLevels.Ask, cap)
                        : session.Info.Access.AccessLevelBeforePlan ?? Core.Sessions.SessionAccessLevels.Ask,
                    AccessLevelBeforePlan = null,
                    // A person approved leaving plan: the level is theirs, no longer the default's.
                    AccessLevelSource = Core.Sessions.SessionAccessLevelSources.Explicit,
                }
                : null;
            if (afterPlan is not null)
                (updatedPermissions ??= []).Add(new
                {
                    type = "setMode", mode = PermissionModeFor(afterPlan.AccessLevel), destination = "session",
                });

            RespondPermissionAllow(session, pending.RequestId, pending.Input, updatedPermissions);
            // Recorded only once the CLI confirms the mode; awaited off this call because the
            // confirmation can take a moment and the approval itself is already answered.
            if (afterPlan is not null) _ = LeavePlanAsync(sessionId, afterPlan);
            EmitPermissionResolved(session, pending.RequestId, decision);
            SessionUpdated?.Invoke(session.Info);
            _log($"[Claude] Permission {pending.RequestId} for '{pending.ToolName}' {decision} on session {sessionId}", null);
            return Core.Sessions.QuestionAnswerResult.Allowed;
        }
        catch (Exception ex)
        {
            _log($"[Claude] Failed to answer permission {pending.RequestId} on {sessionId}: {ex.Message}", null);
            return Core.Sessions.QuestionAnswerResult.Error;
        }
    }

    /// <summary>The CLI's suggestions, verbatim except that each applies to this session only.</summary>
    internal static IEnumerable<object> SessionScopedSuggestions(JsonElement suggestions)
    {
        foreach (var suggestion in suggestions.EnumerateArray())
        {
            // Only allow rules pass, kept to this session. A suggested setMode would change the
            // level behind RedCompute's back, and addDirectories widens where edits may go: neither
            // is what the person saw on the card.
            if (suggestion.ValueKind != JsonValueKind.Object
                || !suggestion.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String
                || type.GetString() != "addRules"
                || !suggestion.TryGetProperty("behavior", out var behavior) || behavior.ValueKind != JsonValueKind.String
                || behavior.GetString() != "allow")
                continue;
            var copy = new Dictionary<string, object?>();
            foreach (var property in suggestion.EnumerateObject())
                copy[property.Name] = property.Value;
            copy["destination"] = "session";
            yield return copy;
        }
    }

    private const string ExitPlanModeTool = "ExitPlanMode";

    /// <summary>
    /// An approved ExitPlanMode: the allow already carried <c>setMode</c>; this confirms the mode
    /// with <c>set_permission_mode</c> and records the level only when the CLI accepted it. A
    /// refusal keeps the session at plan (logged by <see cref="ApplyAccessStateAsync"/>).
    /// </summary>
    private async Task LeavePlanAsync(string sessionId, Core.Sessions.SessionAccessState afterPlan)
    {
        try
        {
            await ApplyAccessStateAsync(sessionId, afterPlan);
        }
        catch (Exception ex)
        {
            _log($"[Claude] Session {sessionId} could not leave plan for {afterPlan.AccessLevel}: {ex.Message}", null);
        }
    }

    /// <summary>
    /// The level the person approved ExitPlanMode out of plan into, or any other live change.
    /// </summary>
    public Core.Sessions.SessionAccessState? GetAccessState(string sessionId)
    {
        if (_sessions.TryGetValue(sessionId, out var session)) return session.Info.Access;
        return _sessionStore.FindSession(sessionId) is { } record ? AccessOf(record) : null;
    }

    /// <summary>
    /// Persists an access state already authorized by RedCompute. Blocks until the CLI confirmed a
    /// live switch; prefer <see cref="ApplyAccessStateAsync"/>. Never call it on a session's stdout
    /// reader: the confirmation arrives through that reader.
    /// </summary>
    public bool SetAccessState(string sessionId, Core.Sessions.SessionAccessState state)
        => ApplyAccessStateAsync(sessionId, state).GetAwaiter().GetResult().Applied;

    /// <summary>
    /// Persists an access state already authorized by RedCompute. A live session switches with
    /// <c>set_permission_mode</c> and is recorded at the new level only once the CLI's
    /// control_response confirms it; a refusal (or no answer in time) keeps the level it had and
    /// returns the CLI's reason. A stopped session is persisted and launches at it next time.
    /// </summary>
    public async Task<Core.Sessions.SessionAccessApplyResult> ApplyAccessStateAsync(string sessionId,
        Core.Sessions.SessionAccessState state, CancellationToken ct = default)
    {
        if (_sessions.TryGetValue(sessionId, out var session))
        {
            await session.AccessGate.WaitAsync(ct);
            try
            {
                var mode = PermissionModeFor(state.AccessLevel);
                if (mode != session.Info.PermissionMode
                    && await SendPermissionModeAsync(session, mode) is { } refusal)
                {
                    _log($"[Claude] Session {sessionId} stays at access level {session.Info.Access.AccessLevel ?? "unset"}: " +
                         $"the CLI did not switch to '{mode}' for {state.AccessLevel} ({refusal})", null);
                    return Core.Sessions.SessionAccessApplyResult.Refused(refusal);
                }
                session.Info.Access = state;
                session.Info.PermissionMode = mode;
                PersistSessionRecord(session.Info);
                SessionUpdated?.Invoke(session.Info);
                _log($"[Claude] Session {sessionId} access level {state.AccessLevel} (permission mode {mode})", null);
                return Core.Sessions.SessionAccessApplyResult.Done;
            }
            finally
            {
                session.AccessGate.Release();
            }
        }

        var record = _sessionStore.FindSession(sessionId);
        if (record is null) return Core.Sessions.SessionAccessApplyResult.NotApplied;
        record.AccessLevel = state.AccessLevel;
        record.MaxAccessLevel = state.MaxAccessLevel;
        record.AccessLevelBeforePlan = state.AccessLevelBeforePlan;
        record.Unattended = state.Unattended;
        record.AccessLevelSource = state.AccessLevelSource;
        _sessionStore.SaveSession(record);
        return Core.Sessions.SessionAccessApplyResult.Done;
    }

    /// <summary>
    /// The CLI reported a mode that is not its level's. Sets the level's mode once per mismatch per
    /// process, never again (each turn's init would otherwise retry a refusal forever). When the CLI
    /// does not switch, the session is recorded at the level its real mode stands for, as long as
    /// that is not higher than the level it held: a report can lower a level, never raise one.
    /// </summary>
    private async Task ReconcilePermissionModeAsync(ManagedSession session, string reportedMode)
    {
        try
        {
            await session.AccessGate.WaitAsync(session.Cts.Token);
        }
        catch (OperationCanceledException) { return; }
        try
        {
            var level = session.Info.Access.AccessLevel;
            var expectedMode = PermissionModeFor(level);
            // A confirmed change or a newer init got here first.
            if (session.Info.PermissionMode != reportedMode || reportedMode == expectedMode) return;
            if (!session.ReconciledModes.TryAdd($"{reportedMode}>{expectedMode}", 0)) return;

            _log($"[Claude] Session {session.Info.Id} reports permission mode '{reportedMode}' at access level " +
                 $"'{level ?? "unset"}'; setting '{expectedMode}' once", null);
            var refusal = await SendPermissionModeAsync(session, expectedMode);
            if (refusal is null)
            {
                session.Info.PermissionMode = expectedMode;
                PersistSessionRecord(session.Info);
                SessionUpdated?.Invoke(session.Info);
                return;
            }

            // Fail closed: a process never keeps running above the level the person set. A mode that
            // is not a known level cannot be shown to be at or below it, so it counts as above.
            var held = level ?? Core.Sessions.SessionAccessLevels.Ask;
            if (!Core.Sessions.SessionAccessLevels.TryFromPermissionMode(reportedMode, out var actual)
                || Core.Sessions.SessionAccessLevels.IsAbove(actual, held))
            {
                await StopAboveLevelAsync(session, reportedMode, held, refusal);
                return;
            }
            session.Info.Access = session.Info.Access with
            {
                AccessLevel = actual,
                AccessLevelBeforePlan = actual == Core.Sessions.SessionAccessLevels.Plan
                    ? (level == Core.Sessions.SessionAccessLevels.Plan ? session.Info.Access.AccessLevelBeforePlan : level)
                    : null,
            };
            PersistSessionRecord(session.Info);
            SessionUpdated?.Invoke(session.Info);
            _log($"[Claude] Session {session.Info.Id}: the CLI stays in '{reportedMode}' ({refusal}); " +
                 $"access level recorded as {actual} (was {level}) to match it", null);
        }
        catch (Exception ex)
        {
            _log($"[Claude] Session {session.Info.Id}: permission mode reconcile failed: {ex.Message}", null);
        }
        finally
        {
            session.AccessGate.Release();
        }
    }

    /// <summary>
    /// The CLI runs in a mode above the session's level and would not switch down: stop it, with
    /// one log line and a visible notice. The recorded level stays, so the next launch (the next
    /// message resumes it) runs at the level the person set.
    /// </summary>
    private async Task StopAboveLevelAsync(ManagedSession session, string reportedMode, string level, string refusal)
    {
        var id = session.Info.Id;
        _log($"[Claude] Session {id}: the CLI runs in '{reportedMode}', above access level {level}, and would not " +
             $"switch down ({refusal}); stopping it, the level stays {level}", null);
        StreamEvent?.Invoke(id, new ClaudeStreamEvent
        {
            Type = "error",
            Content = $"Stopped this session: Claude Code was running with more access than {LevelName(level)} " +
                      $"(mode '{reportedMode}') and would not switch down. {refusal} The session stays at " +
                      $"{LevelName(level)}; send a message to resume it there.",
        });
        await StopSession(id, Core.Sessions.SessionStopReasons.AboveAccessLevel);
    }

    private static string LevelName(string level) => level switch
    {
        Core.Sessions.SessionAccessLevels.Plan => "Plan",
        Core.Sessions.SessionAccessLevels.Ask => "Ask",
        Core.Sessions.SessionAccessLevels.Auto => "Auto",
        Core.Sessions.SessionAccessLevels.Full => "Full",
        _ => level,
    };

    /// <summary>How long a live switch waits for the CLI's control_response before it counts as refused.</summary>
    internal TimeSpan PermissionModeConfirmTimeout { get; set; } = TimeSpan.FromSeconds(10);

    private const string ProcessEndedControlError = "The Claude process ended";

    /// <summary>
    /// Writes <c>set_permission_mode</c> and waits for its control_response. Null when the CLI
    /// switched; otherwise a sentence a person can read, quoting the CLI's own error. Never await
    /// this on the session's stdout reader, which is what delivers the answer.
    /// </summary>
    private async Task<string?> SendPermissionModeAsync(ManagedSession session, string mode)
    {
        var requestId = $"perm-{++session.ControlRequestCounter}";
        var response = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.PendingControlRequests[requestId] = response;
        try
        {
            await WriteControlLineAsync(session, new
            {
                type = "control_request",
                request_id = requestId,
                request = new { subtype = "set_permission_mode", mode },
            });
        }
        catch (Exception ex)
        {
            session.PendingControlRequests.TryRemove(requestId, out _);
            _log($"[Claude] Failed to set permission mode for {session.Info.Id}: {ex.Message}", null);
            return $"Claude Code could not be reached ({ex.Message}).";
        }

        if (await Task.WhenAny(response.Task, Task.Delay(PermissionModeConfirmTimeout)) != response.Task)
        {
            // Left registered: a late success means the CLI switched after we gave up and recorded
            // nothing, so put it back to the mode of the level the session really holds.
            _ = response.Task.ContinueWith(late =>
            {
                if (late.Result is not null || !_sessions.TryGetValue(session.Info.Id, out var current)
                    || !ReferenceEquals(current, session)) return;
                var expected = PermissionModeFor(session.Info.Access.AccessLevel);
                if (expected == mode) return;
                _log($"[Claude] Session {session.Info.Id}: the CLI switched to '{mode}' after the change timed out; restoring '{expected}'", null);
                _ = SendPermissionModeAsync(session, expected);
            }, TaskScheduler.Default);
            return $"Claude Code did not confirm the change within {PermissionModeConfirmTimeout.TotalSeconds:0} seconds.";
        }

        return await response.Task switch
        {
            null => null,
            ProcessEndedControlError => "The Claude process ended before it confirmed the change.",
            var error => $"Claude Code said: \"{error}\"",
        };
    }

    /// <summary>
    /// Builds the tool's `answers` map, which is keyed by the exact question text. Explicit
    /// keys win; anything unmatched falls back to positional order against the questions we
    /// parked on, so a client that sends answers in order cannot mis-key them (a mis-keyed
    /// answer is not an error — it renders as "(no option selected)" to the model).
    /// </summary>
    private static Dictionary<string, string> BuildAnswerMap(PendingQuestion pending, Core.Sessions.SessionQuestionAnswer answer)
    {
        var questions = new List<string>();
        if (pending.Input.ValueKind == JsonValueKind.Object &&
            pending.Input.TryGetProperty("questions", out var qs) && qs.ValueKind == JsonValueKind.Array)
        {
            foreach (var q in qs.EnumerateArray())
            {
                if (q.TryGetProperty("question", out var qt) && qt.GetString() is { } text)
                    questions.Add(text);
            }
        }

        var map = new Dictionary<string, string>();

        if (answer.Answers is { Count: > 0 })
        {
            foreach (var (key, value) in answer.Answers)
            {
                // Keep a key we recognise; otherwise map it onto the first question still
                // unanswered so a near-miss on the question text still lands.
                var target = questions.Contains(key)
                    ? key
                    : questions.FirstOrDefault(q => !map.ContainsKey(q)) ?? key;
                map[target] = value;
            }
            return map;
        }

        if (answer.PositionalAnswers is { Count: > 0 })
        {
            for (var i = 0; i < answer.PositionalAnswers.Count && i < questions.Count; i++)
                map[questions[i]] = answer.PositionalAnswers[i];
        }

        return map;
    }

    /// <summary>
    /// Drops every parked question without answering. Called when the process is gone, so
    /// there is nothing left to write a control_response to — this only stops the expiry
    /// timers firing into a dead pipe half an hour later, and clears the clients' cards.
    /// </summary>
    private void DiscardPendingQuestions(ManagedSession session, string outcome)
    {
        foreach (var requestId in session.PendingQuestions.Keys)
        {
            if (!session.PendingQuestions.TryRemove(requestId, out var pending))
                continue;
            pending.Dispose();
            EmitQuestionResolved(session, requestId, outcome);
        }
    }

    private void EmitQuestionResolved(ManagedSession session, string requestId, string outcome) =>
        StreamEvent?.Invoke(session.Info.Id, new ClaudeStreamEvent
        {
            Type = "question_resolved",
            RequestId = requestId,
            Content = outcome,
        });

    /// <summary>A permission resolved by a decision: the decision in both decision and content.</summary>
    private void EmitPermissionResolved(ManagedSession session, string requestId, string decision, string? content = null) =>
        StreamEvent?.Invoke(session.Info.Id, new ClaudeStreamEvent
        {
            Type = "question_resolved",
            Kind = Core.Sessions.PendingSessionQuestion.PermissionKind,
            RequestId = requestId,
            Content = content ?? decision,
            Decision = decision,
        });

    // updatedInput is required by the control protocol on allow; the tool runs with it verbatim.
    private void RespondPermissionAllow(ManagedSession session, string requestId, JsonElement input, List<object>? updatedPermissions)
        => WriteControlLine(session, new
        {
            type = "control_response",
            response = new
            {
                subtype = "success",
                request_id = requestId,
                response = updatedPermissions is { Count: > 0 }
                    ? new Dictionary<string, object?> { ["behavior"] = "allow", ["updatedInput"] = input, ["updatedPermissions"] = updatedPermissions }
                    : new Dictionary<string, object?> { ["behavior"] = "allow", ["updatedInput"] = input },
            },
        });

    // Outer subtype stays "success" -- it describes the round trip, not the verdict. The
    // message becomes the tool_result the model reads, with is_error set.
    private void RespondPermissionDeny(ManagedSession session, string requestId, string message)
        => TryWriteControlResponse(session, requestId, new
        {
            type = "control_response",
            response = new
            {
                subtype = "success",
                request_id = requestId,
                response = new { behavior = "deny", message }
            }
        });

    private void RespondControlError(ManagedSession session, string requestId, string error)
        => TryWriteControlResponse(session, requestId, new
        {
            type = "control_response",
            response = new { subtype = "error", request_id = requestId, error }
        });

    private void TryWriteControlResponse(ManagedSession session, string requestId, object message)
    {
        try
        {
            WriteControlLine(session, message);
        }
        catch (Exception ex)
        {
            _log($"[Claude] Failed to respond to control_request {requestId} on {session.Info.Id}: {ex.Message}", null);
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
                        MessageId = toolId,
                        ParentToolUseId = ParentToolUseId(root),
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
                IsPartial = false,
                ParentToolUseId = ParentToolUseId(root),
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
                IsPartial = true,
                BlockIndex = evt.TryGetProperty("index", out var index) && index.TryGetInt32(out var i) ? i : 0,
                ParentToolUseId = ParentToolUseId(root),
            },
            "thinking_delta" => new ClaudeStreamEvent
            {
                Type = "thinking",
                Content = delta.TryGetProperty("thinking", out var th) ? th.GetString() : null,
                IsPartial = true,
                BlockIndex = evt.TryGetProperty("index", out var index) && index.TryGetInt32(out var i) ? i : 0,
                ParentToolUseId = ParentToolUseId(root),
            },
            _ => null
        };
    }

    /// <summary>
    /// Records how the turn ended (SessionTurnOutcomes) before the Idle that publishes it, so the
    /// turn's observers read the outcome with the status. A success that raced an interrupt is
    /// completed: nothing was aborted.
    /// </summary>
    private static void MarkTurnEnded(ManagedSession session, string outcome)
    {
        session.Info.LastTurnOutcome = outcome;
        session.Info.LastTurnEndedAt = DateTimeOffset.UtcNow;
    }

    private ClaudeStreamEvent? ParseResultEvent(JsonElement root, ManagedSession session)
    {
        // Buffered stdout can deliver a result after StopSession, ForceKill or a forced restart
        // already settled this session. Their terminal state is authoritative: writing Idle
        // here would persist a live-looking row with no process behind it.
        if (session.Cts.IsCancellationRequested || session.RestartPending
            || !_sessions.TryGetValue(session.Info.Id, out var current) || !ReferenceEquals(current, session))
        {
            SettleTurnContent(session);
            DiscardPendingSteers(session);
            session.InterruptCompletion?.TrySetResult(true);
            return null;
        }

        var subtype = root.TryGetProperty("subtype", out var sub) ? sub.GetString() : null;

        // A turn the CLI started by itself after a background task finished. result.origin is the
        // CLI's confirmation; the turn's own records were tagged when it opened.
        var wakeResult = IsTaskNotificationResult(root);
        var turnOrigin = wakeResult ? ConfirmWakeTurnOrigin(root, session) : null;
        session.WakeTurnOrigin = null;
        session.PendingLiveTurnOrigin = null;
        // DurableTurnOrigin stays: the turn's final text settles below, and its uid matches
        // nothing else.
        if (!wakeResult)
        {
            session.UserInputUnconsumed = false;
            // A notification that landed during a user turn was folded into it.
            session.WakeTasks.Clear();
        }

        var terminalError = ReadTerminalResultError(root, subtype);
        if (terminalError == null && subtype == "success")
        {
            // Persist the answer before Idle is published: the settlement callback reads the
            // transcript as soon as SessionUpdated reports Idle.
            SettleTurnContent(session);

            // Turn finished normally, possibly racing a just-sent interrupt that
            // never got the chance to abort anything -- still a clean, safe-to-write
            // outcome, so resolve the pending interrupt wait the same as below.
            session.InterruptPending = false;
            session.InterruptCompletion?.TrySetResult(true);
            session.InterruptCompletion = null;
            if (!session.PendingSteers.IsEmpty)
            {
                // Claude reports the old result before starting a buffered stdin command. The
                // exact replay is the acceptance event; remain Active so after-current delivery
                // cannot race the queued turn and clients never observe an Idle flicker.
                session.Info.Status = SessionStatus.Active;
                session.Info.StopReason = null;
                if (root.TryGetProperty("total_cost_usd", out var boundaryCost))
                    session.Info.CostUsd = boundaryCost.GetDouble();
                ParseTokenUsage(root, session);
                SyncTitleFromClaudeSession(session);
                PersistSessionRecord(session.Info);
                SessionUpdated?.Invoke(session.Info);
                StartSteerBoundaryWatchdogs(session);
                return null;
            }
            if (wakeResult && session.UserInputUnconsumed)
            {
                // The CLI ran a background-task wake turn ahead of a user message it has not taken
                // yet (a resumed CLI does this for every task the previous process lost). That
                // message's turn is still to come: Idle now would invite another send into it.
                session.CurrentAssistantUid = null;
                if (root.TryGetProperty("total_cost_usd", out var wakeCost))
                    session.Info.CostUsd = wakeCost.GetDouble();
                ParseTokenUsage(root, session);
                PersistSessionRecord(session.Info);
                SessionUpdated?.Invoke(session.Info);
                return null;
            }
            session.Info.Status = SessionStatus.Idle;
            session.Info.StopReason = "completed";
            MarkTurnEnded(session, Core.Sessions.SessionTurnOutcomes.Completed);

            if (root.TryGetProperty("total_cost_usd", out var cost))
                session.Info.CostUsd = cost.GetDouble();
            ParseTokenUsage(root, session);

            SyncTitleFromClaudeSession(session);
            PersistSessionRecord(session.Info);
            SessionUpdated?.Invoke(session.Info);

            return new ClaudeStreamEvent { Type = "status", Content = "idle", TurnOrigin = turnOrigin };
        }

        if (terminalError != null)
        {
            SettleTurnContent(session);
            var error = terminalError;
            _log($"[Claude] Session {session.Info.Id} result error: {error}", null);

            var isUsageLimit = IsUsageLimitError(error);
            if (isUsageLimit)
            {
                session.Info.StopReason = "usage_limit";
                _log($"[Claude] Session {session.Info.Id} hit usage limit", null);
            }
            else if (IsAuthenticationError(root, error))
            {
                session.Info.StopReason = "authentication_failed";
            }

            if (!session.PendingSteers.IsEmpty)
            {
                // An interrupt can abort the old turn while explicitly leaving this stdin command
                // queued. The scratch sequence proves Claude then replays and runs that command.
                session.InterruptPending = false;
                session.InterruptCompletion?.TrySetResult(true);
                session.InterruptCompletion = null;
                session.Info.Status = SessionStatus.Active;
                session.Info.StopReason = null;
                if (root.TryGetProperty("total_cost_usd", out var boundaryCost))
                    session.Info.CostUsd = boundaryCost.GetDouble();
                ParseTokenUsage(root, session);
                PersistSessionRecord(session.Info);
                SessionUpdated?.Invoke(session.Info);
                StartSteerBoundaryWatchdogs(session);
                return null;
            }

            if (session.InterruptPending)
            {
                session.InterruptPending = false;
                session.InterruptCompletion?.TrySetResult(true);
                session.InterruptCompletion = null;
                session.Info.Status = SessionStatus.Idle;
                MarkTurnEnded(session, Core.Sessions.SessionTurnOutcomes.Interrupted);

                if (root.TryGetProperty("total_cost_usd", out var intCost))
                    session.Info.CostUsd = intCost.GetDouble();
                ParseTokenUsage(root, session);

                PersistSessionRecord(session.Info);
                SessionUpdated?.Invoke(session.Info);

                // Graceful, acknowledged interrupt: the CLI actually aborted the
                // turn and the process is idle and alive. Distinct from "killed",
                // which the caller should treat as unsafe to write into.
                return new ClaudeStreamEvent { Type = "status", Content = "interrupted" };
            }

            session.Info.Status = SessionStatus.Idle;
            MarkTurnEnded(session, Core.Sessions.SessionTurnOutcomes.Failed);
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

    private static bool IsTaskNotificationResult(JsonElement root)
        => root.TryGetProperty("origin", out var origin) && origin.ValueKind == JsonValueKind.Object
            && origin.TryGetProperty("kind", out var kind) && kind.ValueKind == JsonValueKind.String
            && kind.GetString() == "task-notification";

    /// <summary>
    /// The task a wake turn reported, by the run id the CLI puts on result.origin, else the task
    /// predicted when the turn opened. Tasks of a previous process (whose stopped notifications a
    /// resumed CLI replays) are not this session's to report.
    /// </summary>
    private static TurnOrigin? ConfirmWakeTurnOrigin(JsonElement root, ManagedSession session)
    {
        var runId = root.GetProperty("origin").TryGetProperty("runId", out var run)
            && run.ValueKind == JsonValueKind.String ? run.GetString() : null;
        var taskId = session.BackgroundTasks.TaskIdForRun(runId) ?? session.WakeTurnOrigin?.TaskId;
        return taskId is not null && session.BackgroundTasks.Knows(taskId)
            ? new TurnOrigin { Kind = TurnOrigin.BackgroundTask, TaskId = taskId }
            : null;
    }

    internal static string? ReadTerminalResultError(JsonElement root, string? subtype = null)
    {
        subtype ??= root.TryGetProperty("subtype", out var sub) ? sub.GetString() : null;
        var isError = subtype == "error"
            || (root.TryGetProperty("is_error", out var flag)
                && flag.ValueKind is JsonValueKind.True or JsonValueKind.False
                && flag.GetBoolean())
            || (root.TryGetProperty("terminal_reason", out var reason)
                && reason.ValueKind == JsonValueKind.String
                && reason.GetString() == "api_error");

        if (!isError)
            return null;

        foreach (var propertyName in new[] { "error", "result" })
        {
            if (!root.TryGetProperty(propertyName, out var value))
                continue;

            var message = value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();
            if (!string.IsNullOrWhiteSpace(message))
                return message;
        }

        if (ReadApiErrorStatus(root) is { } code)
            return $"Claude API request failed with HTTP {code}";

        return "Claude request failed";
    }

    // The CLI sends api_error_status: null on errors with no HTTP status, safeguards refusals among
    // them, and TryGetInt32 throws on a JSON null rather than returning false.
    private static int? ReadApiErrorStatus(JsonElement root)
        => root.TryGetProperty("api_error_status", out var status)
            && status.ValueKind == JsonValueKind.Number
            && status.TryGetInt32(out var code)
                ? code
                : null;

    private static string? ReadOptionalString(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool IsAuthenticationError(JsonElement root, string error)
        => ReadApiErrorStatus(root) is 401 or 403
            || error.Contains("authentication", StringComparison.OrdinalIgnoreCase)
            || error.Contains("authenticate", StringComparison.OrdinalIgnoreCase)
            || error.Contains("oauth", StringComparison.OrdinalIgnoreCase);

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

        // Only fills a gap, never overwrites: the CLI reports a flat 200k here even for
        // 1M-context models, so an already-established window is the more reliable value.
        if (session.Info.ContextWindow == null && root.TryGetProperty("modelUsage", out var modelUsage))
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
        DiscardPendingQuestions(session, "session_ended");
        DiscardPendingSteers(session);
        session.Cts.Cancel();
        SettleTurnContent(session);
        SettleLostBackgroundTasks(session);

        if (session.RestartPending)
        {
            _log($"[Claude] Session {sessionId} process exited for restart (code {exitCode})", null);
            return;
        }

        if (session.Info.Status == SessionStatus.Idle)
        {
            // No turn was in flight, so nothing can be replayed: losing an idle process is
            // recoverable infrastructure loss, not a terminal error. Keeping Idle's stale
            // "completed" reason under Error left queued input blocked with no resume path.
            session.Info.Status = SessionStatus.Stopped;
            session.Info.StopReason = SessionStopReasons.OrphanedOnRestart;
            session.Info.ProcessId = null;
            session.Info.StoppedAt = DateTimeOffset.UtcNow;
            PersistSessionRecord(session.Info);
            _log($"[Claude] Session {sessionId} idle process exited (code {exitCode}); recoverable as {session.Info.StopReason}", null);
            SessionUpdated?.Invoke(session.Info);
            SessionEnded?.Invoke(sessionId, $"process_exited:{exitCode}", session.Info.StopReason);
            return;
        }

        if (session.Info.Status != SessionStatus.Stopped)
        {
            session.Info.Status = SessionStatus.Error;
            // A turn-level reason such as authentication_failed is more specific; a leftover
            // "completed" from the previous turn is not a reason for this exit.
            if (session.Info.StopReason is null or "completed")
                session.Info.StopReason = "process_exited";
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

            var jsonlPath = ClaudeTranscriptPaths.FindTranscript(session.Info.ProjectPath, claudeSessionId);
            if (jsonlPath is null) return;

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

    private Task WriteStdinOnDedicatedThread(Process process, string data, CancellationToken ct, Stopwatch sw)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                ct.ThrowIfCancellationRequested();
                process.StandardInput.Write(data);
                process.StandardInput.Close();
                _log($"[Claude] TIMING stdin write took {sw.ElapsedMilliseconds}ms ({data.Length} chars, dedicated thread)", null);
                tcs.TrySetResult();
            }
            catch (OperationCanceledException)
            {
                tcs.TrySetCanceled(ct);
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        })
        {
            IsBackground = true,
            Name = "claude-stdin-writer"
        };
        thread.Start();
        return tcs.Task;
    }

    // Every ManagedSession stdin write (user messages, answers, control requests)
    // goes through one of these so a queued-message drain can never interleave its
    // JSON line with an in-flight interrupt/permission-mode write on the same pipe.
    /// <summary>Test seam: every JSON line written to a session's CLI stdin, after it is written.</summary>
    internal Action<string, string>? ControlLineWritten { get; set; }

    private void WriteControlLine(ManagedSession session, object message)
    {
        var json = JsonSerializer.Serialize(message);
        session.WriteLock.Wait(session.Cts.Token);
        try
        {
            session.Process.StandardInput.WriteLine(json);
            session.Process.StandardInput.Flush();
        }
        finally
        {
            session.WriteLock.Release();
        }
        ControlLineWritten?.Invoke(session.Info.Id, json);
    }

    private async Task WriteControlLineAsync(ManagedSession session, object message, Action? onWritten = null)
    {
        var json = JsonSerializer.Serialize(message);
        await session.WriteLock.WaitAsync(session.Cts.Token);
        try
        {
            session.Process.StandardInput.WriteLine(json);
            onWritten?.Invoke();
            session.Process.StandardInput.Flush();
        }
        finally
        {
            session.WriteLock.Release();
        }
        ControlLineWritten?.Invoke(session.Info.Id, json);
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
                ScratchDirectory = info.ScratchDirectory,
                RepositoryId = info.RepositoryId,
                Status = info.Status.ToString(),
                StopReason = info.StopReason,
                StoppedAt = info.StoppedAt,
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
                QualityTier = info.QualityTier,
                ProviderEntity = info.ProviderEntity,
                DeveloperInstructions = info.DeveloperInstructions,
                JobId = info.JobId,
                Source = info.Source,
                UserId = info.UserId,
                UserName = info.UserName,
                UserAvatarUrl = info.UserAvatarUrl,
                ProcessId = info.ProcessId,
                LastActivity = DateTimeOffset.UtcNow,
                BackgroundTasksJson = info.BackgroundTasks.Count > 0
                    ? JsonSerializer.Serialize(info.BackgroundTasks)
                    : null,
                AccessLevel = info.Access.AccessLevel,
                MaxAccessLevel = info.Access.MaxAccessLevel,
                AccessLevelBeforePlan = info.Access.AccessLevelBeforePlan,
                Unattended = info.Access.Unattended,
                AccessLevelSource = info.Access.AccessLevelSource,
            });
        }
        catch (Exception ex)
        {
            _log($"[Claude] Failed to persist session record: {ex.Message}", null);
        }
    }

    private void PersistMessage(string sessionId, string role, string eventType, string? content,
        string? toolName, string? toolInput, string? toolResult, string? messageId,
        string? attachmentsJson = null, string? messageUid = null, string? phase = null,
        string? parentToolUseId = null, TurnOrigin? turnOrigin = null, string? promptUuid = null)
    {
        try
        {
            // The first durable record of a self-started turn carries why it started, whichever
            // record that is (a settled block, a tool call): live-only deltas are not durable.
            if (turnOrigin is null && messageUid is not null && parentToolUseId is null
                && _sessions.TryGetValue(sessionId, out var owner)
                && owner.DurableTurnOrigin is { } pendingOrigin && pendingOrigin.Uid == messageUid)
            {
                turnOrigin = pendingOrigin.Origin;
                owner.DurableTurnOrigin = null;
            }
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
                MessageUid = messageUid,
                PromptUuid = promptUuid,
                Phase = phase,
                ParentToolUseId = parentToolUseId,
                TurnOriginJson = turnOrigin is null ? null : JsonSerializer.Serialize(turnOrigin),
                Timestamp = DateTimeOffset.UtcNow,
                AttachmentsJson = attachmentsJson,
            });
        }
        catch (Exception ex)
        {
            _log($"[Claude] Failed to persist message: {ex.Message}", null);
        }
    }

    // A control_request from the CLI that we have parked awaiting a client, held open until
    // answered, cancelled by the CLI, or expired. Mirrors the request-id correlation the
    // outbound direction does with TaskCompletionSource -- but the CLI is the one blocking
    // here, so the "completion" is the control_response we write, not a task we await.
    private sealed class PendingQuestion : IDisposable
    {
        public required string RequestId { get; init; }
        public required string? ToolUseId { get; init; }
        /// <summary>The tool input as the CLI sent it; echoed back inside updatedInput.</summary>
        public required JsonElement Input { get; init; }
        public required DateTimeOffset AskedAt { get; init; }
        public required DateTimeOffset ExpiresAt { get; init; }
        public Timer? ExpiryTimer { get; set; }
        /// <summary>The asking tool; null means AskUserQuestion.</summary>
        public string? ToolName { get; init; }
        /// <summary>Set for a permission ask; null for an AskUserQuestion question.</summary>
        public Core.Sessions.PendingPermissionRequest? Permission { get; init; }
        /// <summary>The CLI's permission_suggestions, used for allow_session.</summary>
        public JsonElement? Suggestions { get; init; }

        public void Dispose() => ExpiryTimer?.Dispose();
    }

    /// <summary>
    /// An allow_session RedCompute keeps itself, when the CLI offered no suggestion: the exact
    /// tool, and for Bash the exact command. Lives as long as the CLI process.
    /// </summary>
    /// <summary>
    /// What "allow for this session" remembers when the CLI offered no rule of its own: exactly
    /// the approved call. Bash by its command; any other tool by its whole input, never tool-wide
    /// (approving one WebFetch or MCP call must not approve every later one).
    /// </summary>
    internal sealed record SessionAllowRule(string ToolName, string? Command, string? Input = null)
    {
        public static SessionAllowRule For(string toolName, JsonElement input)
            => toolName == "Bash"
                ? new(toolName, CommandOf(input) ?? "")
                : new(toolName, null, input.ValueKind == JsonValueKind.Undefined ? "" : input.GetRawText());

        public bool Matches(string toolName, JsonElement input)
            => toolName == ToolName
                && (Command is null || CommandOf(input) == Command)
                && (Input is null || (input.ValueKind == JsonValueKind.Undefined ? "" : input.GetRawText()) == Input);

        private static string? CommandOf(JsonElement input)
            => input.ValueKind == JsonValueKind.Object && input.TryGetProperty("command", out var command)
                && command.ValueKind == JsonValueKind.String ? command.GetString() : null;
    }

    private sealed class PendingSteer
    {
        public required string MessageUid { get; init; }
        public required string TranscriptContent { get; init; }
        public required string? AttachmentsJson { get; init; }
        public required TaskCompletionSource<SessionInputDeliveryResult> Completion { get; init; }
        public bool Committed { get; set; }
        public object WatchdogGate { get; } = new();
        public CancellationTokenSource? WatchdogCts { get; set; }
    }

    private class ManagedSession
    {
        public ClaudeSessionInfo Info { get; }
        public Process Process { get; }
        public CancellationTokenSource Cts { get; }
        // Inbound control_requests awaiting a client answer, keyed by request_id. Removal is
        // the claim: whoever wins TryRemove owns writing the single control_response, so an
        // answer racing the expiry timer cannot produce two.
        public ConcurrentDictionary<string, PendingQuestion> PendingQuestions { get; } = new();
        // allow_session decisions RedCompute keeps itself (no CLI suggestion to hand back).
        public ConcurrentBag<SessionAllowRule> AllowRules { get; } = [];
        // Outbound steering messages wait for their exact replay UUID before the queue item
        // is acknowledged. Content matching would conflate duplicate user text.
        public ConcurrentDictionary<string, PendingSteer> PendingSteers { get; } = new();
        public List<ClaudeStreamEvent> MessageHistory { get; } = new();
        public bool InterruptPending { get; set; }
        // Completed by ParseResultEvent when the result for the interrupted (or
        // racingly-already-finishing) turn arrives. Null when no interrupt is in flight.
        public TaskCompletionSource<bool>? InterruptCompletion { get; set; }
        public bool RestartPending { get; set; }
        public int ControlRequestCounter { get; set; }
        // Provider-neutral uid shared by all events of the current assistant
        // turn. Minted lazily at the turn's first content event; cleared on
        // turn boundaries (status/error result, user message, answer) so the
        // next turn mints a fresh one.
        public string? CurrentAssistantUid { get; set; }
        // Streamed text/thinking blocks awaiting phase or block-stop settlement.
        public ClaudeTextBlockTracker TextBlocks { get; } = new();
        // Background tasks (subagents, background shells) of this CLI process.
        public ClaudeBackgroundTaskTracker BackgroundTasks { get; } = new();
        // tool_use id -> uid of the logical turn that made the call. A background child line names
        // its launching call, so it resolves to that turn however long after it arrives.
        public ConcurrentDictionary<string, string?> ToolTurnUids { get; } = new(StringComparer.Ordinal);
        // A written user message (send or answer) the CLI has not yet echoed back as taken.
        public bool UserInputUnconsumed { get; set; }
        // Top-level tasks that finished; the CLI wakes its main loop for each, in order.
        public ConcurrentQueue<string> WakeTasks { get; } = new();
        // The current self-started turn, its first live event still to be tagged, and its first
        // durable record still to be tagged.
        public TurnOrigin? WakeTurnOrigin { get; set; }
        public TurnOrigin? PendingLiveTurnOrigin { get; set; }
        public (string Uid, TurnOrigin Origin)? DurableTurnOrigin { get; set; }
        // Outbound control requests awaiting their control_response (stop_task, set_permission_mode), keyed by request_id.
        // The result is the CLI's error, or null on success.
        public ConcurrentDictionary<string, TaskCompletionSource<string?>> PendingControlRequests { get; } = new();
        // Serializes access-level switches (route changes, plan exits, init reconciles) so each
        // compares against the mode the last one left, and records only what the CLI confirmed.
        public SemaphoreSlim AccessGate { get; } = new(1, 1);
        // "reported>expected" mode mismatches this process already had its one reconcile attempt for.
        public ConcurrentDictionary<string, byte> ReconciledModes { get; } = new(StringComparer.Ordinal);
        public ConcurrentDictionary<string, byte> LoggedSystemSubtypes { get; } = new(StringComparer.Ordinal);
        // The ContextCompaction tool_use awaiting its result, with the turn it belongs to.
        public (string ToolUseId, string? TurnUid)? OpenCompaction { get; set; }
        public object BackgroundPublishGate { get; } = new();
        public DateTimeOffset LastBackgroundPublish { get; set; }
        public bool BackgroundPublishScheduled { get; set; }
        // Guards every StandardInput write so a queued-message drain can never
        // interleave with a concurrent interrupt/permission-mode control write
        // (mirrors OpenCodeSessionService.ManagedSession.WriteLock).
        private readonly SemaphoreSlim _writeLock = new(1, 1);
        public SemaphoreSlim WriteLock => _writeLock;

        public ManagedSession(ClaudeSessionInfo info, Process process, CancellationTokenSource cts)
        {
            Info = info;
            Process = process;
            Cts = cts;
        }
    }
}

public record ImageAttachment(string MediaType, string Base64);
