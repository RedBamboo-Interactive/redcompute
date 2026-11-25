using System.Text.RegularExpressions;
using RedCompute.Core.Sessions;
using RedCompute.PluginSdk;

namespace RedCompute.Plugin.OpenCode;

/// <summary>
/// The models this machine's OpenCode can run, read from <c>opencode models</c> (one <c>provider/model</c> per line).
///
/// This replaces a hardcoded list (Claude Sonnet 4, Claude Opus 4, GPT-4o, Gemini 2.5 Pro) that described no real
/// install: on 25 Nov 2026 the local OpenCode had no anthropic, openai or google provider at all, so the list hid
/// that the standard-opencode quality mode named a model ("sonnet") OpenCode could not resolve. What OpenCode
/// offers depends on its configured providers and credentials, which is why it must come from OpenCode itself.
/// </summary>
public sealed partial class OpenCodeModelCatalog(
    Func<string?> resolveCliPath, Action<string, Guid?> log, ICliCommandRunner? runner = null)
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(60);

    private readonly ICliCommandRunner _runner = runner ?? new ProcessCliCommandRunner();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IReadOnlyList<ModelInfo>? _cached;
    private DateTimeOffset _fetchedAt;
    private int _priming;

    /// <summary>Last fetch failure, while the catalog serves nothing or a stale list.</summary>
    public string? LastError { get; private set; }

    /// <summary>
    /// Cache-only view for the synchronous <see cref="ISessionProvider.GetAvailableModels"/>: empty until a fetch
    /// has succeeded. An empty or expired cache starts one refresh in the background, never blocking the caller.
    /// </summary>
    public IReadOnlyList<ModelInfo> Cached
    {
        get
        {
            if (_cached is not { Count: > 0 } || DateTimeOffset.UtcNow - _fetchedAt >= CacheTtl)
                _ = PrimeAsync();
            return _cached ?? [];
        }
    }

    /// <summary>One background refresh at a time; failures are logged, not thrown.</summary>
    public async Task PrimeAsync(CancellationToken ct = default)
    {
        if (Interlocked.Exchange(ref _priming, 1) == 1) return;
        try { await GetAsync(forceRefresh: true, ct); }
        catch (Exception ex) { log($"[OpenCode] Model catalog prime failed: {ex.Message}", null); }
        finally { Volatile.Write(ref _priming, 0); }
    }

    public async Task<IReadOnlyList<ModelInfo>> GetAsync(bool forceRefresh = false, CancellationToken ct = default)
    {
        if (!forceRefresh && _cached is { Count: > 0 } && DateTimeOffset.UtcNow - _fetchedAt < CacheTtl)
            return _cached;

        await _gate.WaitAsync(ct);
        try
        {
            if (!forceRefresh && _cached is { Count: > 0 } && DateTimeOffset.UtcNow - _fetchedAt < CacheTtl)
                return _cached;

            var models = await FetchAsync(ct);
            _cached = models;
            _fetchedAt = DateTimeOffset.UtcNow;
            LastError = null;
            return models;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            LastError = ex.Message;
            log($"[OpenCode] opencode models failed: {ex.Message}", null);
            // A stale list beats none: a transient failure should not empty the model picker.
            return _cached ?? [];
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<IReadOnlyList<ModelInfo>> FetchAsync(CancellationToken ct)
    {
        var cli = resolveCliPath()
            ?? throw new InvalidOperationException("Could not find the 'opencode' CLI.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(CommandTimeout);
        var result = await _runner.RunAsync(new CliCommand(cli, ["models"]), timeout.Token);
        if (result.ExitCode != 0)
            throw new InvalidOperationException(
                $"opencode models exited {result.ExitCode}: {Truncate(result.CombinedOutput, 300)}");
        var models = Parse(result.StandardOutput);
        if (models.Count == 0)
            throw new InvalidOperationException("opencode models listed no model.");
        return models;
    }

    /// <summary>
    /// Every <c>provider/model</c> line, in OpenCode's order, without duplicates. Colour codes and anything that
    /// is not a model id (a warning, a banner) are ignored.
    /// </summary>
    internal static IReadOnlyList<ModelInfo> Parse(string output)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var models = new List<ModelInfo>();
        foreach (var raw in output.Split('\n'))
        {
            var line = Ansi().Replace(raw, "").Trim();
            if (!ModelId().IsMatch(line) || !seen.Add(line)) continue;
            models.Add(new ModelInfo { Id = line, Name = line, Fast = false });
        }
        return models;
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];

    [GeneratedRegex(@"\x1B\[[0-9;?]*[A-Za-z]")]
    private static partial Regex Ansi();

    // provider/model, where the model part may itself contain slashes (openrouter/vendor/model).
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]*/[A-Za-z0-9._:@/-]*[A-Za-z0-9._:@-]$")]
    private static partial Regex ModelId();
}
