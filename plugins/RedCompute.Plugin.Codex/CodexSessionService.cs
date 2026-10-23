using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using RedCompute.Core.Sessions;
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

    /// <summary>Test seam for non-interactive exec invocations.</summary>
    internal Func<ProcessStartInfo, Process?> LaunchExecProcess { get; set; } = Process.Start;
    internal ICliProcessLaunchGate LaunchGate { get; set; } = UnrestrictedCliProcessLaunchGate.Instance;

    public CodexSessionService(CodexConfig config, IJobTracker jobTracker, ICodexSessionStore store, Action<string, Guid?> log)
    {
        _config = config;
        _jobTracker = jobTracker;
        _store = store;
        _log = log;
        RecoverSessions(_store, _log);
    }

    /// <summary>
    /// 0x40010004: Windows terminates console children with this code at shutdown or logoff,
    /// often before RedCompute is notified, so the exit races the infrastructure stop.
    /// </summary>
    internal const string WindowsTerminatedStopReason = "process_exited:1073807364";

    internal static void RecoverSessions(
        ICodexSessionStore store,
        Action<string, Guid?> log)
    {
        try
        {
            var active = store.GetActiveSessions();
            foreach (var s in active)
            {
                s.Status = "Stopped";
                s.StopReason = RecoveredStopReason(s.Id);
                s.StoppedAt = DateTimeOffset.UtcNow;
                log($"[Codex] Marked recovered session {s.Id} ({s.ProjectName}) as stopped ({s.StopReason})", null);
                store.SaveSession(s);
            }

            foreach (var s in store.GetSessionsWithStopReason("Error", WindowsTerminatedStopReason))
            {
                if (s.Dismissed || string.IsNullOrWhiteSpace(s.ThreadId)) continue;
                s.Status = "Stopped";
                s.ProcessId = null;
                s.StopReason = RecoveredStopReason(s.Id);
                s.StoppedAt = DateTimeOffset.UtcNow;
                log($"[Codex] Marked Windows-terminated session {s.Id} ({s.ProjectName}) as stopped ({s.StopReason})", null);
                store.SaveSession(s);
            }
        }
        catch (Exception ex)
        {
            log($"[Codex] Failed to recover sessions: {ex.Message}", null);
        }
    }

    private static string RecoveredStopReason(string sessionId)
        => PlannedRestartCheckpoint.ContainsSession("codex", sessionId)
            ? SessionStopReasons.MaintenanceRestart
            : SessionStopReasons.OrphanedOnRestart;

    public record ExecuteResult(bool Success, string? Text, string? StreamOutput, string? Model,
                                int InputTokens, int OutputTokens, double? CostUsd, string? Error,
                                string? Isolation = null, string? IsolationMechanism = null,
                                bool NativeResponseSchema = false);

    internal const string ToolFreeProviderId = "redcompute_tool_free";

    private CodexToolFreeRelay? _toolFreeRelay;

    /// <summary>Loopback model-boundary relay for tool-free generate; created on first use. Test seam.</summary>
    internal CodexToolFreeRelay ToolFreeRelay
    {
        get
        {
            if (_toolFreeRelay != null) return _toolFreeRelay;
            var created = new CodexToolFreeRelay(_log);
            if (Interlocked.CompareExchange(ref _toolFreeRelay, created, null) != null)
                _ = created.DisposeAsync().AsTask();
            return _toolFreeRelay!;
        }
        set => _toolFreeRelay = value;
    }

    public Task<ExecuteResult> ExecuteExecAsync(
        string prompt, string? container, string? workingDir,
        string? model, string? sandbox, int timeout,
        CancellationToken ct,
        string? streamKey = null,
        Dictionary<string, string>? env = null,
        bool networkAccess = false,
        string? effort = null,
        IReadOnlyList<string>? extraArgs = null,
        bool retryTransientLocks = true)
        => LaunchGate.RunWhenInstallIdleAsync(
            () => ExecuteExecCoreAsync(prompt, container, workingDir, model, sandbox, timeout,
                ct, streamKey, env, networkAccess, effort, extraArgs, retryTransientLocks),
            ct);

    private async Task<ExecuteResult> ExecuteExecCoreAsync(
        string prompt, string? container, string? workingDir,
        string? model, string? sandbox, int timeout,
        CancellationToken ct,
        string? streamKey,
        Dictionary<string, string>? env,
        bool networkAccess,
        string? effort,
        IReadOnlyList<string>? extraArgs,
        bool retryTransientLocks)
    {
        ExecuteResult? last = null;
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            last = await ExecuteExecOnceAsync(prompt, container, workingDir, model, sandbox,
                timeout, ct, streamKey, env, networkAccess, effort, extraArgs);
            if (last.Success || !retryTransientLocks || !IsTransientWorkspaceLock(last) || attempt == 3)
                return last;

            var delay = TimeSpan.FromMilliseconds(400 * attempt);
            _log($"[Codex] Workspace file was temporarily locked; retrying in {delay.TotalMilliseconds:0}ms " +
                 $"(attempt {attempt + 1}/3)", null);
            await Task.Delay(delay, ct);
        }

        return last!;
    }

    /// <summary>
    /// Runs the existing Codex generate behavior from a fresh empty directory. Codex's own tools
    /// remain available to the model, bounded only by the read-only sandbox, so this reports
    /// sandboxed-best-effort. <see cref="ExecuteToolFreeGenerateAsync"/> is the verified path.
    /// Only <see cref="ResponseSchema.StrictJson"/> is ever passed as --output-schema.
    /// </summary>
    public Task<ExecuteResult> ExecuteGenerateAsync(
        string prompt, string? model, int timeout, CancellationToken ct, string? effort = null,
        ResponseSchema? responseSchema = null, IReadOnlyList<GenerateImage>? images = null)
        => WithSchemaFallbackAsync(responseSchema, async strictSchema =>
        {
            using var workingDirectory = IsolatedWorkingDirectory.Create("redcompute-codex-generate");
            using var schema = OutputSchemaFile.Create(strictSchema);
            using var imageFiles = ImageFiles.Create(images);
            var args = new List<string>();
            if (schema != null) args.AddRange(schema.Args);
            if (imageFiles != null) args.AddRange(imageFiles.Args);
            var result = await ExecuteExecAsync(prompt, null, workingDirectory.Path, model, "read-only",
                timeout, ct, effort: effort, extraArgs: args);
            return result with
            {
                Isolation = SessionIsolation.SandboxedBestEffort,
                IsolationMechanism = "codex-sandbox:read-only",
            };
        });

    /// <summary>
    /// Generate with every tool removed at the model boundary. Codex is pointed at a private route
    /// on <see cref="CodexToolFreeRelay"/> through a custom model provider, so each model request
    /// is stripped of tool definitions and each response is inspected before Codex can act on it.
    /// The text is returned only when the relay verified the call and Codex's own event stream
    /// shows no tool item; otherwise the call fails closed without model text. Nothing is retried
    /// behind the caller's back (see <see cref="ToolFreeExecArgs"/>), so one call is billed once.
    /// </summary>
    /// <remarks>
    /// Images are not handed to Codex at all. Codex's own <c>--image</c> flag re-encodes large images
    /// (0.163 sent a 3000x2000 PNG as 1920x1280) and announces the file path to the model, so the
    /// relay could not prove which bytes reached it. Instead the relay injects the validated bytes
    /// into each model request itself and the call is verified only if every request carried them.
    /// </remarks>
    public Task<ExecuteResult> ExecuteToolFreeGenerateAsync(
        string prompt, string? model, int timeout, CancellationToken ct, string? effort = null,
        ResponseSchema? responseSchema = null, Action<ToolFreeEvidence>? evidence = null,
        IReadOnlyList<GenerateImage>? images = null)
        => WithSchemaFallbackAsync(responseSchema, strictSchema =>
            ExecuteToolFreeGenerateOnceAsync(prompt, model, timeout, ct, effort, strictSchema, evidence, images));

    private async Task<ExecuteResult> ExecuteToolFreeGenerateOnceAsync(
        string prompt, string? model, int timeout, CancellationToken ct, string? effort,
        string? strictSchema, Action<ToolFreeEvidence>? evidence, IReadOnlyList<GenerateImage>? images)
    {
        using var workingDirectory = IsolatedWorkingDirectory.Create("redcompute-codex-generate");
        using var schema = OutputSchemaFile.Create(strictSchema);
        using var lease = await ToolFreeRelay.OpenAsync(ct, images);

        var args = ToolFreeExecArgs(lease.BaseUrl, timeout);
        if (schema != null) args.AddRange(schema.Args);

        var result = await ExecuteExecAsync(prompt, null, workingDirectory.Path, model, "read-only",
            timeout, ct, effort: effort, extraArgs: args, retryTransientLocks: false);
        evidence?.Invoke(lease.Evidence);

        var toolItems = ExecToolItemTypes(result.StreamOutput);
        var refusal = toolItems.Count > 0
            ? $"Codex reported tool items: {string.Join(", ", toolItems)}"
            : lease.Evidence.Refusals.Count > 0 ? lease.Evidence.VerificationError : null;
        // An ordinary failure (auth, quota, timeout) keeps Codex's own error; it returns no text.
        if (refusal == null && !result.Success)
            return result;
        refusal ??= lease.Evidence.VerificationError;
        if (refusal != null)
        {
            _log($"[Codex] Tool-free generate failed closed: {refusal}", null);
            return new ExecuteResult(false, null, result.StreamOutput, result.Model,
                result.InputTokens, result.OutputTokens, result.CostUsd,
                $"Codex tool-free generate could not be verified: {refusal}");
        }

        _log($"[Codex] Tool-free generate verified: {lease.Evidence.Requests} request(s), " +
             $"{lease.Evidence.Responses} response(s), {lease.Evidence.StrippedToolDefinitions} tool definition(s) removed", null);
        return result with
        {
            Isolation = SessionIsolation.VerifiedToolFree,
            IsolationMechanism = "codex-model-boundary-relay",
        };
    }

    /// <summary>
    /// Runs with the strict schema when there is one. If the model API refused that schema
    /// (HTTP 400 <c>invalid_json_schema</c> on <c>text.format.schema</c>, rejected before any
    /// generation, so nothing was billed), runs once more in prompt mode rather than failing.
    /// </summary>
    private async Task<ExecuteResult> WithSchemaFallbackAsync(ResponseSchema? responseSchema,
        Func<string?, Task<ExecuteResult>> run)
    {
        var strict = responseSchema?.StrictJson;
        if (strict == null)
            return await run(null);

        var result = await run(strict);
        if (!IsSchemaRejection(result))
            return result with { NativeResponseSchema = result.Success };

        _log("[Codex] Model API rejected the strict output schema; retrying once in prompt mode", null);
        return await run(null);
    }

    /// <summary>
    /// True only for Codex's own turn failure carrying the API's schema rejection. Model text can
    /// never satisfy this: agent messages are items, not turn errors.
    /// </summary>
    internal static bool IsSchemaRejection(ExecuteResult result)
    {
        if (result.Success || string.IsNullOrEmpty(result.StreamOutput)) return false;
        foreach (var line in result.StreamOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object
                    || !root.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String
                    || type.GetString() != "turn.failed"
                    || !root.TryGetProperty("error", out var error) || error.ValueKind != JsonValueKind.Object
                    || !error.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.String)
                    continue;
                var text = message.GetString()!;
                if (text.Contains("\"invalid_json_schema\"", StringComparison.Ordinal)
                    && text.Contains("\"text.format.schema\"", StringComparison.Ordinal))
                    return true;
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException) { }
        }
        return false;
    }

    /// <summary>
    /// Exec flags for tool-free generate. User config, rules and session files are left out so
    /// nothing ambient (MCP servers, hooks, providers) joins the run, and request compression is
    /// disabled so the relay can read the body (an encoded body is refused, never forwarded).
    ///
    /// The relay withholds the response until the whole upstream answer is inspected. Codex 0.163
    /// would otherwise retry a stalled stream five times (six billed requests, observed against a
    /// stalling loopback server) and retry failed requests, so both retry counts are zero and the
    /// stream idle timeout is pushed past the call's own timeout. The idle timer only runs once
    /// response headers arrive (a header-less stall did not trip a 3 s idle timeout), so the
    /// call timeout, enforced by RedCompute killing the process, is the bound that applies.
    /// </summary>
    internal static List<string> ToolFreeExecArgs(Uri relayBaseUrl, int timeoutSeconds) =>
    [
        "--ephemeral",
        "--ignore-user-config",
        "--ignore-rules",
        "--disable", "enable_request_compression",
        "-c", $"model_provider=\"{ToolFreeProviderId}\"",
        "-c", $"model_providers.{ToolFreeProviderId}.name=\"RedCompute tool-free relay\"",
        "-c", $"model_providers.{ToolFreeProviderId}.base_url=\"{relayBaseUrl}\"",
        "-c", $"model_providers.{ToolFreeProviderId}.wire_api=\"responses\"",
        "-c", $"model_providers.{ToolFreeProviderId}.requires_openai_auth=true",
        "-c", $"model_providers.{ToolFreeProviderId}.request_max_retries=0",
        "-c", $"model_providers.{ToolFreeProviderId}.stream_max_retries=0",
        "-c", $"model_providers.{ToolFreeProviderId}.stream_idle_timeout_ms={ToolFreeStreamIdleTimeoutMs(timeoutSeconds)}",
    ];

    internal static long ToolFreeStreamIdleTimeoutMs(int timeoutSeconds)
        => (Math.Max(timeoutSeconds, 1) + 60L) * 1000;

    private static readonly HashSet<string> NonToolExecItems = ["agentMessage", "reasoning", "error"];

    /// <summary>
    /// Codex exec item types in a JSONL stream that are not plain messages or reasoning. This is a
    /// security check, so it fails closed: an item it cannot read is reported as a tool item, never
    /// skipped, and nothing in a line can make it throw.
    /// </summary>
    internal static List<string> ExecToolItemTypes(string? stdout)
    {
        var found = new List<string>();
        if (string.IsNullOrEmpty(stdout)) return found;

        void Add(string itemType)
        {
            if (!NonToolExecItems.Contains(itemType) && !found.Contains(itemType))
                found.Add(itemType);
        }

        foreach (var rawLine in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            if (line.Length == 0) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) continue;
                var hasItem = root.TryGetProperty("item", out var item);
                if (!root.TryGetProperty("type", out var t) || t.ValueKind != JsonValueKind.String)
                {
                    if (hasItem) Add("(uninspectable)");
                    continue;
                }
                if (t.GetString() is not ("item.started" or "item.updated" or "item.completed") || !hasItem)
                    continue;
                if (item.ValueKind != JsonValueKind.Object)
                {
                    Add("(uninspectable)");
                    continue;
                }
                var itemType = item.TryGetProperty("type", out var it) && it.ValueKind == JsonValueKind.String
                    ? CodexEventMapper.NormaliseItemType(it.GetString()) ?? "(untyped)"
                    : "(untyped)";
                Add(itemType);
            }
            catch (JsonException)
            {
                // Codex writes one JSON object per line; a broken object line could hide an item.
                if (line.StartsWith('{')) Add("(unparseable)");
            }
            catch (InvalidOperationException)
            {
                Add("(uninspectable)");
            }
        }
        return found;
    }

    /// <summary>
    /// Validated images for the non-tool-free path's --image flag, written to a private directory
    /// outside the empty working directory and deleted with the call.
    /// </summary>
    private sealed class ImageFiles : IDisposable
    {
        private readonly IsolatedWorkingDirectory _directory;

        private ImageFiles(IReadOnlyList<GenerateImage> images)
        {
            _directory = IsolatedWorkingDirectory.Create("redcompute-codex-images");
            var args = new List<string>();
            for (var i = 0; i < images.Count; i++)
            {
                var extension = images[i].MediaType switch
                {
                    "image/jpeg" => ".jpg",
                    "image/webp" => ".webp",
                    _ => ".png",
                };
                var path = Path.Combine(_directory.Path, $"image-{i + 1}{extension}");
                File.WriteAllBytes(path, images[i].Bytes);
                // One "--image=" per file: the flag takes several values, so a bare "-i" could
                // swallow whatever argument follows it.
                args.Add($"--image={path}");
            }
            Args = args;
        }

        public IReadOnlyList<string> Args { get; }

        public static ImageFiles? Create(IReadOnlyList<GenerateImage>? images)
            => images is { Count: > 0 } ? new ImageFiles(images) : null;

        public void Dispose() => _directory.Dispose();
    }

    /// <summary>A response schema written where only Codex reads it, outside the empty working directory.</summary>
    private sealed class OutputSchemaFile : IDisposable
    {
        private readonly IsolatedWorkingDirectory _directory;

        private OutputSchemaFile(string json)
        {
            _directory = IsolatedWorkingDirectory.Create("redcompute-codex-schema");
            var path = Path.Combine(_directory.Path, "output-schema.json");
            File.WriteAllText(path, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            Args = ["--output-schema", path];
        }

        public IReadOnlyList<string> Args { get; }

        public static OutputSchemaFile? Create(string? json)
            => string.IsNullOrWhiteSpace(json) ? null : new OutputSchemaFile(json);

        public void Dispose() => _directory.Dispose();
    }

    private static bool IsTransientWorkspaceLock(ExecuteResult result)
    {
        var detail = string.Join('\n', result.Error, result.Text, result.StreamOutput);
        return detail.Contains("being used by another process", StringComparison.OrdinalIgnoreCase)
            || detail.Contains("process cannot access the file", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<ExecuteResult> ExecuteExecOnceAsync(
        string prompt, string? container, string? workingDir,
        string? model, string? sandbox, int timeout,
        CancellationToken ct, string? streamKey,
        Dictionary<string, string>? env,
        bool networkAccess,
        string? effort,
        IReadOnlyList<string>? extraArgs)
    {
        var useDocker = !string.IsNullOrWhiteSpace(container);

        if (!useDocker)
        {
            var codexPath = ResolveCodexPath();
            if (codexPath == null)
                return new ExecuteResult(false, null, null, null, 0, 0, null,
                    "Could not find the Codex CLI. Install the official standalone package or set CodexPath in config.");
        }

        var startInfo = new ProcessStartInfo
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        if (useDocker)
        {
            DockerExecHelper.ConfigureForDockerExec(startInfo, container!, "codex", workingDir, env);
        }
        else
        {
            startInfo.FileName = ResolveCodexPath()!;
            if (!string.IsNullOrWhiteSpace(workingDir))
                startInfo.WorkingDirectory = workingDir;
            if (env is not null)
                foreach (var (k, v) in env)
                    startInfo.EnvironmentVariables[k] = v;
        }

        BuildExecArgs(startInfo, model, sandbox, networkAccess, effort);
        if (extraArgs != null)
            foreach (var arg in extraArgs)
                startInfo.ArgumentList.Add(arg);

        var sw = Stopwatch.StartNew();
        using var process = LaunchExecProcess(startInfo);
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

    private void BuildExecArgs(ProcessStartInfo startInfo, string? model, string? sandbox,
        bool networkAccess, string? effort)
    {
        startInfo.ArgumentList.Add("exec");
        startInfo.ArgumentList.Add("-");
        startInfo.ArgumentList.Add("--json");

        // Without this, exec refuses to run anywhere that is not a trusted git repo — "Not inside a
        // trusted directory" — which kills every call with no working directory and every call
        // against a plain folder. The sandbox flag below is what actually bounds what it can touch.
        startInfo.ArgumentList.Add("--skip-git-repo-check");

        var resolvedSandbox = sandbox ?? _config.SandboxMode;
        startInfo.ArgumentList.Add("--sandbox");
        startInfo.ArgumentList.Add(resolvedSandbox);

        // Unattended Nova jobs need the same localhost and outbound command access Claude Code
        // had. This stable legacy setting composes with --sandbox workspace-write; permission
        // profiles do not, and Codex 0.146 cannot express dotted IP keys through inline -c.
        if (resolvedSandbox == "workspace-write" && networkAccess)
            AddConfig(startInfo, "sandbox_workspace_write.network_access=true");

        if (IsSafeEffort(effort))
            AddConfig(startInfo, $"model_reasoning_effort=\"{effort}\"");

        var resolvedModel = model ?? _config.Model;
        if (!string.IsNullOrEmpty(resolvedModel))
        {
            startInfo.ArgumentList.Add("--model");
            startInfo.ArgumentList.Add(resolvedModel);
        }
    }

    private static void AddConfig(ProcessStartInfo startInfo, string value)
    {
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add(value);
    }

    private static bool IsSafeEffort(string? effort)
        => effort is "none" or "minimal" or "low" or "medium" or "high" or "xhigh" or "max" or "ultra";

    internal static List<CodexStreamEvent> ParseExecStreamLine(string line)
    {
        var events = new List<CodexStreamEvent>();
        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;
        var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;

        if (type == "item.completed")
        {
            var item = root.TryGetProperty("item", out var i) ? i : root;
            var itemType = CodexEventMapper.NormaliseItemType(
                item.TryGetProperty("type", out var it) ? it.GetString() : null);

            switch (itemType)
            {
                case "agentMessage":
                {
                    var content = ExtractTextContent(item);
                    if (content != null)
                        events.Add(new CodexStreamEvent
                        {
                            Type = "text",
                            Content = content,
                            Phase = ExtractMessagePhase(item),
                        });
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
            var itemType = CodexEventMapper.NormaliseItemType(
                item.TryGetProperty("type", out var it) ? it.GetString() : null);

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

    private static string? ExtractMessagePhase(JsonElement item)
    {
        var phase = item.TryGetProperty("phase", out var value)
            && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        return phase is "commentary" or "final_answer" ? phase : null;
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
                    var itemType = CodexEventMapper.NormaliseItemType(
                        item.TryGetProperty("type", out var it2) ? it2.GetString() : null);

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
            // A line whose fields have unexpected kinds or undecodable text is skipped, not fatal.
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException) { }
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

    public async Task StopAllAsync()
    {
        foreach (var key in _runningProcesses.Keys.ToList())
            CancelExecution(key);
        await Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _toolFreeRelay, null) is { } relay)
            await relay.DisposeAsync();
    }

    private static CodexSessionInfo ToSessionInfo(CodexSessionRecord r) => new()
    {
        Id = r.Id,
        ProjectName = r.ProjectName,
        ProjectPath = r.ProjectPath,
        RepositoryId = r.RepositoryId,
        Status = r.Status,
        StartedAt = r.StartedAt,
        Model = r.Model,
        Title = r.Title,
        MessageCount = r.MessageCount,
        CostUsd = r.CostUsd,
        InputTokens = r.InputTokens,
        OutputTokens = r.OutputTokens,
        CachedInputTokens = r.CachedInputTokens,
        ContextTokens = r.ContextTokens,
        JobId = r.JobId,
        StopReason = r.StopReason,
        StoppedAt = r.StoppedAt,
    };

    public string? ResolveCodexPath() => CodexCliLocator.Resolve(_config.CodexPath);
}
