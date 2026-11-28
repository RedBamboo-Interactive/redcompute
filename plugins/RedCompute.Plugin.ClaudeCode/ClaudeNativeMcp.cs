using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using RedCompute.Core.Jobs;
using RedCompute.PluginSdk;
using RedCompute.PluginSdk.Hosting;
using RedCompute.PluginSdk.Hosting.Local;

namespace RedCompute.Plugin.ClaudeCode;

/// <summary>Local, operator-pinned Claude projection. Leaf grants and the host installation must agree.</summary>
internal static class ClaudeNativeMcp
{
    private const string Flag = "REDCOMPUTE_NATIVE_MCP";
    private const string Prefix = "leaf_native_";
    private const int Version = 1;
    private const int MaxServers = 8;
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private const int MaxPlanBytes = 128 * 1024;

    internal sealed record Declaration(string Installation, string Sha256, string Revision, string[] Tools);
    internal sealed record Server(Guid Id, Declaration Declaration);
    internal sealed record Plan(int Version, string SessionId, string AgentId, string OwnerId, string DiscussionId, Server[] Servers);
    internal sealed record Registration(Guid ServerId, string Installation, string Revision, string Sha256, Guid AgentId,
        string OwnerId, string DiscussionId, string Executable, string[] Arguments);
    internal sealed record HostRegistry(string ClaudeSha256, Registration[] Registrations);
    internal sealed record Projection(string Name, Registration Installation, string[] Tools);

    internal static async Task<IReadOnlyList<string>> ApplyAsync(IExecutionHost host, string cli, string sessionId,
        JobProvenance? provenance, IReadOnlyDictionary<string, string>? environment, List<string> arguments,
        CancellationToken ct)
    {
        if (host is not LocalExecutionHost || !host.Platform.IsWindows
            || host.Platform.GetEnvironmentVariable(Flag) != "1" || provenance is null
            || LeafMcpGateway.TokenFor(environment) is not { } bearer
            || !environment!.TryGetValue(SessionScratch.RedLeafUrlVariable, out var baseUrl)
            || !TryPlanUri(baseUrl, out var uri))
            return [];

        try
        {
            var registryFile = host.Path(host.PathRules.Combine(host.Platform.ComputeDataRoot, "mcp-native-installations.json"));
            var registryStat = host.Files.Stat(registryFile);
            if (!registryStat.Exists || registryStat.IsDirectory || registryStat.Length > MaxPlanBytes) return [];
            var registry = JsonSerializer.Deserialize<HostRegistry>(await host.Files.ReadAllTextAsync(registryFile, ct), Web);
            if (registry?.Registrations is not { Length: > 0 and <= 128 }) return [];

            var plan = await FetchPlanAsync(host.Network, uri, bearer, ct);
            if (plan is null) return [];
            var projected = Select(plan, registry, sessionId, provenance)
                .Where(p => IsPinnedExecutable(host, p.Installation.Executable, p.Installation.Sha256)).ToArray();
            if (projected.Length == 0 || !IsPinnedExecutable(host, cli, registry.ClaudeSha256)) return [];
            var helper = host.PathRules.Combine(AppContext.BaseDirectory, "RedCompute.exe");
            return host.Files.FileExists(host.Path(helper)) ? Append(arguments, helper, projected) : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
            or HttpRequestException or JsonException or CryptographicException
            || ex is OperationCanceledException && !ct.IsCancellationRequested)
        {
            // A mixed-version kernel, inaccessible registry or failed verification leaves only the managed gateway.
            return [];
        }
    }

    internal static async Task<Plan?> FetchPlanAsync(IHostNetwork network, Uri uri, string bearer, CancellationToken ct)
    {
        using var handler = network.CreateServiceHandler(uri);
        // The local service seam supplies this handler. Any other transport lacks the required policy.
        if (handler is not HttpClientHandler direct) return null;
        direct.AllowAutoRedirect = false;
        direct.UseProxy = false;
        direct.UseCookies = false;

        using var client = new HttpClient(direct, disposeHandler: false)
        {
            Timeout = TimeSpan.FromSeconds(3),
            MaxResponseContentBufferSize = MaxPlanBytes,
        };
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", bearer);
        using var response = await client.SendAsync(request, ct);
        return response.IsSuccessStatusCode
            ? JsonSerializer.Deserialize<Plan>(await response.Content.ReadAsStringAsync(ct), Web)
            : null;
    }

    internal static bool TryPlanUri(string value, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Uri? uri)
    {
        uri = null;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var root)
            || root.Host is not ("127.0.0.1" or "[::1]")
            || root.Scheme is not ("http" or "https") || root.AbsolutePath != "/"
            || root.UserInfo.Length != 0 || root.Query.Length != 0 || root.Fragment.Length != 0)
            return false;
        uri = new Uri(root, "/api/mcp/native-plan");
        return true;
    }

    internal static IReadOnlyList<Projection> Select(Plan plan, HostRegistry registry, string sessionId, JobProvenance provenance)
    {
        var discussion = provenance.Context.FirstOrDefault(c => c.Kind == "discussion")?.Id;
        if (plan.Version != Version || plan.SessionId != sessionId || provenance.Origin.App.Id != "nova"
            || provenance.Actor.Kind != "agent" || provenance.OnBehalfOf.Kind != "user"
            || !Guid.TryParse(provenance.Actor.EntityId, out var agent) || !Guid.TryParse(plan.AgentId, out var planAgent)
            || planAgent != agent || string.IsNullOrEmpty(discussion) || plan.DiscussionId != discussion
            || string.IsNullOrEmpty(plan.OwnerId) || plan.OwnerId != provenance.OnBehalfOf.Id
            || plan.Servers is not { Length: > 0 and <= MaxServers }
            || plan.Servers.Any(s => s is null) || plan.Servers.Select(s => s.Id).Distinct().Count() != plan.Servers.Length)
            return [];

        var projected = new List<Projection>();
        foreach (var server in plan.Servers)
        {
            if (server.Id == Guid.Empty || server.Declaration is not { } declaration
                || !IsDigest(declaration.Revision) || !IsDigest(declaration.Sha256) || !IsName(declaration.Installation)
                || declaration.Tools is not { Length: > 0 and <= 32 } || declaration.Tools.Any(t => !IsName(t))
                || declaration.Tools.Distinct(StringComparer.Ordinal).Count() != declaration.Tools.Length)
                continue;
            var matches = registry.Registrations.Where(r => r is not null && r.ServerId == server.Id
                && r.AgentId == agent && r.OwnerId == plan.OwnerId && r.DiscussionId == discussion
                && r.Installation == declaration.Installation && r.Revision == declaration.Revision
                && string.Equals(r.Sha256, declaration.Sha256, StringComparison.OrdinalIgnoreCase)).Take(2).ToArray();
            if (matches.Length != 1 || matches[0].Arguments is not { Length: <= 32 } argv
                || argv.Any(a => a is null || a.Length > 1024 || a.Contains('\0')))
                continue;
            projected.Add(new(Prefix + server.Id.ToString("N"), matches[0], declaration.Tools));
        }
        return projected;
    }

    internal static IReadOnlyList<string> Append(List<string> arguments, string helper, IReadOnlyList<Projection> servers)
    {
        if (servers.Count == 0) return [];
        var index = arguments.IndexOf("--mcp-config");
        if (index < 0 || index + 1 >= arguments.Count || arguments.Contains("--settings")) return [];
        var config = JsonNode.Parse(arguments[index + 1])!["mcpServers"]!.AsObject();
        var hooks = new JsonArray();
        foreach (var server in servers)
        {
            config.Add(server.Name, JsonSerializer.SerializeToNode(new
            {
                type = "stdio",
                command = helper,
                args = new[] { "--leaf-mcp-stdio", server.Installation.Sha256, server.Installation.Executable }
                    .Concat(server.Installation.Arguments),
            }, Web));
            hooks.Add(JsonSerializer.SerializeToNode(new
            {
                matcher = "^mcp__" + server.Name + "__",
                hooks = new[]
                {
                    new
                    {
                        type = "command", command = helper,
                        args = new[] { "--leaf-mcp-read-guard" }.Concat(server.Tools.Select(t => $"mcp__{server.Name}__{t}")),
                        timeout = 5, onFailure = "block",
                    },
                },
            }, Web));
        }
        arguments[index + 1] = new JsonObject { ["mcpServers"] = config.DeepClone() }.ToJsonString();
        // Use only this session's hook configuration and skip ambient user/project settings in the pilot.
        arguments.AddRange(["--setting-sources", "", "--settings",
            new JsonObject { ["hooks"] = new JsonObject { ["PreToolUse"] = hooks } }.ToJsonString()]);
        return servers.Select(s => s.Name).ToArray();
    }

    private static bool IsPinnedExecutable(IExecutionHost host, string? path, string? digest)
    {
        if (string.IsNullOrWhiteSpace(path) || !IsDigest(digest) || !host.PathRules.IsPathFullyQualified(path)
            || !Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase))
            return false;
        var file = host.Path(path);
        if (!host.Files.FileExists(file)) return false;
        using var stream = host.Files.OpenRead(file, FileShare.Read);
        return string.Equals(Convert.ToHexString(SHA256.HashData(stream)), digest, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsName(string? value)
        => value is { Length: > 0 and <= 64 } && char.IsAsciiLetterOrDigit(value[0])
            && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-');

    private static bool IsDigest(string? value) => value is { Length: 64 } && value.All(char.IsAsciiHexDigit);
}
