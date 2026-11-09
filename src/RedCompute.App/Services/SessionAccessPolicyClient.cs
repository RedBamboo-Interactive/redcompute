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
/// cannot read all mean default <c>ask</c> with Full disabled. No policy record (404, an empty
/// answer, or RedLeaf's own no-record shape confirmed by revision 0) means default <c>auto</c> with
/// Full disabled (<see cref="SessionAccessPolicy.NoRecord"/>). Failures are cached only briefly, so a
/// recovered RedLeaf is seen quickly.
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
            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.NoContent) return SessionAccessPolicy.NoRecord;
            if (!response.IsSuccessStatusCode)
            {
                _log($"[Access] Policy read returned HTTP {(int)response.StatusCode}; failing closed (ask, Full disabled)", null);
                return SessionAccessPolicy.FailClosed;
            }
            var text = await response.Content.ReadAsStringAsync(ct);
            if (string.IsNullOrWhiteSpace(text)) return SessionAccessPolicy.NoRecord;
            using var document = JsonDocument.Parse(text);
            var root = Unwrap(document.RootElement);
            if (IsEmpty(root)) return SessionAccessPolicy.NoRecord;
            var policy = Parse(root);
            if (!policy.Available || !LooksLikeNoRecord(root)) return policy;
            // RedLeaf answers "no record" with the same compact shape as an owner's saved plain Ask.
            // Its revision tells them apart: 0 is no record, a saved policy is 1 or more.
            var revision = Revision(root) ?? await ReadRevisionAsync(ct);
            return revision == 0 ? SessionAccessPolicy.NoRecord : policy;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _log($"[Access] Policy unavailable ({ex.Message}); failing closed (ask, Full disabled)", null);
            return SessionAccessPolicy.FailClosed;
        }
    }

    /// <summary>
    /// The policy's revision from RedLeaf's Settings view (<c>GET /api/agent-permissions</c>), read
    /// only when the compact policy looks like RedLeaf's no-record answer. Null when it cannot be
    /// read, which keeps the compact reading (the stricter ask).
    /// </summary>
    private async Task<long?> ReadRevisionAsync(CancellationToken ct)
    {
        try
        {
            using var response = await _http.GetAsync("api/agent-permissions", ct);
            if (!response.IsSuccessStatusCode)
            {
                _log($"[Access] Policy revision read returned HTTP {(int)response.StatusCode}; reading the policy as saved (ask)", null);
                return null;
            }
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            return document.RootElement.ValueKind == JsonValueKind.Object ? Revision(document.RootElement) : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _log($"[Access] Policy revision unavailable ({ex.Message}); reading the policy as saved (ask)", null);
            return null;
        }
    }

    private static JsonElement Unwrap(JsonElement root)
        => root.ValueKind == JsonValueKind.Object && root.TryGetProperty("policy", out var wrapped)
            && wrapped.ValueKind == JsonValueKind.Object
                ? wrapped
                : root;

    /// <summary>An empty answer: JSON null or an object with nothing in it.</summary>
    private static bool IsEmpty(JsonElement root)
        => root.ValueKind == JsonValueKind.Null
            || (root.ValueKind == JsonValueKind.Object && !root.EnumerateObject().Any());

    private static long? Revision(JsonElement root)
        => root.TryGetProperty("revision", out var revision) && revision.ValueKind == JsonValueKind.Number
            && revision.TryGetInt64(out var value) ? value : null;

    /// <summary>
    /// RedLeaf's <c>AgentAccessPolicy.None</c> in the compact shape: default ask, no Agent entries,
    /// Full access never accepted. A saved policy can look the same, so this alone proves nothing.
    /// </summary>
    internal static bool LooksLikeNoRecord(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return false;
        if (!root.TryGetProperty("defaultLevel", out var level) || level.ValueKind != JsonValueKind.String
            || level.GetString() != SessionAccessLevels.Ask)
            return false;
        if (root.TryGetProperty("agents", out var agents)
            && !(agents.ValueKind == JsonValueKind.Null
                 || (agents.ValueKind == JsonValueKind.Object && !agents.EnumerateObject().Any())))
            return false;
        if (!root.TryGetProperty("fullAccess", out var full) || full.ValueKind == JsonValueKind.Null) return true;
        if (full.ValueKind != JsonValueKind.Object) return false;
        foreach (var property in full.EnumerateObject())
        {
            var unset = property.Name == "enabled"
                ? property.Value.ValueKind is JsonValueKind.False or JsonValueKind.Null
                : property.Value.ValueKind == JsonValueKind.Null;
            if (!unset) return false;
        }
        return true;
    }

    /// <summary>
    /// <c>{version, defaultLevel, agents: {agentEntityId: level}, fullAccess: {enabled, textVersion,
    /// acknowledgedAt, acknowledgedBy}}</c>, bare or under <c>policy</c>, as a saved policy. Unknown
    /// levels are ignored, which leaves the stricter default in place. Whether RedLeaf holds a
    /// record at all is decided before this, in <see cref="FetchAsync"/>.
    /// </summary>
    internal static SessionAccessPolicy Parse(JsonElement root)
    {
        root = Unwrap(root);
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
