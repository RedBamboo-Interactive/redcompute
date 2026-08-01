using System.Collections.Concurrent;
using System.Text.Json;
using RedCompute.Core.Sessions;
using RedCompute.PluginSdk;

namespace RedCompute.Plugin.Codex;

/// <summary>
/// Persistent, interactive Codex sessions over <c>codex app-server</c>.
///
/// One app-server process per session, holding one thread. The thread id is persisted, so a session
/// survives a RedCompute restart: the process is gone but <c>thread/resume</c> reattaches to Codex's
/// own rollout on disk. That also means a thread started here is resumable from the Codex CLI and
/// desktop app, since all surfaces share the same store.
///
/// Approval behaviour mirrors Claude Code's <c>--permission-mode bypassPermissions</c>: command and
/// file-change approvals are auto-accepted, and only a question the model *deliberately* asked
/// (<c>item/tool/requestUserInput</c>) is surfaced to the user. See CodexApprovals below.
/// </summary>
public sealed class CodexInteractiveService : IAsyncDisposable
{
    private sealed class ManagedSession
    {
        public required CodexSessionInfo Info { get; init; }
        public required CodexAppServerConnection Connection { get; set; }

        /// <summary>Turn-scoped uid, minted on first event of a turn and cleared when it ends.</summary>
        public string? CurrentTurnUid { get; set; }

        /// <summary>Id of the in-flight turn, needed by turn/interrupt.</summary>
        public string? ActiveTurnId { get; set; }

        /// <summary>Questions parked awaiting a user answer, keyed by request id.</summary>
        public ConcurrentDictionary<string, PendingQuestion> Questions { get; } = new();

        /// <summary>
        /// Ids of items whose text arrived as deltas. Codex sends both the stream *and* the whole
        /// item again on completion; the client appends partials, so re-emitting the full text
        /// renders every message twice.
        /// </summary>
        public HashSet<string> StreamedItems { get; } = [];
    }

    public sealed record PendingQuestion(JsonElement RequestId, string ItemId, JsonElement Questions);

    private readonly ConcurrentDictionary<string, ManagedSession> _sessions = new();
    private readonly CodexConfig _config;
    private readonly ICodexSessionStore _store;
    private readonly CodexModelCatalog _catalog;
    private readonly CodexSessionJobLifecycle _jobLifecycle;

    /// <summary>Stateless exec, used only to name sessions with a cheap one-shot.</summary>
    private readonly CodexSessionService _exec;

    private readonly Action<string, Guid?> _log;

    public event Action<CodexSessionInfo>? SessionCreated;
    public event Action<CodexSessionInfo>? SessionUpdated;
    public event Action<string, string>? SessionEnded;
    public event Action<string, CodexStreamEvent>? StreamEvent;

    public CodexInteractiveService(
        CodexConfig config, ICodexSessionStore store, CodexModelCatalog catalog,
        CodexSessionService exec, IJobTracker jobTracker, Action<string, Guid?> log)
    {
        _config = config;
        _store = store;
        _catalog = catalog;
        _exec = exec;
        _jobLifecycle = new CodexSessionJobLifecycle(jobTracker);
        _log = log;
        RecoverSessions();
    }

    /// <summary>
    /// Nothing survives a process exit, so any session still marked active belongs to a dead
    /// app-server. Kill the orphan by pid and mark it stopped — it stays resumable via its ThreadId.
    /// </summary>
    private void RecoverSessions()
    {
        try
        {
            foreach (var s in _store.GetActiveSessions())
            {
                if (s.ProcessId is { } pid) TryKillByPid(pid);
                s.Status = "Stopped";
                s.ProcessId = null;
                s.StopReason = "orphaned_on_restart";
                _store.SaveSession(s);
                _log($"[Codex] Marked orphaned session {s.Id} ({s.ProjectName}) as stopped", null);
            }
        }
        catch (Exception ex)
        {
            _log($"[Codex] Failed to recover sessions: {ex.Message}", null);
        }
    }

    /// <summary>
    /// Republish the local lifecycle snapshot after the suite mirror is installed.
    /// Providers are constructed before RelayServer assigns SuiteMirror's delegates, so
    /// recovery writes performed in the constructor otherwise never reach RedLeaf and its
    /// read path keeps reporting dead sessions as Active forever.
    /// </summary>
    public void RepublishStoredSessions()
    {
        foreach (var session in _store.GetRecentSessions([], 1_000, includeDismissed: true))
            _store.SaveSession(session);
    }

    /// <summary>
    /// Repairs sessions created before interactive Codex sessions were linked to compute jobs.
    /// The session id is the idempotency key, so startup retries cannot create duplicate jobs.
    /// </summary>
    public int ReconcileMissingJobs()
    {
        var restored = 0;
        foreach (var session in _store.GetSessionsWithoutJobs())
        {
            try
            {
                var job = _jobLifecycle.Restore(session);
                session.JobId = job.Id;
                _store.SaveSession(session);
                restored++;
            }
            catch (Exception ex)
            {
                _log($"[Codex] Failed to restore compute job for session {session.Id}: {ex.Message}", null);
            }
        }

        return restored;
    }

    private void TryKillByPid(int pid)
    {
        try { System.Diagnostics.Process.GetProcessById(pid).Kill(entireProcessTree: true); }
        catch { }
    }

    // ===== Lifecycle =========================================================================

    public async Task<CodexSessionInfo?> StartSessionAsync(
        string projectPath, string? callerInfo = null, string? model = null,
        string? userId = null, string? userName = null, string? userAvatarUrl = null,
        string? effort = null)
    {
        if (_sessions.Count >= _config.MaxSessions)
        {
            _log($"[Codex] Session cap reached ({_config.MaxSessions})", null);
            return null;
        }

        var id = Guid.NewGuid().ToString("N")[..12];
        var info = new CodexSessionInfo
        {
            Id = id,
            ProjectName = Path.GetFileName(projectPath.TrimEnd('/', '\\')) ?? projectPath,
            ProjectPath = projectPath,
            StartedAt = DateTimeOffset.UtcNow,
            Status = "Starting",
            Model = model,
            Effort = effort,
            Source = callerInfo,
            UserId = userId,
            UserName = userName,
            UserAvatarUrl = userAvatarUrl,
            LastActivity = DateTimeOffset.UtcNow,
        };

        CodexAppServerConnection? conn = null;
        try
        {
            conn = await ConnectAsync(id, projectPath);
            var session = new ManagedSession { Info = info, Connection = conn };

            var result = await conn.SendRequestAsync("thread/start", new
            {
                cwd = projectPath,
                model,
            }, timeoutSeconds: 60);

            // The id lives at result.thread.id — not result.threadId, which is the obvious guess.
            info.ThreadId = result.TryGetProperty("thread", out var thread) && thread.TryGetProperty("id", out var tid)
                ? tid.GetString()
                : null;

            if (info.ThreadId == null)
            {
                await conn.DisposeAsync();
                _log($"[Codex] thread/start returned no thread id for {id}", null);
                return null;
            }

            info.ProcessId = conn.ProcessId;
            info.Status = "Idle";
            _jobLifecycle.Start(info, callerInfo);
            _sessions[id] = session;
            Persist(info);
            SessionCreated?.Invoke(info);
            _log($"[Codex] Session {id} started on thread {info.ThreadId} (model={model ?? "default"})", null);
            return info;
        }
        catch (Exception ex)
        {
            _sessions.TryRemove(id, out _);
            if (conn != null)
            {
                try { await conn.DisposeAsync(); } catch { }
            }
            if (info.JobId != null)
                _jobLifecycle.Fail(info, $"Interactive Codex session failed to start: {ex.Message}");
            _log($"[Codex] Failed to start session for {projectPath}: {ex.Message}", null);
            return null;
        }
    }

    public async Task<CodexSessionInfo?> ResumeSessionAsync(string sessionId)
    {
        if (_sessions.TryGetValue(sessionId, out var live)) return live.Info;

        var record = _store.FindSession(sessionId);
        if (record?.ThreadId == null)
        {
            _log($"[Codex] Cannot resume {sessionId}: no thread id on record", null);
            return null;
        }

        var info = ToInfo(record);
        CodexAppServerConnection? conn = null;
        var jobRunning = false;
        try
        {
            conn = await ConnectAsync(sessionId, record.ProjectPath);
            await conn.SendRequestAsync("thread/resume", new
            {
                threadId = record.ThreadId,
                cwd = record.ProjectPath,
                model = record.Model,
            }, timeoutSeconds: 60);

            info.ProcessId = conn.ProcessId;
            info.Status = "Idle";
            info.StopReason = null;
            info.LastActivity = DateTimeOffset.UtcNow;
            _jobLifecycle.Resume(info);
            jobRunning = true;
            _sessions[sessionId] = new ManagedSession { Info = info, Connection = conn };
            Persist(info);
            SessionUpdated?.Invoke(info);
            _log($"[Codex] Session {sessionId} resumed on thread {record.ThreadId}", null);
            return info;
        }
        catch (Exception ex)
        {
            _sessions.TryRemove(sessionId, out _);
            if (conn != null)
            {
                try { await conn.DisposeAsync(); } catch { }
            }
            if (jobRunning)
            {
                info.Status = "Error";
                info.StopReason = "resume_failed";
                info.ProcessId = null;
                info.LastActivity = DateTimeOffset.UtcNow;
                _jobLifecycle.Fail(info, $"Interactive Codex session failed to resume: {ex.Message}");
                try { Persist(info); } catch { }
            }
            _log($"[Codex] Failed to resume {sessionId}: {ex.Message}", null);
            return null;
        }
    }

    private async Task<CodexAppServerConnection> ConnectAsync(string sessionId, string projectPath)
    {
        var conn = await CodexAppServerConnection.StartAsync(_config.CodexPath, projectPath, _log);
        conn.Notification += (method, p) => OnNotification(sessionId, method, p);
        conn.ServerRequest += (id, method, p) => _ = OnServerRequestAsync(sessionId, id, method, p);
        conn.Exited += code => OnExited(sessionId, code);
        return conn;
    }

    public Task StopSessionAsync(string sessionId) => StopSessionAsync(sessionId, "user_stopped");

    private async Task StopSessionAsync(string sessionId, string stopReason)
    {
        if (_sessions.TryRemove(sessionId, out var session))
        {
            await session.Connection.DisposeAsync();
            session.Info.Status = "Stopped";
            session.Info.StopReason = stopReason;
            session.Info.ProcessId = null;
            session.Info.LastActivity = DateTimeOffset.UtcNow;
            _jobLifecycle.Complete(session.Info);
            Persist(session.Info);
            SessionEnded?.Invoke(sessionId, "stopped");
            return;
        }

        var stored = _store.FindSession(sessionId);
        if (stored == null || stored.Status is "Stopped" or "Error") return;

        if (stored.ProcessId is { } pid) TryKillByPid(pid);
        stored.Status = "Stopped";
        stored.StopReason = stopReason;
        stored.ProcessId = null;
        stored.LastActivity = DateTimeOffset.UtcNow;

        if (stored.JobId != null)
        {
            var info = ToInfo(stored);
            _jobLifecycle.Complete(info);
        }
        else
        {
            stored.JobId = _jobLifecycle.Restore(stored).Id;
        }

        _store.SaveSession(stored);
        SessionEnded?.Invoke(sessionId, "stopped");
    }

    public Task ForceKillAsync(string sessionId) => StopSessionAsync(sessionId);

    private void OnExited(string sessionId, int code)
    {
        if (!_sessions.TryRemove(sessionId, out var session)) return;
        session.Info.Status = code == 0 ? "Stopped" : "Error";
        session.Info.StopReason = code == 0 ? "process_exited" : $"process_exited:{code}";
        session.Info.ProcessId = null;
        session.Info.LastActivity = DateTimeOffset.UtcNow;
        if (code == 0)
            _jobLifecycle.Complete(session.Info);
        else
            _jobLifecycle.Fail(session.Info, $"Codex app-server exited with code {code}");
        Persist(session.Info);
        SessionEnded?.Invoke(sessionId, code == 0 ? "stopped" : $"exited:{code}");
    }

    // ===== Messaging =========================================================================

    public async Task<bool> SendMessageAsync(
        string sessionId, string content, ImageAttachment[]? images = null,
        string? attachmentsJson = null, string? messageUid = null)
    {
        var session = await EnsureLiveAsync(sessionId);
        if (session == null) return false;

        var info = session.Info;
        if (images is { Length: > 0 })
        {
            var support = GetImageAttachmentSupport(info.Model, _catalog.Cached);
            if (!support.Supported)
            {
                var error = support.Reason ?? $"Model '{info.Model}' does not support image input";
                _log($"[Codex] {error} for session {sessionId}", null);
                Emit(session, new CodexStreamEvent { Type = "error", Content = error });
                return false;
            }
        }

        var turnInput = BuildTurnInput(content, images);
        if (turnInput.Length == 0) return false;

        // A user message opens a new turn, so the previous turn's uid must not leak into it.
        session.CurrentTurnUid = null;

        _store.AddMessage(new CodexMessageRecord
        {
            SessionId = sessionId,
            Role = "user",
            EventType = "text",
            Content = content,
            MessageUid = messageUid,
            AttachmentsJson = attachmentsJson,
            Timestamp = DateTimeOffset.UtcNow,
        });

        info.MessageCount++;
        info.Status = "Active";
        info.LastActivity = DateTimeOffset.UtcNow;
        EnsureTitle(session, content);
        Persist(info);
        SessionUpdated?.Invoke(info);

        try
        {
            var result = await session.Connection.SendRequestAsync("turn/start", new
            {
                threadId = info.ThreadId,
                input = turnInput,
                model = info.Model,
                effort = info.Effort,

                // Without this, reasoning items arrive with empty summary/content and every
                // thinking block renders blank. It is not on by default.
                summary = "detailed",

                // bypassPermissions equivalent: ask us rather than self-denying, and we accept.
                // "never" would mean never ask, which the server treats as decline.
                approvalPolicy = "on-request",
                sandboxPolicy = BuildSandboxPolicy(info.ProjectPath),
            }, timeoutSeconds: 120);

            var returnedTurnId = result.TryGetProperty("turn", out var turn) && turn.TryGetProperty("id", out var tid)
                ? tid.GetString()
                : result.TryGetProperty("turnId", out var t2) ? t2.GetString() : null;

            // turn/started is the authoritative live signal and can arrive before this JSON-RPC
            // response. Never overwrite a newer active id with a late response from an older turn.
            if (info.Status == "Active" && session.ActiveTurnId == null)
                session.ActiveTurnId = returnedTurnId;

            return true;
        }
        catch (Exception ex)
        {
            _log($"[Codex] turn/start failed for {sessionId}: {ex.Message}", null);
            Emit(session, new CodexStreamEvent { Type = "error", Content = ex.Message });
            info.Status = "Idle";
            Persist(info);
            return false;
        }
    }

    /// <summary>
    /// Build the app-server's multimodal <c>UserInput[]</c>. Inline data URLs keep the image tied
    /// to the turn without creating temporary files whose lifetime would need to span persistence
    /// and resume. The installed app-server schema accepts <c>text</c>, <c>image</c>, and
    /// <c>localImage</c> variants here.
    /// </summary>
    internal static object[] BuildTurnInput(string content, ImageAttachment[]? images)
    {
        var input = new List<object>();
        if (!string.IsNullOrWhiteSpace(content))
            input.Add(new { type = "text", text = content });

        if (images is not null)
        {
            foreach (var image in images)
            {
                input.Add(new
                {
                    type = "image",
                    url = $"data:{image.MediaType};base64,{image.Base64}",
                });
            }
        }

        return [.. input];
    }

    internal static ImageAttachmentSupport GetImageAttachmentSupport(
        string? modelId, IReadOnlyList<CodexModel> catalog)
    {
        if (string.IsNullOrWhiteSpace(modelId))
            return new(true);

        var model = catalog.FirstOrDefault(m =>
            m.Id.Equals(modelId, StringComparison.OrdinalIgnoreCase));
        return model is { SupportsImages: false }
            ? new(false, $"Model '{modelId}' does not support image input")
            : new(true);
    }

    /// <summary>
    /// workspaceWrite scoped to the project. The sandbox param on thread/start is a *string* enum
    /// and is ignored here; the per-turn policy is an object, and without writableRoots every edit
    /// is refused as "writing outside of the project".
    /// </summary>
    private object BuildSandboxPolicy(string projectPath) =>
        _config.SandboxMode switch
        {
            "danger-full-access" => new { type = "dangerFullAccess" },
            "read-only" => new { type = "readOnly", networkAccess = false },
            _ => new
            {
                type = "workspaceWrite",
                writableRoots = new[] { projectPath },
                networkAccess = true,
            },
        };

    /// <summary>
    /// Last-resort safety net for <see cref="InterruptSession"/>. <c>turn/interrupt</c> is a request
    /// to stop, not a guarantee: a turn wedged inside a tool call can outlive it, and the app-server
    /// acks the request rather than the abort. Claude Code has exactly this problem and answers it by
    /// force-replacing the process after a grace window, so this mirrors that timeout and that
    /// client-facing vocabulary ("killed" then "idle") rather than inventing a second dialect.
    /// Sized for "something is stuck", not for how long a normal interrupt takes.
    /// </summary>
    private static readonly TimeSpan InterruptGraceTimeout = TimeSpan.FromSeconds(10);

    public Core.Sessions.InterruptResult InterruptSession(string sessionId)
    {
        if (!_sessions.TryGetValue(sessionId, out var session))
            return Core.Sessions.InterruptResult.NotFound;

        if (session.Info.ThreadId == null)
            return Core.Sessions.InterruptResult.NotFound;

        // Nothing is running. This used to report success anyway, which is what made stop a no-op
        // you could not diagnose: the caller takes "interrupted" as proof a turn is ending, keeps
        // showing itself as busy, and nothing ever arrives to contradict it. Saying NotActive lets
        // the client unstick a stale "responding" state instead of waiting forever.
        if (session.Info.Status != "Active")
            return Core.Sessions.InterruptResult.NotActive;

        // Same word Claude Code emits, because the frontend already understands it: an ack of
        // RECEIPT, not a turn-ended signal, so isStreaming deliberately stays true.
        //
        // Deliberately not routed through Emit(): this is transient UI state, and Emit would both
        // persist it as a transcript row and null CurrentTurnUid, which mid-turn splits the
        // assistant's message into two blocks in the live view but not in the rebuilt one.
        StreamEvent?.Invoke(session.Info.Id, new CodexStreamEvent { Type = "status", Content = "interrupting" });

        _ = InterruptAndEscalateAsync(session, session.ActiveTurnId);
        return Core.Sessions.InterruptResult.Interrupted;
    }

    /// <summary>
    /// Sends <c>turn/interrupt</c> and, if the turn does not actually end, replaces the app-server.
    /// </summary>
    private async Task InterruptAndEscalateAsync(ManagedSession session, string? turnId)
    {
        var sessionId = session.Info.Id;

        try
        {
            await session.Connection.SendRequestAsync("turn/interrupt", new
            {
                threadId = session.Info.ThreadId,
                turnId,
            }, timeoutSeconds: 15);
        }
        catch (Exception ex)
        {
            // Discarding this task is what hid the failure. The request can be rejected outright —
            // a null or stale turnId is the ordinary case, since ActiveTurnId is only ever populated
            // by a turn/start reply in this process — and the caller was still told "Interrupted".
            // A rejection is not fatal here: the escalation below is what actually stops the turn.
            _log($"[Codex] turn/interrupt rejected for {sessionId}: {ex.Message}", null);
        }

        var deadline = DateTimeOffset.UtcNow + InterruptGraceTimeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            // turn/completed sets Idle. A different turnId means the turn we were asked to stop is
            // already gone and a new one has started — never escalate into that.
            if (session.Info.Status != "Active" || session.ActiveTurnId != turnId) return;
            await Task.Delay(250);
        }

        if (!_sessions.TryGetValue(sessionId, out var current) || !ReferenceEquals(current, session))
            return;

        _log($"[Codex] Interrupt not honoured after {InterruptGraceTimeout.TotalSeconds}s, replacing app-server for {sessionId}", null);

        // "killed" is terminal but explicitly NOT safe-to-write-into: the client holds any queued
        // message until the "idle" below says the replacement is ready. Emitted raw for the same
        // reason as "interrupting" — lifecycle, not transcript.
        StreamEvent?.Invoke(sessionId, new CodexStreamEvent { Type = "status", Content = "killed" });

        // Remove before disposing so OnExited sees no session and stays quiet: a forced interrupt
        // must not surface as SessionEnded, which would tell every client the session died.
        _sessions.TryRemove(sessionId, out _);
        try { await session.Connection.DisposeAsync(); } catch { }

        // The thread lives in Codex's own rollout on disk, so thread/resume reattaches to the same
        // conversation — the turn is lost, the history is not.
        var resumed = await ResumeSessionAsync(sessionId);
        if (resumed == null)
        {
            session.Info.Status = "Error";
            session.Info.StopReason = "resume_failed_after_interrupt";
            session.Info.ProcessId = null;
            session.Info.LastActivity = DateTimeOffset.UtcNow;
            _jobLifecycle.Fail(session.Info, "Turn was force-stopped and the session failed to resume");
            Persist(session.Info);
            SessionUpdated?.Invoke(session.Info);
            StreamEvent?.Invoke(sessionId, new CodexStreamEvent
            {
                Type = "error",
                Content = "Turn was force-stopped and the session failed to resume.",
            });
            return;
        }

        StreamEvent?.Invoke(sessionId, new CodexStreamEvent { Type = "status", Content = "idle" });
    }

    public Task<CodexSessionInfo?> UpdateSessionConfigAsync(string sessionId, string? model, string? effort)
        => Task.FromResult(UpdateSessionConfig(sessionId, model, effort));

    private CodexSessionInfo? UpdateSessionConfig(string sessionId, string? model, string? effort)
    {
        var record = _store.FindSession(sessionId);
        if (record == null) return null;

        // Model and effort are per-turn overrides on turn/start rather than session-wide state,
        // so this only has to record the intent — the next turn picks it up.
        if (model != null) record.Model = model;
        if (effort != null) record.Effort = effort;
        _store.SaveSession(record);

        if (_sessions.TryGetValue(sessionId, out var session))
        {
            if (model != null) session.Info.Model = model;
            if (effort != null) session.Info.Effort = effort;
            SessionUpdated?.Invoke(session.Info);
            return session.Info;
        }
        return ToInfo(record);
    }

    private async Task<ManagedSession?> EnsureLiveAsync(string sessionId)
    {
        if (_sessions.TryGetValue(sessionId, out var session)) return session;
        await ResumeSessionAsync(sessionId);
        return _sessions.GetValueOrDefault(sessionId);
    }

    // ===== Inbound ===========================================================================

    private void OnNotification(string sessionId, string method, JsonElement @params)
    {
        if (!_sessions.TryGetValue(sessionId, out var session)) return;

        switch (method)
        {
            case "turn/started":
                if (@params.TryGetProperty("turn", out var startedTurn) &&
                    startedTurn.TryGetProperty("id", out var startedTurnId) &&
                    startedTurnId.ValueKind == JsonValueKind.String)
                {
                    session.ActiveTurnId = startedTurnId.GetString();
                    session.Info.Status = "Active";
                    session.Info.LastActivity = DateTimeOffset.UtcNow;
                    Persist(session.Info);
                    SessionUpdated?.Invoke(session.Info);
                }
                return;

            case "turn/completed":
                ApplyUsage(session, @params);
                session.ActiveTurnId = null;
                session.StreamedItems.Clear();
                session.Info.Status = "Idle";
                session.Info.LastActivity = DateTimeOffset.UtcNow;
                Persist(session.Info);
                SessionUpdated?.Invoke(session.Info);
                Emit(session, new CodexStreamEvent { Type = "status", Content = "completed" });
                return;

            case "thread/tokenUsage/updated":
                ApplyUsage(session, @params);
                Persist(session.Info);
                SessionUpdated?.Invoke(session.Info);
                return;

            case "thread/name/updated":
                if (@params.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String)
                {
                    session.Info.Title = n.GetString();
                    _jobLifecycle.Rename(session.Info);
                    Persist(session.Info);
                    SessionUpdated?.Invoke(session.Info);
                }
                return;
        }

        foreach (var evt in CodexEventMapper.Map(method, @params))
        {
            // Text and reasoning arrive twice: streamed as deltas, then whole again on
            // item/completed. The client appends partials into one part, so emitting the complete
            // version too renders the message a second time, concatenated onto itself.
            var broadcast = true;
            if (evt.MessageId is { Length: > 0 } itemId && evt.Type is "text" or "thinking" or "tool_result")
            {
                if (evt.IsPartial) session.StreamedItems.Add(itemId);
                // Still persisted — the live client already has this content from the deltas, but
                // partials are never written, so the store would otherwise lose the message.
                else if (session.StreamedItems.Contains(itemId)) broadcast = false;
            }

            Emit(session, evt, broadcast);
        }
    }

    /// <summary>
    /// Reads <c>thread/tokenUsage/updated</c>, whose shape is
    /// <c>{tokenUsage: {total: {...}, last: {...}, modelContextWindow}}</c>.
    ///
    /// <c>total</c> is cumulative across the thread and <c>last</c> is just the most recent turn —
    /// the session counters want the cumulative one. Note <c>turn/completed</c> carries no usage at
    /// all, so this notification is the only source.
    /// </summary>
    private void ApplyUsage(ManagedSession session, JsonElement p)
    {
        if (!p.TryGetProperty("tokenUsage", out var usage)) return;

        if (usage.TryGetProperty("total", out var total))
        {
            if (Int(total, "inputTokens") is { } i) session.Info.InputTokens = i;
            if (Int(total, "outputTokens") is { } o) session.Info.OutputTokens = o;
            if (Int(total, "cachedInputTokens") is { } c) session.Info.CachedInputTokens = c;
        }

        // The real window for this model on this account — smaller than the published spec figure,
        // so trust it over anything a quality mode declares.
        if (Int(usage, "modelContextWindow") is { } w) session.Info.ContextWindow = w;
    }

    private static int? Int(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v)
        && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i)
            ? i
            : null;

    /// <summary>
    /// Stamps the turn uid, broadcasts, and persists — in that order, so the streamed transcript and
    /// the one rebuilt from the database group their parts identically.
    /// </summary>
    private void Emit(ManagedSession session, CodexStreamEvent evt, bool broadcast = true)
    {
        if (evt.Type is "status" or "error")
            session.CurrentTurnUid = null;
        else
            evt.MessageUid = session.CurrentTurnUid ??= Guid.NewGuid().ToString("N");

        if (broadcast) StreamEvent?.Invoke(session.Info.Id, evt);

        if (evt.IsPartial) return; // partials are deltas of a part we persist once, on completion

        _store.AddMessage(new CodexMessageRecord
        {
            SessionId = session.Info.Id,
            Role = "assistant",
            EventType = evt.Type,
            Content = evt.Content,
            ToolName = evt.ToolName,
            ToolInput = evt.ToolInput is null ? null : JsonSerializer.Serialize(evt.ToolInput),
            ToolResult = evt.ToolResult,
            MessageId = evt.MessageId,
            MessageUid = evt.MessageUid,
            Timestamp = DateTimeOffset.UtcNow,
        });
    }

    // ===== Server-initiated requests ==========================================================

    /// <summary>
    /// The server blocks until these are answered, so every branch must reply — including the
    /// unknown-method fallback, or an unrecognised request stalls the turn forever.
    /// </summary>
    private async Task OnServerRequestAsync(string sessionId, JsonElement id, string method, JsonElement @params)
    {
        if (!_sessions.TryGetValue(sessionId, out var session)) return;
        var conn = session.Connection;

        try
        {
            switch (method)
            {
                // Parity with Claude Code's bypassPermissions: these never reach the user.
                case "item/commandExecution/requestApproval":
                case "item/fileChange/requestApproval":
                case "applyPatchApproval":
                case "execCommandApproval":
                    await conn.RespondAsync(id, new { decision = "acceptForSession" });
                    return;

                case "item/permissions/requestApproval":
                    await conn.RespondAsync(id, new { permissions = new { }, scope = "session" });
                    return;

                // The one thing the model asks on purpose — the AskUserQuestion analogue.
                case "item/tool/requestUserInput":
                    HandleUserInputRequest(session, id, @params);
                    return;

                default:
                    _log($"[Codex] Unhandled server request '{method}' — declining to avoid a stalled turn", null);
                    await conn.RespondErrorAsync(id, $"Unhandled request: {method}");
                    return;
            }
        }
        catch (Exception ex)
        {
            _log($"[Codex] Failed to answer server request '{method}': {ex.Message}", null);
        }
    }

    private void HandleUserInputRequest(ManagedSession session, JsonElement id, JsonElement @params)
    {
        var itemId = @params.TryGetProperty("itemId", out var i) ? i.GetString() ?? "" : "";
        @params.TryGetProperty("questions", out var questions);

        session.Questions[itemId] = new PendingQuestion(id.Clone(), itemId, questions.Clone());

        Emit(session, new CodexStreamEvent
        {
            Type = "question",
            Content = questions.ValueKind == JsonValueKind.Undefined ? null : questions.ToString(),
            MessageId = itemId,
            RequestId = itemId,
        });

        session.Info.Status = "Waiting";
        Persist(session.Info);
        SessionUpdated?.Invoke(session.Info);
    }

    /// <summary>
    /// Answers whatever single question is outstanding, for callers that only have free text and no
    /// question id. Refuses when there is more than one, since guessing which to answer would put
    /// the reply against the wrong question.
    /// </summary>
    public bool SubmitSingleAnswer(string sessionId, string answer)
    {
        if (!_sessions.TryGetValue(sessionId, out var session)) return false;
        if (session.Questions.Count != 1) return false;

        var itemId = session.Questions.Keys.First();
        return SubmitQuestionAnswer(sessionId, new Core.Sessions.SessionQuestionAnswer
        {
            RequestId = itemId,
            Response = answer,
        });
    }

    /// <summary>
    /// Answers a parked question. Returns false when the id is unknown or already answered.
    ///
    /// Codex keys its answers map by question id, while the caller may supply answers keyed by
    /// question *text*, positionally, or as one freeform response — so the parked question list is
    /// the authority for turning any of those into ids.
    /// </summary>
    public bool SubmitQuestionAnswer(string sessionId, Core.Sessions.SessionQuestionAnswer answer)
    {
        if (!_sessions.TryGetValue(sessionId, out var session)) return false;
        if (!session.Questions.TryRemove(answer.RequestId, out var pending)) return false;

        var ids = QuestionIds(pending.Questions);
        var payload = new Dictionary<string, object>();

        if (answer.Decline)
        {
            // Declining still has to produce a reply, or the turn waits forever.
            _ = session.Connection.RespondErrorAsync(pending.RequestId, "User declined to answer");
        }
        else
        {
            if (answer.Answers is { Count: > 0 })
            {
                // Keyed by question text: resolve each back to its id via the parked list.
                var byText = QuestionTextToId(pending.Questions);
                foreach (var (text, value) in answer.Answers)
                    if (byText.TryGetValue(text, out var qid))
                        payload[qid] = new { text = value };
            }

            if (payload.Count == 0 && answer.PositionalAnswers is { Count: > 0 })
                for (var i = 0; i < answer.PositionalAnswers.Count && i < ids.Count; i++)
                    payload[ids[i]] = new { text = answer.PositionalAnswers[i] };

            if (payload.Count == 0 && answer.Response != null && ids.Count > 0)
                payload[ids[0]] = new { text = answer.Response };

            _ = session.Connection.RespondAsync(pending.RequestId, new { answers = payload });
        }

        Emit(session, new CodexStreamEvent
        {
            Type = "question_resolved", MessageId = answer.RequestId, RequestId = answer.RequestId,
        });
        session.Info.Status = "Active";
        Persist(session.Info);
        SessionUpdated?.Invoke(session.Info);
        return true;
    }

    // ===== Titles =============================================================================

    /// <summary>
    /// Codex never names a thread on its own — every thread comes back with <c>name: null</c>, and
    /// <c>thread/name/set</c> is the client's job. Claude Code's CLI generates its own title, so
    /// without this a Codex discussion sits untitled in the sidebar forever.
    ///
    /// A derived title is applied immediately so the sidebar is never blank, then upgraded in the
    /// background by a one-shot on the cheap model. If that is unconfigured or fails, the derived
    /// title simply stands.
    /// </summary>
    private void EnsureTitle(ManagedSession session, string firstUserMessage)
    {
        if (!string.IsNullOrWhiteSpace(session.Info.Title)) return;

        var derived = DeriveTitle(firstUserMessage);
        if (derived != null) ApplyTitle(session, derived);

        if (!string.IsNullOrWhiteSpace(_config.TitleModel))
            _ = GenerateTitleAsync(session, firstUserMessage);
    }

    /// <summary>
    /// Names the session with a one-shot on the cheap model rather than an extra turn on the
    /// session's own — possibly flagship — model. Detached on purpose: a title is never worth
    /// delaying or failing the user's actual message.
    /// </summary>
    private async Task GenerateTitleAsync(ManagedSession session, string firstUserMessage)
    {
        try
        {
            var opening = StripContext(firstUserMessage);
            if (string.IsNullOrWhiteSpace(opening)) return;

            var excerpt = opening.Length > 800 ? opening[..800] : opening;
            var result = await _exec.ExecuteExecAsync(
                "Write a title of at most six words for a conversation that opens with the message "
                + "below. Reply with the title only: no quotes, no trailing punctuation, no preamble.\n\n"
                + excerpt,
                container: null, workingDir: null,
                model: _config.TitleModel, sandbox: "read-only", timeout: 60,
                CancellationToken.None);

            if (!result.Success || string.IsNullOrWhiteSpace(result.Text)) return;
            if (CleanGeneratedTitle(result.Text) is { } title) ApplyTitle(session, title);
        }
        catch (Exception ex)
        {
            _log($"[Codex] Title generation failed for {session.Info.Id}: {ex.Message}", null);
        }
    }

    /// <summary>Models editorialise. Strip quotes and trailing punctuation, and reject a paragraph.</summary>
    public static string? CleanGeneratedTitle(string raw)
    {
        var line = raw.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .LastOrDefault(l => l.Length > 0);
        if (line == null) return null;

        line = line.Trim('"', '\'', '`', '*', ' ').TrimEnd('.', '!', ':', ';', ',').Trim();

        // A model that ignored the instruction and wrote prose is worse than the derived title.
        return line.Length is 0 or > 70 ? null : line;
    }

    private void ApplyTitle(ManagedSession session, string title)
    {
        if (session.Info.Title == title) return;
        session.Info.Title = title;
        _jobLifecycle.Rename(session.Info);
        Persist(session.Info);
        SessionUpdated?.Invoke(session.Info);

        // Push it back to Codex too, so the thread is recognisable in the CLI and desktop app.
        if (session.Info.ThreadId is { } threadId)
            _ = session.Connection
                .SendRequestAsync("thread/name/set", new { threadId, name = title }, timeoutSeconds: 15)
                .ContinueWith(t =>
                {
                    if (t.IsFaulted)
                        _log($"[Codex] thread/name/set failed: {t.Exception?.GetBaseException().Message}", null);
                }, TaskScheduler.Default);
    }

    /// <summary>
    /// Removes Nova's injected &lt;nova-context&gt; preamble and any other markup, leaving what the
    /// user actually typed. Without this every title — and every title prompt — would start with
    /// "&lt;nova-context timestamp=...".
    /// </summary>
    public static string StripContext(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return "";

        var text = System.Text.RegularExpressions.Regex.Replace(
            message, @"<nova-context\b[^>]*>.*?</nova-context>", " ",
            System.Text.RegularExpressions.RegexOptions.Singleline);

        // An unterminated opening tag still means everything up to it is machine preamble.
        var lastTag = text.LastIndexOf("<nova-context", StringComparison.OrdinalIgnoreCase);
        if (lastTag >= 0)
        {
            var close = text.IndexOf('>', lastTag);
            text = close >= 0 ? text[(close + 1)..] : text[..lastTag];
        }

        text = System.Text.RegularExpressions.Regex.Replace(text, @"<[^>]+>", " ");
        return System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ").Trim();
    }

    /// <summary>
    /// Turns an opening message into a short label, used as the immediate title and as the fallback
    /// when model-generated naming is off or fails.
    /// </summary>
    public static string? DeriveTitle(string? message)
    {
        var text = StripContext(message);
        if (string.IsNullOrEmpty(text)) return null;

        // Prefer the first sentence, but only break on punctuation that actually ends one —
        // requiring a following space keeps "calc.py" and "v1.2" from truncating the title.
        var stop = -1;
        for (var i = 12; i < text.Length && i <= 60; i++)
        {
            if (text[i] is not ('.' or '!' or '?')) continue;
            if (i + 1 < text.Length && !char.IsWhiteSpace(text[i + 1])) continue;
            stop = i;
            break;
        }
        if (stop > 0) return text[..stop].Trim();

        if (text.Length <= 60) return text;

        var cut = text.LastIndexOf(' ', 60);
        return (cut > 20 ? text[..cut] : text[..60]).Trim() + "...";
    }

    private static List<string> QuestionIds(JsonElement questions)
    {
        var ids = new List<string>();
        if (questions.ValueKind != JsonValueKind.Array) return ids;
        foreach (var q in questions.EnumerateArray())
            if (q.TryGetProperty("id", out var qid) && qid.ValueKind == JsonValueKind.String)
                ids.Add(qid.GetString()!);
        return ids;
    }

    private static Dictionary<string, string> QuestionTextToId(JsonElement questions)
    {
        var map = new Dictionary<string, string>();
        if (questions.ValueKind != JsonValueKind.Array) return map;
        foreach (var q in questions.EnumerateArray())
        {
            if (!q.TryGetProperty("id", out var qid) || qid.ValueKind != JsonValueKind.String) continue;
            foreach (var field in new[] { "question", "text", "prompt", "label" })
                if (q.TryGetProperty(field, out var t) && t.ValueKind == JsonValueKind.String)
                {
                    map[t.GetString()!] = qid.GetString()!;
                    break;
                }
        }
        return map;
    }

    // ===== Querying ==========================================================================

    public List<CodexSessionInfo> GetSessions(int limit = 20, bool includeDismissed = false)
    {
        var live = _sessions.Values.Select(s => s.Info).ToList();
        var liveIds = live.Select(l => l.Id).ToHashSet();
        var stored = _store.GetRecentSessions(liveIds, limit, includeDismissed).Select(ToInfo);
        return live.Concat(stored).OrderByDescending(s => s.LastActivity ?? s.StartedAt).Take(limit).ToList();
    }

    public (CodexSessionInfo? Info, List<CodexMessageRecord> Messages) GetSession(string sessionId)
    {
        if (_sessions.TryGetValue(sessionId, out var live))
            return (live.Info, _store.GetMessages(sessionId));

        var record = _store.FindSession(sessionId);
        return record == null ? (null, []) : (ToInfo(record), _store.GetMessages(sessionId));
    }

    public bool IsLive(string sessionId) => _sessions.ContainsKey(sessionId);

    public async Task StopAllAsync()
    {
        foreach (var id in _sessions.Keys.ToList())
            await StopSessionAsync(id, "service_shutdown");
    }

    // ===== Mapping ===========================================================================

    private void Persist(CodexSessionInfo info) => _store.SaveSession(new CodexSessionRecord
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
        CachedInputTokens = info.CachedInputTokens,
        JobId = info.JobId,
        ThreadId = info.ThreadId,
        ProcessId = info.ProcessId,
        LastActivity = info.LastActivity,
        Effort = info.Effort,
        Source = info.Source,
        ContextWindow = info.ContextWindow,
        UserId = info.UserId,
        UserName = info.UserName,
        UserAvatarUrl = info.UserAvatarUrl,
        StopReason = info.StopReason,
    });

    private static CodexSessionInfo ToInfo(CodexSessionRecord r) => new()
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
        ThreadId = r.ThreadId,
        ProcessId = r.ProcessId,
        LastActivity = r.LastActivity,
        Effort = r.Effort,
        Source = r.Source,
        ContextWindow = r.ContextWindow,
        UserId = r.UserId,
        UserName = r.UserName,
        UserAvatarUrl = r.UserAvatarUrl,
        StopReason = r.StopReason,
    };

    public async ValueTask DisposeAsync() => await StopAllAsync();
}
