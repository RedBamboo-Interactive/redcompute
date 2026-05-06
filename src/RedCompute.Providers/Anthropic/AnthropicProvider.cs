using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using RedCompute.Core.Capabilities;
using RedCompute.Core.Configuration;
using RedCompute.Core.Jobs;
using RedCompute.Core.Providers;

namespace RedCompute.Providers.Anthropic;

public class AnthropicProvider : IBackendProvider
{
    private readonly ProviderConfig _config;
    private readonly Action<string> _log;
    private readonly string _defaultModel;
    private string? _claudePath;
    private BackendStatus _status = BackendStatus.Stopped;

    public string Name => "Anthropic";
    public CapabilityType Capability { get; }
    public TimeSpan HealthCheckInterval => TimeSpan.FromSeconds(60);

    public AnthropicProvider(ProviderConfig config, CapabilityType capability, Action<string> log)
    {
        _config = config;
        Capability = capability;
        _log = log;
        _defaultModel = GetExtra("DefaultModel", "haiku");
    }

    public Task<bool> StartAsync(CancellationToken ct = default)
    {
        _claudePath = ResolveClaudePath();
        if (_claudePath == null)
        {
            _status = BackendStatus.Error;
            _log("[AiPrompt] Could not find claude CLI on PATH or in config");
            return Task.FromResult(false);
        }

        _status = BackendStatus.Running;
        _log($"[AiPrompt] Ready (claude: {_claudePath}, default model: {_defaultModel})");
        return Task.FromResult(true);
    }

    public Task StopAsync(CancellationToken ct = default)
    {
        _status = BackendStatus.Stopped;
        _log("[AiPrompt] Stopped");
        return Task.CompletedTask;
    }

    public Task<BackendStatus> GetStatusAsync(CancellationToken ct = default) =>
        Task.FromResult(_status);

    public string? GetProxyTargetUrl() => null;

    public async Task<JobResult?> ExecuteAsync(JobRequest request, CancellationToken ct = default)
    {
        if (_claudePath == null)
            return new JobResult { Success = false, ErrorMessage = "claude CLI not found" };

        var p = request.Parameters;

        var model = GetParam<string>(p, "model") ?? _defaultModel;
        var system = GetParam<string>(p, "system");
        var maxTokens = GetParam<int?>(p, "maxTokens") ?? 1024;
        var messagesRaw = p.TryGetValue("messages", out var msgVal) ? msgVal : null;

        if (messagesRaw == null)
            return new JobResult { Success = false, ErrorMessage = "messages is required" };

        var prompt = BuildPrompt(messagesRaw);
        if (string.IsNullOrWhiteSpace(prompt))
            return new JobResult { Success = false, ErrorMessage = "messages produced empty prompt" };

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = _claudePath,
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            startInfo.ArgumentList.Add("-p");
            startInfo.ArgumentList.Add("--output-format");
            startInfo.ArgumentList.Add("json");
            startInfo.ArgumentList.Add("--model");
            startInfo.ArgumentList.Add(model);
            startInfo.ArgumentList.Add("--no-session-persistence");
            if (!string.IsNullOrEmpty(system))
            {
                startInfo.ArgumentList.Add("--system-prompt");
                startInfo.ArgumentList.Add(system);
            }

            using var process = Process.Start(startInfo);
            if (process == null)
                return new JobResult { Success = false, ErrorMessage = "Failed to start claude process" };

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(30));

            await process.StandardInput.WriteAsync(prompt);
            process.StandardInput.Close();

            var stdout = await process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
            var stderr = await process.StandardError.ReadToEndAsync(timeoutCts.Token);

            await process.WaitForExitAsync(timeoutCts.Token);

            if (process.ExitCode != 0 && string.IsNullOrWhiteSpace(stdout))
            {
                var errMsg = stderr.Length > 300 ? stderr[..300] : stderr;
                _log($"[AiPrompt] claude exited {process.ExitCode}: {errMsg}");
                return new JobResult { Success = false, ErrorMessage = $"claude exited with code {process.ExitCode}: {errMsg}" };
            }

            var text = stdout.Trim();
            var inputTokens = 0;
            var outputTokens = 0;
            var actualModel = model;

            // claude --output-format json returns a JSON array of events
            // Find the "result" event which contains the final text and usage
            try
            {
                using var doc = JsonDocument.Parse(stdout);
                var root = doc.RootElement;

                if (root.ValueKind == JsonValueKind.Array)
                {
                    foreach (var evt in root.EnumerateArray())
                    {
                        var evtType = evt.TryGetProperty("type", out var t) ? t.GetString() : null;
                        if (evtType == "result")
                        {
                            if (evt.TryGetProperty("result", out var r))
                                text = r.GetString() ?? text;

                            if (evt.TryGetProperty("usage", out var usage))
                            {
                                if (usage.TryGetProperty("input_tokens", out var it)) inputTokens = it.GetInt32();
                                if (usage.TryGetProperty("output_tokens", out var ot)) outputTokens = ot.GetInt32();
                            }

                            if (evt.TryGetProperty("total_cost_usd", out var cost))
                                _log($"[AiPrompt] Cost: ${cost.GetDouble():F6}");

                            break;
                        }

                        if (evtType == "system" && evt.TryGetProperty("model", out var m))
                            actualModel = m.GetString() ?? model;
                    }
                }
            }
            catch (JsonException)
            {
                // Plain text output -- use as-is
            }

            var resultJson = JsonSerializer.Serialize(new
            {
                text,
                model = actualModel,
                inputTokens,
                outputTokens
            });

            _log($"[AiPrompt] {actualModel} done ({text.Length} chars)");

            return new JobResult
            {
                Success = true,
                ResultJson = resultJson,
                ContentType = "application/json"
            };
        }
        catch (TaskCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log($"[AiPrompt] Exception: {ex.Message}");
            return new JobResult { Success = false, ErrorMessage = ex.Message };
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
    }

    private static string BuildPrompt(object messagesRaw)
    {
        var sb = new StringBuilder();

        IEnumerable<JsonElement>? elements = null;
        if (messagesRaw is JsonElement je && je.ValueKind == JsonValueKind.Array)
            elements = je.EnumerateArray();

        if (elements == null)
            return messagesRaw.ToString() ?? "";

        foreach (var msg in elements)
        {
            var role = msg.TryGetProperty("role", out var r) ? r.GetString() : "user";
            var content = msg.TryGetProperty("content", out var c) ? c.GetString() : "";
            if (string.IsNullOrEmpty(content)) continue;

            if (role == "assistant")
                sb.AppendLine($"[Assistant]: {content}");
            else
                sb.AppendLine($"[User]: {content}");
        }

        return sb.ToString().TrimEnd();
    }

    private string? ResolveClaudePath()
    {
        // 1. Explicit path from provider config Extra
        var configPath = GetExtra("ClaudePath", "");
        if (!string.IsNullOrEmpty(configPath) && File.Exists(configPath))
            return configPath;

        // 2. Agent SDK location
        var npmGlobal = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var sdkExe = Path.Combine(npmGlobal, "npm", "node_modules", "@anthropic-ai",
            "claude-agent-sdk", "node_modules", "@anthropic-ai",
            "claude-agent-sdk-win32-x64", "claude.exe");
        if (File.Exists(sdkExe))
            return sdkExe;

        // 3. Search PATH
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

    private string GetExtra(string key, string defaultValue)
    {
        if (_config.Extra != null && _config.Extra.TryGetValue(key, out var val) && val != null)
            return val.ToString()!;
        return defaultValue;
    }

    private static T? GetParam<T>(Dictionary<string, object?> p, string key)
    {
        if (!p.TryGetValue(key, out var val) || val == null) return default;
        if (val is T t) return t;
        if (val is JsonElement je)
        {
            if (typeof(T) == typeof(string)) return (T)(object)(je.GetString() ?? "");
            if (typeof(T) == typeof(int?) || typeof(T) == typeof(int))
            {
                if (je.TryGetInt32(out var i)) return (T)(object)i;
            }
        }
        try { return (T)Convert.ChangeType(val, Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T)); }
        catch { return default; }
    }
}
