using System.Net;
using System.Net.Http;
using System.Text.Json;
using RedBamboo.AppHost.Auth;
using RedCompute.Core.Sessions;

namespace RedCompute.App.Services;

/// <summary>Where RedCompute reads the Agent permission policy. RedLeaf owns it.</summary>
public interface ISessionAccessPolicySource
{
    /// <summary>The current policy; the fail-closed defaults when it cannot be read. Never throws.</summary>
    Task<SessionAccessPolicy> GetAsync(bool refresh = false, CancellationToken ct = default);
}

/// <summary>
/// Reads <c>GET /api/agent-permissions/policy</c> from RedLeaf and keeps it at most
/// <see cref="CacheLifetime"/>. Fails closed: an unreachable RedLeaf, an error status or a body it
/// cannot read all mean default <c>ask</c> with Full disabled. A 404 is the "no record" answer and
/// reads the same. Failures are cached only briefly, so a recovered RedLeaf is seen quickly.
/// </summary>
public sealed class RedLeafSessionAccessPolicyClient : ISessionAccessPolicySource
{
    public static readonly TimeSpan CacheLifetime = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan FailureLifetime = TimeSpan.FromSeconds(5);

    private readonly HttpClient _http;
    private readonly Action<string, Guid?> _log;
    private readonly Func<DateTimeOffset> _now;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private (SessionAccessPolicy Policy, DateTimeOffset Until)? _cached;

    public RedLeafSessionAccessPolicyClient(string redLeafBaseUrl, JwtService jwtService, Action<string, Guid?> log)
        : this(new HttpClient(new BadgeBearerHandler(
            () => jwtService.GenerateAccessToken("system", "system@redsuite", "System", ["admin"])))
        {
            BaseAddress = new Uri(redLeafBaseUrl.TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromSeconds(5),
        }, log)
    {
    }

    internal RedLeafSessionAccessPolicyClient(HttpClient http, Action<string, Guid?> log, Func<DateTimeOffset>? now = null)
    {
        _http = http;
        _log = log;
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    public async Task<SessionAccessPolicy> GetAsync(bool refresh = false, CancellationToken ct = default)
    {
        if (!refresh && _cached is { } hit && hit.Until > _now()) return hit.Policy;
        await _gate.WaitAsync(ct);
        try
        {
            if (!refresh && _cached is { } again && again.Until > _now()) return again.Policy;
            var policy = await FetchAsync(ct);
            _cached = (policy, _now() + (policy.Available ? CacheLifetime : FailureLifetime));
            return policy;
        }
        finally { _gate.Release(); }
    }

    private async Task<SessionAccessPolicy> FetchAsync(CancellationToken ct)
    {
        try
        {
            using var response = await _http.GetAsync("api/agent-permissions/policy", ct);
            if (response.StatusCode == HttpStatusCode.NotFound) return SessionAccessPolicy.NoRecord;
            if (!response.IsSuccessStatusCode)
            {
                _log($"[Access] Policy read returned HTTP {(int)response.StatusCode}; failing closed (ask, Full disabled)", null);
                return SessionAccessPolicy.FailClosed;
            }
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            return Parse(document.RootElement);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _log($"[Access] Policy unavailable ({ex.Message}); failing closed (ask, Full disabled)", null);
            return SessionAccessPolicy.FailClosed;
        }
    }

    /// <summary>
    /// <c>{version, defaultLevel, agents: {agentEntityId: level}, fullAccess: {enabled, textVersion,
    /// acknowledgedAt, acknowledgedBy}}</c>, bare or under <c>policy</c>. Unknown levels are ignored,
    /// which leaves the stricter default in place.
    /// </summary>
    internal static SessionAccessPolicy Parse(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("policy", out var wrapped)
            && wrapped.ValueKind == JsonValueKind.Object)
            root = wrapped;
        if (root.ValueKind != JsonValueKind.Object) return SessionAccessPolicy.FailClosed;

        var agents = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (root.TryGetProperty("agents", out var agentMap) && agentMap.ValueKind == JsonValueKind.Object)
            foreach (var entry in agentMap.EnumerateObject())
                if (entry.Value.ValueKind == JsonValueKind.String
                    && SessionAccessLevels.TryNormalize(entry.Value.GetString(), out var level))
                    agents[entry.Name] = level;

        FullAccessAcknowledgement? full = null;
        if (root.TryGetProperty("fullAccess", out var fullAccess) && fullAccess.ValueKind == JsonValueKind.Object)
            full = new FullAccessAcknowledgement(
                fullAccess.TryGetProperty("enabled", out var enabled) && enabled.ValueKind == JsonValueKind.True,
                fullAccess.TryGetProperty("textVersion", out var text) && text.ValueKind == JsonValueKind.Number
                    && text.TryGetInt32(out var version) ? version : null,
                fullAccess.TryGetProperty("acknowledgedAt", out var at) && at.ValueKind == JsonValueKind.String
                    && DateTimeOffset.TryParse(at.GetString(), out var when) ? when : null,
                fullAccess.TryGetProperty("acknowledgedBy", out var by) && by.ValueKind == JsonValueKind.String
                    ? by.GetString() : null);

        return new SessionAccessPolicy
        {
            Version = root.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.Number
                && v.TryGetInt32(out var number) ? number : 0,
            DefaultLevel = root.TryGetProperty("defaultLevel", out var d) && d.ValueKind == JsonValueKind.String
                && SessionAccessLevels.TryNormalize(d.GetString(), out var defaultLevel) ? defaultLevel : null,
            Agents = agents,
            FullAccess = full,
        };
    }
}

/// <summary>A fixed policy, for tests and for hosts with no RedLeaf.</summary>
public sealed class StaticSessionAccessPolicySource(SessionAccessPolicy policy) : ISessionAccessPolicySource
{
    public SessionAccessPolicy Policy { get; set; } = policy;
    public int Reads { get; private set; }

    public Task<SessionAccessPolicy> GetAsync(bool refresh = false, CancellationToken ct = default)
    {
        Reads++;
        return Task.FromResult(Policy);
    }
}
