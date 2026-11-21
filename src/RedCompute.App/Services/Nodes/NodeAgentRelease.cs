using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace RedCompute.App.Services.Nodes;

/// <summary>Where the Nightly published leaf-node (Phase 3 R7, D3), for the "Add a node" dialog.</summary>
/// <param name="Available">False when the feed could not be read or names no leaf-node; <see cref="Reason"/> says why.</param>
/// <param name="Version">The agent's version: the RedCompute release it was built with, from the same commit.</param>
/// <param name="ReleaseUrl">The release page that lists every file, when the release is on GitHub.</param>
public sealed record NodeAgentDownload(
    bool Available,
    string? Reason,
    string Channel,
    string? Version,
    string? SourceCommit,
    string? ReleaseUrl,
    string? ManifestUrl,
    NodeAgentPlatformDownload? Windows,
    NodeAgentPlatformDownload? Linux);

/// <param name="InstallCommand">The one line that downloads, verifies and installs the agent. Pairing is a separate
/// <c>leaf-node pair</c> step.</param>
public sealed record NodeAgentPlatformDownload(
    string ArchiveUrl,
    string Sha256,
    long SizeBytes,
    string InstallerUrl,
    string InstallCommand);

/// <summary>
/// Resolves where leaf-node was published, on request: the signed release feed's current RedCompute record
/// (its channel pointer, then the record the pointer names by SHA-256), then <c>leaf-node-manifest.v1.json</c>
/// in that record's release, which must name the record's version and source commit. This is the chain the
/// installers follow before they install anything. HTTPS only; the answer is kept for 10 minutes (a failure
/// for 1 minute). No background work: nothing is fetched until someone asks.
/// </summary>
public sealed partial class NodeAgentRelease
{
    public const string DefaultFeed = "https://raw.githubusercontent.com/RedBamboo-Interactive/redleaf-releases/release-feed";
    public const string FeedVariable = "LEAF_NODE_FEED_URL";
    public const string Channel = "nightly";
    private const int MaxDocumentBytes = 1 << 20;
    private static readonly TimeSpan Fresh = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan RetryAfterFailure = TimeSpan.FromMinutes(1);

    private readonly HttpClient _http;
    private readonly string _feed;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private (NodeAgentDownload Value, DateTimeOffset At)? _cached;

    public NodeAgentRelease(HttpMessageHandler? handler = null, string? feed = null, TimeProvider? time = null)
    {
        _http = new HttpClient(handler ?? new SocketsHttpHandler { AutomaticDecompression = System.Net.DecompressionMethods.All })
        {
            Timeout = TimeSpan.FromSeconds(10),
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("RedCompute");
        var configured = feed ?? Environment.GetEnvironmentVariable(FeedVariable);
        _feed = (string.IsNullOrWhiteSpace(configured) ? DefaultFeed : configured).TrimEnd('/');
        _time = time ?? TimeProvider.System;
    }

    public async Task<NodeAgentDownload> GetAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var now = _time.GetUtcNow();
            if (_cached is { } cached && now - cached.At < (cached.Value.Available ? Fresh : RetryAfterFailure))
                return cached.Value;
            var value = await ResolveAsync(ct);
            _cached = (value, now);
            return value;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<NodeAgentDownload> ResolveAsync(CancellationToken ct)
    {
        try
        {
            var pointerJson = await FetchAsync($"{_feed}/components/redcompute/channels/{Channel}.json", ct);
            var pointer = JsonNode.Parse(pointerJson)?["payload"]?["record"];
            var recordUrl = Text(pointer?["url"]);
            var recordSha = Text(pointer?["sha256"]);
            if (recordUrl is null || recordSha is null) return Unavailable("The feed's RedCompute pointer is unreadable.");
            var recordBytes = await FetchAsync(recordUrl, ct);
            if (!string.Equals(Convert.ToHexStringLower(SHA256.HashData(recordBytes)), recordSha, StringComparison.Ordinal))
                return Unavailable("The RedCompute record does not match the hash its pointer names.");
            var record = JsonNode.Parse(recordBytes)?["payload"];
            var version = Text(record?["version"]);
            var commit = Text(record?["sourceCommit"]);
            var artifactUrl = Text(record?["artifact"]?["url"]);
            if (Text(record?["componentId"]) != "redcompute" || version is null || commit is null || artifactUrl is null
                || artifactUrl.LastIndexOf('/') <= "https://".Length)
                return Unavailable("The feed's RedCompute record is unreadable.");
            var folder = artifactUrl[..artifactUrl.LastIndexOf('/')];

            var manifestUrl = $"{folder}/leaf-node-manifest.v1.json";
            byte[] manifestBytes;
            try
            {
                manifestBytes = await FetchAsync(manifestUrl, ct);
            }
            catch (HttpRequestException)
            {
                return Unavailable($"RedCompute {version} was released without leaf-node.") with { Version = version };
            }
            var manifest = JsonNode.Parse(manifestBytes);
            if (manifest?["schemaVersion"]?.GetValue<int>() != 1 || Text(manifest["componentId"]) != "leaf-node"
                || Text(manifest["version"]) != version || Text(manifest["sourceCommit"]) != commit)
                return Unavailable($"The leaf-node manifest does not belong to RedCompute {version}.");

            NodeAgentPlatformDownload? Platform(string rid, string archive, string installer, Func<string, string> command)
            {
                var entry = manifest["artifacts"]?.AsArray().OfType<JsonObject>()
                    .SingleOrDefault(artifact => Text(artifact["rid"]) == rid);
                if (entry is null || Text(entry["name"]) != archive || Text(entry["sha256"]) is not { } sha
                    || !HashPattern().IsMatch(sha) || entry["sizeBytes"] is not JsonValue sizeValue
                    || !sizeValue.TryGetValue<long>(out var size) || size <= 0)
                    return null;
                var installerUrl = $"{folder}/{installer}";
                return new NodeAgentPlatformDownload($"{folder}/{archive}", sha, size, installerUrl, command(installerUrl));
            }

            var windows = Platform("win-x64", "leaf-node-win-x64.zip", "leaf-node-install.ps1",
                url => $"irm {url} | iex; Install-LeafNode");
            var linux = Platform("linux-x64", "leaf-node-linux-x64.tar.gz", "leaf-node-install.sh",
                url => $"curl -fsSL {url} | sh");
            if (windows is null || linux is null) return Unavailable($"The leaf-node manifest of RedCompute {version} is incomplete.");
            var release = ReleasePage().Match(folder);
            return new NodeAgentDownload(true, null, Channel, version, commit,
                release.Success ? $"{release.Groups["repository"].Value}/releases/tag/{release.Groups["tag"].Value}" : null,
                manifestUrl, windows, linux);
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException
                                          or InvalidOperationException or FormatException)
        {
            if (ct.IsCancellationRequested) throw;
            return Unavailable($"The release feed could not be read: {error.Message}");
        }
    }

    private async Task<byte[]> FetchAsync(string url, CancellationToken ct)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new HttpRequestException($"Refusing {url}: https only.");
        using var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > MaxDocumentBytes)
            throw new HttpRequestException($"{url} is larger than {MaxDocumentBytes} bytes.");
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > MaxDocumentBytes) throw new HttpRequestException($"{url} is larger than {MaxDocumentBytes} bytes.");
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    private static NodeAgentDownload Unavailable(string reason)
        => new(false, reason, Channel, null, null, null, null, null, null);

    private static string? Text(JsonNode? node)
        => node is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text) ? text : null;

    [GeneratedRegex("^[0-9a-f]{64}$")]
    private static partial Regex HashPattern();

    [GeneratedRegex("^(?<repository>https://github\\.com/[^/]+/[^/]+)/releases/download/(?<tag>[^/]+)$")]
    private static partial Regex ReleasePage();
}
