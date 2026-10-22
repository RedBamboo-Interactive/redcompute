using System.Text.Json;
using RedCompute.Core.Sessions;

namespace RedCompute.Plugin.ClaudeCode;

/// <summary>
/// Folds Claude Code's background task lifecycle (`system` lines task_started, task_progress,
/// task_updated, task_notification and background_tasks_changed) into one state per task.
///
/// The CLI's `background_tasks_changed` snapshot is authoritative for which tasks are running; the
/// other events carry the details. A task gets exactly two durable records: one when it starts and
/// one when it reaches a terminal state. Progress is live-only. Tasks the CLI reports as
/// foreground (`is_backgrounded: false`, a subagent's own blocking shell) are not background work
/// and are ignored. One tracker belongs to one CLI process: the tasks are in-process children of
/// that process and die with it.
/// </summary>
internal sealed class ClaudeBackgroundTaskTracker
{
    private readonly object _gate = new();
    private readonly Dictionary<string, TaskState> _tasks = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _taskIdsByRunId = new(StringComparer.Ordinal);
    private readonly HashSet<string> _foreground = new(StringComparer.Ordinal);
    private readonly HashSet<string> _running = new(StringComparer.Ordinal);

    internal enum ChangeKind { None, Progress, Lifecycle }

    /// <param name="Records">Durable task records to write, in order.</param>
    /// <param name="WakeTaskId">A top-level task that just finished; the CLI wakes its main loop next.</param>
    internal sealed record Change(ChangeKind Kind, IReadOnlyList<BackgroundTaskInfo> Records, string? WakeTaskId = null)
    {
        public static readonly Change None = new(ChangeKind.None, []);
    }

    private sealed class TaskState
    {
        public required string TaskId { get; init; }
        public string? RunId { get; set; }
        public string Kind { get; set; } = BackgroundTaskKinds.Other;
        public string Status { get; set; } = BackgroundTaskStatuses.Running;
        public string? Description { get; set; }
        public string? AgentType { get; set; }
        public string? Summary { get; set; }
        public BackgroundTaskUsage? Usage { get; set; }
        public DateTimeOffset StartedAt { get; set; }
        public DateTimeOffset? EndedAt { get; set; }
        public string? LastActivity { get; set; }
        public string? LastToolName { get; set; }
        public string? ParentToolUseId { get; set; }
        public string? MessageUid { get; set; }
        public string? ParentTaskId { get; set; }
        /// <summary>task_updated's terminal status, applied when the notification or snapshot lands.</summary>
        public string? ReportedTerminalStatus { get; set; }
        public bool StartRecorded { get; set; }
        public bool TerminalRecorded { get; set; }

        public BackgroundTaskInfo ToInfo() => new()
        {
            TaskId = TaskId,
            Kind = Kind,
            Status = Status,
            Description = Description,
            AgentType = AgentType,
            Summary = Summary,
            Usage = Usage,
            StartedAt = StartedAt,
            EndedAt = EndedAt,
            LastActivity = LastActivity,
            LastToolName = LastToolName,
            ParentToolUseId = ParentToolUseId,
            MessageUid = MessageUid,
            ParentTaskId = ParentTaskId,
        };
    }

    /// <summary>Running tasks, oldest first.</summary>
    public IReadOnlyList<BackgroundTaskInfo> Running
    {
        get
        {
            lock (_gate)
                return _running.Select(id => _tasks[id])
                    .OrderBy(task => task.StartedAt)
                    .ThenBy(task => task.TaskId, StringComparer.Ordinal)
                    .Select(task => task.ToInfo())
                    .ToList();
        }
    }

    /// <summary>Whether this process started the task. Unknown tasks belong to a previous process.</summary>
    public bool Knows(string taskId)
    {
        lock (_gate) return _tasks.ContainsKey(taskId);
    }

    public string? TaskIdForRun(string? runId)
    {
        if (runId is null) return null;
        lock (_gate) return _taskIdsByRunId.GetValueOrDefault(runId);
    }

    public Change Started(JsonElement root, Func<string, string?> resolveMessageUid, DateTimeOffset now)
    {
        if (Str(root, "task_id") is not { } taskId) return Change.None;
        lock (_gate)
        {
            if (root.TryGetProperty("is_backgrounded", out var backgrounded)
                && backgrounded.ValueKind == JsonValueKind.False)
            {
                _foreground.Add(taskId);
                return Change.None;
            }

            var task = GetOrAdd(taskId, now);
            Apply(task, root);
            if (Str(root, "tool_use_id") is { } toolUseId)
            {
                task.ParentToolUseId = toolUseId;
                task.MessageUid ??= resolveMessageUid(toolUseId);
            }
            if (task.StartRecorded || task.TerminalRecorded) return new(ChangeKind.Lifecycle, []);
            task.StartRecorded = true;
            if (!BackgroundTaskStatuses.IsTerminal(task.Status)) _running.Add(taskId);
            return new(ChangeKind.Lifecycle, [task.ToInfo()]);
        }
    }

    public Change Progress(JsonElement root)
    {
        if (Str(root, "task_id") is not { } taskId) return Change.None;
        lock (_gate)
        {
            if (!_tasks.TryGetValue(taskId, out var task) || task.TerminalRecorded) return Change.None;
            task.LastActivity = Str(root, "description") ?? task.LastActivity;
            task.LastToolName = Str(root, "last_tool_name") ?? task.LastToolName;
            if (root.TryGetProperty("usage", out var usage)) task.Usage = ReadUsage(usage) ?? task.Usage;
            return new(ChangeKind.Progress, []);
        }
    }

    public Change Updated(JsonElement root)
    {
        if (Str(root, "task_id") is not { } taskId) return Change.None;
        lock (_gate)
        {
            if (!_tasks.TryGetValue(taskId, out var task)
                || !root.TryGetProperty("patch", out var patch) || patch.ValueKind != JsonValueKind.Object)
                return Change.None;
            if (TerminalStatus(Str(patch, "status")) is { } terminal)
                task.ReportedTerminalStatus = terminal;
            if (patch.TryGetProperty("end_time", out var end) && end.TryGetInt64(out var endMs))
                task.EndedAt = DateTimeOffset.FromUnixTimeMilliseconds(endMs);
            return Change.None;
        }
    }

    public Change Notification(JsonElement root, DateTimeOffset now)
    {
        if (Str(root, "task_id") is not { } taskId) return Change.None;
        lock (_gate)
        {
            // A resumed CLI reports tasks of the process it replaced as stopped. Those were
            // already recorded as lost when that process ended, and they have nothing to attach to.
            if (!_tasks.TryGetValue(taskId, out var task)) return Change.None;
            task.Summary = Str(root, "summary") ?? task.Summary;
            if (root.TryGetProperty("usage", out var usage)) task.Usage = ReadUsage(usage) ?? task.Usage;
            var status = TerminalStatus(Str(root, "status")) ?? task.ReportedTerminalStatus
                ?? BackgroundTaskStatuses.Completed;
            var records = Terminate(task, status, now);
            return new(ChangeKind.Lifecycle, records, task.ParentTaskId is null ? taskId : null);
        }
    }

    public Change Snapshot(JsonElement root, DateTimeOffset now)
    {
        if (!root.TryGetProperty("tasks", out var tasks) || tasks.ValueKind != JsonValueKind.Array)
            return Change.None;
        lock (_gate)
        {
            var listed = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in tasks.EnumerateArray())
            {
                if (Str(item, "task_id") is not { } taskId || _foreground.Contains(taskId)) continue;
                listed.Add(taskId);
                var task = GetOrAdd(taskId, now);
                if (task.TerminalRecorded) continue;
                Apply(task, item);
            }

            var changed = !listed.SetEquals(_running);
            var records = new List<BackgroundTaskInfo>();
            foreach (var taskId in _running.Where(id => !listed.Contains(id)).ToList())
            {
                // The snapshot dropped a task whose notification never arrived: settle it from
                // the last status the CLI reported rather than leaving it running forever.
                var task = _tasks[taskId];
                records.AddRange(Terminate(task, task.ReportedTerminalStatus ?? BackgroundTaskStatuses.Completed, now));
            }
            foreach (var taskId in listed)
                if (!_tasks[taskId].TerminalRecorded) _running.Add(taskId);
            return changed || records.Count > 0 ? new(ChangeKind.Lifecycle, records) : Change.None;
        }
    }

    /// <summary>The CLI process is gone: every task still running died with it.</summary>
    public IReadOnlyList<BackgroundTaskInfo> MarkAllLost(DateTimeOffset now)
    {
        lock (_gate)
        {
            var records = new List<BackgroundTaskInfo>();
            foreach (var task in _tasks.Values.Where(task => !task.TerminalRecorded)
                         .OrderBy(task => task.StartedAt).ToList())
            {
                // Only tasks that were announced (start record or snapshot) can be lost.
                if (!task.StartRecorded && !_running.Contains(task.TaskId)) continue;
                records.AddRange(Terminate(task, BackgroundTaskStatuses.Lost, now));
            }
            _running.Clear();
            return records;
        }
    }

    private List<BackgroundTaskInfo> Terminate(TaskState task, string status, DateTimeOffset now)
    {
        _running.Remove(task.TaskId);
        if (task.TerminalRecorded) return [];
        task.Status = status;
        task.EndedAt ??= now;
        task.TerminalRecorded = true;
        return [task.ToInfo()];
    }

    private TaskState GetOrAdd(string taskId, DateTimeOffset now)
    {
        if (!_tasks.TryGetValue(taskId, out var task))
            _tasks[taskId] = task = new TaskState { TaskId = taskId, StartedAt = now };
        return task;
    }

    private void Apply(TaskState task, JsonElement source)
    {
        if (Str(source, "run_id") is { } runId)
        {
            task.RunId = runId;
            _taskIdsByRunId[runId] = task.TaskId;
        }
        if (Str(source, "task_type") is { } taskType) task.Kind = Kind(taskType);
        task.Description = Str(source, "description") ?? task.Description;
        task.AgentType = Str(source, "subagent_type") ?? task.AgentType;
        task.ParentTaskId = Str(source, "parent_task_id") ?? task.ParentTaskId;
    }

    internal static string Kind(string taskType) => taskType switch
    {
        "local_agent" or "remote_agent" => BackgroundTaskKinds.Agent,
        "local_bash" => BackgroundTaskKinds.Shell,
        _ => BackgroundTaskKinds.Other,
    };

    internal static string? TerminalStatus(string? status) => status switch
    {
        "completed" => BackgroundTaskStatuses.Completed,
        "killed" or "stopped" or "cancelled" => BackgroundTaskStatuses.Stopped,
        "failed" or "error" => BackgroundTaskStatuses.Failed,
        _ => null,
    };

    private static BackgroundTaskUsage? ReadUsage(JsonElement usage)
    {
        if (usage.ValueKind != JsonValueKind.Object) return null;
        return new BackgroundTaskUsage
        {
            ToolUses = usage.TryGetProperty("tool_uses", out var uses) && uses.TryGetInt32(out var u) ? u : null,
            Tokens = usage.TryGetProperty("total_tokens", out var tokens) && tokens.TryGetInt64(out var t) ? t : null,
            DurationMs = usage.TryGetProperty("duration_ms", out var duration) && duration.TryGetInt64(out var d) ? d : null,
        };
    }

    private static string? Str(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>The durable `task` record's toolInput: the task's state at that transition.</summary>
    internal static string RecordInput(BackgroundTaskInfo task) => JsonSerializer.Serialize(new Dictionary<string, object?>
    {
        ["taskId"] = task.TaskId,
        ["kind"] = task.Kind,
        ["status"] = task.Status,
        ["description"] = task.Description,
        ["agentType"] = task.AgentType,
        ["summary"] = task.Summary,
        ["usage"] = task.Usage is { } usage
            ? new Dictionary<string, object?>
            {
                ["toolUses"] = usage.ToolUses,
                ["tokens"] = usage.Tokens,
                ["durationMs"] = usage.DurationMs,
            }
            : null,
        ["startedAt"] = task.StartedAt.ToString("O"),
        ["endedAt"] = task.EndedAt?.ToString("O"),
        ["parentTaskId"] = task.ParentTaskId,
    }.Where(pair => pair.Value is not null).ToDictionary(pair => pair.Key, pair => pair.Value));
}
